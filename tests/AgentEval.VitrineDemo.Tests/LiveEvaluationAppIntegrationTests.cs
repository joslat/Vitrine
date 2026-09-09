// SPDX-License-Identifier: MIT

using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.App;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals.Live;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Galaxus.RecommendationAgent.Catalog;

namespace AgentEval.VitrineDemo.Tests;

public sealed class LiveEvaluationAppIntegrationTests
{
    [AvaloniaFact]
    public async Task SelectedLivePlanShowsItsScenarioTrialReliabilityAndPersistenceSections()
    {
        var window = new MainWindow { Width = 1480, Height = 900 };
        window.Show();
        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
        viewModel.SelectedMode = VitrineRunMode.Evals;
        viewModel.Setup.SelectedEvaluationPlan = viewModel.Setup.EvaluationPlans.Single(item =>
            item.Plan == VitrineEvaluationPlan.LiveEval03AgentVsWorkflow);
        var tab = window.GetVisualDescendants().OfType<TabItem>()
            .Single(item => string.Equals(item.Header?.ToString(), "EVALUATION BOARD", StringComparison.Ordinal));
        tab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();

        var visible = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(static item => item.IsEffectivelyVisible)
            .Select(static item => item.Text ?? string.Empty)
            .ToArray();
        Assert.Contains("SELECTED LIVE PLAN", visible);
        Assert.Contains("AUTHORED USE-CASE SCENARIOS · QUERY + CRITERIA", visible);
        Assert.Contains("SUBJECT TRIALS · RESPONSE + TOOL / WORKFLOW EVIDENCE", visible);
        Assert.Contains("PER-ARM CHECK CENSUS + WILSON RELIABILITY", visible);
        Assert.Contains("PROVIDER USAGE + PHYSICAL RUN DIRECTORIES", visible);
        Assert.DoesNotContain("CATALOGUE INTEGRITY SELF-TEST · isolated catalogue row removed → detection expected (exit 1) → state restored · no provider LLM", visible);

        window.Close();
        await viewModel.DisposeAsync();
    }

    [Fact]
    public void OfflineSuiteRemainsTheDefaultAndAllUseCasesArePreviewedBeforeRun()
    {
        var setup = new RunSetupViewModel();

        Assert.Equal(VitrineEvaluationPlan.OfflineSuite, setup.SelectedEvaluationPlan.Plan);
        Assert.False(setup.SelectedEvaluationPlan.IsLive);
        Assert.Equal(7, setup.EvaluationPlans.Count);

        setup.SelectedEvaluationPlan = setup.EvaluationPlans.Single(item =>
            item.Plan == VitrineEvaluationPlan.LiveEval03AgentVsWorkflow);
        setup.SelectedLiveScenario = setup.LiveScenarios.Single(item => item.Id is null);

        Assert.Equal(LiveUseCaseScenarios.All.Count, setup.SelectedLiveScenario.CaseCount);
        Assert.All(LiveUseCaseScenarios.All, scenario =>
        {
            Assert.Contains(scenario.Id, setup.EvaluationScenarioQuery, StringComparison.Ordinal);
            Assert.Contains(PersonaScenarios.Require(scenario.PersonaId).Query,
                setup.EvaluationScenarioQuery, StringComparison.Ordinal);
        });
        Assert.Equal(LiveUseCaseScenarios.All.Count * 2, setup.PlannedLiveSubjectRuns);
        Assert.Equal(setup.PlannedLiveSubjectRuns, setup.PlannedLiveJudgeCalls);
    }

    [Fact]
    public void IncreasingPaidWorkloadAlwaysRequiresFreshConfirmation()
    {
        var setup = new RunSetupViewModel
        {
            SelectedEvaluationPlan = VitrineEvaluationPlans.Require(
                VitrineEvaluationPlan.LiveEval04StochasticAgent),
        };
        setup.PaidEvaluationAcknowledged = true;
        Assert.True(setup.PaidEvaluationAcknowledged);
        Assert.Equal(4, setup.MinimumEvaluationRepetitions);
        setup.EvaluationRepetitions = 1;
        Assert.Equal(4, setup.EvaluationRepetitions);
        setup.PaidEvaluationAcknowledged = true;

        setup.EvaluationRepetitions++;
        Assert.False(setup.PaidEvaluationAcknowledged);

        setup.PaidEvaluationAcknowledged = true;
        setup.SelectedLiveScenario = setup.LiveScenarios.Single(item => item.Id is null);
        Assert.False(setup.PaidEvaluationAcknowledged);
    }

