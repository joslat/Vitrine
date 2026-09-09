// SPDX-License-Identifier: MIT
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using Galaxus.RecommendationAgent.Guardrails;
namespace AgentEval.VitrineDemo.Evals;
public sealed record VitrineAdmittedChecksSelfTestResult(
    VitrineOfflineBenchmarkResult Benchmark,
    IReadOnlyList<string> Failures,
    int BenchmarkChecks,
    int ProductionChecks) {
    public bool Passed => Failures.Count == 0;
    public int ExitCode => Passed ? EvaluationExitCodes.Passed : EvaluationExitCodes.GateFailed;
    public IReadOnlyList<VitrineAdmittedCheckSelfTestFact> Checks { get; init; } = [];
}
public sealed record VitrineAdmittedCheckSelfTestFact(
    string Key,
    string Lane,
    bool? HealthyPassed,
    bool? AblationWentRed,
    string Detail);
/// <summary>
/// Offline, deterministic end-to-end proof that every admitted VITRINE check executes and that its
/// deliberately degraded observation is discriminated. It never calls an external model.
/// </summary>
public static class VitrineAdmittedChecksSelfTest {
    public static async Task<VitrineAdmittedChecksSelfTestResult> RunAsync(
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default) {
        var execution = await VitrineOfflineBenchmark.ExecuteAsync(workspaceRoot, cancellationToken)
            .ConfigureAwait(false);
        var failures = new List<string>();
        void Expect(bool condition, string message) {
            if (!condition)
                failures.Add(message);
        }
        var definition = execution.Definition;
        var expectedRowsPerRun = definition.Cases.Count * definition.Checks.Count;
        var expectedInputsPerArm = definition.Cases.Count * VitrineOfflineBenchmark.Repetitions;
        var expectedArmIds = new[] {
            VitrineOfflineBenchmark.Demo01ArmId,
            VitrineOfflineBenchmark.Demo02ArmId,
            VitrineOfflineBenchmark.DegradedArmId,
        };
        var expectedProductionKeys = new[] {
            "catalogue-shape",
            "workflow-shape",
            "matched-quality",
            "injection",
            "recall",
            "honesty",
        };
        // 0 · POSITIVE CONTROLS. No score, verdict, or comparison value is read before all of these
        // counts are known to be non-zero and exact.
        Expect(definition.Cases.Count == 2,
            $"benchmark definition contains {definition.Cases.Count} case(s), not the Nadia/Sofia pair");
        Expect(definition.Cases.Select(static testCase => testCase.Id).SequenceEqual(
                new[] { VitrineOfflineBenchmark.NadiaCaseId, VitrineOfflineBenchmark.SofiaCaseId },
                StringComparer.Ordinal),
            "benchmark case identities are not the exact Nadia/Sofia stimulus set");
        Expect(definition.Checks.Count == 5,
            $"benchmark definition contains {definition.Checks.Count} admitted check(s), not five");
        Expect(definition.Checks.Select(static check => check.Eval.Key).SequenceEqual(
                VitrineOfflineBenchmark.CheckKeys,
                StringComparer.Ordinal),
            "benchmark admitted-check identities are not the exact five registered recommendation predicates");
        Expect(execution.RunsByArm.Keys.OrderBy(static id => id, StringComparer.Ordinal).SequenceEqual(
                expectedArmIds.OrderBy(static id => id, StringComparer.Ordinal),
                StringComparer.Ordinal),
            "benchmark run collections are not the exact three registered arms");
        Expect(VitrineProductionChecks.All.Count == 6,
            $"production admitted-check registry contains {VitrineProductionChecks.All.Count} check(s), not six");
        Expect(VitrineProductionChecks.All.Select(static check => check.Key).SequenceEqual(
                expectedProductionKeys,
                StringComparer.Ordinal),
            "production admitted-check identities are not the exact six registered predicates");
        foreach (var armId in expectedArmIds) {
            Expect(execution.RunsByArm.TryGetValue(armId, out var runs),
                $"benchmark arm '{armId}' produced no declared run collection");
            if (!execution.RunsByArm.TryGetValue(armId, out runs))
                continue;
            Expect(runs.Count == VitrineOfflineBenchmark.Repetitions,
                $"benchmark arm '{armId}' produced {runs.Count} run(s), not {VitrineOfflineBenchmark.Repetitions}");
            foreach (var run in runs) {
                Expect(run.Observations.Count == expectedRowsPerRun,
                    $"benchmark run '{run.RunId}' produced {run.Observations.Count} observation(s), not {expectedRowsPerRun}");
            }
            var inputCount = execution.Inputs.Count(input => input.ArmId == armId);
            Expect(inputCount == expectedInputsPerArm,
                $"benchmark arm '{armId}' captured {inputCount} raw input(s), not {expectedInputsPerArm}");
        }
        var productionFacts = new List<VitrineAdmittedCheckSelfTestFact>(expectedProductionKeys.Length);
        if (failures.Count > 0) {
            productionFacts.AddRange(expectedProductionKeys.Select(static key =>
                new VitrineAdmittedCheckSelfTestFact(
                    key,
                    "production",
                    HealthyPassed: null,
                    AblationWentRed: null,
                    "NOT VERIFIED: the positive registration/cardinality control failed.")));
            return Result(execution, failures, productionFacts, benchmarkValuesAvailable: false);
        }
        // The six admitted leaves used by EvaluationSuite own another exact builder-result count
        // check before they inspect healthy/ablated values and absence-state behavior.
        var productionFailures = await VitrineProductionChecks
            .SelfTestFailuresAsync(cancellationToken)
            .ConfigureAwait(false);
        failures.AddRange(productionFailures.Select(static failure => $"production: {failure}"));
        foreach (var check in VitrineProductionChecks.All) {
            var healthy = await VitrineProductionChecks.EvaluateAdmittedAsync(
                    check, check.HealthyInput(), cancellationToken)
                .ConfigureAwait(false);
            var ablated = await VitrineProductionChecks.EvaluateAdmittedAsync(
                    check, check.AblatedInput(), cancellationToken)
                .ConfigureAwait(false);
            productionFacts.Add(new(
                check.Key,
                "production",
                healthy.Score.CensusBucket() == MeasurementState.Measured && healthy.Score.Passed,
                ablated.Score.CensusBucket() == MeasurementState.Measured && !ablated.Score.Passed,
                $"floor {check.Floor.State}: {check.Floor.Derivation}"));
        }
        // 1 · CENSUS FIRST. The unit is the case; repetitions have already collapsed.
        var censusByArm = expectedArmIds.ToDictionary(
            static armId => armId,
            armId => BenchmarkScore.Census(execution.RunsByArm[armId]),
            StringComparer.Ordinal);
        foreach (var armId in expectedArmIds) {
            foreach (var (checkKey, census) in censusByArm[armId]) {
                Expect(census.Measured == definition.Cases.Count &&
                       census.NotApplicable == 0 &&
                       census.NotMeasured == 0,
                    $"arm '{armId}', check '{checkKey}' census is {census.Describe()}, expected every case measured");
            }
        }
        // 2 · Healthy arms must hold every predicate; the explicit control must turn every check red.
        ExpectArmValues(execution, VitrineOfflineBenchmark.Demo01ArmId, expectedValue: 1.0, Expect);
        ExpectArmValues(execution, VitrineOfflineBenchmark.Demo02ArmId, expectedValue: 1.0, Expect);
        ExpectArmValues(execution, VitrineOfflineBenchmark.DegradedArmId, expectedValue: 0.0, Expect);
        // 3 · AgentEval's paired scorer owns comparison arithmetic and rep collapse. Demo02 is
        // expected to tie the Demo01 reference; the empty-answer arm must lose every case/check.
        foreach (var (checkKey, comparison) in BenchmarkScore.AgainstReference(
                     execution.RunsByArm[VitrineOfflineBenchmark.Demo01ArmId],
                     execution.RunsByArm[VitrineOfflineBenchmark.Demo02ArmId],
                     RepCollapse.All)) {
            Expect(comparison.Wins == 0 && comparison.Losses == 0 &&
                   comparison.Ties == definition.Cases.Count,
                $"Demo02 vs Demo01, check '{checkKey}' produced W/L/T {comparison.Wins}/{comparison.Losses}/{comparison.Ties}, expected 0/0/{definition.Cases.Count}");
        }
        foreach (var (checkKey, comparison) in BenchmarkScore.AgainstReference(
                     execution.RunsByArm[VitrineOfflineBenchmark.Demo01ArmId],
                     execution.RunsByArm[VitrineOfflineBenchmark.DegradedArmId],
                     RepCollapse.All)) {
            Expect(comparison.Wins == 0 && comparison.Losses == definition.Cases.Count &&
                   comparison.Ties == 0,
                $"degraded vs Demo01, check '{checkKey}' produced W/L/T {comparison.Wins}/{comparison.Losses}/{comparison.Ties}, expected 0/{definition.Cases.Count}/0");
        }
        // 4 · Non-TestResult tool-call projection keeps all three states distinct.
        foreach (var captured in execution.Inputs.Where(static input =>
                     input.ArmId == VitrineOfflineBenchmark.Demo01ArmId)) {
            Expect(captured.Input.ToolCalls is { Count: > 0 },
                $"Demo01 case '{captured.CaseId}', rep {captured.Repetition} did not project its observed tool events");
        }
        foreach (var captured in execution.Inputs.Where(static input =>
                     input.ArmId is VitrineOfflineBenchmark.Demo02ArmId or VitrineOfflineBenchmark.DegradedArmId)) {
            Expect(captured.Input.ToolCalls is { Count: 0 },
                $"arm '{captured.ArmId}', case '{captured.CaseId}' did not preserve watched-zero as an empty journal");
        }
        var unwatched = RecommendationBenchmarkInput.Create(
            "selftest-unwatched",
            "unwatched input",
            "response",
            toolCalls: null,
            new(true, "the journal was not observed"));
        Expect(unwatched.ToolCalls is null,
            "the non-TestResult projection collapsed an unwatched null journal into watched-zero");
        return Result(execution, failures, productionFacts, benchmarkValuesAvailable: true);
    }
    private static void ExpectArmValues(
        VitrineBenchmarkExecution execution,
        string armId,
        double expectedValue,
        Action<bool, string> expect) {
        foreach (var run in execution.RunsByArm[armId]) {
            foreach (var observation in run.Observations) {
                expect(
                    observation.Observation.State == MeasurementState.Measured &&
                    observation.Observation.Value == expectedValue,
                    $"arm '{armId}', check '{observation.CheckKey}', case '{observation.Observation.CaseId}' " +
                    $"reported {observation.Observation.State}/{observation.Observation.Value}, expected Measured/{expectedValue}");
            }
        }
    }
    private static VitrineAdmittedChecksSelfTestResult Result(
        VitrineBenchmarkExecution execution,
        List<string> failures,
        IReadOnlyList<VitrineAdmittedCheckSelfTestFact> productionFacts,
        bool benchmarkValuesAvailable) {
        var benchmarkFacts = new List<VitrineAdmittedCheckSelfTestFact>(execution.Definition.Checks.Count);
        if (benchmarkValuesAvailable) {
            var healthy = execution.Result.Arms.Single(static arm =>
                arm.ArmId == VitrineOfflineBenchmark.Demo01ArmId).Checks
                .ToDictionary(static check => check.CheckKey, StringComparer.Ordinal);
            var degraded = execution.Result.Arms.Single(static arm =>
                arm.ArmId == VitrineOfflineBenchmark.DegradedArmId).Checks
                .ToDictionary(static check => check.CheckKey, StringComparer.Ordinal);
            var comparisons = execution.Result.ReferenceComparisons.Where(static comparison =>
                    comparison.ChallengerArmId == VitrineOfflineBenchmark.DegradedArmId)
                .ToDictionary(static comparison => comparison.CheckKey, StringComparer.Ordinal);
            foreach (var key in VitrineOfflineBenchmark.CheckKeys) {
                var healthyRow = healthy[key];
                var degradedRow = degraded[key];
                var comparison = comparisons[key];
                var expectedCases = execution.Definition.Cases.Count;
                var healthyComplete = healthyRow.Census.Measured == expectedCases &&
                    healthyRow.Census.NotApplicable == 0 &&
                    healthyRow.Census.NotMeasured == 0 &&
                    healthyRow.Trials == expectedCases;
                var degradedComplete = degradedRow.Census.Measured == expectedCases &&
                    degradedRow.Census.NotApplicable == 0 &&
                    degradedRow.Census.NotMeasured == 0 &&
                    degradedRow.Trials == expectedCases;
                benchmarkFacts.Add(new(
                    key,
                    "benchmark",
                    healthyComplete && healthyRow.Successes == expectedCases,
                    degradedComplete && degradedRow.Successes == 0 &&
                        comparison.Losses == expectedCases && comparison.Wins == 0 && comparison.Ties == 0,
                    $"degraded vs Demo01 W/L/T {comparison.Wins}/{comparison.Losses}/{comparison.Ties}; " +
                    $"case n {comparison.Cases}; {comparison.TotalRepObservations} rep-observations collapsed by {comparison.RepCollapse}"));
            }
        }
        else {
            benchmarkFacts.AddRange(VitrineOfflineBenchmark.CheckKeys.Select(static key => new VitrineAdmittedCheckSelfTestFact(
                key,
                "benchmark",
                HealthyPassed: null,
                AblationWentRed: null,
                "NOT VERIFIED: the positive observation-cardinality control failed.")));
        }
        return new(
            execution.Result,
            Array.AsReadOnly(failures.ToArray()),
            execution.Definition.Checks.Count,
            VitrineProductionChecks.All.Count) {
            Checks = Array.AsReadOnly(benchmarkFacts.Concat(productionFacts).ToArray()),
        };
    }
}
