// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

namespace Galaxus.RecommendationAgent.Providers;

/// <summary>The inference hosts VITRINE can be pointed at.</summary>
/// <remarks>
/// Two protocol families sit underneath these five values. <see cref="AzureOpenAI"/> and
/// <see cref="Foundry"/> speak the Azure OpenAI protocol; every other value is an
/// OpenAI-compatible HTTP API reached at its own base URL. Adding a host is a new value here,
/// a row in <see cref="InferenceProviderEnvironment"/>'s variable table, and nothing else.
/// </remarks>
public enum InferenceProvider
{
    /// <summary>No host has usable configuration.</summary>
    None,

    /// <summary>An Azure OpenAI resource.</summary>
    AzureOpenAI,

    /// <summary>Bitdeer AI Model Studio.</summary>
    Bitdeer,

    /// <summary>OpenAI's own API.</summary>
    OpenAI,

    /// <summary>A Microsoft Foundry resource/inference endpoint.</summary>
    Foundry,

    /// <summary>Any other OpenAI-compatible host: vLLM, Ollama, LM Studio, Together, Groq.</summary>
    OpenAiCompatible
}

/// <summary>How the resolved provider was chosen.</summary>
public enum InferenceProviderSelection
{
    /// <summary>Nothing was resolved.</summary>
    None,

    /// <summary><c>AI_INFERENCE_PROVIDER</c> named it.</summary>
    Explicit,

    /// <summary>The selector was unset and this host was the first with complete credentials.</summary>
    AutoDetected
}

/// <summary>
/// One environment variable whose value may never reach a log line, a report, or an artifact,
/// together with how it is redacted. Every redaction boundary in the solution iterates this list
/// rather than keeping its own copy, so a newly supported host cannot be half-covered.
/// </summary>
/// <param name="Name">Environment variable name.</param>
/// <param name="Replacement">Text substituted for the value.</param>
/// <param name="Comparison">How the value is matched; endpoints are matched case-insensitively.</param>
public sealed record SecretBearingVariable(string Name, string Replacement, StringComparison Comparison);

/// <summary>
/// The resolved host, its credentials, and the models selected on it. This type deliberately
/// depends on no inference SDK: the CLI, the app, and the evaluation project share one resolver
/// and the SDK dependency stays at the composition edge.
/// </summary>
/// <param name="Provider">The resolved host.</param>
/// <param name="ProviderTag">Stable lowercase tag — <c>azure</c>, <c>bitdeer</c>, <c>openai</c>, <c>foundry</c>, <c>openai-compatible</c>, <c>none</c>.</param>
/// <param name="DisplayName">Human-readable host name for operator banners.</param>
/// <param name="RawEndpoint">The endpoint exactly as configured, before any family-specific validation.</param>
/// <param name="ApiKey">The host's API key. Never logged, never printed, never hashed.</param>
/// <param name="Model">Resolved subject model, after the host's default is applied.</param>
/// <param name="SecondaryModel">Optional second model for a comparison run.</param>
/// <param name="TertiaryModel">Optional third model for a comparison run.</param>
/// <param name="JudgeModel">Optional judge model; null means the judge shares the subject model.</param>
/// <param name="EmbeddingModel">Optional embedding model; null means the shared default applies.</param>
/// <param name="Selection">Whether the host was named or detected.</param>
/// <param name="Diagnostic">Why nothing is configured, when nothing is.</param>
public sealed record InferenceProviderSettings(
    InferenceProvider Provider,
    string ProviderTag,
    string DisplayName,
    string? RawEndpoint,
    string? ApiKey,
    string? Model,
    string? SecondaryModel,
    string? TertiaryModel,
    string? JudgeModel,
    string? EmbeddingModel,
    InferenceProviderSelection Selection,
    string? Diagnostic)
{
    /// <summary>True when a host was resolved. It says nothing about connectivity.</summary>
    public bool IsConfigured => Provider != InferenceProvider.None;

    /// <summary>
    /// True when this host speaks the Azure OpenAI protocol. An Azure OpenAI resource and a
    /// Microsoft Foundry resource share it; everything else is an OpenAI client at its own base URL.
    /// </summary>
    public bool UsesAzureProtocol =>
        Provider is InferenceProvider.AzureOpenAI or InferenceProvider.Foundry;

    /// <summary>
    /// <c>model@provider</c> — the identity a measurement should carry. The same model name on two
    /// hosts is not the same measurement, so recording the model alone makes a Bitdeer run and an
    /// Azure run indistinguishable in provenance.
    /// </summary>
    public string ModelIdentity => $"{(string.IsNullOrWhiteSpace(Model) ? "?" : Model)}@{ProviderTag}";

    /// <summary>Nothing resolved, with the reason an operator needs.</summary>
    /// <param name="diagnostic">Why nothing is configured.</param>
    public static InferenceProviderSettings NotConfigured(string diagnostic) => new(
        InferenceProvider.None,
        "none",
        "no inference provider",
        RawEndpoint: null,
        ApiKey: null,
        Model: null,
        SecondaryModel: null,
        TertiaryModel: null,
        JudgeModel: null,
        EmbeddingModel: null,
        InferenceProviderSelection.None,
        diagnostic);

    /// <summary>
    /// Safe by construction. A positional record's generated <c>ToString</c> prints every property
    /// including the key, and this is exactly the kind of object that ends up in a line such as
    /// "resolved settings: {settings}".
    /// </summary>
    public override string ToString() =>
        $"InferenceProviderSettings {{ Provider = {Provider}, ProviderTag = {ProviderTag}, "
        + $"Endpoint = {InferenceProviderEnvironment.SafeEndpointText(RawEndpoint)}, "
        + $"ApiKey = {(ApiKey is null ? "null" : "[redacted]")}, Model = {Model}, "
        + $"Selection = {Selection}, Diagnostic = {Diagnostic} }}";
}

