// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class AppRuntimeRegressionTests
{
    [Fact]
    public async Task ConcurrentAppendsNotifyObserversInSequenceOrder()
    {
        var store = new VitrineEventStore();
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var observed = new ConcurrentQueue<long>();
        store.EventAppended += item =>
        {
            if (item.Sequence == 1)
            {
                firstEntered.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            }
            observed.Enqueue(item.Sequence);
        };

        var first = Task.Run(() => store.Append(Draft("first")));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));
        var second = Task.Run(() => store.Append(Draft("second")));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirst.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([1L, 2L], observed);
    }

    [Fact]
    public void RuntimeGraphsContainEveryEdgeEndpoint()
    {
        var graphs = new[]
        {
            VitrineGraphFactory.FromRegisteredDemo01Functions(),
            VitrineGraphFactory.ForEvaluationSuite(),
        };

        foreach (var graph in graphs)
        {
            var nodeIds = graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(graph.Edges, edge =>
            {
                Assert.Contains(edge.SourceId, nodeIds);
                Assert.Contains(edge.TargetId, nodeIds);
            });
        }
    }

    [Fact]
    public void GraphRevisionAdvancesWhenOnlyTheActiveRouteChanges()
    {
        var graph = new VitrineGraphSnapshot(
            "routes",
            VitrineGraphSource.MafWorkflow,
            [new("a", "A", "executor"), new("b", "B", "executor"), new("c", "C", "executor")],
            [new("ab", "a", "b", "first"), new("ac", "a", "c", "second")]);
        var viewModel = new GraphViewModel();
        viewModel.Load(graph);

        viewModel.Apply(Event(1, "a", "b", VitrineEventDisposition.Neutral));
        var firstRouteRevision = viewModel.GraphRevision;
        viewModel.Apply(Event(2, "a", "c", VitrineEventDisposition.Neutral));

        Assert.True(viewModel.GraphRevision > firstRouteRevision);
        Assert.False(viewModel.Edges.Single(edge => edge.Id == "ab").IsActive);
        Assert.True(viewModel.Edges.Single(edge => edge.Id == "ac").IsActive);
    }

    [Theory]
    [InlineData(true, GraphNodeState.Succeeded)]
    [InlineData(false, GraphNodeState.Failed)]
    [InlineData(null, GraphNodeState.NotMeasured)]
    public void EvaluationSuiteTerminalEventLeavesNoActiveNode(bool? passed, GraphNodeState expected)
    {
        var store = new VitrineEventStore();
        var graph = new GraphViewModel();
        graph.Load(VitrineGraphFactory.ForEvaluationSuite());
        graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteStarted, "suite", "suite", "started"))));
        graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteCompleted, "suite", "suite", "finished", Passed: passed))));

        Assert.Equal(expected, graph.StateOf("suite"));
    }

    [Fact]
    public void NotMeasuredLiveSessionLeavesNoNodeLookingSucceeded()
    {
        var store = new VitrineEventStore();
        var graph = new GraphViewModel();
        graph.Load(VitrineGraphFactory.ForRunningLiveEvaluation(
            VitrineEvaluationPlan.LiveEval01Agent));
        graph.Apply(store.Append(VitrineEventAdapters.FromLiveEvaluation(new(
            VitrineEvaluationPlan.LiveEval01Agent,
            LiveEvalProgressPhase.SessionStarting,
            null, null, null, null, null, null,
            "Starting paid evaluation."))));
        graph.Apply(store.Append(VitrineEventAdapters.FromLiveEvaluation(new(
            VitrineEvaluationPlan.LiveEval01Agent,
            LiveEvalProgressPhase.SessionCompleted,
            null, null, null, null,
            Measurement: AgentEval.Evals.Meta.MeasurementState.NotMeasured,
            Passed: null,
            Detail: "Subject or judge measurement was unavailable."))));

        Assert.Equal(GraphNodeState.NotMeasured, graph.StateOf("live-session"));
        Assert.Equal(GraphNodeState.NotMeasured, graph.StateOf("live-complete"));
        Assert.DoesNotContain(graph.Nodes, static node => node.State == GraphNodeState.Active);
    }

    [Fact]
    public void NotApplicableGateKeepsADistinctLiveEventAndRenderedState()
    {
        var gate = GateResult.NotApplicable("catalogue", null,
            "This gate does not apply to the observed subject.");
        var draft = VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.GateCompleted,
            "catalogue",
            gate.Name,
            gate.Evidence,
            Passed: gate.Passed,
            Gate: gate));

        Assert.Equal(VitrineEventDisposition.NotApplicable, draft.Disposition);

        var store = new VitrineEventStore();
        var item = store.Append(draft);
        var timeline = new TimelineViewModel();
        timeline.Add(item);
        var card = Assert.Single(timeline.Events);
        Assert.Equal("NOT APPLICABLE", card.Disposition);
        Assert.Equal("#9FC5FF", card.DispositionColor);

        var graph = new GraphViewModel();
        graph.Load(VitrineGraphFactory.ForEvaluationSuite());
        graph.Apply(item);
        var node = Assert.Single(graph.Nodes, candidate => candidate.Id == "catalogue");
        Assert.Equal(GraphNodeState.NotApplicable, node.State);
        Assert.Equal("NOT APPLICABLE", node.StateText);
        Assert.Equal("#9FC5FF", node.StateColor);

        var artifact = VitrineArtifactSerializer.Create(new(
            store.RunId,
            new(VitrineRunMode.Evals, Personas.NadiaUserId),
            VitrineGraphFactory.ForEvaluationSuite(),
            store.Snapshot(),
            Evaluation: new SuiteResult([gate], [])));
        var json = VitrineArtifactSerializer.Serialize(artifact);
        var html = VitrineHtmlReport.Render(artifact);
        Assert.Contains("\"disposition\": \"notApplicable\"", json, StringComparison.Ordinal);
        Assert.Contains("GateCompleted · NOT APPLICABLE", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedCatalogueDetectionAndControlDetectionUseExplicitGreenSemantics()
    {
        var catalogue = new GateResult(
            "Catalogue shape contract",
            false,
            0,
            null,
            "Expected products=99; observed products=98; isolated SKU GLX-9999 was removed.");
        var detectedDraft = VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.GateCompleted,
            "catalogue",
            catalogue.Name,
            catalogue.Evidence,
            Passed: false,
            Gate: catalogue,
            Expectation: EvaluationProgressExpectation.CatalogueDefectDetection));
        Assert.Equal(VitrineEventDisposition.ExpectedDefectDetected, detectedDraft.Disposition);
        Assert.Contains("EXPECTED DEFECT DETECTED", detectedDraft.Title, StringComparison.Ordinal);
        Assert.Contains("Expected products=99", detectedDraft.Payload, StringComparison.Ordinal);
        Assert.Contains("observed products=98", detectedDraft.Payload, StringComparison.Ordinal);
        Assert.Contains("FAIL AS EXPECTED", detectedDraft.Payload, StringComparison.Ordinal);

        var store = new VitrineEventStore();
        var graph = new GraphViewModel();
        graph.Load(VitrineGraphFactory.ForEvaluationSuite());
        var detected = store.Append(detectedDraft);
        graph.Apply(detected);
        Assert.Equal(GraphNodeState.ExpectedDefectDetected, graph.StateOf("catalogue"));
        Assert.Equal("EXPECTED DEFECT DETECTED · 99→98",
            graph.Nodes.Single(node => node.Id == "catalogue").StateText);

        var suiteDraft = VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteCompleted,
            "suite",
            "VITRINE evaluation suite",
            "SELF-TEST SUCCEEDED: expected products 99 → observed 98 was detected; underlying exit 1 retained.",
            Passed: false,
            Completed: 6,
            Total: 6,
            Expectation: EvaluationProgressExpectation.CatalogueSelfTestSucceeded));
        Assert.Equal(VitrineEventDisposition.SelfTestSucceeded, suiteDraft.Disposition);
        Assert.Contains("SELF-TEST SUCCEEDED", suiteDraft.Title, StringComparison.Ordinal);
        Assert.Contains("underlying exit 1", suiteDraft.Payload, StringComparison.OrdinalIgnoreCase);
        var suiteEvent = store.Append(suiteDraft);
        graph.Apply(suiteEvent);
        Assert.Equal(GraphNodeState.SelfTestSucceeded, graph.StateOf("suite"));

        var controlDraft = VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.ControlBrokenCompleted,
            "NC-01",
            "Synthetic control",
            "The planted defect was caught.",
            Passed: true));
        Assert.Equal(VitrineEventDisposition.Succeeded, controlDraft.Disposition);
        var timeline = new TimelineViewModel();
        timeline.Add(store.Append(controlDraft));
        var controlCard = Assert.Single(timeline.Events);
        Assert.Equal("DETECTED", controlCard.Disposition);
        Assert.Equal("#63D391", controlCard.DispositionColor);
        Assert.DoesNotContain("BLOCKED", controlCard.Disposition, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticFailureIsRenderedAsANonVerdictBearingFinding()
    {
        var diagnostic = new GateResult(
            "Matched agent/workflow judged quality",
            false,
            0.25,
            null,
            "The matched comparison did not satisfy its diagnostic criteria.")
        {
            Authority = GateAuthority.Diagnostic,
        };
        var draft = VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.GateCompleted,
            "judged",
            diagnostic.Name,
            diagnostic.Evidence,
            Passed: false,
            Gate: diagnostic,
            Authority: GateAuthority.Diagnostic));

        Assert.Equal(VitrineEventDisposition.Warning, draft.Disposition);
        Assert.Contains("DIAGNOSTIC FINDING · no exit authority", draft.Title,
            StringComparison.Ordinal);
        Assert.Contains("Authority: Diagnostic", draft.Payload, StringComparison.Ordinal);
        Assert.Contains("Exit effect: none", draft.Payload, StringComparison.Ordinal);
        Assert.Contains("OBSERVED FAIL · diagnostic only", draft.Payload,
            StringComparison.Ordinal);

        var board = new EvaluationBoardViewModel();
        board.Apply(new(EvaluationProgressKind.GateCompleted, "judged", diagnostic.Name,
            diagnostic.Evidence, Passed: false, Gate: diagnostic,
            Authority: GateAuthority.Diagnostic));
        var row = Assert.Single(board.Gates);
        Assert.Equal("DIAGNOSTIC", row.Authority);
        Assert.Equal("#F6C55C", row.StatusColor);
        Assert.Contains("DIAGNOSTIC FINDING · no exit authority", board.ActiveStage,
            StringComparison.Ordinal);

        var graph = new GraphViewModel();
        var store = new VitrineEventStore();
        graph.Load(VitrineGraphFactory.ForRunningEvaluationSuite());
        graph.Apply(store.Append(draft));
        Assert.Equal(GraphNodeState.Warning, graph.StateOf("judged"));

        var suiteCompleted = VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteCompleted,
            "suite",
            "Evaluation suite",
            "All mandatory evaluation gates passed; the diagnostic remains non-authoritative.",
            Passed: true,
            Completed: 6,
            Total: 6));
        graph.Apply(store.Append(suiteCompleted));
        Assert.Equal(GraphNodeState.Warning, graph.StateOf("judged"));
    }

    [Fact]
    public void DiagnosticPassIsGreenButRetainsNoExitAuthority()
    {
        var diagnostic = new GateResult(
            "Matched agent/workflow judged quality",
            true,
            1,
            null,
            "The matched comparison satisfied its diagnostic criteria.")
        {
            Authority = GateAuthority.Diagnostic,
        };
        var progress = new EvaluationProgressEvent(
            EvaluationProgressKind.GateCompleted,
            "judged",
            diagnostic.Name,
            diagnostic.Evidence,
            Passed: true,
            Gate: diagnostic,
            Authority: GateAuthority.Diagnostic);
        var draft = VitrineEventAdapters.FromEvaluation(progress);

        Assert.Equal(VitrineEventDisposition.Succeeded, draft.Disposition);
        Assert.Contains("DIAGNOSTIC OBSERVATION · no exit authority", draft.Title,
            StringComparison.Ordinal);

        var board = new EvaluationBoardViewModel();
        board.Apply(progress);
        var row = Assert.Single(board.Gates);
        Assert.Equal("DIAGNOSTIC", row.Authority);
        Assert.Equal("PASS", row.Status);
        Assert.Equal("#63D391", row.StatusColor);

        var graph = new GraphViewModel();
        var store = new VitrineEventStore();
        graph.Load(VitrineGraphFactory.ForRunningEvaluationSuite());
        graph.Apply(store.Append(draft));
        Assert.Equal(GraphNodeState.Succeeded, graph.StateOf("judged"));

        graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteCompleted,
            "suite",
            "Evaluation suite",
            "All mandatory evaluation gates passed; the diagnostic remains non-authoritative.",
            Passed: true,
            Completed: 6,
            Total: 6))));
        Assert.Equal(GraphNodeState.Succeeded, graph.StateOf("judged"));
    }

    [Fact]
    public void DiagnosticInstrumentErrorIsRenderedAsFailedInfrastructureEvidence()
    {
        var diagnostic = GateResult.InstrumentError(
            "Matched agent/workflow judged quality",
            null,
            typeof(InvalidDataException)) with
        {
            Authority = GateAuthority.Diagnostic,
        };
        var progress = new EvaluationProgressEvent(
            EvaluationProgressKind.GateCompleted,
            "judged",
            diagnostic.Name,
            diagnostic.Evidence,
            Passed: null,
            Gate: diagnostic,
            Authority: GateAuthority.Diagnostic);
        var draft = VitrineEventAdapters.FromEvaluation(progress);

        Assert.Equal(VitrineEventDisposition.Failed, draft.Disposition);
        Assert.Contains("DIAGNOSTIC INSTRUMENT ERROR · no exit authority", draft.Title,
            StringComparison.Ordinal);
        Assert.Contains("Measurement: InstrumentError", draft.Payload,
            StringComparison.Ordinal);
        Assert.Contains("Verdict: INSTRUMENT ERROR", draft.Payload,
            StringComparison.Ordinal);

        var board = new EvaluationBoardViewModel();
        board.Apply(progress);
        var row = Assert.Single(board.Gates);
        Assert.Equal("DIAGNOSTIC", row.Authority);
        Assert.Equal("INSTRUMENT ERROR", row.Status);
        Assert.Equal("#F07076", row.StatusColor);

        var graph = new GraphViewModel();
        var store = new VitrineEventStore();
        graph.Load(VitrineGraphFactory.ForRunningEvaluationSuite());
        graph.Apply(store.Append(draft));
        Assert.Equal(GraphNodeState.Failed, graph.StateOf("judged"));
    }

    [Fact]
    public void WorkflowFailureMapsToAFailedTerminalEvent()
    {
        var draft = VitrineEventAdapters.FromDiscovery(new DiscoveryEvent(
            DiscoveryEventKind.RunFailed, string.Empty, "required operation failed"));

        Assert.Equal(VitrineEventCategory.Workflow, draft.Category);
        Assert.Equal(VitrineEventDisposition.Failed, draft.Disposition);
    }

    [Fact]
    public async Task CoordinatorReservationRejectsRunPreparedReentrancy()
    {
        await using var coordinator = new VitrineRunCoordinator();
        Exception? reentrantFailure = null;
        coordinator.RunPrepared += (_, _) =>
        {
            try
            {
                _ = coordinator.RunAsync(new(VitrineRunMode.Demo01, Personas.NadiaUserId));
            }
            catch (Exception exception)
            {
                reentrantFailure = exception;
            }
        };

        var outcome = await coordinator.RunAsync(new(VitrineRunMode.Demo01, Personas.NadiaUserId));

        Assert.Null(outcome.FailureKind);
        Assert.IsType<InvalidOperationException>(reentrantFailure);
    }

    [Fact]
    public async Task PreparationFailureIsContainedAndCoordinatorCanRunAgain()
    {
        var attempt = 0;
        await using var coordinator = new VitrineRunCoordinator(_ =>
            Interlocked.Increment(ref attempt) == 1
                ? throw new InvalidOperationException("synthetic preparation failure")
                : VitrineGraphFactory.FromRegisteredDemo01Functions());

        var failed = await coordinator.RunAsync(new(VitrineRunMode.Demo01, Personas.NadiaUserId));
        var recovered = await coordinator.RunAsync(new(VitrineRunMode.Demo01, Personas.NadiaUserId));

        Assert.Equal(nameof(InvalidOperationException), failed.FailureKind);
        Assert.Contains(failed.Events, item => item.Kind == "RunFailed");
        Assert.Null(recovered.FailureKind);
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public void PausedTimelinePreservesTheOperatorsSelection()
    {
        var timeline = new TimelineViewModel { SelectedFilter = TimelineFilter.Debug };
        timeline.Add(Event(1, "a", "b", VitrineEventDisposition.Active));
        var selected = timeline.SelectedEvent;
        timeline.FollowEvents = false;

        timeline.Add(Event(2, "b", "c", VitrineEventDisposition.Succeeded));

        Assert.Same(selected, timeline.SelectedEvent);
        Assert.Equal(2, timeline.VisibleCount);
    }

    [Fact]
    public void EvaluationStatusColorsDoNotRenderFailureOrAbsenceAsGreen()
    {
        var failed = new EvaluationBoardViewModel();
        failed.Load(new SuiteResult([new GateResult("failed", false, 0.2, null, "evidence")], []));
        Assert.Equal("#F07076", failed.OverallStatusColor);
        Assert.Equal("#F07076", Assert.Single(failed.Gates).StatusColor);

        var absent = new EvaluationBoardViewModel();
        absent.Load(new SuiteResult([GateResult.NotMeasured("absent", null, "no observation")], []));
        Assert.Equal("#F6C55C", absent.OverallStatusColor);
        Assert.Equal("#F6C55C", Assert.Single(absent.Gates).StatusColor);

        var instrumentFailure = new ControlResult("C-X", "instrument", "test", false, false, "failed")
        {
            BrokenOutcome = ControlAttemptOutcome.InstrumentError,
            RestoredOutcome = ControlAttemptOutcome.NotMeasured,
        };
        var infrastructure = new EvaluationBoardViewModel();
        infrastructure.Load(new SuiteResult([], [instrumentFailure]));
        Assert.Equal("INFRASTRUCTURE FAILURE · exit 4", infrastructure.OverallStatus);
        Assert.Equal("#F07076", infrastructure.OverallStatusColor);
        Assert.Equal("INSTRUMENT ERROR", Assert.Single(infrastructure.Controls).Broken);
        Assert.Equal("NOT MEASURED", Assert.Single(infrastructure.Controls).Restored);
    }

    [AvaloniaFact]
    public async Task RunArmLabelsAndAvailabilityMatchTheSelectedExecutionPath()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.SelectedMode = VitrineRunMode.Demo02;
        viewModel.Setup.SelectedArm = viewModel.Setup.Arms.Single(arm => arm.Arm == RecommendationExecutionArm.ScriptedAgent);

        Assert.Contains("MOCKED", viewModel.RunButtonText, StringComparison.Ordinal);
        Assert.True(viewModel.RunCommand.CanExecute(null));

        viewModel.Setup.SelectedArm = viewModel.Setup.Arms.Single(arm => arm.Arm == RecommendationExecutionArm.LiveAzure);
        Assert.Contains("LIVE", viewModel.RunButtonText, StringComparison.Ordinal);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        viewModel.Setup.PaidExecutionAcknowledged = true;
        Assert.Equal(Config.IsConfigured, viewModel.RunCommand.CanExecute(null));

        viewModel.SelectedMode = VitrineRunMode.Demo01;
        viewModel.Setup.SelectedArm = viewModel.Setup.Arms.Single(arm => arm.Arm == RecommendationExecutionArm.ScriptedAgent);
        Assert.Equal(Personas.NadiaUserId, viewModel.Setup.SelectedPersona.Id);
        Assert.Contains("MOCKED", viewModel.RunButtonText, StringComparison.Ordinal);
        Assert.True(viewModel.RunCommand.CanExecute(null));
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task CompletedExecutionIsImmediatelyUsableAndPacedPresentationCannotContaminateTheNextRun()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Setup.AudiencePacingMilliseconds = 1000;

        await viewModel.RunSelectedModeAsync();
        Dispatcher.UIThread.RunJobs();
        var firstRunId = Assert.IsType<VitrineRunOutcome>(viewModel.LastOutcome).RunId;
        Assert.False(viewModel.IsRunning);
        Assert.True(viewModel.IsPresentationActive);
        Assert.NotNull(viewModel.CurrentArtifact);
        Assert.False(viewModel.CancelCommand.CanExecute(null));
        Assert.True(viewModel.ExportJsonCommand.CanExecute(null));
        Assert.Contains("presenting recorded events", viewModel.Status, StringComparison.Ordinal);

        Assert.True(viewModel.ClearCommand.CanExecute(null));
        viewModel.ClearCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(viewModel.Timeline.Events);
        Assert.False(viewModel.IsPresentationActive);

        viewModel.Setup.AudiencePacingMilliseconds = 0;
        await viewModel.RunSelectedModeAsync();
        await viewModel.DrainPresentationAsync();
        Dispatcher.UIThread.RunJobs();
        var secondRunId = Assert.IsType<VitrineRunOutcome>(viewModel.LastOutcome).RunId;
        Assert.NotEqual(firstRunId, secondRunId);
        Assert.False(viewModel.IsPresentationActive);
        Assert.NotEmpty(viewModel.Timeline.Events);
        Assert.All(viewModel.Timeline.Events, item => Assert.Equal(secondRunId, item.Event.RunId));
        await viewModel.DisposeAsync();
    }

    private static VitrineEventDraft Draft(string title) => new(
        VitrineEventCategory.System,
        title,
        VitrineEventDisposition.Neutral,
        "source",
        "target",
        title,
        title);

    private static VitrineEvent Event(
        long sequence,
        string source,
        string target,
        VitrineEventDisposition disposition) => new(
            Guid.Empty,
            sequence,
            DateTimeOffset.UnixEpoch,
            TimeSpan.Zero,
            VitrineEventCategory.Workflow,
            "Route",
            disposition,
            source,
            target,
            null,
            "route",
            string.Empty,
            null);
}
