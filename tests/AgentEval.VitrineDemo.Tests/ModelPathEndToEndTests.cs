// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Tools;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.VitrineDemo.Tests;

public sealed class ModelPathEndToEndTests
{
    [Fact]
    [Trait("Category", "MockedE2E")]
    public async Task Demo01RunsActualAgentToolsAndScreeningWithMockedModelBoundary()
    {
        using var client = OfflineRecommendationScript.Create(Personas.NadiaUserId);
        var firstObserver = new RecordingRecommendationRuntimeEventSink();
        var secondObserver = new RecordingRecommendationRuntimeEventSink();
        var observers = new CompositeRecommendationRuntimeEventSink(firstObserver, secondObserver);

        var result = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(
                Personas.NadiaUserId,
                Arm: RecommendationExecutionArm.ScriptedAgent,
                ChatClient: client),
            observers);

        AssertAgentContract(result);
        Assert.Equal(client.CallCount, result.ModelCalls);
        Assert.Equal(6, result.ModelCalls);
        Assert.Equal(4, result.ToolCallsUsed);
        Assert.Equal(13, result.RegisteredToolNames.Count);
        Assert.Equal(15,
            RecommendationAgentFactory.BuildReadOnlyTools().Length
            + RecommendationAgentFactory.BuildApprovalGatedCommitTools().Length);

        var finalConversation = client.ReceivedMessages[^1];
        var searchResult = finalConversation
            .SelectMany(static message => message.Contents)
            .OfType<FunctionResultContent>()
            .Last(content => string.Equals(content.CallId, "search", StringComparison.Ordinal));
        Assert.Contains("GLX-1003", searchResult.Result?.ToString(), StringComparison.Ordinal);

