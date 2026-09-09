// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Reflection;
using System.Text.Json;
using Galaxus.RecommendationAgent.Catalog;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Tools;

/// <summary>The exact detector and the two historical defects used for causal ablation.</summary>
public enum ToolResultCodeDetector
{
    /// <summary>Reads and compares the typed <c>code</c> property.</summary>
    ExactDeclaredCode,

    /// <summary>The old detector: it can inspect only a raw CLR string.</summary>
    LegacyStringOnly,

    /// <summary>The old repair: it searches the whole rendered payload for a substring.</summary>
    LooseSubstring,
}

/// <summary>An ordered false-positive: a result declaring one code answered to another.</summary>
public sealed record ToolResultCodeCollision(string DeclaredCode, string MatchedAsCode);

/// <summary>
/// Model-free observation of the real AIFunction result shape and the complete declared-code
/// matching matrix. It contains measurements only; <see cref="ToolRefusalBoundary.IsSatisfied"/>
/// owns the acceptance rule so healthy and ablated arms are judged identically.
/// </summary>
public sealed record ToolRefusalBoundaryObservation(
    ToolResultCodeDetector Detector,
    string LiveResultShape,
    bool LiveResultWasNonString,
    bool LiveRefusalWasTyped,
    bool LiveRefusalDetected,
    bool OrdinaryResultDetected,
    int PublicCodeCount,
    int OwnCodeMatches,
    int OrderedCrossCodeChecks,
    IReadOnlyList<ToolResultCodeCollision> CrossCodeFalsePositives);

/// <summary>
/// Exercises refusal detection through the production MEAI tool boundary without a model or
/// network. The ordinary and refusal samples come from the real Galaxus tool registration; the
/// all-code matrix is derived from the public constants and production payload writers.
/// </summary>
public static class ToolRefusalBoundary
{
    /// <summary>Runs the full observation using one detector implementation.</summary>
    public static async Task<ToolRefusalBoundaryObservation> ObserveAsync(
        ToolResultCodeDetector detector = ToolResultCodeDetector.ExactDeclaredCode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(detector))
            throw new ArgumentOutOfRangeException(nameof(detector));

        var profile = UserProfiles.Require(Personas.NadiaUserId);
        var function = AIFunctionFactory.Create(GalaxusTools.GetInterestMap);

        object? refusal;
        using (GalaxusTools.BeginProfileScope(profile.WithPersonalization(false)))
        {
            refusal = await function.InvokeAsync(
                new AIFunctionArguments { ["userId"] = profile.Id }, cancellationToken)
                .ConfigureAwait(false);
        }

        object? ordinary;
        using (GalaxusTools.BeginProfileScope())
        {
            ordinary = await function.InvokeAsync(
                new AIFunctionArguments { ["userId"] = profile.Id }, cancellationToken)
                .ConfigureAwait(false);
        }

        var codes = PublicCodes();
        var ownMatches = 0;
        var crossChecks = 0;
        var collisions = new List<ToolResultCodeCollision>();

        foreach (var declared in codes)
        {
            var result = MarshalLikeAIFunction(PayloadFor(declared));
            if (Matches(result, declared, detector)) ownMatches++;

            foreach (var probe in codes)
            {
                if (string.Equals(declared, probe, StringComparison.Ordinal)) continue;
                crossChecks++;
                if (Matches(result, probe, detector))
                    collisions.Add(new ToolResultCodeCollision(declared, probe));
            }
        }

        return new ToolRefusalBoundaryObservation(
            detector,
            refusal?.GetType().Name ?? "(null)",
            refusal is not null and not string,
            ToolJson.TryParseRefusal(refusal, out var parsed) &&
                string.Equals(parsed!.Code, ToolRefusalCodes.PersonalizationDisabled, StringComparison.Ordinal),
            Matches(refusal, ToolRefusalCodes.PersonalizationDisabled, detector),
            Matches(ordinary, ToolRefusalCodes.PersonalizationDisabled, detector),
            codes.Count,
            ownMatches,
            crossChecks,
            collisions.AsReadOnly());
    }

    /// <summary>The shared acceptance rule for the healthy and both ablated observations.</summary>
    public static bool IsSatisfied(ToolRefusalBoundaryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return observation.LiveResultWasNonString &&
               observation.LiveRefusalWasTyped &&
               observation.LiveRefusalDetected &&
               !observation.OrdinaryResultDetected &&
               observation.PublicCodeCount >= 2 &&
               observation.OwnCodeMatches == observation.PublicCodeCount &&
               observation.OrderedCrossCodeChecks ==
                   observation.PublicCodeCount * (observation.PublicCodeCount - 1) &&
               observation.CrossCodeFalsePositives.Count == 0;
    }

    /// <summary>All distinct public string constants, so a newly added code joins the matrix.</summary>
    public static IReadOnlyList<string> PublicCodes() =>
        Array.AsReadOnly(typeof(ToolRefusalCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray());

    private static bool Matches(object? result, string code, ToolResultCodeDetector detector) =>
        detector switch
        {
            ToolResultCodeDetector.ExactDeclaredCode => ToolJson.HasDeclaredCode(result, code),
            ToolResultCodeDetector.LegacyStringOnly =>
                result is string text && text.Contains(code, StringComparison.Ordinal),
            ToolResultCodeDetector.LooseSubstring =>
                Render(result).Contains(code, StringComparison.Ordinal),
            _ => false,
        };

    private static string PayloadFor(string code) => code switch
    {
        ToolRefusalCodes.BudgetExhausted => ToolJson.BudgetExhausted(24, 24),
        ToolRefusalCodes.SearchCapExhausted => ToolJson.SearchCapExhausted(8, 8),
        ToolRefusalCodes.AlreadyReturned =>
            ToolJson.AlreadyReturned(nameof(GalaxusTools.SearchProductsByMeaning), 1, ["GLX-1001"]),
        _ => ToolJson.Refused(code, "Typed refusal boundary observation."),
    };

    private static JsonElement MarshalLikeAIFunction(string payload) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(payload, ToolJson.Options), ToolJson.Options);

    private static string Render(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        JsonDocument document => Render(document.RootElement),
        _ => JsonSerializer.Serialize(result, result.GetType(), ToolJson.Options),
    };
}
