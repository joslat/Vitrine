// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Azure;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Providers;

namespace Galaxus.RecommendationAgent;

/// <summary>The explicit Azure OpenAI authentication path selected for a live composition.</summary>
public enum AzureOpenAiAuthenticationMode
{
    /// <summary>Authenticate with <c>AZURE_OPENAI_API_KEY</c>.</summary>
    ApiKey,

    /// <summary>
    /// Authenticate through <c>DefaultAzureCredential</c>. This convenience chain is intended for
    /// local development, where it can use the developer's Azure CLI, PowerShell, or IDE login.
    /// </summary>
    DefaultAzureCredential,

    /// <summary>
    /// Authenticate through the Azure host's deterministic system- or user-assigned managed identity.
    /// </summary>
    ManagedIdentity
}

/// <summary>
/// The three resolved model names. Its string representation is log-safe: it never contains
/// an endpoint or credential, and control characters are removed from labels.
/// </summary>
public sealed record AzureOpenAiDeploymentSelection(
    string SubjectDeployment,
    string JudgeDeployment,
    string EmbeddingDeployment)
{
    /// <summary>True when subject and judge intentionally resolve to the same model.</summary>
    public bool JudgeSharesSubjectDeployment =>
        string.Equals(SubjectDeployment, JudgeDeployment, StringComparison.Ordinal);

    /// <summary>Log-safe subject model label.</summary>
    public string SubjectLabel => Config.SafeDeploymentLabel(SubjectDeployment);

    /// <summary>Log-safe judge model label.</summary>
    public string JudgeLabel => Config.SafeDeploymentLabel(JudgeDeployment);

    /// <summary>Log-safe embedding model label.</summary>
    public string EmbeddingLabel => Config.SafeDeploymentLabel(EmbeddingDeployment);

    /// <inheritdoc />
    public override string ToString() =>
        $"subject={SubjectLabel}; judge={JudgeLabel}; embeddings={EmbeddingLabel}";
}

/// <summary>
/// A local, non-network readiness assessment safe to show in the CLI, UI, or an evaluation
/// receipt. It deliberately exposes no endpoint, key, token, or managed-identity client id.
/// </summary>
public sealed record AzureOpenAiReadiness(
    bool IsReady,
    AzureOpenAiAuthenticationMode? AuthenticationMode,
    string AuthenticationLabel,
    AzureOpenAiDeploymentSelection Deployments,
    string? BlockingReason,
    string ProviderTag = "none",
    string ProviderDisplayName = "no inference provider")
{
    /// <summary>A concise safe status for operator surfaces.</summary>
    public string SafeSummary => IsReady
        ? $"Local live configuration found · {ProviderDisplayName} · {AuthenticationLabel} · subject {Deployments.SubjectLabel} · "
          + (Deployments.JudgeSharesSubjectDeployment
              ? "judge shares subject model"
              : $"judge {Deployments.JudgeLabel}")
          + " · provider not contacted"
        : $"Live configuration not ready · {BlockingReason ?? "configuration is incomplete"} · provider not contacted";
}

