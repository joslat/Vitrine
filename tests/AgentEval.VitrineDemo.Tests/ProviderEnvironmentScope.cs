// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Providers;

namespace AgentEval.VitrineDemo.Tests;

/// <summary>
/// Hands a test a scrubbed inference environment and restores the developer's on dispose.
/// </summary>
/// <remarks>
/// <para>
/// It scrubs <b>every</b> provider variable, not only the ones a test sets. Clearing the three
/// Azure names no longer means "no provider configured": an ambient <c>BITDEER_API_KEY</c> or
/// <c>OPENAI_API_KEY</c> on the developer's machine configures one through auto-detection, and a
/// test asserting that nothing is configured would then fail on their machine and pass in CI.
/// </para>
/// <para>
/// Optional variables are scrubbed too. An <c>OPENAI_BASE_URL</c> left set from a shell profile
/// changes the endpoint a test believes it is asserting on.
/// </para>
/// <para>
/// Environment variables are process-wide mutable state; the assembly disables test
/// parallelization (see <c>AvaloniaTestHost</c>), which is what makes this safe.
/// </para>
/// </remarks>
internal sealed class ProviderEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _original = new(StringComparer.Ordinal);
    private readonly string? _originalModelOverride = Config.ModelOverride;
    private readonly string? _originalEmbeddingOverride = Config.EmbeddingModelOverride;

    public ProviderEnvironmentScope()
    {
        foreach (var name in InferenceProviderEnvironment.AllVariables)
        {
            _original[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        Config.ModelOverride = null;
        Config.EmbeddingModelOverride = null;
    }

    /// <summary>Sets one variable for the lifetime of this scope.</summary>
    /// <param name="name">Variable name.</param>
    /// <param name="value">Value, or null to leave it unset.</param>
    public void Set(string name, string? value)
    {
        // A variable the scope does not already track would not be restored on dispose.
        if (!_original.ContainsKey(name))
            _original[name] = Environment.GetEnvironmentVariable(name);

        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var pair in _original)
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);

        Config.ModelOverride = _originalModelOverride;
        Config.EmbeddingModelOverride = _originalEmbeddingOverride;
    }
}