/// <summary>
/// Turns the environment into an <see cref="InferenceProviderSettings"/>. This is the only place
/// that decides <em>which</em> host VITRINE talks to.
/// </summary>
/// <remarks>
/// <para>Four rules, each written because the obvious alternative caused a real problem:</para>
/// <list type="number">
///   <item><b>Explicit beats detected.</b> When <c>AI_INFERENCE_PROVIDER</c> names a host, that host is used.</item>
///   <item>
///     <b>An explicit mistake fails closed.</b> A named host with missing variables, or a value
///     that is not one of the five, resolves to nothing with a reason. It never silently falls back
///     to a host the operator did not choose: running a paid benchmark on the wrong model is worse
///     than not running it.
///   </item>
///   <item>
///     <b>Auto-detect in a fixed order when the selector is unset</b> — Azure OpenAI, Bitdeer,
///     OpenAI, Foundry, then the generic OpenAI-compatible endpoint. Azure is first so a machine
///     that has only ever set <c>AZURE_OPENAI_*</c> behaves exactly as it did before this selector
///     existed.
///   </item>
///   <item>
///     <b>A half-configured host is a typo, not an unchosen one.</b> Naming every host's full
///     requirements would tell an operator that <c>AZURE_OPENAI_ENDPOINT</c> is missing when they
///     have just set it, so a partial setup reports only what <em>that</em> host lacks.
///   </item>
/// </list>
/// </remarks>
public static class InferenceProviderEnvironment
{
    /// <summary>The one selector variable.</summary>
    public const string SelectorVariable = "AI_INFERENCE_PROVIDER";

    /// <summary>Per-attempt network timeout, in seconds, applied to every constructed client.</summary>
    public const string NetworkTimeoutVariable = "VITRINE_PROVIDER_NETWORK_TIMEOUT_S";

    /// <summary>Recommended Azure OpenAI subject deployment when none is configured.</summary>
    public const string AzureDefaultModel = "gpt-5-mini";

    /// <summary>Bitdeer AI Model Studio's OpenAI-compatible base URL.</summary>
    public const string BitdeerDefaultEndpoint = "https://api-inference.bitdeer.ai/v1";

    /// <summary>The Bitdeer model VITRINE uses when <c>BITDEER_MODEL</c> is unset.</summary>
    public const string BitdeerDefaultModel = "zai-org/GLM-5.3-Flash";

    /// <summary>OpenAI's own base URL.</summary>
    public const string OpenAiDefaultEndpoint = "https://api.openai.com/v1";

    /// <summary>The OpenAI model VITRINE uses when <c>OPENAI_MODEL</c> is unset.</summary>
    public const string OpenAiDefaultModel = "gpt-4o-mini";

    /// <summary>The value a keyless local OpenAI-compatible server is given, because the SDK requires one.</summary>
    public const string KeylessSentinel = "no-key-needed";

