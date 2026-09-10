// SPDX-License-Identifier: MIT

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.VitrineDemo.Evals;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Guardrails;

namespace AgentEval.VitrineDemo.Tests;

public sealed class AgentEval035IntegrationTests
{
    [Fact]
    public void NativeStatisticsOwnExactTestsCensusAndFloorComparison()
    {
        Assert.Equal(0.625, ExactTests.TwoSidedSignP(3, 4), 12);
        Assert.Equal(0.125, ExactTests.MinimumAttainableP(4), 12);

        var forced = FloorComparison.Compute(
            [
                Observation.Measured("persona-1", "forced-choice", 1),
                Observation.Measured("persona-2", "forced-choice", 1),
                Observation.Measured("persona-3", "forced-choice", 0),
            ],
            "forced-choice",
            ChanceFloor.UniformChoice(14));

        Assert.Equal(3, forced.Trials);
        Assert.Equal(2, forced.Successes);
        Assert.Equal(ExactTests.BinomialTailP(2, 3, 1.0 / 14), forced.PValue);
        Assert.Equal(new ObservationCensus(3, 0, 0), forced.Census);
    }

    [Fact]
    public async Task RecommendationCheckUsesBuilderFloorAdmissionWithoutSelfReportingABar()
    {
        var eval = new ScreenedDeliverableEval();
        var floor = ScreenedDeliverableEval.DeclaredFloor;
        await using var runner = await new AgentEvalBuilder()
            .AddEval(eval, floor)
            .BuildAsync();
        var input = RecommendationBenchmarkInput.Create(
            "case-1",
            "request",
            "A screened recommendation for GLX-1003 because it matches your interest.",
            [],
            new(true, "measured independently"));

        var result = Assert.Single(await runner.EvaluateEvalsAsync(input));

        Assert.True(result.Score.Passed);
        Assert.Equal(FloorState.NotDerivable, floor.State);
        Assert.False(string.IsNullOrWhiteSpace(floor.Derivation));
    }