/// <summary>Live inference configuration sourced from environment variables.</summary>
/// <remarks>
/// <para>
/// <c>AI_INFERENCE_PROVIDER</c> selects the host: <c>azure</c>, <c>bitdeer</c>, <c>openai</c>,
/// <c>foundry</c>, or <c>openai-compatible</c>. Leaving it unset auto-detects in that order, so a
/// machine that has only ever configured <c>AZURE_OPENAI_*</c> behaves exactly as it did before the
/// selector existed. <see cref="Providers.InferenceProviderEnvironment"/> owns that decision and the
/// per-host variable table; this type applies the family-specific validation and composes the
/// snapshot a client is built from.
/// </para>
/// <para>
/// <b>Azure OpenAI.</b> <c>AZURE_OPENAI_ENDPOINT</c> is required.
/// <c>AZURE_OPENAI_AUTH_MODE</c> explicitly selects <c>api-key</c>, <c>default-credential</c>, or
/// <c>managed-identity</c>. When it is unset, VITRINE retains its backward-compatible API-key
/// behavior; an explicitly selected identity mode never silently falls back to a key. API-key mode
/// additionally requires <c>AZURE_OPENAI_API_KEY</c>. Managed-identity mode may set
/// <c>AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID</c> for a user-assigned identity; otherwise it uses
/// the system-assigned identity. <c>default-credential</c> is for local development only.
/// </para>
/// <para>
/// <b>Bitdeer and every other OpenAI-compatible host.</b> One API key is the whole requirement:
/// <c>BITDEER_API_KEY</c>, with <c>BITDEER_ENDPOINT</c> and <c>BITDEER_MODEL</c> defaulted. These
/// hosts authenticate by key only, and their endpoint must be https or http to loopback.
/// </para>
/// <para>Optional model variables, per host:</para>
/// <list type="bullet">
///   <item><c>&lt;HOST&gt;_DEPLOYMENT</c> / <c>&lt;HOST&gt;_MODEL</c> — subject chat model.</item>
///   <item><c>&lt;HOST&gt;_JUDGE_DEPLOYMENT</c> / <c>&lt;HOST&gt;_JUDGE_MODEL</c> — evaluation judge; defaults explicitly to the subject model.</item>
///   <item><c>&lt;HOST&gt;_EMBEDDING_DEPLOYMENT</c> / <c>&lt;HOST&gt;_EMBEDDING_MODEL</c> — live embedding model.</item>
/// </list>
/// </remarks>
public static class Config
{
    public const string AuthenticationModeEnvironmentVariable = "AZURE_OPENAI_AUTH_MODE";
    public const string ManagedIdentityClientIdEnvironmentVariable = "AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID";

    /// <summary>
    /// Recommended Azure OpenAI subject deployment. Used when <c>AZURE_OPENAI_DEPLOYMENT</c> is
    /// unset. Override it with a deployment name that exists in the configured Azure OpenAI resource.
    /// </summary>
    public const string PreferredDeployment = InferenceProviderEnvironment.AzureDefaultModel;

    /// <summary>
    /// Recommended embedding model. Changing the embedding space invalidates any precomputed
    /// vector asset carrying a different model/template stamp.
    /// </summary>
    public const string PreferredEmbeddingDeployment = "text-embedding-3-small";

    private static readonly AsyncLocal<string?> ModelOverrideSlot = new();
    private static readonly AsyncLocal<string?> EmbeddingModelOverrideSlot = new();

    /// <summary>Programmatic subject-model override, scoped to the current async flow.</summary>
    public static string? ModelOverride
    {
        get => ModelOverrideSlot.Value;
        set => ModelOverrideSlot.Value = value;
    }

    /// <summary>Programmatic embedding-model override, scoped to the current async flow.</summary>
    public static string? EmbeddingModelOverride
    {
        get => EmbeddingModelOverrideSlot.Value;
        set => EmbeddingModelOverrideSlot.Value = value;
    }

    /// <summary>Sets <see cref="ModelOverride"/> and returns its previous value.</summary>
    public static string? UseModel(string? deployment)
    {
        var previous = ModelOverride;
        ModelOverride = deployment;
        return previous;
    }

    /// <summary>Sets <see cref="EmbeddingModelOverride"/> and returns its previous value.</summary>
    public static string? UseEmbeddingModel(string? deployment)
    {
        var previous = EmbeddingModelOverride;
        EmbeddingModelOverride = deployment;
        return previous;
    }

    /// <summary>
    /// The host this process would use, re-read from the environment on every access.
    /// </summary>
    /// <remarks>
    /// Never cached: a cached first resolution makes the process ignore a variable set later, and
    /// makes every test after the first see stale settings.
    /// </remarks>
    public static InferenceProviderSettings ProviderSettings => InferenceProviderEnvironment.Resolve();

    /// <summary>Stable lowercase tag of the resolved host, or <c>none</c>.</summary>
    public static string ProviderTag => ProviderSettings.ProviderTag;

    /// <summary>
    /// <c>model@provider</c> for the subject, built from the log-safe label. The host is part of
    /// what was measured, so a Bitdeer run and an Azure run on the same model name stay
    /// distinguishable in provenance rather than collapsing into one indistinguishable record.
    /// </summary>
    public static string ModelIdentity => $"{Deployments.SubjectLabel}@{ProviderTag}";

