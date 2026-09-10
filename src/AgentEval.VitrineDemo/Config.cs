// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Azure;
using Galaxus.RecommendationAgent.Observability;

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
/// The three resolved deployment names. Its string representation is log-safe: it never contains
/// an endpoint or credential, and control characters are removed from labels.
/// </summary>
public sealed record AzureOpenAiDeploymentSelection(
    string SubjectDeployment,
    string JudgeDeployment,
    string EmbeddingDeployment)
{
    /// <summary>True when subject and judge intentionally resolve to the same deployment.</summary>
    public bool JudgeSharesSubjectDeployment =>
        string.Equals(SubjectDeployment, JudgeDeployment, StringComparison.Ordinal);

    /// <summary>Log-safe subject deployment label.</summary>
    public string SubjectLabel => Config.SafeDeploymentLabel(SubjectDeployment);

    /// <summary>Log-safe judge deployment label.</summary>
    public string JudgeLabel => Config.SafeDeploymentLabel(JudgeDeployment);

    /// <summary>Log-safe embedding deployment label.</summary>
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
    string? BlockingReason)
{
    /// <summary>A concise safe status for operator surfaces.</summary>
    public string SafeSummary => IsReady
        ? $"Local live configuration found · {AuthenticationLabel} · subject {Deployments.SubjectLabel} · "
          + (Deployments.JudgeSharesSubjectDeployment
              ? "judge shares subject deployment"
              : $"judge {Deployments.JudgeLabel}")
          + " · provider not contacted"
        : $"Live configuration not ready · {BlockingReason ?? "configuration is incomplete"} · provider not contacted";
}

/// <summary>Azure OpenAI configuration sourced from environment variables.</summary>
/// <remarks>
/// <para><c>AZURE_OPENAI_ENDPOINT</c> is required for every live path.</para>
/// <para>
/// <c>AZURE_OPENAI_AUTH_MODE</c> explicitly selects <c>api-key</c>,
/// <c>default-credential</c>, or <c>managed-identity</c>. When it is unset, VITRINE retains its
/// backward-compatible API-key behavior; an explicitly selected identity mode never silently
/// falls back to a key.
/// </para>
/// <para>
/// API-key mode additionally requires <c>AZURE_OPENAI_API_KEY</c>. Managed-identity mode may set
/// <c>AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID</c> for a user-assigned identity; otherwise it uses
/// the system-assigned identity. <c>default-credential</c> is for local development only.
/// </para>
/// <para>Optional deployment variables:</para>
/// <list type="bullet">
///   <item><c>AZURE_OPENAI_DEPLOYMENT</c> — subject chat deployment.</item>
///   <item><c>AZURE_OPENAI_JUDGE_DEPLOYMENT</c> — evaluation judge deployment; defaults explicitly to the subject deployment.</item>
///   <item><c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> — live embedding deployment.</item>
/// </list>
/// </remarks>
public static class Config
{
    public const string AuthenticationModeEnvironmentVariable = "AZURE_OPENAI_AUTH_MODE";
    public const string ManagedIdentityClientIdEnvironmentVariable = "AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID";

    /// <summary>
    /// Recommended subject deployment. Used when <c>AZURE_OPENAI_DEPLOYMENT</c> is unset.
    /// Override it with a deployment name that exists in the configured Azure OpenAI resource.
    /// </summary>
    public const string PreferredDeployment = "gpt-5-mini";

    /// <summary>
    /// Recommended embedding deployment. Changing the embedding space invalidates any precomputed
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
    /// Local configuration readiness. This does not contact Azure and therefore cannot establish
    /// connectivity, authorization, deployment compatibility, quota, or model health.
    /// </summary>
    public static AzureOpenAiReadiness Readiness => ResolveLiveConfiguration().Readiness;

    /// <summary>True when the selected local authentication path has all required settings.</summary>
    public static bool IsConfigured => Readiness.IsReady;

    /// <summary>Azure OpenAI resource/inference endpoint. It is never included in a safe status.</summary>
    public static Uri Endpoint => RequireLiveConfiguration().Endpoint;

