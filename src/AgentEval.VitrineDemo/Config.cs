// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Azure;

namespace Galaxus.RecommendationAgent;

/// <summary>
/// Azure OpenAI configuration sourced from environment variables.
/// </summary>
/// <remarks>
/// <para>Required environment variables:</para>
/// <list type="bullet">
///   <item>AZURE_OPENAI_ENDPOINT</item>
///   <item>AZURE_OPENAI_API_KEY</item>
/// </list>
/// <para>Optional environment variables:</para>
/// <list type="bullet">
///   <item>AZURE_OPENAI_DEPLOYMENT — overrides <see cref="PreferredDeployment"/> (default: <c>gpt-5-mini</c>).</item>
///   <item>AZURE_OPENAI_EMBEDDING_DEPLOYMENT — overrides <see cref="PreferredEmbeddingDeployment"/>
///         (default: <c>text-embedding-3-small</c>). Only read by the LIVE embedding path
///         (<c>AzureEmbeddingSource</c>) and by <c>--rebuild-embeddings</c>; the offline
///         default retrieval path needs no key at all.</item>
/// </list>
/// </remarks>
public static class Config
{
    /// <summary>
    /// Recommended deployment for Galaxus.RecommendationAgent + its Evals — picked for
    /// a balance of fast per-turn latency and reliable function-calling on
    /// Azure AI Foundry. Used when <c>AZURE_OPENAI_DEPLOYMENT</c> is unset.
    /// </summary>
    /// <remarks>
    /// Empirical development baseline on a private Azure AI Foundry resource:
    /// <list type="bullet">
    ///   <item><c>gpt-5-chat</c>: reasoning-heavy, slow per turn.</item>
    ///   <item><c>gpt-5-mini</c>: same reasoning family, faster turns. RECOMMENDED.</item>
    ///   <item><c>gpt-4o</c> / <c>gpt-4o-mini</c>: battle-tested function-calling, fastest overall.</item>
    /// </list>
    /// Override with <c>AZURE_OPENAI_DEPLOYMENT</c> to pin a specific deployment.
    /// </remarks>
    public const string PreferredDeployment = "gpt-5-mini";

    /// <summary>
    /// Recommended embedding deployment. 1536 dimensions, the model the embedding
    /// document template (<c>Retrieval/EmbeddingDocument.cs</c>) and the committed
    /// vector assets are stamped against. Used when
    /// <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> is unset.
    /// </summary>
    /// <remarks>
    /// Changing this invalidates any precomputed vector asset: the cache carries a
    /// template-plus-model version stamp and <c>PrecomputedEmbeddingSource</c>
    /// degrades LOUDLY on a mismatch rather than silently retrieving garbage.
    /// </remarks>
    public const string PreferredEmbeddingDeployment = "text-embedding-3-small";

    /// <summary>
    /// Programmatic model override. When non-null, takes precedence over BOTH
    /// the <c>AZURE_OPENAI_DEPLOYMENT</c> env var AND <see cref="PreferredDeployment"/>.
    /// Set at the top of <c>Program.cs</c> (or before a demo's <c>RunAsync</c>)
    /// to pin a specific deployment for an entire process without juggling
    /// shell env vars across runs.
    /// </summary>
    /// <example>
    /// <code>
    /// // Pin a model label before an explicitly authorized live integration call:
    /// Config.ModelOverride = "gpt-4o-mini";
    /// // The parameterless demo entry point remains offline; the CLI additionally
    /// // requires --live --confirm-paid before it selects a provider-backed arm.
    /// </code>
    /// </example>
    private static readonly AsyncLocal<string?> ModelOverrideSlot = new();
    private static readonly AsyncLocal<string?> EmbeddingModelOverrideSlot = new();

    public static string? ModelOverride
    {
        get => ModelOverrideSlot.Value;
        set => ModelOverrideSlot.Value = value;
    }

    /// <summary>
    /// Programmatic embedding-deployment override. When non-null, takes precedence
    /// over BOTH the <c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> env var AND
    /// <see cref="PreferredEmbeddingDeployment"/>.
    /// </summary>
    public static string? EmbeddingModelOverride
    {
        get => EmbeddingModelOverrideSlot.Value;
        set => EmbeddingModelOverrideSlot.Value = value;
    }

    /// <summary>
    /// Convenience setter for <see cref="ModelOverride"/>. Returns the previous
    /// override value so callers can restore it (useful inside a <c>try/finally</c>
    /// when only one demo should use a different model).
    /// </summary>
    public static string? UseModel(string? deployment)
    {
        var prev = ModelOverride;
        ModelOverride = deployment;
        return prev;
    }

    /// <summary>
    /// Convenience setter for <see cref="EmbeddingModelOverride"/>. Returns the
    /// previous override value so callers can restore it.
    /// </summary>
    public static string? UseEmbeddingModel(string? deployment)
    {
        var prev = EmbeddingModelOverride;
        EmbeddingModelOverride = deployment;
        return prev;
    }

    /// <summary>True when endpoint and key are both set. Deployment is optional and falls back to <see cref="PreferredDeployment"/>.</summary>
    public static bool IsConfigured => CaptureLiveConfiguration() is not null;

