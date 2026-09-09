// SPDX-License-Identifier: MIT

using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class LifecycleCorrelationTests
{
    [Theory]
    [InlineData(RecommendationRuntimeEventKind.RunCompleted, VitrineEventDisposition.Succeeded)]
    [InlineData(RecommendationRuntimeEventKind.RunCancelled, VitrineEventDisposition.Warning)]
    [InlineData(RecommendationRuntimeEventKind.RunFailed, VitrineEventDisposition.Failed)]
    public void Demo01RunTerminalClosesTheStableRunOperation(
        RecommendationRuntimeEventKind terminalKind,
        VitrineEventDisposition terminalDisposition)
    {
        var timeline = new TimelineViewModel();
        var store = new VitrineEventStore();
        Add(timeline, store, VitrineEventAdapters.FromRecommendation(new(
            RecommendationRuntimeEventKind.RunStarted,
            "customer", "Robin", "run started", "start")));
        Add(timeline, store, VitrineEventAdapters.FromRecommendation(new(
            terminalKind,
            "Robin", "customer", "run terminal", "terminal")));

        Assert.All(timeline.Events, card => Assert.Equal("operation demo01:run", card.OperationId));
        Assert.All(timeline.Events, card => Assert.Equal(terminalDisposition, card.Event.Kind == "RunStarted"
            ? EffectiveDisposition(card)
            : card.Event.Disposition));
        AssertNoStartedLifecycle(timeline);
    }

    [Fact]
    public void Demo02RunAndTwoRoundsCloseWithStableRoundOperations()
    {
        var timeline = new TimelineViewModel();
        var store = new VitrineEventStore();
        DiscoveryEvent[] events =
        [
            new(DiscoveryEventKind.RunStarted, string.Empty, "workflow started"),
            DiscoveryEvent.RoundStarted(1, 2),
            DiscoveryEvent.RoundComplete(1, 3, 0, 3),
            DiscoveryEvent.RoundStarted(2, 2),
            DiscoveryEvent.RoundComplete(2, 1, 1, 4),
            new(DiscoveryEventKind.RunComplete, string.Empty, "workflow completed"),
        ];

        foreach (var item in events)
            Add(timeline, store, VitrineEventAdapters.FromDiscovery(item));

        var roundStarts = timeline.Events.Where(card => card.Kind == "RoundStarted").ToArray();
        var roundCompletions = timeline.Events.Where(card => card.Kind == "RoundComplete").ToArray();
        Assert.Equal(["operation round:1", "operation round:2"],
            roundStarts.Select(static card => card.OperationId));
        Assert.Equal(["operation round:1", "operation round:2"],
            roundCompletions.Select(static card => card.OperationId));
        Assert.All(roundCompletions, card => Assert.Equal(VitrineEventDisposition.Succeeded,
            card.Event.Disposition));
        Assert.All(roundStarts, card => Assert.Equal("DONE", card.Disposition));
        Assert.Equal("DONE", timeline.Events.Single(card => card.Kind == "RunStarted").Disposition);
        AssertNoStartedLifecycle(timeline);
    }

    [Fact]
    public void OfflineSuiteGateAndControlCloseOnlyAtTheirTypedTerminalEvents()
    {
        var timeline = new TimelineViewModel();
        var store = new VitrineEventStore();
        var gate = new GateResult("Catalogue", true, 1, null, "measured evidence");

        Add(timeline, store, VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteStarted, "suite", "suite", "started", Total: 1)));
        Add(timeline, store, VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.GateStarted, "catalogue", "Catalogue", "started")));
        Add(timeline, store, VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.GateCompleted, "catalogue", "Catalogue", "done",
            Passed: true, Gate: gate, Completed: 1, Total: 1)));
        Add(timeline, store, VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.ControlStarted, "NC-01", "control", "started", Total: 1)));
        var controlStart = timeline.Events.Single(card => card.Kind == "ControlStarted");

        foreach (var kind in new[]
                 {
                     EvaluationProgressKind.ControlHealthyCompleted,
                     EvaluationProgressKind.ControlBrokenCompleted,
                     EvaluationProgressKind.ControlRestoredCompleted,
                 })
        {
            Add(timeline, store, VitrineEventAdapters.FromEvaluation(new(
                kind, "NC-01", "control", "phase evidence", Passed: true, Total: 1)));
            Assert.Equal("STARTED", controlStart.Disposition);
        }

        Add(timeline, store, VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.ControlCompleted, "NC-01", "control", "complete",
            Passed: true, Completed: 1, Total: 1)));
        Add(timeline, store, VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteCompleted, "suite", "suite", "complete",
            Passed: true, Completed: 1, Total: 1, IncludesDiagnosticControls: true)));

        Assert.Equal("operation offline:suite",
            timeline.Events.Single(card => card.Kind == "SuiteStarted").OperationId);
        Assert.Equal("operation offline:gate:catalogue",
            timeline.Events.Single(card => card.Kind == "GateStarted").OperationId);
        Assert.Equal("operation offline:control:NC-01", controlStart.OperationId);
        Assert.Equal("DONE", controlStart.Disposition);
        AssertNoStartedLifecycle(timeline);
    }

    [Fact]
    public void LiveTerminalClosesSessionPersistenceTrialSubjectAndCheckOperations()
    {
        var timeline = new TimelineViewModel();
        var store = new VitrineEventStore();
        const VitrineEvaluationPlan plan = VitrineEvaluationPlan.LiveEval01Agent;
        const string scenario = "nadia-cross-category";
        const string arm = "vitrine-live-agent";
        const int repetition = 1;
        var check = LiveUseCaseBenchmark.UseCaseQualityCheckKey;
        LiveEvalProgress[] progress =
        [
            Progress(LiveEvalProgressPhase.SessionStarting),
            Progress(LiveEvalProgressPhase.TrialStarting, scenario, arm, repetition),
            Progress(LiveEvalProgressPhase.SubjectRunning, scenario, arm, repetition),
            Progress(LiveEvalProgressPhase.SubjectCompleted, scenario, arm, repetition,
                measurement: MeasurementState.Measured, passed: true),
            Progress(LiveEvalProgressPhase.CheckStarting, scenario, arm, repetition, check),
            Progress(LiveEvalProgressPhase.CheckCompleted, scenario, arm, repetition, check,
                MeasurementState.Measured, true),
            Progress(LiveEvalProgressPhase.TrialCompleted, scenario, arm, repetition,
                measurement: MeasurementState.Measured, passed: true),
            Progress(LiveEvalProgressPhase.Persisting),
            Progress(LiveEvalProgressPhase.SessionCompleted,
                measurement: MeasurementState.Measured, passed: true),
        ];

        foreach (var item in progress)
            Add(timeline, store, VitrineEventAdapters.FromLiveEvaluation(item));

        var trialStart = timeline.Events.Single(card => card.Kind == "LiveTrialStarting");
        var subjectStart = timeline.Events.Single(card => card.Kind == "LiveSubjectRunning");
        Assert.NotEqual(trialStart.OperationId, subjectStart.OperationId);
        Assert.StartsWith("operation live:trial:", trialStart.OperationId, StringComparison.Ordinal);
        Assert.StartsWith("operation live:subject:", subjectStart.OperationId, StringComparison.Ordinal);
        Assert.Equal("DONE", timeline.Events.Single(card => card.Kind == "LiveSessionStarting").Disposition);
        Assert.Equal("DONE", timeline.Events.Single(card => card.Kind == "LivePersisting").Disposition);
        AssertNoStartedLifecycle(timeline);

        LiveEvalProgress Progress(
            LiveEvalProgressPhase phase,
            string? scenarioId = null,
            string? armId = null,
            int? rep = null,
            string? checkKey = null,
            MeasurementState? measurement = null,
            bool? passed = null) =>
            new(plan, phase, scenarioId, armId, rep, checkKey, measurement, passed, phase.ToString());
    }

    private static void Add(TimelineViewModel timeline, VitrineEventStore store, VitrineEventDraft draft) =>
        timeline.Add(store.Append(draft));

    private static VitrineEventDisposition EffectiveDisposition(EventCardViewModel card) => card.Disposition switch
    {
        "DONE" => VitrineEventDisposition.Succeeded,
        "WARNING" => VitrineEventDisposition.Warning,
        "FAILED" => VitrineEventDisposition.Failed,
        _ => throw new InvalidOperationException($"Unexpected lifecycle disposition '{card.Disposition}'."),
    };

    private static void AssertNoStartedLifecycle(TimelineViewModel timeline) =>
        Assert.DoesNotContain(timeline.Events, static card => card.Disposition == "STARTED");
}
