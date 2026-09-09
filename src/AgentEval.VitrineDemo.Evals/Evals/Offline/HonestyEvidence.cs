// SPDX-License-Identifier: MIT
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Evals.Meta;
using Galaxus.RecommendationAgent.Observability;
namespace AgentEval.VitrineDemo.Evals;
public sealed record StatedNeedEvidence(
    IReadOnlyList<string> CaseIds,
    double LiveScore,
    double OracleScore,
    double MatchedMeanK,
    int ExactKMatchedCases,
    int PresentedSlots);
public sealed record NextPurchaseEvidence(
    int PairCount,
    int Wins,
    int Losses,
    int Ties,
    int InformativePairs,
    double ObservedTwoSidedP,
    double MinimumAttainableTwoSidedP,
    double Alpha);
public sealed record BaselineEvidence(string Name, int ModelCalls, double Score, string Scope);
public sealed record HonestyEvidenceArtifact(
    string SpdxLicense,
    int SchemaVersion,
    string MeasurementId,
    string Source,
    StatedNeedEvidence StatedNeed,
    NextPurchaseEvidence NextPurchase,
    BaselineEvidence Baseline);
public sealed record HonestyEvidenceLoadResult(
    HonestyEvidenceArtifact? Evidence,
    string? FailureKind) {
    public bool Measured => Evidence is not null && FailureKind is null;
}
public static class HonestyEvidenceLoader {
    public const string FileName = "HonestyEvidence.v1.json";
    internal const string ExpectedSha256 = "31410BB7C960C2ECE0B5F9568214E61155A95E76B24D1312438F1A97C4A3E90E";
    public static HonestyEvidenceLoadResult Load(string? path = null) {
        path ??= Path.Combine(AppContext.BaseDirectory, "Data", FileName);
        try {
            if (!File.Exists(path)) return new(null, "EvidenceFileMissing");
            var bytes = File.ReadAllBytes(path);
            if (RecommendationRuntimeEvents.ContainsSecretBearingContent(Encoding.UTF8.GetString(bytes)))
                return new(null, "EvidenceContainsDisallowedContent");
            var digest = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(digest, ExpectedSha256, StringComparison.Ordinal))
                return new(null, "EvidenceIntegrityMismatch");
            var artifact = JsonSerializer.Deserialize<HonestyEvidenceArtifact>(bytes, new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true,
            });
            return artifact is not null && Validate(artifact)
                ? new(artifact, null)
                : new(null, "EvidenceSchemaInvalid");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            return new(null, exception.GetType().Name);
        }
    }
    private static bool Validate(HonestyEvidenceArtifact artifact) {
        var expectedCases = Enumerable.Range(1, 12).Select(index => $"SN-{index:00}").ToArray();
        var next = artifact.NextPurchase;
        if (next.Wins < 0 || next.Losses < 0 || next.Ties < 0 ||
            !double.IsFinite(next.Alpha) || next.Alpha <= 0 || next.Alpha >= 1 ||
            (long)next.Wins + next.Losses + next.Ties > int.MaxValue)
            return false;
        var informativePairs = next.Wins + next.Losses;
        var pairCount = informativePairs + next.Ties;
        if (pairCount == 0)
            return false;
        var exactP = ExactTests.TwoSidedSignP(next.Wins, informativePairs);
        var minimumP = ExactTests.MinimumAttainableP(informativePairs);
        var underpowered = minimumP > next.Alpha;
        return artifact.SpdxLicense == "MIT" &&
            artifact.SchemaVersion == 1 &&
            !string.IsNullOrWhiteSpace(artifact.MeasurementId) &&
            !string.IsNullOrWhiteSpace(artifact.Source) &&
            artifact.StatedNeed.CaseIds.SequenceEqual(expectedCases, StringComparer.Ordinal) &&
            artifact.StatedNeed.CaseIds.Distinct(StringComparer.Ordinal).Count() == expectedCases.Length &&
            artifact.StatedNeed.LiveScore is >= 0 and <= 1 &&
            artifact.StatedNeed.OracleScore is >= 0 and <= 1 &&
            artifact.StatedNeed.MatchedMeanK > 0 &&
            artifact.StatedNeed.ExactKMatchedCases is >= 0 and <= 12 &&
            artifact.StatedNeed.PresentedSlots > 0 &&
            next.PairCount == pairCount &&
            next.InformativePairs == informativePairs &&
            Math.Abs(next.ObservedTwoSidedP - exactP) < 1e-12 &&
            Math.Abs(next.MinimumAttainableTwoSidedP - minimumP) < 1e-12 &&
            underpowered &&
            Math.Abs(next.Alpha - ExactTests.DefaultAlpha) < 1e-12 &&
            artifact.Baseline.ModelCalls == 0 &&
            Math.Abs(artifact.Baseline.Score - 1.0) < 1e-12 &&
            !string.IsNullOrWhiteSpace(artifact.Baseline.Scope);
    }
}
public static class HonestyInterpretation {
    public const string NextPurchaseExactMethod = "AgentEval.Evals.Meta.ExactTests.TwoSidedSignP";
    private static HonestyClaims ExpectedClaims { get; } = new(
        "SHOWN: live 0.889 vs tag-join oracle 1.000 at matched k",
        "NOT SHOWN: 4 informative pairs; minimum attainable two-sided p = 0.125",
        "A trivial tag join scores 1.000 on several questions with 0 model calls",
        "vitrine-synthetic-live-2026-09-04-eval02b-02c",
        "docs/evidence/vitrine-synthetic-live-2026-09-04-eval02b-02c.html") {
        NextPurchaseMethod = NextPurchaseExactMethod,
    };
    public static HonestyClaims Build(HonestyEvidenceArtifact evidence) {
        ArgumentNullException.ThrowIfNull(evidence);
        var informativePairs = checked(evidence.NextPurchase.Wins + evidence.NextPurchase.Losses);
        var minimumP = ExactTests.MinimumAttainableP(informativePairs);
        return new HonestyClaims(
            StatedNeed(evidence),
            NextPurchase(evidence),
            Baseline(evidence),
            evidence.MeasurementId,
            evidence.Source) {
            NextPurchaseRemedy = minimumP > evidence.NextPurchase.Alpha
                ? "Add informative pairs before making a next-purchase prediction claim."
                : "No remedy is required by the exact-sign power check.",
            NextPurchaseMethod = NextPurchaseExactMethod,
        };
    }
    public static bool Validate(HonestyEvidenceArtifact evidence, HonestyClaims claims) {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(claims);
        var next = evidence.NextPurchase;
        if (next.Wins < 0 || next.Losses < 0 || next.Ties < 0 ||
            !double.IsFinite(next.Alpha) || next.Alpha <= 0 || next.Alpha >= 1 ||
            (long)next.Wins + next.Losses + next.Ties > int.MaxValue)
            return false;
        var informativePairs = next.Wins + next.Losses;
        var pairCount = informativePairs + next.Ties;
        if (pairCount == 0)
            return false;
        var exactP = ExactTests.TwoSidedSignP(next.Wins, informativePairs);
        var minimumP = ExactTests.MinimumAttainableP(informativePairs);
        var underpowered = minimumP > next.Alpha;
        return evidence.StatedNeed is { LiveScore: 0.889, OracleScore: 1.0 } &&
            evidence.NextPurchase is { PairCount: 13, Wins: 3, Losses: 1, Ties: 9, InformativePairs: 4 } &&
            evidence.Baseline is { Score: 1.0, ModelCalls: 0 } &&
            evidence.NextPurchase.PairCount == pairCount &&
            evidence.NextPurchase.InformativePairs == informativePairs &&
            Math.Abs(exactP - evidence.NextPurchase.ObservedTwoSidedP) < 1e-12 &&
            Math.Abs(minimumP - evidence.NextPurchase.MinimumAttainableTwoSidedP) < 1e-12 &&
            underpowered &&
            claims == ExpectedClaims &&
            !string.IsNullOrWhiteSpace(claims.NextPurchaseRemedy) &&
            string.Equals(claims.NextPurchaseMethod,
                NextPurchaseExactMethod, StringComparison.Ordinal);
    }
    public static string StatedNeed(HonestyEvidenceArtifact evidence) => FormattableString.Invariant(
        $"SHOWN: live {evidence.StatedNeed.LiveScore:0.000} vs tag-join oracle {evidence.StatedNeed.OracleScore:0.000} at matched k");
    public static string NextPurchase(HonestyEvidenceArtifact evidence) {
        var informativePairs = checked(evidence.NextPurchase.Wins + evidence.NextPurchase.Losses);
        var minimumP = ExactTests.MinimumAttainableP(informativePairs);
        return FormattableString.Invariant(
            $"NOT SHOWN: {informativePairs} informative pairs; minimum attainable two-sided p = {minimumP:0.000}");
    }
    public static string Baseline(HonestyEvidenceArtifact evidence) => FormattableString.Invariant(
        $"A trivial tag join scores {evidence.Baseline.Score:0.000} on {evidence.Baseline.Scope} with {evidence.Baseline.ModelCalls} model calls");
}
