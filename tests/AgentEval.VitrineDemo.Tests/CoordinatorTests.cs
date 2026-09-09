// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public void EventStoreIsStrictlySequencedAndObserverFailuresAreIsolated()
    {
        var store = new VitrineEventStore(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        store.EventAppended += _ => throw new InvalidOperationException("presentation failed");

        store.Append(new(VitrineEventCategory.System, "one", VitrineEventDisposition.Active,
            "a", "b", "One", "first"));
        store.Append(new(VitrineEventCategory.System, "two", VitrineEventDisposition.Succeeded,
            "b", "c", "Two", "second"));

        Assert.Equal([1L, 2L], store.Snapshot().Select(item => item.Sequence));
        Assert.All(store.Snapshot(), item => Assert.Equal(store.RunId, item.RunId));
    }

    [Fact]
    public async Task Demo01CoordinatorUsesActualReadOnlyToolSurface()
    {
        await using var coordinator = new VitrineRunCoordinator();

        var outcome = await coordinator.RunAsync(new(
            VitrineRunMode.Demo01,
            Personas.NadiaUserId));

        Assert.Null(outcome.FailureKind);
        Assert.Equal(VitrineGraphSource.RegisteredFunctions, outcome.Graph.Source);
        Assert.Equal(13, outcome.Graph.Nodes.Count(node => node.Kind == "tool"));
        Assert.DoesNotContain(outcome.Graph.Nodes, node => node.Id is "AddToCart" or "PlaceOrder");
        Assert.Equal(Enumerable.Range(1, outcome.Events.Count).Select(value => (long)value),
            outcome.Events.Select(item => item.Sequence));
        Assert.Contains(outcome.Events, item => item.Kind == "ToolExecutionStarted");
    }

    [Fact]
    public async Task Demo02CoordinatorUsesActualMafGraphAndObservesLoopBack()
    {
        await using var coordinator = new VitrineRunCoordinator();

        var outcome = await coordinator.RunAsync(new(
            VitrineRunMode.Demo02,
            Personas.MarcoUserId,
            MaxRounds: 3));

        Assert.Null(outcome.FailureKind);
        Assert.Equal(VitrineGraphSource.MafWorkflow, outcome.Graph.Source);
        Assert.Equal(5, outcome.Graph.Nodes.Count);
        Assert.Equal(5, outcome.Graph.Edges.Count);
        Assert.Single(outcome.Graph.Edges, edge => edge.IsLoopBack);
        var nodeIds = outcome.Graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(outcome.Graph.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        });
        Assert.True(outcome.Workflow?.Looped);
        Assert.Contains(outcome.Events, item => item.OperationId == DiscoveryRouteIds.ReviewToMoreDiscovery);
        Assert.Contains(outcome.Events, item => item.Kind == DiscoveryEventKind.NodeCompleted.ToString()
            && item.Detail.Contains("model calls 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvalCoordinatorStreamsEveryBrokenAndRestoredControlWithoutRescoring()
    {
        await using var coordinator = new VitrineRunCoordinator();

        var outcome = await coordinator.RunAsync(new(
            VitrineRunMode.Evals,
            Personas.NadiaUserId));

        Assert.Equal(0, outcome.ProcessEquivalentExitCode);
        Assert.Equal(VitrineEvalCriteria.NegativeControlCount, outcome.Evaluation?.CaughtControls);
        Assert.Equal(6, outcome.Evaluation!.Gates.Count);
        Assert.Equal(6, outcome.Events.Count(item =>
            item.Kind == EvaluationProgressKind.GateCompleted.ToString()));
        Assert.DoesNotContain(outcome.Events, item =>
            item.Kind == EvaluationProgressKind.GateCompleted.ToString() &&
            string.Equals(item.SourceId, "controls", StringComparison.Ordinal));
        Assert.Equal(43, outcome.Events.Count(item => item.Kind == EvaluationProgressKind.ControlBrokenCompleted.ToString()));
        Assert.Equal(43, outcome.Events.Count(item => item.Kind == EvaluationProgressKind.ControlRestoredCompleted.ToString()));
        var benchmarkChecks = outcome.Events
            .Where(item => item.Kind == EvaluationProgressKind.BenchmarkCheckCompleted.ToString()).ToArray();
        var benchmark = outcome.Events.Single(item => item.Kind == EvaluationProgressKind.BenchmarkPersisted.ToString());
        var firstControl = outcome.Events.First(item => item.Kind == EvaluationProgressKind.ControlStarted.ToString());
        var completed = outcome.Events.Single(item => item.Kind == EvaluationProgressKind.SuiteCompleted.ToString());
        Assert.Equal(VitrineOfflineBenchmark.CheckKeys,
            benchmarkChecks.Select(static item => item.TargetId));
        Assert.True(benchmarkChecks[^1].Sequence < benchmark.Sequence);
        Assert.True(benchmark.Sequence < firstControl.Sequence);
        Assert.True(firstControl.Sequence < completed.Sequence);
        Assert.Contains(outcome.Evaluation!.OfflineBenchmark!.RunId, benchmark.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AblationCoordinatorKeepsExpectedProcessEquivalentFailure()
    {
        await using var coordinator = new VitrineRunCoordinator();

        var outcome = await coordinator.RunAsync(new(
            VitrineRunMode.Ablation,
            Personas.NadiaUserId));

        Assert.Equal(1, outcome.ProcessEquivalentExitCode);
        Assert.Contains(outcome.Evaluation!.Gates, gate => gate.Passed == false);
        Assert.Equal(43, outcome.Evaluation.CaughtControls);
        var catalogue = outcome.Events.Single(item =>
            item.Kind == EvaluationProgressKind.GateCompleted.ToString()
            && string.Equals(item.SourceId, "catalogue", StringComparison.Ordinal));
        Assert.Equal(VitrineEventDisposition.ExpectedDefectDetected, catalogue.Disposition);
        Assert.Contains("EXPECTED DEFECT DETECTED", catalogue.Title, StringComparison.Ordinal);
        Assert.Contains("expected products=99", catalogue.SanitizedPayload, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("observed products=98", catalogue.SanitizedPayload, StringComparison.OrdinalIgnoreCase);

        var suite = outcome.Events.Single(item =>
            item.Kind == EvaluationProgressKind.SuiteCompleted.ToString());
        Assert.Equal(VitrineEventDisposition.SelfTestSucceeded, suite.Disposition);
        Assert.Contains("SELF-TEST SUCCEEDED", suite.Title, StringComparison.Ordinal);
        Assert.Contains("underlying process-equivalent exit 1", suite.SanitizedPayload,
            StringComparison.OrdinalIgnoreCase);

        var graph = new GraphViewModel();
        graph.Load(outcome.Graph);
        foreach (var item in outcome.Events) graph.Apply(item);
        Assert.Equal(GraphNodeState.ExpectedDefectDetected, graph.StateOf("catalogue"));
        Assert.Equal(GraphNodeState.SelfTestSucceeded, graph.StateOf("suite"));
        var artifact = VitrineArtifactSerializer.Create(outcome);
        var (title, _) = VitrineOutcomeInspector.Describe(artifact);
        var html = VitrineHtmlReport.Render(artifact);
        Assert.Equal("self-test-detected", artifact.Result.Status);
        Assert.Contains("expected detection observed", title, StringComparison.Ordinal);
        Assert.Contains("Catalogue integrity self-test", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoordinatorRejectsOverlapAndCancellationUnwindsCurrentRun()
    {
        await using var coordinator = new VitrineRunCoordinator();
        var running = coordinator.RunAsync(new(VitrineRunMode.Demo01, Personas.NadiaUserId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunAsync(new(VitrineRunMode.Demo02, Personas.MarcoUserId)));

        coordinator.Cancel();
        var outcome = await running;
        Assert.True(outcome.Cancelled);
        Assert.False(coordinator.IsRunning);
    }
}