    [Fact]
    public async Task MissingContractAndMissingObservationStayOutOfTheMeasuredDenominator()
    {
        await using var runner = await new AgentEvalBuilder()
            .AddEval(new ScreenedDeliverableEval(), ScreenedDeliverableEval.DeclaredFloor)
            .BuildAsync();
        var inapplicable = Assert.Single(await runner.EvaluateEvalsAsync(new EvalInput("case with no observation")));
        var notMeasured = Assert.Single(await runner.EvaluateEvalsAsync(RecommendationBenchmarkInput.Create(
            "case-2",
            "request",
            "response",
            null,
            new(false, "recorder did not run"))));

        Assert.Equal(MeasurementState.NotApplicable, inapplicable.Score.CensusBucket());
        Assert.Equal(MeasurementState.NotMeasured, notMeasured.Score.CensusBucket());
        Assert.False(inapplicable.Score.Passed);
        Assert.False(notMeasured.Score.Passed);
        Assert.Contains("typed observation boundary", inapplicable.Details.Summary, StringComparison.Ordinal);
        Assert.Contains("NOT MEASURED", notMeasured.Details.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HonestyEvidenceAbsenceInvalidityAndLoaderFailureRemainThreeDistinctStates()
    {
        var absent = await VitrineProductionChecks.EvaluateAdmittedAsync(
            VitrineProductionChecks.Honesty,
            HonestyProductionEval.Input(null));
        var invalid = await VitrineProductionChecks.EvaluateAdmittedAsync(
            VitrineProductionChecks.Honesty,
            HonestyProductionEval.Input(new(null, "EvidenceIntegrityMismatch")));
        var loaderFailure = await VitrineProductionChecks.EvaluateAdmittedAsync(
            VitrineProductionChecks.Honesty,
            HonestyProductionEval.FailedInput("the evidence loader failed"));

        Assert.Equal(MeasurementState.NotApplicable, absent.Score.CensusBucket());
        Assert.Equal(MeasurementState.Measured, invalid.Score.CensusBucket());
        Assert.False(invalid.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, loaderFailure.Score.CensusBucket());
        Assert.False(absent.Score.CountsTowardAggregate());
        Assert.True(invalid.Score.CountsTowardAggregate());
        Assert.False(loaderFailure.Score.CountsTowardAggregate());
    }

    [Fact]
    public async Task TopologyProductionEvalRequiresTheExactEdgeCount()
    {
        var exact = await VitrineProductionChecks.EvaluateAdmittedAsync(
            VitrineProductionChecks.Topology,
            TopologyProductionEval.Input(new(
                VitrineEvalCriteria.ExecutorCount,
                VitrineEvalCriteria.ConditionalLoopBackEdges,
                VitrineEvalCriteria.EdgeCount)));
        var extraEdge = await VitrineProductionChecks.EvaluateAdmittedAsync(
            VitrineProductionChecks.Topology,
            TopologyProductionEval.Input(new(
                VitrineEvalCriteria.ExecutorCount,
                VitrineEvalCriteria.ConditionalLoopBackEdges,
                VitrineEvalCriteria.EdgeCount + 1)));

        Assert.True(exact.Score.Passed);
        Assert.Equal(MeasurementState.Measured, extraEdge.Score.CensusBucket());
        Assert.False(extraEdge.Score.Passed);
    }

    [Fact]
    public void NonTestResultProjectionPreservesAllThreeToolJournalStatesExactly()
    {
        var observation = new RecommendationSurfaceObservation(
            Instrumented: true,
            Evidence: "observed independently");
        var watchedZero = Array.Empty<ToolCall>();
        IReadOnlyList<ToolCall> invoked =
        [
            new ToolCall(
                "SearchProducts",
                new Dictionary<string, object> { ["query"] = "quiet keyboard" },
                "GLX-1003"),
        ];

        var unwatched = RecommendationBenchmarkInput.Create(
            "unwatched", "request", "answer", null, observation);
        var zero = RecommendationBenchmarkInput.Create(
            "watched-zero", "request", "answer", watchedZero, observation);
        var called = RecommendationBenchmarkInput.Create(
            "watched-called", "request", "answer", invoked, observation);

        Assert.Null(unwatched.ToolCalls);
        Assert.Same(watchedZero, zero.ToolCalls);
        Assert.Empty(zero.ToolCalls!);
        Assert.Same(invoked, called.ToolCalls);
        var toolCall = Assert.Single(called.ToolCalls!);
        Assert.Equal("SearchProducts", toolCall.Name);
        Assert.Equal("quiet keyboard", toolCall.Arguments!["query"]);
        Assert.Equal("GLX-1003", toolCall.Result);
    }

    [Fact]
    public void JudgedProjectionUsesIEvaluatorResultDirectlyWithoutASyntheticTestResultAdapter()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "AgentEval.VitrineDemo.Evals",
            "Evals",
            "Offline",
            "EvaluationSuite.cs"));

        Assert.Contains("AgentEval.Core.IEvaluator evaluator", source, StringComparison.Ordinal);
        Assert.Contains("evaluator.EvaluateAsync(", source, StringComparison.Ordinal);
        Assert.Contains("AgentEval.Core.EvaluationResult Result", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AgentEval.Models.TestResult", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MAFEvaluationHarness", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FixedResponseAgent", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PositiveToolCountWithoutMatchingJournalIsNotMeasuredRatherThanWatchedZero()
    {
        var run = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(Personas.NadiaUserId, Arm: RecommendationExecutionArm.ScriptedAgent));
        Assert.True(run.ToolCallsUsed > 0);
        Assert.NotEmpty(VitrineOfflineBenchmark.ProjectDemo01ToolCalls(run)!);

        var missingJournal = run with { Events = [] };
        Assert.Null(VitrineOfflineBenchmark.ProjectDemo01ToolCalls(missingJournal));
        var watchedZero = run with { ToolCallsUsed = 0, Presented = [], Events = [] };
        Assert.Empty(VitrineOfflineBenchmark.ProjectDemo01ToolCalls(watchedZero)!);
    }