    [Fact]
    public async Task CoordinatorRunsOnlyTheSelectedLivePlanAndStreamsTypedEvidence()
    {
        var calls = 0;
        await using var coordinator = new VitrineRunCoordinator(
            (request, _) => VitrineGraphFactory.ForRunningLiveEvaluation(request.EvaluationPlan),
            (plan, paidExecutionConfirmed, options, progress, _) =>
            {
                calls++;
                Assert.True(paidExecutionConfirmed);
                Assert.Equal(VitrineEvaluationPlan.LiveEval01Agent, plan);
                Assert.Equal(1, options.Repetitions);
                Assert.Equal(["nadia-cross-category"], options.ScenarioIds);
                progress?.Report(Progress(plan, LiveEvalProgressPhase.SessionStarting));
                progress?.Report(Progress(plan, LiveEvalProgressPhase.TrialStarting,
                    "nadia-cross-category", "fake-agent", 1));
                progress?.Report(Progress(plan, LiveEvalProgressPhase.SubjectRunning,
                    "nadia-cross-category", "fake-agent", 1));
                progress?.Report(Progress(plan, LiveEvalProgressPhase.SubjectCompleted,
                    "nadia-cross-category", "fake-agent", 1, measurement: MeasurementState.Measured,
                    passed: true));
                progress?.Report(Progress(plan, LiveEvalProgressPhase.CheckStarting,
                    "nadia-cross-category", "fake-agent", 1,
                    LiveUseCaseBenchmark.UseCaseQualityCheckKey));
                progress?.Report(Progress(plan, LiveEvalProgressPhase.CheckCompleted,
                    "nadia-cross-category", "fake-agent", 1,
                    LiveUseCaseBenchmark.UseCaseQualityCheckKey, MeasurementState.Measured, true));
                progress?.Report(Progress(plan, LiveEvalProgressPhase.Persisting));
                progress?.Report(Progress(plan, LiveEvalProgressPhase.SessionCompleted,
                    measurement: MeasurementState.Measured, passed: true));
                return Task.FromResult(Result(plan));
            });

        var outcome = await coordinator.RunAsync(new(
            VitrineRunMode.Evals,
            Personas.NadiaUserId,
            EvaluationPlan: VitrineEvaluationPlan.LiveEval01Agent,
            LiveScenarioId: "nadia-cross-category",
            EvaluationRepetitions: 1,
            PaidEvaluationConfirmed: true));

        Assert.Equal(1, calls);
        Assert.Null(outcome.Evaluation);
        Assert.NotNull(outcome.LiveEvaluation);
        Assert.Equal(0, outcome.ProcessEquivalentExitCode);
        Assert.Contains(outcome.Events, item => item.Kind == "LiveSubjectRunning"
            && item.Disposition == AgentEval.VitrineDemo.App.Models.VitrineEventDisposition.Active);
        Assert.Contains(outcome.Events, item => item.Kind == "LiveCheckCompleted"
            && item.SanitizedPayload!.Contains("Admitted check", StringComparison.Ordinal));
        Assert.Contains(outcome.Events, item => item.Kind == "LiveSessionCompleted"
            && item.Disposition == AgentEval.VitrineDemo.App.Models.VitrineEventDisposition.Succeeded);

        var graph = new GraphViewModel();
        graph.Load(outcome.Graph);
        foreach (var item in outcome.Events) graph.Apply(item);
        Assert.Equal(1, graph.Nodes.Single(item => item.Id == "live-agent").ExecutionCount);
        Assert.Equal(1, graph.Nodes.Single(item =>
            item.Id == LiveUseCaseBenchmark.UseCaseQualityCheckKey).ExecutionCount);
        Assert.Equal(GraphNodeState.Succeeded, graph.StateOf("live-complete"));
    }