    /// <summary>
    /// Azure OpenAI API-key credential for compatibility callers. Throws when an identity mode is selected.
    /// New composition code should use <see cref="AzureOpenAiClientFactory"/>.
    /// </summary>
    public static AzureKeyCredential KeyCredential => RequireLiveConfiguration().ApiKeyCredential
        ?? throw new InvalidOperationException(
            "An API-key credential is unavailable because AZURE_OPENAI_AUTH_MODE selects Microsoft Entra authentication.");

    /// <summary>Resolved subject chat deployment.</summary>
    public static string Model => FirstNonBlank(
        ModelOverride,
        Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
        PreferredDeployment);

    /// <summary>
    /// Resolved judge deployment. A missing <c>AZURE_OPENAI_JUDGE_DEPLOYMENT</c> deliberately
    /// shares the subject deployment; set it to obtain independent judge routing.
    /// </summary>
    public static string JudgeDeployment => FirstNonBlank(
        Environment.GetEnvironmentVariable("AZURE_OPENAI_JUDGE_DEPLOYMENT"),
        Model);

    /// <summary>Resolved live embedding deployment.</summary>
    public static string EmbeddingDeployment => FirstNonBlank(
        EmbeddingModelOverride,
        Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT"),
        PreferredEmbeddingDeployment);

    /// <summary>Resolved, typed deployment selection for safe operator surfaces.</summary>
    public static AzureOpenAiDeploymentSelection Deployments =>
        new(Model, JudgeDeployment, EmbeddingDeployment);

    /// <summary>Captures the environment exactly once for one client composition.</summary>
    internal static LiveConfigurationSnapshot? CaptureLiveConfiguration() =>
        ResolveLiveConfiguration().Configuration;

    /// <summary>Captures or throws a safe, configuration-only readiness reason.</summary>
    internal static LiveConfigurationSnapshot RequireLiveConfiguration()
    {
        var resolution = ResolveLiveConfiguration();
        return resolution.Configuration
            ?? throw new InvalidOperationException(
                resolution.Readiness.BlockingReason ?? "Azure OpenAI live configuration is incomplete.");
    }

