// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Extensions.AI;

namespace AgentEval.VitrineDemo.Tests;

public sealed class ControlRoomUxTests
{
    [AvaloniaFact]
    public async Task ModeSelectionShowsHonestTopologyBeforeAnyRun()
    {
        var viewModel = new MainWindowViewModel();

        Assert.True(viewModel.Graph.IsPreview);
        Assert.Equal(VitrineGraphSource.RegisteredFunctionsPreview, viewModel.Graph.Snapshot.Source);
        Assert.Equal(13, viewModel.Graph.Nodes.Count(node => node.Kind == "tool"));
        Assert.Empty(viewModel.Timeline.Events);

        viewModel.SelectedMode = VitrineRunMode.Demo02;

        Assert.True(viewModel.Graph.IsPreview);
        Assert.True(viewModel.Graph.ShowActionShelf);
        Assert.Equal(VitrineGraphSource.DiscoveryTopologyPreview, viewModel.Graph.Snapshot.Source);
        Assert.Equal(DiscoveryTopology.Shipped.Nodes.Count, viewModel.Graph.Nodes.Count);
        Assert.Equal(DiscoveryTopology.Shipped.Routes.Count, viewModel.Graph.Edges.Count);
        Assert.Single(viewModel.Graph.Edges, static edge => edge.IsLoopBack);
        Assert.Contains("no execution", viewModel.ResultSummary, StringComparison.OrdinalIgnoreCase);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public void CorrelatedOperationShowsRequestAndResponseWithSemanticLabels()
    {
        var timeline = new TimelineViewModel();
        timeline.Add(Event(1, "ModelRequestStarted", VitrineEventDisposition.Active,
            "Robin", "model", "model-op", "[User]\nFind a camera"));
        var started = timeline.Events[0];
        Assert.Equal("STARTED", started.Disposition);
        timeline.Add(Event(2, "ModelResponseReceived", VitrineEventDisposition.Succeeded,
            "model", "Robin", "model-op", "[Assistant]\ntool call SearchProductsByMeaning"));

        var completed = timeline.Events[1];
        Assert.Equal("DONE", started.Disposition);
        Assert.Equal("DONE", completed.Disposition);
        Assert.DoesNotContain(timeline.Events, static item => item.Disposition == "RUNNING");
        Assert.Equal("operation model-op", completed.OperationId);
        Assert.Equal(["MODEL INPUT", "MODEL OUTPUT"],
            completed.PayloadSections.Select(static section => section.Label));
        Assert.Equal(2, started.PayloadSections.Count);

        completed.ToggleExpandedCommand.Execute(null);
        Assert.True(completed.IsExpanded);
        Assert.Contains("REQUEST + RESPONSE", completed.ExpandControlText, StringComparison.Ordinal);

        var transition = new EventCardViewModel(Event(3, "RunCompleted", VitrineEventDisposition.Succeeded,
            "agent", "customer", "complete", null));
        Assert.True(transition.HasNoExpandableContent);
        Assert.Contains("typed state transition", transition.PayloadAbsenceExplanation, StringComparison.Ordinal);

        timeline.Add(Event(4, "ToolExecutionStarted", VitrineEventDisposition.Active,
            "agent", "tool", "tool-op", "{\"sku\":\"GLX-1001\"}"));
        timeline.Add(Event(5, "ToolFailed", VitrineEventDisposition.Failed,
            "tool", "agent", "tool-op", null));
        var failedTool = timeline.Events[^1];
        Assert.True(failedTool.HasExpandableContent);
        Assert.True(failedTool.HasPayloadAbsenceExplanation);
        Assert.Contains("No response payload was produced", failedTool.PayloadAbsenceExplanation, StringComparison.Ordinal);

        var emptyResponse = VitrineEventAdapters.FromDiscovery(DiscoveryEvent.ModelResponseReceived(
            "node", "agent", "", "empty-model-op"));
        Assert.Contains("[no observable text or function content]", emptyResponse.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalEventUsesOperationCorrelationAndReplayReconstructsCounts()
    {
        var snapshot = new VitrineGraphSnapshot(
            "operation correlation",
            VitrineGraphSource.RegisteredFunctions,
            [new("agent", "Agent", "agent"), new("model", "Model", "model")],
            [new("agent-model", "agent", "model", "model turn")]);
        var events = new[]
        {
            Event(1, "ModelRequestStarted", VitrineEventDisposition.Active,
                "agent", "model", "op-1", "request"),
            // Deliberately omit useful terminal endpoints. OperationId remains authoritative.
            Event(2, "ModelResponseReceived", VitrineEventDisposition.Succeeded,
                "unknown", "unknown", "op-1", "response"),
        };
        var graph = new GraphViewModel();

        graph.Load(snapshot);
        foreach (var item in events) graph.Apply(item);

        var model = graph.Nodes.Single(node => node.Id == "model");
        Assert.Equal(GraphNodeState.Succeeded, model.State);
        Assert.Equal("DONE", model.StateText);
        Assert.Equal(1, model.ExecutionCount);
        Assert.Equal(1, Assert.Single(graph.Edges).TraversalCount);

        graph.Load(snapshot);
        foreach (var item in events) graph.Apply(item);
        Assert.Equal(1, graph.Nodes.Single(node => node.Id == "model").ExecutionCount);
        Assert.Equal(1, Assert.Single(graph.Edges).TraversalCount);
    }

    [Fact]
    public void TypedDiscoveryEventsDriveExecutorModelSearchAndBackRouteObservations()
    {
        const string operationId = "executor-op";
        var store = new VitrineEventStore(Guid.Empty);
        var graph = new GraphViewModel();
        graph.Load(VitrineGraphFactory.FromDiscoveryTopologyContract());
        var events = new[]
        {
            DiscoveryEvent.NodeStarted(DiscoveryExecutorIds.Discovery, operationId: operationId),
            new DiscoveryEvent(DiscoveryEventKind.Search, DiscoveryExecutorIds.Discovery,
                "Search(\"camera\") → 3"),
            DiscoveryEvent.NodeCompleted(DiscoveryExecutorIds.Discovery, 2,
                TimeSpan.FromMilliseconds(5), operationId),
            DiscoveryEvent.Route(DiscoveryRouteIds.ReviewToMoreDiscovery, "gaps remain"),
        };

        foreach (var item in events)
            graph.Apply(store.Append(VitrineEventAdapters.FromDiscovery(item)));

        var discovery = graph.Nodes.Single(node => node.Id == DiscoveryExecutorIds.Discovery);
        Assert.Equal(GraphNodeState.Succeeded, discovery.State);
        Assert.Equal(1, discovery.ExecutionCount);
        Assert.Equal(1, graph.Actions.Single(action => action.Kind == "EXECUTOR").Count);
        Assert.Equal(1, graph.Actions.Single(action => action.Kind == "RETRIEVAL").Count);
        Assert.Equal(2, graph.Actions.Single(action => action.Kind == "MODEL").Count);
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == DiscoveryRouteIds.ReviewToMoreDiscovery).TraversalCount);
    }

    [Fact]
    public void RejectLoopApproveCountsDiscoveryAndCoverageReviewerExactlyTwice()
    {
        var store = new VitrineEventStore(Guid.Empty);
        var graph = new GraphViewModel();
        graph.Load(VitrineGraphFactory.FromDiscoveryTopologyContract());
        DiscoveryEvent[] events =
        [
            DiscoveryEvent.NodeStarted(DiscoveryExecutorIds.Discovery, operationId: "discovery-1"),
            DiscoveryEvent.RoundStarted(1, 2),
            DiscoveryEvent.NodeCompleted(DiscoveryExecutorIds.Discovery, 0, TimeSpan.Zero, "discovery-1"),
            DiscoveryEvent.NodeStarted(DiscoveryExecutorIds.CoverageReviewer, operationId: "review-1"),
            DiscoveryEvent.NodeCompleted(DiscoveryExecutorIds.CoverageReviewer, 1, TimeSpan.Zero, "review-1"),
            DiscoveryEvent.Route(DiscoveryRouteIds.ReviewToMoreDiscovery, "coverage rejected; discover again"),
            DiscoveryEvent.NodeStarted(DiscoveryExecutorIds.Discovery, operationId: "discovery-2"),
            DiscoveryEvent.RoundStarted(2, 2),
            DiscoveryEvent.NodeCompleted(DiscoveryExecutorIds.Discovery, 0, TimeSpan.Zero, "discovery-2"),
            DiscoveryEvent.NodeStarted(DiscoveryExecutorIds.CoverageReviewer, operationId: "review-2"),
            DiscoveryEvent.NodeCompleted(DiscoveryExecutorIds.CoverageReviewer, 1, TimeSpan.Zero, "review-2"),
            DiscoveryEvent.Route(DiscoveryRouteIds.ReviewToRanker, "coverage approved"),
        ];

        foreach (var item in events)
            graph.Apply(store.Append(VitrineEventAdapters.FromDiscovery(item)));

        Assert.Equal(2, graph.Nodes.Single(node => node.Id == DiscoveryExecutorIds.Discovery).ExecutionCount);
        Assert.Equal(2, graph.Nodes.Single(node => node.Id == DiscoveryExecutorIds.CoverageReviewer).ExecutionCount);
        Assert.Equal(2, graph.Actions.Single(action => action.Id == $"executor:{DiscoveryExecutorIds.Discovery}").Count);
        Assert.Equal(2, graph.Actions.Single(action => action.Id == $"executor:{DiscoveryExecutorIds.CoverageReviewer}").Count);
    }

    [AvaloniaFact]
    public async Task SelectedPersonaShowsItsExactAuthoredScenarioBeforeRun()
    {
        var viewModel = new MainWindowViewModel();
        var marco = viewModel.Setup.Personas.Single(option => option.Id == Personas.MarcoUserId);
        viewModel.Setup.SelectedPersona = marco;

        Assert.Equal(Personas.CanonicalPromptFor(Personas.MarcoUserId), viewModel.Setup.ScenarioQuery);
        Assert.Equal(PersonaScenarios.Require(Personas.MarcoUserId).Title, viewModel.Setup.ScenarioTitle);
        Assert.Contains("gift", viewModel.Setup.ScenarioDescription, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            PersonaScenarios.Require(Personas.NadiaUserId).Description,
            viewModel.Setup.ScenarioDescription);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ObservedModelPayloadContainsVisibleMessagesToolArgumentsAndPriorToolResults()
    {
        using var inner = new DeterministicChatClient()
            .AddToolCall("next-call", "SearchProductsByMeaning",
                new Dictionary<string, object?> { ["query"] = "mirrorless camera" });
        var events = new RecordingRecommendationRuntimeEventSink();
        using var observed = new ObservedChatClient(inner, events);
        ChatMessage[] messages =
        [
            new(ChatRole.User, "Find a camera"),
            new(ChatRole.Tool, [new FunctionResultContent("prior-call", "three safe candidates")]),
        ];

        _ = await observed.GetResponseAsync(messages);

        var request = events.Events.Single(item =>
            item.Kind == RecommendationRuntimeEventKind.ModelRequestStarted);
        var response = events.Events.Single(item =>
            item.Kind == RecommendationRuntimeEventKind.ModelResponseReceived);
        Assert.Equal(request.OperationId, response.OperationId);
        Assert.Contains("Find a camera", request.PayloadPreview, StringComparison.Ordinal);
        Assert.Contains("three safe candidates", request.PayloadPreview, StringComparison.Ordinal);
        Assert.Contains("SearchProductsByMeaning", response.PayloadPreview, StringComparison.Ordinal);
        Assert.Contains("mirrorless camera", response.PayloadPreview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Demo02PublishesOneSanitizedRequestAndTerminalResponsePerModelCall()
    {
        var progress = new RecordingDiscoveryProgressSink();
        using var client = new DeterministicDiscoveryChatClient();

        var run = await GalaxusDiscoveryLoop.RunAsync(
            Personas.MarcoUserId,
            new DiscoveryLoopOptions(Offline: false, ChatClient: client, Progress: progress, MaxRounds: 3));

        var started = progress.Events
            .Where(static item => item.Kind == DiscoveryEventKind.ModelRequestStarted)
            .ToArray();
        var terminal = progress.Events
            .Where(static item => item.Kind is DiscoveryEventKind.ModelResponseReceived
                or DiscoveryEventKind.ModelRequestCancelled or DiscoveryEventKind.ModelRequestFailed)
            .ToArray();
        Assert.Equal(run.State.ModelCalls, started.Length);
        Assert.Equal(started.Length, terminal.Length);
        Assert.Equal(started.Length, started.Select(static item => item.OperationId).Distinct().Count());
        Assert.All(started, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.OperationId));
            Assert.Contains("USER INPUT", Assert.Single(item.Detail!), StringComparison.Ordinal);
            Assert.Single(terminal, candidate => candidate.OperationId == item.OperationId);
        });
        Assert.All(terminal.Where(static item => item.Kind == DiscoveryEventKind.ModelResponseReceived),
            item => Assert.False(string.IsNullOrWhiteSpace(Assert.Single(item.Detail!))));
    }

    [Fact]
    public void SafePreviewRedactsBareConfiguredValuesBeforeTheyEnterAnEvent()
    {
        // Scrub EVERY provider variable: an ambient key for any host would otherwise
        // auto-detect into this test and change what it is asserting on.
        using var providerEnvironment = new ProviderEnvironmentScope();
        const string key = "bare-configured-value-that-is-not-a-token-pattern";
        const string endpoint = "https://private-resource.example.invalid";
        var previousKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var previousEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", key);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", endpoint);

            var safe = RecommendationRuntimeEvents.SafePreview(
                $"response enclosed [{key}] and [{endpoint}/models]");

            Assert.DoesNotContain(key, safe, StringComparison.Ordinal);
            Assert.DoesNotContain(endpoint, safe, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED_KEY]", safe, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", previousEndpoint);
        }
    }

    [AvaloniaFact]
    public async Task PositivePersonalizationAndDisabledActionsAreVisuallyUnambiguous()
    {
        var window = new MainWindow { Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
        viewModel.SelectedMode = VitrineRunMode.Demo02;
        Dispatcher.UIThread.RunJobs();
        var personalization = window.GetVisualDescendants().OfType<ToggleSwitch>()
            .Single(control => AutomationProperties.GetName(control) == "Enable personalization");
        var maxRounds = window.GetVisualDescendants().OfType<NumericUpDown>()
            .Single(control => AutomationProperties.GetName(control) == "Maximum workflow rounds");
        var audiencePace = window.GetVisualDescendants().OfType<NumericUpDown>()
            .Single(control => AutomationProperties.GetName(control) == "Audience pacing milliseconds");
        var cancel = window.GetVisualDescendants().OfType<Button>()
            .Single(control => AutomationProperties.GetName(control) == "Cancel active run");

        Assert.True(viewModel.Setup.PersonalizationEnabled);
        Assert.True(personalization.IsChecked);
        Assert.Equal("enabled", personalization.OnContent);
        Assert.True(maxRounds.Width >= audiencePace.Width);
        Assert.Equal(650, ToolTip.GetShowDelay(personalization));
        Assert.False(cancel.IsEnabled);
        Assert.True(cancel.Opacity < 0.5);
        Assert.True(ToolTip.GetShowOnDisabled(cancel));
        Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(cancel)?.ToString()));

        window.Hide();
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task CompletedRunOpensScreenedOutcomeAndLeavesNoRunningGraphNode()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Setup.AudiencePacingMilliseconds = 0;

        await viewModel.RunSelectedModeAsync();
        await viewModel.DrainPresentationAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.IsOutcomeExpanded);
        Assert.Contains("SCREENED", viewModel.OutcomeTitle, StringComparison.Ordinal);
        Assert.DoesNotContain(viewModel.Graph.Nodes,
            static node => node.State == GraphNodeState.Active || node.StateText == "RUNNING");
        Assert.Contains(viewModel.Graph.Nodes,
            static node => node.Kind == "tool" && node.ExecutionCount > 0);
        Assert.Contains(viewModel.Timeline.Events,
            static card => card.PayloadSections.Count > 1
                && card.PayloadSections.Any(section => section.Label == "TOOL PARAMETERS")
                && card.PayloadSections.Any(section => section.Label == "TOOL RESPONSE"));
        await viewModel.DisposeAsync();
    }

    private static VitrineEvent Event(
        long sequence,
        string kind,
        VitrineEventDisposition disposition,
        string source,
        string target,
        string? operationId,
        string? payload) => new(
            Guid.Empty,
            sequence,
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMilliseconds(sequence),
            VitrineEventCategory.Model,
            kind,
            disposition,
            source,
            target,
            operationId,
            kind,
            "observable detail",
            payload);
}