    /// <summary>
    /// Default per-attempt network timeout. The SDK default of 100 seconds fires on a slow real
    /// model behind a busy host and aborts a whole run, so this is deliberately generous.
    /// </summary>
    public static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromSeconds(180);

    // Azure first: a machine that has only ever configured AZURE_OPENAI_* must resolve exactly as
    // it did before the selector existed. This order is pinned by a test for that reason.
    private static readonly InferenceProvider[] AutoDetectOrder =
    [
        InferenceProvider.AzureOpenAI,
        InferenceProvider.Bitdeer,
        InferenceProvider.OpenAI,
        InferenceProvider.Foundry,
        InferenceProvider.OpenAiCompatible
    ];

    private static readonly ProviderVariables[] Table =
    [
        new(InferenceProvider.AzureOpenAI, "azure", "Azure OpenAI",
            EndpointVariable: "AZURE_OPENAI_ENDPOINT", DefaultEndpoint: null,
            ApiKeyVariable: "AZURE_OPENAI_API_KEY",
            ModelVariable: "AZURE_OPENAI_DEPLOYMENT", DefaultModel: AzureDefaultModel,
            JudgeVariable: "AZURE_OPENAI_JUDGE_DEPLOYMENT",
            EmbeddingVariable: "AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),

        new(InferenceProvider.Bitdeer, "bitdeer", "Bitdeer AI Model Studio",
            EndpointVariable: "BITDEER_ENDPOINT", DefaultEndpoint: BitdeerDefaultEndpoint,
            ApiKeyVariable: "BITDEER_API_KEY",
            ModelVariable: "BITDEER_MODEL", DefaultModel: BitdeerDefaultModel,
            JudgeVariable: "BITDEER_JUDGE_MODEL",
            EmbeddingVariable: "BITDEER_EMBEDDING_MODEL"),

        new(InferenceProvider.OpenAI, "openai", "OpenAI",
            EndpointVariable: "OPENAI_BASE_URL", DefaultEndpoint: OpenAiDefaultEndpoint,
            ApiKeyVariable: "OPENAI_API_KEY",
            ModelVariable: "OPENAI_MODEL", DefaultModel: OpenAiDefaultModel,
            JudgeVariable: "OPENAI_JUDGE_MODEL",
            EmbeddingVariable: "OPENAI_EMBEDDING_MODEL"),

        new(InferenceProvider.Foundry, "foundry", "Microsoft Foundry",
            EndpointVariable: "FOUNDRY_ENDPOINT", DefaultEndpoint: null,
            ApiKeyVariable: "FOUNDRY_API_KEY",
            ModelVariable: "FOUNDRY_MODEL", DefaultModel: null,
            JudgeVariable: "FOUNDRY_JUDGE_MODEL",
            EmbeddingVariable: "FOUNDRY_EMBEDDING_MODEL"),

        new(InferenceProvider.OpenAiCompatible, "openai-compatible", "OpenAI-compatible host",
            EndpointVariable: "OPENAI_COMPATIBLE_ENDPOINT", DefaultEndpoint: null,
            ApiKeyVariable: "OPENAI_COMPATIBLE_API_KEY",
            ModelVariable: "OPENAI_COMPATIBLE_MODEL", DefaultModel: null,
            JudgeVariable: "OPENAI_COMPATIBLE_JUDGE_MODEL",
            EmbeddingVariable: "OPENAI_COMPATIBLE_EMBEDDING_MODEL")
    ];

    /// <summary>
    /// Every variable the resolver reads, required and optional alike, plus the Azure-only
    /// authentication variables.
    /// </summary>
    /// <remarks>
    /// <see cref="AnyConfigurationAttempted"/> walks this one list, and the test suite scrubs it.
    /// A newly added variable that is not here would quietly stop counting as configuration, which
    /// is why a test asserts the list stays in step with the table.
    /// </remarks>
    public static IReadOnlyList<string> AllVariables { get; } =
    [
        SelectorVariable,
        NetworkTimeoutVariable,
        "AZURE_OPENAI_AUTH_MODE",
        "AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID",
        .. Table.SelectMany(static provider => new[]
        {
            provider.EndpointVariable,
            provider.ApiKeyVariable,
            provider.ModelVariable,
            provider.SecondaryModelVariable,
            provider.TertiaryModelVariable,
            provider.JudgeVariable,
            provider.EmbeddingVariable
        })
    ];

    /// <summary>
    /// Values that may never appear in a printed line, a written report, or a persisted artifact.
    /// </summary>
    /// <remarks>
    /// Endpoints are included as well as keys: an endpoint URL names the resource, and a
    /// user-configured URL can itself carry a credential in its user-info, path, query, or fragment.
    /// </remarks>
    public static IReadOnlyList<SecretBearingVariable> SecretBearingVariables { get; } =
    [
        .. Table.Select(static provider =>
            new SecretBearingVariable(provider.ApiKeyVariable, "[REDACTED_KEY]", StringComparison.Ordinal)),
        .. Table.Select(static provider =>
            new SecretBearingVariable(provider.EndpointVariable, "[REDACTED_ENDPOINT]", StringComparison.OrdinalIgnoreCase)),
        new("AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID", "[REDACTED_MANAGED_IDENTITY]", StringComparison.OrdinalIgnoreCase)
    ];

    /// <summary>The accepted <c>AI_INFERENCE_PROVIDER</c> values, in auto-detect order.</summary>
    public static IReadOnlyList<string> KnownValues { get; } =
        [.. AutoDetectOrder.Select(TagOf)];

    /// <summary>Resolves the host from the process environment. Never cached: see the remarks.</summary>
    /// <remarks>
    /// A cached first resolution makes the process ignore a variable set later and makes every test
    /// after the first see stale settings. It is a handful of environment reads.
    /// </remarks>
    public static InferenceProviderSettings Resolve() => Resolve(Environment.GetEnvironmentVariable);

    /// <summary>Resolves the host from an arbitrary variable reader, so tests need no process-wide mutation.</summary>
    /// <param name="getEnvironmentVariable">Reads one variable by name.</param>
    public static InferenceProviderSettings Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        string? Env(string name)
        {
            var value = getEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        var selector = Env(SelectorVariable);
        if (selector is not null)
        {
            var named = Parse(selector);
            if (named is null)
            {
                return InferenceProviderSettings.NotConfigured(
                    $"{SelectorVariable} is not one of {string.Join(" | ", KnownValues)}.");
            }

            var missing = MissingVariablesOf(named.Value, Env);
            return missing.Count == 0
                ? Describe(named.Value, Env, InferenceProviderSelection.Explicit)
                : InferenceProviderSettings.NotConfigured(
                    $"{SelectorVariable}={TagOf(named.Value)}, but it is missing: {string.Join(", ", missing)}. "
                    + $"It needs {RequiredVariablesOf(named.Value)}.");
        }

        foreach (var candidate in AutoDetectOrder)
        {
            if (MissingVariablesOf(candidate, Env).Count == 0)
                return Describe(candidate, Env, InferenceProviderSelection.AutoDetected);
        }

        // Half-done setup: name exactly what THAT host lacks. Listing every host's full
        // requirements tells an operator that AZURE_OPENAI_ENDPOINT is missing when they just set it.
        var partial = AutoDetectOrder
            .Where(provider => IsPartiallyConfigured(provider, Env))
            .Select(provider =>
                $"{DisplayNameOf(provider)} is partially configured — missing: "
                + $"{string.Join(", ", MissingVariablesOf(provider, Env))}.")
            .ToList();

        return InferenceProviderSettings.NotConfigured(partial.Count > 0
            ? string.Join(" ", partial)
            : $"{SelectorVariable} is not set and no provider has credentials. Set one of: "
              + string.Join("; ", AutoDetectOrder.Select(provider =>
                  $"{TagOf(provider)} -> {RequiredVariablesOf(provider)}"))
              + ".");
    }

    /// <summary>True when the selector or any provider variable carries a value.</summary>
    /// <remarks>
    /// A host that was named or half-configured and could not be built is a typo, not an
    /// unconfigured machine. Any degraded or placeholder path must refuse to rescue it, or the
    /// resolver's fail-closed contract is undone from beneath.
    /// </remarks>
    /// <param name="getEnvironmentVariable">Reads one variable by name.</param>
    public static bool AnyConfigurationAttempted(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        return AllVariables.Any(name => !string.IsNullOrWhiteSpace(getEnvironmentVariable(name)));
    }

    /// <summary>Per-attempt network timeout, from <see cref="NetworkTimeoutVariable"/>.</summary>
    /// <param name="getEnvironmentVariable">Reads one variable by name; null uses the process environment.</param>
    public static TimeSpan NetworkTimeout(Func<string, string?>? getEnvironmentVariable = null)
    {
        var read = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var raw = read(NetworkTimeoutVariable);
        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw.Trim(), out var seconds)
            && seconds is > 0 and <= 3600
                ? TimeSpan.FromSeconds(seconds)
                : DefaultNetworkTimeout;
    }