    /// <summary>
    /// Prints deployment and authentication labels only. The endpoint, API key, token, and
    /// managed-identity client id are never printed, fingerprinted, or hashed.
    /// </summary>
    public static void PrintAzureTarget()
    {
        var readiness = Readiness;

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Azure target ────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  Authentication : {readiness.AuthenticationLabel}");
        Console.WriteLine($"  Subject        : {readiness.Deployments.SubjectLabel}  [source: {SubjectDeploymentSource()}]");
        Console.WriteLine($"  Judge          : {readiness.Deployments.JudgeLabel}  [source: {JudgeDeploymentSource()}]");
        Console.WriteLine($"  Embeddings     : {readiness.Deployments.EmbeddingLabel}  [source: {EmbeddingDeploymentSource()}]");
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
                target[index] = char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'
                    ? character
                    : '?';
            }
        });
    }

    private static LiveConfigurationResolution ResolveLiveConfiguration()
    {
        var deployments = Deployments;
        var rawAuthMode = NonBlank(Environment.GetEnvironmentVariable(AuthenticationModeEnvironmentVariable));
        var auth = ParseAuthenticationMode(rawAuthMode);

        if (auth.Mode is null)
            return NotReady(auth.Label, deployments, auth.Error!);

        if (DeploymentValueRequiresRedaction(deployments.SubjectDeployment)
            || DeploymentValueRequiresRedaction(deployments.JudgeDeployment)
            || DeploymentValueRequiresRedaction(deployments.EmbeddingDeployment))
        {
            return NotReady(
                auth.Label,
                deployments,
                "A deployment variable contains credential-shaped content; value withheld. Set deployment names, not credentials.",
                auth.Mode);
        }

        if (!IsValidDeploymentName(deployments.SubjectDeployment)
            || !IsValidDeploymentName(deployments.JudgeDeployment)
            || !IsValidDeploymentName(deployments.EmbeddingDeployment))
        {
            return NotReady(
                auth.Label,
                deployments,
                "Deployment names may contain only ASCII letters, digits, hyphens, underscores, and periods; value withheld.",
                auth.Mode);
        }

        var rawEndpoint = NonBlank(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"));
        if (rawEndpoint is null
            || !Uri.TryCreate(rawEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return NotReady(
                auth.Label,
                deployments,
                "AZURE_OPENAI_ENDPOINT must be an absolute HTTPS Azure OpenAI resource endpoint.",
                auth.Mode);
        }

        if (IsFoundryProjectEndpoint(endpoint))
        {
            return NotReady(
                auth.Label,
                deployments,
                "AZURE_OPENAI_ENDPOINT must be an Azure OpenAI resource/inference endpoint, not a Microsoft Foundry project endpoint.",
                auth.Mode);
        }

        if (endpoint.AbsolutePath != "/"
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            return NotReady(
                auth.Label,
                deployments,
                "AZURE_OPENAI_ENDPOINT must be the base Azure OpenAI resource/inference endpoint without a path, query, fragment, or user information.",
                auth.Mode);
        }

        AzureKeyCredential? apiKeyCredential = null;
        string? managedIdentityClientId = null;

        if (auth.Mode == AzureOpenAiAuthenticationMode.ApiKey)
        {
            var apiKey = NonBlank(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"));
            if (apiKey is null)
            {
                return NotReady(
                    auth.Label,
                    deployments,
                    "AZURE_OPENAI_API_KEY is required for api-key authentication.",
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

        var readiness = new AzureOpenAiReadiness(
            true,
            auth.Mode,
            auth.Label,
            deployments,
            BlockingReason: null);

        return new LiveConfigurationResolution(
            readiness,
            new LiveConfigurationSnapshot(
                endpoint,
                auth.Mode.Value,
                apiKeyCredential,
                managedIdentityClientId,
                deployments));
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

    private static LiveConfigurationResolution NotReady(
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
                reason),
            Configuration: null);

    private static string SubjectDeploymentSource() =>
        !string.IsNullOrWhiteSpace(ModelOverride)
            ? "Config.ModelOverride (code)"
            : NonBlank(Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")) is not null
                ? "AZURE_OPENAI_DEPLOYMENT (env)"
                : $"default ({PreferredDeployment})";

    private static string JudgeDeploymentSource() =>
        NonBlank(Environment.GetEnvironmentVariable("AZURE_OPENAI_JUDGE_DEPLOYMENT")) is not null
            ? "AZURE_OPENAI_JUDGE_DEPLOYMENT (env)"
            : "subject deployment";

    private static string EmbeddingDeploymentSource() =>
        !string.IsNullOrWhiteSpace(EmbeddingModelOverride)
            ? "Config.EmbeddingModelOverride (code)"
            : NonBlank(Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT")) is not null
                ? "AZURE_OPENAI_EMBEDDING_DEPLOYMENT (env)"
                : $"default ({PreferredEmbeddingDeployment})";

    private static string FirstNonBlank(params string?[] values) =>
        values.Select(NonBlank).First(value => value is not null)!;

    internal static bool IsValidDeploymentName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim().All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

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
        // deliberately conservative: deployment names are labels, never secret storage.
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
/// Immutable composition-scoped Azure configuration. Its string form deliberately contains only
/// safe authentication and deployment labels.
/// </summary>
internal sealed class LiveConfigurationSnapshot(
    Uri endpoint,
    AzureOpenAiAuthenticationMode authenticationMode,
    AzureKeyCredential? apiKeyCredential,
    string? managedIdentityClientId,
    AzureOpenAiDeploymentSelection deployments)
{
    internal Uri Endpoint { get; } = endpoint;
    internal AzureOpenAiAuthenticationMode AuthenticationMode { get; } = authenticationMode;
    internal AzureKeyCredential? ApiKeyCredential { get; } = apiKeyCredential;
    internal string? ManagedIdentityClientId { get; } = managedIdentityClientId;
    internal string ModelDeployment => Deployments.SubjectDeployment;
    internal string JudgeDeployment => Deployments.JudgeDeployment;
    internal string EmbeddingDeployment => Deployments.EmbeddingDeployment;
    internal AzureOpenAiDeploymentSelection Deployments { get; } = deployments;

    public override string ToString() =>
        $"auth={AuthenticationMode}; {Deployments}";
}