    [Fact]
    public void RecommendationBenchmarkDefinitionUsesStimuliAndFiveFlooredChecks()
    {
        var definition = VitrineOfflineBenchmark.CreateDefinition();

        Assert.Equal(
            [VitrineOfflineBenchmark.NadiaCaseId, VitrineOfflineBenchmark.SofiaCaseId],
            definition.Cases.Select(static testCase => testCase.Id));
        Assert.Equal(VitrineOfflineBenchmark.CheckKeys, definition.Checks.Select(static check => check.Eval.Key));
        Assert.Equal(5, definition.Checks.Count);
        Assert.All(definition.Checks, check =>
        {
            Assert.Equal(FloorState.NotDerivable, check.Floor.State);
            Assert.False(string.IsNullOrWhiteSpace(check.Floor.Derivation));
        });

        var plans = VitrineOfflineBenchmark.CreateArmPlans();
        Assert.Equal(
            [VitrineOfflineBenchmark.Demo01ArmId, VitrineOfflineBenchmark.Demo02ArmId,
                VitrineOfflineBenchmark.DegradedArmId],
            plans.Select(static plan => plan.ArmId));
        Assert.Equal(SubjectKind.Agent, plans[0].Subject.Kind);
        Assert.Equal(SubjectKind.Workflow, plans[1].Subject.Kind);
        Assert.Equal(SubjectKind.Agent, plans[2].Subject.Kind);
    }

    [Fact]
    public async Task ControlProgressPublishesHealthyBeforePlantAndRestore()
    {
        var definition = new ControlDefinition(
            "TEST-035", "Ordered control lifecycle", "meta",
            _ => new Box(false),
            value => ((Box)value) with { Broken = true },
            (_, value) => ControlAssessment.Measured(!((Box)value).Broken, "typed observation"),
            "set the typed broken flag");
        var progress = new RecordingEvaluationProgressSink();

        _ = await NegativeControlRunner.RunAsync(
            [definition], ControlEnvironment.Capture(), progress: progress);

        Assert.Equal(
            [
                EvaluationProgressKind.ControlStarted,
                EvaluationProgressKind.ControlHealthyCompleted,
                EvaluationProgressKind.ControlBrokenCompleted,
                EvaluationProgressKind.ControlRestoredCompleted,
                EvaluationProgressKind.ControlCompleted,
            ],
            progress.Events.Select(item => item.Kind));
        Assert.True(progress.Events[1].Passed);
        Assert.True(progress.Events[2].Passed); // the plant was caught, so this lifecycle step succeeded
        Assert.True(progress.Events[3].Passed);
    }

    [Fact]
    public async Task OfflineSelfTestPersistsOneRunPerArmRepAndDiscriminatesEveryCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "vitrine-agenteval-035-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var selfTest = await VitrineAdmittedChecksSelfTest.RunAsync(root);
            var outcome = selfTest.Benchmark;
            var store = new FileSystemOutputStore(root);
            var manifest = await store.GetRunManifestAsync(outcome.RunId);
            var summary = await store.GetRunSummaryAsync(outcome.RunId);
            var scenarios = new List<ScenarioResult>();
            await foreach (var scenario in store.GetScenarioResultsAsync(outcome.RunId))
                scenarios.Add(scenario);

            Assert.Equal(Path.GetFullPath(root), outcome.WorkspaceRoot);
            Assert.True(Directory.Exists(outcome.RunDirectory));
            Assert.Equal(outcome.RunId, manifest!.Run.RunId);
            Assert.Equal(BenchmarkRunner.RunKind, manifest.Run.Kind);
            Assert.Equal($"BenchmarkRunner/{outcome.DefinitionKey}@{outcome.DefinitionVersion}", manifest.Run.Harness);
            Assert.Equal(10, summary!.Stats.Total);
            Assert.Equal(10, summary.Stats.Passed);
            Assert.Equal(0, summary.Stats.Skipped);
            Assert.Equal("PASS", summary.Verdict);