    [Fact]
    public void ComparisonGraphRetainsAnApplicableCheckResultAfterTheOtherArmReportsNotApplicable()
    {
        var graph = new GraphViewModel();
        var store = new VitrineEventStore();
        graph.Load(VitrineGraphFactory.ForRunningLiveEvaluation(
            VitrineEvaluationPlan.LiveEval03AgentVsWorkflow));

        foreach (var progress in new[]
        {
            Progress(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
                LiveEvalProgressPhase.CheckStarting, "nadia-cross-category", "vitrine-live-agent", 1,
                LiveUseCaseBenchmark.AgentToolJournalCheckKey),
            Progress(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
                LiveEvalProgressPhase.CheckCompleted, "nadia-cross-category", "vitrine-live-agent", 1,
                LiveUseCaseBenchmark.AgentToolJournalCheckKey, MeasurementState.Measured, true),
            Progress(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
                LiveEvalProgressPhase.CheckStarting, "nadia-cross-category", "vitrine-live-workflow", 1,
                LiveUseCaseBenchmark.AgentToolJournalCheckKey),
            Progress(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
                LiveEvalProgressPhase.CheckCompleted, "nadia-cross-category", "vitrine-live-workflow", 1,
                LiveUseCaseBenchmark.AgentToolJournalCheckKey, MeasurementState.NotApplicable),
        })
            graph.Apply(store.Append(VitrineEventAdapters.FromLiveEvaluation(progress)));

        var agentCheck = $"live-agent:{LiveUseCaseBenchmark.AgentToolJournalCheckKey}";
        Assert.Equal(GraphNodeState.Succeeded, graph.StateOf(agentCheck));
        Assert.Equal(1, graph.Nodes.Single(item => item.Id == agentCheck).ExecutionCount);
    }

    [Theory]
    [InlineData(MeasurementState.Measured, false, GraphNodeState.Failed)]
    [InlineData(MeasurementState.NotMeasured, null, GraphNodeState.NotMeasured)]
    public void LaterSuccessfulTrialCannotEraseAnEarlierArmFailureOrMissingMeasurement(
        MeasurementState firstMeasurement,
        bool? firstPassed,
        GraphNodeState expected)
    {
        var graph = new GraphViewModel();
        var store = new VitrineEventStore();
        graph.Load(VitrineGraphFactory.ForRunningLiveEvaluation(
            VitrineEvaluationPlan.LiveEval04StochasticAgent));

        graph.Apply(store.Append(VitrineEventAdapters.FromLiveEvaluation(Progress(
            VitrineEvaluationPlan.LiveEval04StochasticAgent,
            LiveEvalProgressPhase.TrialCompleted,
            "nadia-cross-category", "vitrine-live-agent", 1,
            measurement: firstMeasurement, passed: firstPassed))));
        graph.Apply(store.Append(VitrineEventAdapters.FromLiveEvaluation(Progress(
            VitrineEvaluationPlan.LiveEval04StochasticAgent,
            LiveEvalProgressPhase.TrialCompleted,
            "nadia-cross-category", "vitrine-live-agent", 2,
            measurement: MeasurementState.Measured, passed: true))));

        Assert.Equal(expected, graph.StateOf("live-agent"));
    }

    [Fact]
    public async Task MissingPaidConfirmationCannotReachTheLiveRunner()
    {
        var calls = 0;
        await using var coordinator = new VitrineRunCoordinator(
            (request, _) => VitrineGraphFactory.ForRunningLiveEvaluation(request.EvaluationPlan),
            (_, _, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Result(VitrineEvaluationPlan.LiveEval01Agent));
            });

        var outcome = await coordinator.RunAsync(new(
            VitrineRunMode.Evals,
            Personas.NadiaUserId,
            EvaluationPlan: VitrineEvaluationPlan.LiveEval01Agent));

        Assert.Equal(0, calls);
        Assert.Null(outcome.LiveEvaluation);
        Assert.Equal(nameof(InvalidOperationException), outcome.FailureKind);
    }

    private static LiveEvalProgress Progress(
        VitrineEvaluationPlan plan,
        LiveEvalProgressPhase phase,
        string? scenario = null,
        string? arm = null,
        int? repetition = null,
        string? check = null,
        MeasurementState? measurement = null,
        bool? passed = null) =>
        new(plan, phase, scenario, arm, repetition, check, measurement, passed,
            "Typed fixture progress; no model content.");

    private static LiveEvalResult Result(VitrineEvaluationPlan plan)
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        return new(plan, LiveEvalTerminalStatus.Passed, "app-live-fixture", now, now,
            new(1, 1, 1, 1, 1), 0.75, [],
            new("fixture", "1", "judge", "prompt", "rubric", 256, 256, 320, []),
            [], [], [], [], [],
            new("workspace", "session", "session/outcome.json", "live-sessions/index.json"));
    }
}
