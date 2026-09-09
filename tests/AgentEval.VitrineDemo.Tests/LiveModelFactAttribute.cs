// SPDX-License-Identifier: MIT

namespace AgentEval.VitrineDemo.Tests;

/// <summary>Explicit opt-in boundary for tests that can contact and charge a model deployment.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LiveModelFactAttribute : FactAttribute
{
    private const string OptInVariable = "VITRINE_RUN_LIVE_MODEL_TESTS";

    public LiveModelFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(OptInVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {OptInVariable}=1 to opt in to model calls and possible charges.";
        }
    }
}