    /// <summary>
    /// The endpoint policy for every OpenAI-compatible host: an absolute <c>https</c> URL, or
    /// <c>http</c> only to loopback so a local server still works.
    /// </summary>
    /// <remarks>
    /// The reason names the variable and the rule, never the value. A configured URL can carry a
    /// credential in four places — user-info, path, query, and fragment — and this reason reaches
    /// an operator surface.
    /// </remarks>
    /// <param name="value">The configured endpoint.</param>
    /// <param name="endpoint">The parsed endpoint, on success.</param>
    /// <param name="reason">Why it was refused, on failure.</param>
    public static bool TryValidateEndpoint(string? value, out Uri? endpoint, out string? reason)
    {
        endpoint = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "is not set.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            reason = "is not an absolute http(s) URL.";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            reason = "uses plain http to a non-loopback host; an API key would travel in cleartext. "
                   + "Use https, or a loopback address for a local server.";
            return false;
        }

        endpoint = uri;
        reason = null;
        return true;
    }

    /// <summary>Scheme, host, and port only — never user-info, path, query, or fragment.</summary>
    /// <remarks>
    /// All four of those parts can carry a credential, including a path segment such as
    /// <c>https://host/v1/&lt;token&gt;</c>. A guarantee that covers three of the four is not a guarantee.
    /// </remarks>
    /// <param name="endpoint">The endpoint to describe.</param>
    public static string SafeEndpoint(Uri? endpoint)
    {
        if (endpoint is null) return "(none)";
        if (!endpoint.IsAbsoluteUri) return "(relative)";
        var port = endpoint.IsDefaultPort ? string.Empty : $":{endpoint.Port}";
        return $"{endpoint.Scheme}://{endpoint.Host}{port}";
    }

    /// <summary>Same guarantee as <see cref="SafeEndpoint(Uri?)"/> for a value that may not parse.</summary>
    /// <param name="endpoint">The configured endpoint text.</param>
    public static string SafeEndpointText(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? "(none)"
        : Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) ? SafeEndpoint(uri)
        : "(unparseable)";

    /// <summary>The stable lowercase tag for a host.</summary>
    /// <param name="provider">The host.</param>
    public static string TagOf(InferenceProvider provider) => Row(provider)?.Tag ?? "none";

    /// <summary>The operator-facing name for a host.</summary>
    /// <param name="provider">The host.</param>
    public static string DisplayNameOf(InferenceProvider provider) =>
        Row(provider)?.DisplayName ?? "no inference provider";

    /// <summary>The variable naming a host's subject model.</summary>
    /// <param name="provider">The host.</param>
    public static string? ModelVariableOf(InferenceProvider provider) => Row(provider)?.ModelVariable;

    /// <summary>The variable naming a host's judge model.</summary>
    /// <param name="provider">The host.</param>
    public static string? JudgeVariableOf(InferenceProvider provider) => Row(provider)?.JudgeVariable;

    /// <summary>The variable naming a host's embedding model.</summary>
    /// <param name="provider">The host.</param>
    public static string? EmbeddingVariableOf(InferenceProvider provider) => Row(provider)?.EmbeddingVariable;

    /// <summary>The variable naming a host's endpoint.</summary>
    /// <param name="provider">The host.</param>
    public static string? EndpointVariableOf(InferenceProvider provider) => Row(provider)?.EndpointVariable;

    /// <summary>The variable naming a host's API key.</summary>
    /// <param name="provider">The host.</param>
    public static string? ApiKeyVariableOf(InferenceProvider provider) => Row(provider)?.ApiKeyVariable;

    /// <summary>The default endpoint for a host, when it has one.</summary>
    /// <param name="provider">The host.</param>
    public static string? DefaultEndpointOf(InferenceProvider provider) => Row(provider)?.DefaultEndpoint;

    /// <summary>The default subject model for a host, when it has one.</summary>
    /// <param name="provider">The host.</param>
    public static string? DefaultModelOf(InferenceProvider provider) => Row(provider)?.DefaultModel;

    /// <summary>A human-readable list of a host's required variables.</summary>
    /// <param name="provider">The host.</param>
    public static string RequiredVariablesOf(InferenceProvider provider) =>
        string.Join(" + ", RequiredVariableNames(provider));

    /// <summary>Parses an <c>AI_INFERENCE_PROVIDER</c> value; null when it names no known host.</summary>
    /// <param name="value">The configured selector value.</param>
    public static InferenceProvider? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var tag = value.Trim();

        foreach (var row in Table)
        {
            if (tag.Equals(row.Tag, StringComparison.OrdinalIgnoreCase)) return row.Provider;
        }

        // Spelled-out aliases an operator reasonably reaches for.
        return tag.Equals("azure-openai", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("azureopenai", StringComparison.OrdinalIgnoreCase) ? InferenceProvider.AzureOpenAI
            : tag.Equals("openai_compatible", StringComparison.OrdinalIgnoreCase) ? InferenceProvider.OpenAiCompatible
            : null;
    }

    private static InferenceProviderSettings Describe(
        InferenceProvider provider,
        Func<string, string?> env,
        InferenceProviderSelection selection)
    {
        var row = Row(provider)!;
        var model = env(row.ModelVariable) ?? row.DefaultModel;

        return new InferenceProviderSettings(
            provider,
            row.Tag,
            row.DisplayName,
            env(row.EndpointVariable) ?? row.DefaultEndpoint,
            // A keyless local server still needs a value for the SDK's credential type. The
            // sentinel is only ever reached where the host itself requires no key.
            env(row.ApiKeyVariable)
                ?? (provider == InferenceProvider.OpenAiCompatible ? KeylessSentinel : null),
            model,
            // A host that defines no other default falls back to the primary model rather than to
            // something the operator did not ask for.
            env(row.SecondaryModelVariable) ?? model,
            env(row.TertiaryModelVariable) ?? model,
            env(row.JudgeVariable),
            env(row.EmbeddingVariable),
            selection,
            Diagnostic: null);
    }

    private static IReadOnlyList<string> RequiredVariableNames(InferenceProvider provider)
    {
        var row = Row(provider);
        if (row is null) return [];

        return provider switch
        {
            // The deployment name has a default, and an identity authentication mode needs no key,
            // so requiring either here would break a machine that already works.
            InferenceProvider.AzureOpenAI => [row.EndpointVariable, row.ApiKeyVariable],

            // The endpoint and the model both have defaults: the common case is one variable.
            InferenceProvider.Bitdeer or InferenceProvider.OpenAI => [row.ApiKeyVariable],

            InferenceProvider.Foundry => [row.EndpointVariable, row.ApiKeyVariable, row.ModelVariable],

            // A keyless local host is ordinary, so the key is optional here and only here.
            InferenceProvider.OpenAiCompatible => [row.EndpointVariable, row.ModelVariable],

            _ => []
        };
    }

    private static List<string> MissingVariablesOf(InferenceProvider provider, Func<string, string?> env)
    {
        var missing = RequiredVariableNames(provider)
            .Where(name => env(name) is null)
            .ToList();

        // Azure's key requirement is conditional: an explicitly selected Microsoft Entra mode
        // authenticates without one, and demanding a key there would contradict that choice.
        if (provider == InferenceProvider.AzureOpenAI && UsesEntraAuthentication(env))
            missing.Remove("AZURE_OPENAI_API_KEY");

        return missing;
    }

    private static bool UsesEntraAuthentication(Func<string, string?> env)
    {
        var mode = env("AZURE_OPENAI_AUTH_MODE");
        return mode is not null
            && (mode.Equals("default-credential", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("managed-identity", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPartiallyConfigured(InferenceProvider provider, Func<string, string?> env)
    {
        var required = RequiredVariableNames(provider);
        if (required.Count == 0) return false;
        var present = required.Count(name => env(name) is not null);
        return present > 0 && present < required.Count;
    }

    private static ProviderVariables? Row(InferenceProvider provider) =>
        Table.FirstOrDefault(row => row.Provider == provider);

    private sealed record ProviderVariables(
        InferenceProvider Provider,
        string Tag,
        string DisplayName,
        string EndpointVariable,
        string? DefaultEndpoint,
        string ApiKeyVariable,
        string ModelVariable,
        string? DefaultModel,
        string JudgeVariable,
        string EmbeddingVariable)
    {
        internal string SecondaryModelVariable { get; } = $"{ModelVariable}_2";
        internal string TertiaryModelVariable { get; } = $"{ModelVariable}_3";
    }
}
