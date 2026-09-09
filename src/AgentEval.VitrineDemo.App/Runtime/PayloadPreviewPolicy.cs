// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using Galaxus.RecommendationAgent.Observability;

namespace AgentEval.VitrineDemo.App.Runtime;

/// <summary>Last redaction boundary before any text enters app storage.</summary>
public static partial class PayloadPreviewPolicy
{
    public const int MaximumCharacters = 6000;

    public static string Sanitize(string? value)
    {
        var safe = RecommendationRuntimeEvents.SafePreview(value);
        safe = ConnectionStringPattern().Replace(safe, "$1=[REDACTED]");
        return safe.Length <= MaximumCharacters ? safe : safe[..MaximumCharacters] + "…";
    }

    [GeneratedRegex(@"(?i)\b(endpoint|connection[-_ ]?string|credential)\s*=\s*[^\s,;]+")]
    private static partial Regex ConnectionStringPattern();
}