    /// <summary><c>model@provider</c> for the judge, built from the log-safe label.</summary>
    public static string JudgeModelIdentity => $"{Deployments.JudgeLabel}@{ProviderTag}";

    /// <summary>
    /// Local configuration readiness. This does not contact the provider and therefore cannot
    /// establish connectivity, authorization, model compatibility, quota, or model health.
    /// </summary>
    public static AzureOpenAiReadiness Readiness => ResolveLiveConfiguration().Readiness;

    /// <summary>True when the selected local authentication path has all required settings.</summary>
    public static bool IsConfigured => Readiness.IsReady;

    /// <summary>Resolved inference endpoint. It is never included in a safe status.</summary>
    public static Uri Endpoint => RequireLiveConfiguration().Endpoint;

    /// <summary>
    /// Azure OpenAI API-key credential for compatibility callers. Throws when an identity mode is
    /// selected, or when the resolved host does not speak the Azure protocol. New composition code
    /// should use <see cref="Providers.InferenceClientFactory"/>.
    /// </summary>
    public static AzureKeyCredential KeyCredential => RequireLiveConfiguration().ApiKeyCredential
        ?? throw new InvalidOperationException(
            "An Azure API-key credential is unavailable: either AZURE_OPENAI_AUTH_MODE selects "
            + "Microsoft Entra authentication, or the resolved provider is not an Azure-protocol host.");

    /// <summary>Resolved subject chat model.</summary>
    public static string Model => ResolveSubjectModel(ProviderSettings);

    /// <summary>
    /// Resolved judge model. A host that names no judge model deliberately shares the subject
    /// model; set the host's judge variable to obtain independent judge routing.
    /// </summary>
    public static string JudgeDeployment => ResolveJudgeModel(ProviderSettings);

    /// <summary>Resolved live embedding model.</summary>
    public static string EmbeddingDeployment => ResolveEmbeddingModel(ProviderSettings);

    /// <summary>Resolved, typed model selection for safe operator surfaces.</summary>
    public static AzureOpenAiDeploymentSelection Deployments => SelectionFor(ProviderSettings);

    /// <summary>Captures the environment exactly once for one client composition.</summary>
    internal static LiveConfigurationSnapshot? CaptureLiveConfiguration() =>
        ResolveLiveConfiguration().Configuration;

    /// <summary>Captures or throws a safe, configuration-only readiness reason.</summary>
    internal static LiveConfigurationSnapshot RequireLiveConfiguration()
    {
        var resolution = ResolveLiveConfiguration();
        return resolution.Configuration
            ?? throw new InvalidOperationException(
                resolution.Readiness.BlockingReason ?? "Live inference configuration is incomplete.");
    }

    /// <summary>
    /// Prints provider, model, and authentication labels only. The endpoint, API key, token, and
    /// managed-identity client id are never printed, fingerprinted, or hashed.
    /// </summary>
    public static void PrintProviderTarget()
    {
        var settings = ProviderSettings;
        var readiness = ResolveLiveConfiguration(settings).Readiness;

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Inference target ────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  Provider       : {readiness.ProviderDisplayName}  [{SelectionSource(settings)}]");
        Console.WriteLine($"  Authentication : {readiness.AuthenticationLabel}");
        Console.WriteLine($"  Subject        : {readiness.Deployments.SubjectLabel}  [source: {SubjectModelSource(settings)}]");
        Console.WriteLine($"  Judge          : {readiness.Deployments.JudgeLabel}  [source: {JudgeModelSource(settings)}]");
        Console.WriteLine($"  Embeddings     : {readiness.Deployments.EmbeddingLabel}  [source: {EmbeddingModelSource(settings)}]");
        if (!readiness.IsReady)
            Console.WriteLine($"  Readiness      : {readiness.BlockingReason}");
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
    }

    internal static string SafeDeploymentLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "<unset>";
        if (DeploymentValueRequiresRedaction(value)) return "[REDACTED]";