        Assert.Equal(firstObserver.Events.Count, secondObserver.Events.Count);
        Assert.Contains(result.Events,
            static item => item.Kind == RecommendationRuntimeEventKind.RunCompleted);
    }

    [Fact]
    [Trait("Category", "MockedE2E")]
    public async Task FullFifteenToolConfigurationIsRegisteredOnAnActualChatClientAgent()
    {
        using var client = new DeterministicChatClient().AddText("No commit operation requested.");
        var agent = RecommendationAgentFactory.CreateWithCommitTools(client);
        var session = await agent.CreateSessionAsync();

        _ = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "Describe the available advisory path only.")],
            session);

        var effectiveOptions = Assert.Single(client.ReceivedOptions);
        Assert.NotNull(effectiveOptions);
        var tools = effectiveOptions!.Tools;
        Assert.NotNull(tools);
        Assert.Equal(15, tools.Count);
        Assert.Equal(2, tools.Count(static tool => tool is ApprovalRequiredAIFunction));
    }

    [Fact]
    [Trait("Category", "MockedE2E")]
    public async Task Demo02RunsActualFiveExecutorWorkflowAndConditionalLoopWithMockedModelBoundary()
    {
        using var client = new DeterministicDiscoveryChatClient();
        var progress = new RecordingDiscoveryProgressSink();

        var result = await GalaxusDiscoveryLoop.RunAsync(
            Personas.MarcoUserId,
            new DiscoveryLoopOptions(
                Offline: false,
                ChatClient: client,
                MaxRounds: 3,
                Progress: progress));

        AssertWorkflowContract(result);
        Assert.Equal(client.CallCount, result.State.ModelCalls);
        Assert.Equal(1, client.StageCalls[DeterministicDiscoveryStage.InterestMapper]);
        Assert.Equal(2, client.StageCalls[DeterministicDiscoveryStage.CoverageReviewer]);
        Assert.Equal(1, client.StageCalls[DeterministicDiscoveryStage.Ranker]);
        Assert.Equal(1, client.StageCalls[DeterministicDiscoveryStage.Presenter]);

        var completedNodes = progress.Events
            .Where(static item => item.Kind == DiscoveryEventKind.NodeCompleted)
            .Select(static item => item.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Subset(completedNodes, DiscoveryExecutorIds.All.ToHashSet(StringComparer.Ordinal));
        Assert.Contains(progress.Events, static item => item.Kind == DiscoveryEventKind.RunComplete);
        Assert.DoesNotContain(progress.Events, static item => item.Kind == DiscoveryEventKind.RunFailed);
    }

    [Fact]
    public async Task RecommendationResultIsSerializableAndDoesNotRetainInjectedChatClient()
    {
        using var client = OfflineRecommendationScript.Create(Personas.NadiaUserId);
        var result = await RecommendationRunEngine.RunAsync(new RecommendationRunOptions(
            Personas.NadiaUserId,
            ChatClient: client));

        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("ChatClient", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(DeterministicChatClient), json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(RecommendationRunResult).GetProperties(),
            property => typeof(IChatClient).IsAssignableFrom(property.PropertyType));
    }

    [Fact]
    public async Task PreCancelledRunStopsBeforeProfileModelOrToolWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(Personas.LucaUserId),
            cancellationToken: cancellation.Token);

        Assert.Equal(RecommendationRunStatus.Cancelled, result.Status);
        Assert.Null(result.Profile);
        Assert.Null(result.ModelCalls);
        Assert.Null(result.ToolCallsUsed);
        Assert.DoesNotContain(result.Events, static item =>
            item.Kind is RecommendationRuntimeEventKind.ModelRequestStarted
                or RecommendationRuntimeEventKind.ToolExecutionStarted);
    }

    [LiveModelFact]
    [Trait("Category", "LiveModel")]
    public async Task Demo01LiveModelSatisfiesTheSameAgentContract()
    {
        RequireLiveCredentialsAfterExplicitOptIn();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var result = await RecommendationRunEngine.RunAsync(new RecommendationRunOptions(
            Personas.NadiaUserId,
            Arm: RecommendationExecutionArm.LiveAzure),
            cancellationToken: timeout.Token);

        AssertAgentContract(result);
    }

    [LiveModelFact]
    [Trait("Category", "LiveModel")]
    public async Task Demo02LiveModelSatisfiesTheSameWorkflowContract()
    {
        RequireLiveCredentialsAfterExplicitOptIn();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var result = await GalaxusDiscoveryLoop.RunAsync(
            Personas.MarcoUserId,
            new DiscoveryLoopOptions(Offline: false, MaxRounds: 3),
            timeout.Token);

        AssertWorkflowContract(result);
    }

    private static void AssertAgentContract(RecommendationRunResult result)
    {
        Assert.Equal(RecommendationRunStatus.Completed, result.Status);
        Assert.Null(result.FailureKind);
        Assert.True(result.ModelCalls > 0);
        Assert.True(result.ToolCallsUsed > 0);
        Assert.NotNull(result.Outcome);
        Assert.NotEmpty(result.Presented);
        Assert.NotEmpty(result.Outcome.Cleaned.AllPresented);

        var delivered = result.Outcome.Cleaned.AllPresented
            .Select(static item => item.ProductId)
            .ToHashSet(StringComparer.Ordinal);
        var rawPresented = result.Presented
            .Select(static item => item.Sku)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(delivered, sku => Assert.Contains(sku, rawPresented));
        Assert.All(delivered, sku => Assert.Contains(sku, result.Outcome.VerifiedPrices.Keys));
    }

    private static void AssertWorkflowContract(DiscoveryRunResult result)
    {
        Assert.False(result.Failed,
            $"Workflow failures: {string.Join(", ", result.ExecutorFailures)}");
        Assert.Equal(5, result.ExecutorIds.Count);
        Assert.Equal(DiscoveryExecutorIds.All, result.ExecutorIds);
        Assert.True(result.Looped);
        Assert.Contains(DiscoveryRouteIds.ReviewToMoreDiscovery, result.RoutesTaken);
        Assert.True(result.State.ModelCalls > 0);
        Assert.False(result.State.SelectionWasDeterministic);
        Assert.Empty(result.State.DegradedNotes);
        Assert.NotNull(result.State.Screened);
        Assert.NotEmpty(result.State.Presented);
        Assert.False(string.IsNullOrWhiteSpace(result.State.PresenterDraft));
        Assert.False(string.IsNullOrWhiteSpace(result.State.FinalAnswer));
        Assert.DoesNotContain(result.State.PresenterDraft!, result.State.FinalAnswer, StringComparison.Ordinal);
        Assert.All(result.State.Presented,
            item => Assert.Contains(item.ProductId, result.State.FinalAnswer, StringComparison.Ordinal));
    }

    private static void RequireLiveCredentialsAfterExplicitOptIn()
    {
        Assert.True(Config.IsConfigured,
            "VITRINE_RUN_LIVE_MODEL_TESTS=1 was set, but AZURE_OPENAI_ENDPOINT and "
            + "AZURE_OPENAI_API_KEY are required. Values are intentionally never reported.");
    }
}
