// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals;
internal static class AdmittedCheckDiagnostics {
    internal static bool EveryProductionAblationWentRed(
        IReadOnlyList<VitrineAdmittedCheckSelfTestFact> facts) =>
        ExactAblationPanel(facts, "production", VitrineProductionChecks.ExpectedKeys);
    internal static bool EveryBenchmarkAblationWentRed(
        IReadOnlyList<VitrineAdmittedCheckSelfTestFact> facts) =>
        ExactAblationPanel(facts, "benchmark", VitrineOfflineBenchmark.CheckKeys);
    internal static bool HasPositiveBenchmarkCountsBeforeValues(VitrineOfflineBenchmarkResult benchmark) {
        ArgumentNullException.ThrowIfNull(benchmark);
        if (benchmark.Cases.Count <= 0 || benchmark.Repetitions <= 0 || benchmark.Arms.Count <= 0 ||
            benchmark.Runs.Count != benchmark.Arms.Count * benchmark.Repetitions ||
            benchmark.Runs.Select(static run => run.RunId).Distinct(StringComparer.Ordinal).Count() != benchmark.Runs.Count)
            return false;
        foreach (var arm in benchmark.Arms) {
            if (arm.Checks.Count != VitrineOfflineBenchmark.CheckKeys.Count)
                return false;
            foreach (var check in arm.Checks) {
                if (check.Census.Total <= 0 || check.Census.Measured <= 0 || check.Trials <= 0 ||
                    check.Trials != check.Census.Measured)
                    return false;
            }
        }
        return true;
    }
    internal static bool DegradedArmLosesAgainstReference(VitrineOfflineBenchmarkResult benchmark) {
        ArgumentNullException.ThrowIfNull(benchmark);
        var comparisons = benchmark.ReferenceComparisons
            .Where(static row => row.ChallengerArmId == VitrineOfflineBenchmark.DegradedArmId)
            .ToArray();
        if (benchmark.Cases.Count <= 0 || comparisons.Length != VitrineOfflineBenchmark.CheckKeys.Count ||
            !comparisons.Select(static row => row.CheckKey).OrderBy(static key => key, StringComparer.Ordinal)
                .SequenceEqual(VitrineOfflineBenchmark.CheckKeys.OrderBy(static key => key, StringComparer.Ordinal),
                    StringComparer.Ordinal))
            return false;
        foreach (var comparison in comparisons) {
            if (comparison.Cases <= 0 || comparison.TotalRepObservations <= 0 ||
                comparison.Census.Measured != comparison.Cases ||
                comparison.Census.NotApplicable != 0 || comparison.Census.NotMeasured != 0 ||
                comparison.Wins != 0 || comparison.Losses != comparison.Cases || comparison.Ties != 0)
                return false;
        }
        return true;
    }
    internal static VitrineAdmittedCheckSelfTestFact[] PlantAblationSurvival(
        IReadOnlyList<VitrineAdmittedCheckSelfTestFact> facts) {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0) return [];
        return new[] { facts[0] with { AblationWentRed = false } }.Concat(facts.Skip(1)).ToArray();
    }
    internal static VitrineOfflineBenchmarkResult PlantZeroTrialCount(VitrineOfflineBenchmarkResult benchmark) {
        ArgumentNullException.ThrowIfNull(benchmark);
        if (benchmark.Arms.Count == 0 || benchmark.Arms[0].Checks.Count == 0) return benchmark;
        var arm = benchmark.Arms[0];
        var checks = new[] { arm.Checks[0] with { Trials = 0 } }.Concat(arm.Checks.Skip(1)).ToArray();
        var arms = new[] { arm with { Checks = Array.AsReadOnly(checks) } }.Concat(benchmark.Arms.Skip(1)).ToArray();
        return benchmark with { Arms = Array.AsReadOnly(arms) };
    }
    internal static VitrineOfflineBenchmarkResult PlantDegradedTie(VitrineOfflineBenchmarkResult benchmark) {
        ArgumentNullException.ThrowIfNull(benchmark);
        var rows = benchmark.ReferenceComparisons.ToArray();
        var index = Array.FindIndex(rows,
            static row => row.ChallengerArmId == VitrineOfflineBenchmark.DegradedArmId);
        if (index < 0) return benchmark;
        rows[index] = rows[index] with { Losses = 0, Ties = rows[index].Cases };
        return benchmark with { ReferenceComparisons = Array.AsReadOnly(rows) };
    }
    private static bool ExactAblationPanel(
        IReadOnlyList<VitrineAdmittedCheckSelfTestFact> facts,
        string lane,
        IReadOnlyList<string> expectedKeys) {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != expectedKeys.Count || facts.Any(fact => fact.Lane != lane) ||
            !facts.Select(static fact => fact.Key).SequenceEqual(expectedKeys, StringComparer.Ordinal))
            return false;
        return facts.All(static fact => fact.HealthyPassed == true && fact.AblationWentRed == true);
    }
}