        var trimmed = value.Trim();
        var length = Math.Min(trimmed.Length, 80);
        return string.Create(length, trimmed, static (target, source) =>
        {
            for (var index = 0; index < target.Length; index++)
            {
                var character = source[index];
                target[index] = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':'
                    ? character
                    : '?';
            }
        });
    }

    private static LiveConfigurationResolution ResolveLiveConfiguration() =>
        ResolveLiveConfiguration(ProviderSettings);

    private static LiveConfigurationResolution ResolveLiveConfiguration(InferenceProviderSettings settings)
    {
        var deployments = SelectionFor(settings);

        if (!settings.IsConfigured)
        {
            return NotReady(
                settings,
                "no provider selected",
                deployments,
                settings.Diagnostic ?? "No inference provider is configured.");
        }

        return settings.UsesAzureProtocol
            ? ResolveAzureProtocol(settings, deployments)
            : ResolveOpenAiProtocol(settings, deployments);
    }

    // ── Azure protocol: an Azure OpenAI resource, or a Microsoft Foundry resource endpoint ────

    private static LiveConfigurationResolution ResolveAzureProtocol(
        InferenceProviderSettings settings,
        AzureOpenAiDeploymentSelection deployments)
    {
        // A Foundry resource authenticates by key: AZURE_OPENAI_AUTH_MODE governs the Azure OpenAI
        // host only, and letting it reach across would silently change how a different host connects.
        var auth = settings.Provider == InferenceProvider.AzureOpenAI
            ? ParseAuthenticationMode(NonBlank(Environment.GetEnvironmentVariable(AuthenticationModeEnvironmentVariable)))
            : new AuthenticationResolution(AzureOpenAiAuthenticationMode.ApiKey, "API key", Error: null);

        if (auth.Mode is null)
            return NotReady(settings, auth.Label, deployments, auth.Error!);

        if (RejectUnsafeModelNames(settings, auth, deployments, Config.IsValidDeploymentName) is { } rejected)
            return rejected;

        var endpointVariable = InferenceProviderEnvironment.EndpointVariableOf(settings.Provider)!;
        var resourceNoun = settings.Provider == InferenceProvider.AzureOpenAI
            ? "Azure OpenAI"
            : "Microsoft Foundry";

        var rawEndpoint = NonBlank(settings.RawEndpoint);
        if (rawEndpoint is null
            || !Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return NotReady(
                settings,
                auth.Label,
                deployments,
                $"{endpointVariable} must be an absolute HTTPS {resourceNoun} resource endpoint.",
                auth.Mode);
        }

        if (IsFoundryProjectEndpoint(endpoint))
        {
            return NotReady(
                settings,
                auth.Label,
                deployments,
                $"{endpointVariable} must be an {resourceNoun} resource/inference endpoint, not a Microsoft Foundry project endpoint.",
                auth.Mode);
        }

        if (endpoint.AbsolutePath != "/"
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            return NotReady(
                settings,
                auth.Label,
                deployments,
                $"{endpointVariable} must be the base {resourceNoun} resource/inference endpoint without a path, query, fragment, or user information.",
                auth.Mode);
        }

        AzureKeyCredential? apiKeyCredential = null;
        string? managedIdentityClientId = null;

        if (auth.Mode == AzureOpenAiAuthenticationMode.ApiKey)
        {
            var apiKey = NonBlank(settings.ApiKey);
            if (apiKey is null)
            {
                return NotReady(
                    settings,
                    auth.Label,
                    deployments,
                    $"{InferenceProviderEnvironment.ApiKeyVariableOf(settings.Provider)} is required for api-key authentication.",
                    auth.Mode);
            }

            apiKeyCredential = new AzureKeyCredential(apiKey);
        }
        else if (auth.Mode == AzureOpenAiAuthenticationMode.ManagedIdentity)
        {
            managedIdentityClientId = NonBlank(
                Environment.GetEnvironmentVariable(ManagedIdentityClientIdEnvironmentVariable));
            if (managedIdentityClientId is not null && !Guid.TryParse(managedIdentityClientId, out _))
            {
                return NotReady(
                    settings,
                    auth.Label,
                    deployments,
                    $"{ManagedIdentityClientIdEnvironmentVariable} must be a user-assigned identity client GUID.",
                    auth.Mode);
            }

            auth = auth with
            {
                Label = managedIdentityClientId is null
                    ? "Microsoft Entra ID · managed identity (system-assigned)"
                    : "Microsoft Entra ID · managed identity (user-assigned)"
            };
        }

        return Ready(
            settings,
            auth.Label,
            auth.Mode.Value,
            deployments,
            endpoint,
            apiKeyCredential,
            apiKey: null,
            managedIdentityClientId);
    }

    // ── OpenAI protocol: Bitdeer, OpenAI, and any other OpenAI-compatible host ────────────────

    private static LiveConfigurationResolution ResolveOpenAiProtocol(
        InferenceProviderSettings settings,
        AzureOpenAiDeploymentSelection deployments)
    {
        var auth = new AuthenticationResolution(AzureOpenAiAuthenticationMode.ApiKey, "API key", Error: null);

        if (RejectUnsafeModelNames(settings, auth, deployments, Config.IsValidModelName) is { } rejected)
            return rejected;

        var endpointVariable = InferenceProviderEnvironment.EndpointVariableOf(settings.Provider)!;
        if (!InferenceProviderEnvironment.TryValidateEndpoint(settings.RawEndpoint, out var endpoint, out var reason))
        {
            // The reason names the variable and the rule, never the configured value: a URL can
            // carry a credential in its user-info, path, query, or fragment.
            return NotReady(settings, auth.Label, deployments, $"{endpointVariable} {reason}", auth.Mode);
        }

        var apiKey = NonBlank(settings.ApiKey);
        if (apiKey is null)
        {
            return NotReady(
                settings,
                auth.Label,
                deployments,
                $"{InferenceProviderEnvironment.ApiKeyVariableOf(settings.Provider)} is required for {settings.DisplayName}.",
                auth.Mode);
        }

        return Ready(
            settings,
            auth.Label,
            auth.Mode!.Value,
            deployments,
            endpoint!,
            apiKeyCredential: null,
            apiKey,
            managedIdentityClientId: null);
    }

    // ── Shared model-name policy ──────────────────────────────────────────────────────────────

    private static LiveConfigurationResolution? RejectUnsafeModelNames(
        InferenceProviderSettings settings,
        AuthenticationResolution auth,
        AzureOpenAiDeploymentSelection deployments,
        Func<string?, bool> isValidName)
    {
        if (DeploymentValueRequiresRedaction(deployments.SubjectDeployment)
            || DeploymentValueRequiresRedaction(deployments.JudgeDeployment)
            || DeploymentValueRequiresRedaction(deployments.EmbeddingDeployment))
        {
            return NotReady(
                settings,
                auth.Label,
                deployments,
                "A model variable contains credential-shaped content; value withheld. Set model names, not credentials.",
                auth.Mode);
        }

        if (!isValidName(deployments.SubjectDeployment)
            || !isValidName(deployments.JudgeDeployment)
            || !isValidName(deployments.EmbeddingDeployment))
        {
            return NotReady(
                settings,
                auth.Label,
                deployments,
                settings.UsesAzureProtocol
                    ? "Deployment names may contain only ASCII letters, digits, hyphens, underscores, and periods; value withheld."
                    : "Model names may contain only ASCII letters, digits, hyphens, underscores, periods, slashes, and colons, "
                      + "with no relative segment; value withheld.",
                auth.Mode);
        }

        return null;
    }

    private static AuthenticationResolution ParseAuthenticationMode(string? rawMode)
    {
        if (rawMode is null)
        {
            return new AuthenticationResolution(
                AzureOpenAiAuthenticationMode.ApiKey,
                "API key (compatibility default)",
                Error: null);
        }

        if (rawMode.Equals("api-key", StringComparison.OrdinalIgnoreCase))
            return new(AzureOpenAiAuthenticationMode.ApiKey, "API key", Error: null);

        if (rawMode.Equals("default-credential", StringComparison.OrdinalIgnoreCase))
            return new(
                AzureOpenAiAuthenticationMode.DefaultAzureCredential,
                "Microsoft Entra ID · local DefaultAzureCredential",
                Error: null);

        if (rawMode.Equals("managed-identity", StringComparison.OrdinalIgnoreCase))
            return new(
                AzureOpenAiAuthenticationMode.ManagedIdentity,
                "Microsoft Entra ID · managed identity (system-assigned)",
                Error: null);

        return new(
            Mode: null,
            Label: "unrecognized authentication mode",
            Error: "AZURE_OPENAI_AUTH_MODE must be api-key, default-credential, or managed-identity.");
    }

    private static LiveConfigurationResolution Ready(
        InferenceProviderSettings settings,
        string authenticationLabel,
        AzureOpenAiAuthenticationMode authenticationMode,
        AzureOpenAiDeploymentSelection deployments,
        Uri endpoint,
        AzureKeyCredential? apiKeyCredential,
        string? apiKey,
        string? managedIdentityClientId) =>
        new(
            new AzureOpenAiReadiness(
                true,
                authenticationMode,
                authenticationLabel,
                deployments,
                BlockingReason: null,
                settings.ProviderTag,
                settings.DisplayName),
            new LiveConfigurationSnapshot(
                settings.Provider,
                settings.ProviderTag,
                settings.DisplayName,
                endpoint,
                authenticationMode,
                apiKeyCredential,
                apiKey,
                managedIdentityClientId,
                deployments));

    private static LiveConfigurationResolution NotReady(
        InferenceProviderSettings settings,
        string authenticationLabel,
        AzureOpenAiDeploymentSelection deployments,
        string reason,
        AzureOpenAiAuthenticationMode? authenticationMode = null) =>
        new(
            new AzureOpenAiReadiness(
                false,
                authenticationMode,
                authenticationLabel,
                deployments,
                reason,
                settings.ProviderTag,
                settings.DisplayName),
            Configuration: null);

    // ── Model resolution, and where each resolved name came from ──────────────────────────────

    private static AzureOpenAiDeploymentSelection SelectionFor(InferenceProviderSettings settings) =>
        new(ResolveSubjectModel(settings), ResolveJudgeModel(settings), ResolveEmbeddingModel(settings));

    private static string ResolveSubjectModel(InferenceProviderSettings settings) =>
        FirstNonBlank(ModelOverride, settings.Model, PreferredDeployment);

    private static string ResolveJudgeModel(InferenceProviderSettings settings) =>
        FirstNonBlank(settings.JudgeModel, ResolveSubjectModel(settings));

    private static string ResolveEmbeddingModel(InferenceProviderSettings settings) =>
        FirstNonBlank(EmbeddingModelOverride, settings.EmbeddingModel, PreferredEmbeddingDeployment);

    private static string SelectionSource(InferenceProviderSettings settings) => settings.Selection switch
    {
        InferenceProviderSelection.Explicit => $"{InferenceProviderEnvironment.SelectorVariable} (env)",
        InferenceProviderSelection.AutoDetected => "auto-detected from credentials",
        _ => "none"
    };

    private static string SubjectModelSource(InferenceProviderSettings settings) =>
        !string.IsNullOrWhiteSpace(ModelOverride) ? "Config.ModelOverride (code)"
        : VariableSource(InferenceProviderEnvironment.ModelVariableOf(settings.Provider))
          ?? $"default ({ResolveSubjectModel(settings)})";

    private static string JudgeModelSource(InferenceProviderSettings settings) =>
        VariableSource(InferenceProviderEnvironment.JudgeVariableOf(settings.Provider)) ?? "subject model";

    private static string EmbeddingModelSource(InferenceProviderSettings settings) =>
        !string.IsNullOrWhiteSpace(EmbeddingModelOverride) ? "Config.EmbeddingModelOverride (code)"
        : VariableSource(InferenceProviderEnvironment.EmbeddingVariableOf(settings.Provider))
          ?? $"default ({PreferredEmbeddingDeployment})";

    private static string? VariableSource(string? name) =>
        name is not null && NonBlank(Environment.GetEnvironmentVariable(name)) is not null
            ? $"{name} (env)"
            : null;

    private static string FirstNonBlank(params string?[] values) =>
        values.Select(NonBlank).First(value => value is not null)!;

    /// <summary>
    /// The Azure-protocol name policy. Slashes are excluded deliberately: an Azure deployment name
    /// is interpolated into the request path, so permitting a separator alongside periods would
    /// make <c>../..</c> a traversal rather than a nonsense name.
    /// </summary>
    internal static bool IsValidDeploymentName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim().All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    /// <summary>
    /// The OpenAI-protocol name policy. A model there is a body field rather than a path segment
    /// and is routinely namespaced, as in <c>zai-org/GLM-5.3-Flash</c>, so a slash is allowed while
    /// relative and empty segments are not.
    /// </summary>
    internal static bool IsValidModelName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();
        if (trimmed.StartsWith('/') || trimmed.EndsWith('/')) return false;
        if (trimmed.Contains("..", StringComparison.Ordinal)) return false;
        if (trimmed.Contains("//", StringComparison.Ordinal)) return false;

        return trimmed.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/' or ':');
    }

    /// <summary>The name policy that applies to the currently resolved host.</summary>
    internal static bool IsValidModelNameForResolvedProvider(string? value) =>
        ProviderSettings.UsesAzureProtocol ? IsValidDeploymentName(value) : IsValidModelName(value);

    private static bool IsFoundryProjectEndpoint(Uri endpoint)
    {
        var path = Uri.UnescapeDataString(endpoint.AbsolutePath).TrimEnd('/');
        return path.Equals("/api/projects", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DeploymentValueRequiresRedaction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (RecommendationRuntimeEvents.ContainsSecretBearingContent(value)) return true;

        var trimmed = value.Trim();
        var managedIdentityClientId = NonBlank(
            Environment.GetEnvironmentVariable(ManagedIdentityClientIdEnvironmentVariable));
        if (managedIdentityClientId is not null
            && trimmed.Contains(managedIdentityClientId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Common opaque-token forms that may not equal the currently configured API key. This is
        // deliberately conservative: model names are labels, never secret storage.
        return trimmed.Length >= 16 && trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
            || trimmed.Length >= 24 && trimmed.StartsWith("eyJ", StringComparison.Ordinal)
            || trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NonBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AuthenticationResolution(
        AzureOpenAiAuthenticationMode? Mode,
        string Label,
        string? Error);

    private sealed record LiveConfigurationResolution(
        AzureOpenAiReadiness Readiness,
        LiveConfigurationSnapshot? Configuration);
}

/// <summary>
/// Immutable composition-scoped live configuration. Its string form deliberately contains only
/// safe provider, authentication, and model labels.
/// </summary>
internal sealed class LiveConfigurationSnapshot(
    InferenceProvider provider,
    string providerTag,
    string providerDisplayName,
    Uri endpoint,
    AzureOpenAiAuthenticationMode authenticationMode,
    AzureKeyCredential? apiKeyCredential,
    string? apiKey,
    string? managedIdentityClientId,
    AzureOpenAiDeploymentSelection deployments)
{
    internal InferenceProvider Provider { get; } = provider;
    internal string ProviderTag { get; } = providerTag;
    internal string ProviderDisplayName { get; } = providerDisplayName;
    internal Uri Endpoint { get; } = endpoint;
    internal AzureOpenAiAuthenticationMode AuthenticationMode { get; } = authenticationMode;
    internal AzureKeyCredential? ApiKeyCredential { get; } = apiKeyCredential;

    /// <summary>The raw key for the OpenAI-protocol branch. Never logged, never printed.</summary>
    internal string? ApiKey { get; } = apiKey;

    internal string? ManagedIdentityClientId { get; } = managedIdentityClientId;
    internal string ModelDeployment => Deployments.SubjectDeployment;
    internal string JudgeDeployment => Deployments.JudgeDeployment;
    internal string EmbeddingDeployment => Deployments.EmbeddingDeployment;
    internal AzureOpenAiDeploymentSelection Deployments { get; } = deployments;

    /// <summary>True when this snapshot is built with the Azure OpenAI protocol.</summary>
    internal bool UsesAzureProtocol =>
        Provider is InferenceProvider.AzureOpenAI or InferenceProvider.Foundry;

    public override string ToString() =>
        $"provider={ProviderTag}; auth={AuthenticationMode}; {Deployments}";
}