            // Positive controls precede value assertions.
            Assert.True(selfTest.Passed, string.Join(Environment.NewLine, selfTest.Failures));
            Assert.Equal(5, selfTest.BenchmarkChecks);
            Assert.Equal(6, selfTest.ProductionChecks);
            Assert.Equal(11, selfTest.Checks.Count);
            Assert.Equal(
                VitrineOfflineBenchmark.CheckKeys,
                selfTest.Checks.Where(static check => check.Lane == "benchmark").Select(static check => check.Key));
            Assert.All(selfTest.Checks, check =>
            {
                Assert.True(check.HealthyPassed);
                Assert.True(check.AblationWentRed);
            });
            Assert.Equal(10, scenarios.Count);
            Assert.Equal(3 * VitrineOfflineBenchmark.Repetitions, outcome.Runs.Count);
            Assert.Equal(3, outcome.Arms.Count);
            Assert.Equal(10, outcome.ReferenceComparisons.Count);
            Assert.Contains(scenarios, row => row.Id.StartsWith(VitrineOfflineBenchmark.NadiaCaseId, StringComparison.Ordinal));
            Assert.Contains(scenarios, row => row.Id.StartsWith(VitrineOfflineBenchmark.SofiaCaseId, StringComparison.Ordinal));
            Assert.Equal(outcome.Runs.Count, outcome.Runs.Select(static run => run.RunId).Distinct(StringComparer.Ordinal).Count());
            Assert.All(outcome.Runs, run => Assert.True(Directory.Exists(run.RunDirectory)));

            Assert.All(scenarios, row =>
            {
                Assert.True(row.Passed);
                Assert.NotNull(row.StimulusHash);
                Assert.Equal(FloorState.NotDerivable, row.Comparability!.ChanceFloor!.State);
                Assert.Null(row.Comparability.ChanceFloor.Bar);
                Assert.NotNull(EvalResultPersistence.FromScenarioResult(row));
            });

            Assert.All(outcome.Arms, arm =>
            {
                Assert.Equal(5, arm.Checks.Count);
                Assert.All(arm.Checks, check =>
                {
                    Assert.Equal((2, 0, 0),
                        (check.Census.Measured, check.Census.NotApplicable, check.Census.NotMeasured));
                    Assert.Equal(2, check.Trials);
                    Assert.Equal(
                        arm.ArmId == VitrineOfflineBenchmark.DegradedArmId ? 0 : 2,
                        check.Successes);
                    Assert.Equal("NotDerivable", check.Floor.State);
                    Assert.Null(check.Floor.ComparisonBar);
                    Assert.Null(check.PValue);
                    Assert.Null(check.AboveFloor);
                    Assert.Null(check.UnderpoweredByConstruction);
                });
            });

            Assert.All(outcome.ReferenceComparisons.Where(static comparison =>
                comparison.ChallengerArmId == VitrineOfflineBenchmark.Demo02ArmId), comparison =>
                Assert.Equal((0, 0, 2), (comparison.Wins, comparison.Losses, comparison.Ties)));
            Assert.All(outcome.ReferenceComparisons.Where(static comparison =>
                comparison.ChallengerArmId == VitrineOfflineBenchmark.DegradedArmId), comparison =>
                Assert.Equal((0, 2, 0), (comparison.Wins, comparison.Losses, comparison.Ties)));
            Assert.All(outcome.ReferenceComparisons, comparison =>
            {
                Assert.Equal(VitrineOfflineBenchmark.Demo01ArmId, comparison.ReferenceArmId);
                Assert.Equal(2, comparison.Cases);
                Assert.Equal(8, comparison.TotalRepObservations);
                Assert.Equal(nameof(RepCollapse.All), comparison.RepCollapse);
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SelfTestCliReturnsOneWhenAnyExpectationDoesNotHold()
    {
        var benchmark = new VitrineOfflineBenchmarkResult(
            "fixture", "1.0.0", "reference", "run", "workspace", "directory", [], []);
        var failed = new VitrineAdmittedChecksSelfTestResult(
            benchmark, ["planted expectation mismatch"], BenchmarkChecks: 5, ProductionChecks: 6);
        var services = new EvaluationCliServices(
            (_, _) => Task.FromResult(new SuiteResult([], [])),
            _ => Task.FromResult<IReadOnlyList<ControlResult>>([]),
            (_, _, _) => Task.CompletedTask,
            RunSelfTest: _ => Task.FromResult(failed));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await EvaluationCli.RunAsync(["--self-test"], output, error, services);

        Assert.Equal(EvaluationExitCodes.GateFailed, exit);
        Assert.Contains("planted expectation mismatch", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentEval.VitrineDemo.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate VITRINE repository root.");
    }

    private sealed record Box(bool Broken);
}