    /// <summary>Azure OpenAI endpoint URI.</summary>
    public static Uri Endpoint => CaptureLiveConfiguration()?.Endpoint
        ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");

    /// <summary>Azure OpenAI API key credential.</summary>
    public static AzureKeyCredential KeyCredential => CaptureLiveConfiguration()?.Key
        ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY is not set.");

    /// <summary>
    /// Resolved model deployment name. Resolution order (first non-empty wins):
    /// <list type="number">
    ///   <item><see cref="ModelOverride"/> — programmatic override.</item>
    ///   <item><c>AZURE_OPENAI_DEPLOYMENT</c> env var.</item>
    ///   <item><see cref="PreferredDeployment"/> — built-in default (<c>gpt-5-mini</c>).</item>
    /// </list>
    /// </summary>
    public static string Model =>
        !string.IsNullOrEmpty(ModelOverride)
            ? ModelOverride
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") is { Length: > 0 } env
                ? env
                : PreferredDeployment;

    /// <summary>
    /// Resolved EMBEDDING deployment name. Resolution order (first non-empty wins):
    /// <list type="number">
    ///   <item><see cref="EmbeddingModelOverride"/> — programmatic override.</item>
    ///   <item><c>AZURE_OPENAI_EMBEDDING_DEPLOYMENT</c> env var.</item>
    ///   <item><see cref="PreferredEmbeddingDeployment"/> — built-in default (<c>text-embedding-3-small</c>).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Read ONLY by the live embedding path and by <c>--rebuild-embeddings</c>. The
    /// default offline retrieval path (<c>ConceptEmbeddingSource</c>) never touches it,
    /// which is why the demo runs with no key at all.
    /// </remarks>
    public static string EmbeddingDeployment =>
        !string.IsNullOrEmpty(EmbeddingModelOverride)
            ? EmbeddingModelOverride
            : Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") is { Length: > 0 } env
                ? env
                : PreferredEmbeddingDeployment;

    /// <summary>
    /// Captures endpoint, credential, and deployment choices once for one composition. The
    /// snapshot is intentionally internal: public run/result descriptors contain deployment
    /// names only, never a credential object or endpoint URI.
    /// </summary>
    internal static LiveConfigurationSnapshot? CaptureLiveConfiguration()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(key) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            return null;
        }

        return new LiveConfigurationSnapshot(
            endpointUri,
            new AzureKeyCredential(key),
            Model,
            EmbeddingDeployment);
    }

    /// <summary>
    /// Prints an "Azure target" header block to the console showing the
    /// chat deployment and embedding deployment. Useful at the top of demos and evals so the operator sees
    /// at a glance which model (and which Foundry / OpenAI resource) is about
    /// to be charged.
    /// </summary>
    /// <remarks>
    /// ⚠ The API key is NOT printed at all — not in full, not as a fingerprint, not as a hash. An
    /// earlier version printed <c>first4…last4</c>, which is eight real characters of the secret, and
    /// this banner runs at the top of every demo and eval, so those characters reached every
    /// <c>--log</c> file, terminal scrollback and screenshot of a sample in a public repository. The
    /// endpoint is not reported even as status: neither is needed to interpret a result. Only
    /// deployment names remain in the banner.
    /// </remarks>
    public static void PrintAzureTarget()
    {
        var envDep    = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        var envEmbDep = Environment.GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT");
        var resolved  = Model;
        var resolvedE = EmbeddingDeployment;

        string source;
        if (!string.IsNullOrEmpty(ModelOverride))         source = "Config.ModelOverride (code)";
        else if (!string.IsNullOrEmpty(envDep))           source = "AZURE_OPENAI_DEPLOYMENT (env)";
        else                                              source = $"default ({PreferredDeployment})";

        string embSource;
        if (!string.IsNullOrEmpty(EmbeddingModelOverride)) embSource = "Config.EmbeddingModelOverride (code)";
        else if (!string.IsNullOrEmpty(envEmbDep))         embSource = "AZURE_OPENAI_EMBEDDING_DEPLOYMENT (env)";
        else                                              embSource = $"default ({PreferredEmbeddingDeployment})";

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─── Azure target ────────────────────────────────────────────────────");
        Console.ResetColor();
        Console.WriteLine($"  Model      : {resolved}  [source: {source}]");
        Console.WriteLine($"  Embeddings : {resolvedE}  [source: {embSource}]");
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────");
        Console.ResetColor();
    }

}

/// <summary>
/// Immutable, composition-scoped Azure configuration. Its string form is deliberately safe and
/// contains deployment names only.
/// </summary>
internal sealed class LiveConfigurationSnapshot(
    Uri endpoint,
    AzureKeyCredential key,
    string modelDeployment,
    string embeddingDeployment)
{
    internal Uri Endpoint { get; } = endpoint;
    internal AzureKeyCredential Key { get; } = key;
    internal string ModelDeployment { get; } = modelDeployment;
    internal string EmbeddingDeployment { get; } = embeddingDeployment;

    public override string ToString() =>
        $"model={ModelDeployment}; embeddings={EmbeddingDeployment}";
}
