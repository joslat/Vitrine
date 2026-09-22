// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Runtime.CompilerServices;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Tools;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Extensions.AI;

namespace AgentEval.VitrineDemo.Tests;

public sealed class SubjectHardeningTests
{
    [Fact]
    public void ConfigurationReflectsEnvironmentChangesAfterItsFirstRead()
    {
        // Scrub EVERY provider variable: an ambient key for any host would otherwise
        // auto-detect into this test and change what it is asserting on.
        using var providerEnvironment = new ProviderEnvironmentScope();
        var originalEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var originalKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
            Assert.False(Config.IsConfigured);

            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://configuration-refresh.invalid/");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", "configuration-refresh-secret");
            Assert.True(Config.IsConfigured);

            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
            Assert.False(Config.IsConfigured);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", originalKey);
        }
    }

    [Fact]
    public async Task StructuredToolCancellationInterruptsInFlightWork()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GalaxusTools.GetUserProfile(Personas.NadiaUserId, cancellation.Token));
    }

    [Fact]
    public async Task ModelDeadlineAppliesToStreamingAndNonStreamingCalls()
    {
        using var inner = new NeverCompletingChatClient();
        using var client = new DeadlineChatClient(inner, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<ModelCallTimeoutException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "deadline probe")]));

        await Assert.ThrowsAsync<ModelCallTimeoutException>(async () =>
        {
            await foreach (var _ in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "streaming deadline probe")]))
            {
            }
        });
    }

    [Fact]
    public async Task WholeRunDeadlineStopsAnInFlightRetriever()
    {
        var result = await RecommendationRunEngine.RunAsync(new RecommendationRunOptions(
            Personas.NadiaUserId,
            Arm: RecommendationExecutionArm.ZeroModelBaseline,
            Retriever: new NeverCompletingRetriever(),
            RunTimeout: TimeSpan.FromMilliseconds(30)));

        Assert.Equal(RecommendationRunStatus.Failed, result.Status);
        Assert.Equal("RunTimeout", result.FailureKind);
        Assert.Null(result.ModelCalls);
    }

    [Fact]
    public void WorkflowResultDoesNotRetainTheExecutableWorkflow()
    {
        Assert.DoesNotContain(
            typeof(DiscoveryRunResult).GetProperties(),
            property => property.Name == "Workflow"
                || property.PropertyType.Name.Contains("Workflow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Demo01GraphAndAgentUseTheExactSamePreparedToolSet()
    {
        await using var coordinator = new VitrineRunCoordinator();

        var outcome = await coordinator.RunAsync(new(
            VitrineRunMode.Demo01,
            Personas.NadiaUserId));

        var recommendation = Assert.IsType<RecommendationRunResult>(outcome.Recommendation);
        Assert.NotNull(outcome.Graph.RuntimeSourceId);
        Assert.Equal(recommendation.Options.RegisteredToolSetId, outcome.Graph.RuntimeSourceId);
        Assert.Equal(13, recommendation.RegisteredToolNames.Count);
        Assert.Equal(13, outcome.Graph.Nodes.Count(node => node.Kind == "tool"));
    }

    private sealed class NeverCompletingRetriever : IProductRetriever
    {
        public string Name => "never-completing";
        public bool DenseAvailable => true;
        public int ProductCount => Catalogue.Default.All.Count;

        public async ValueTask<RetrievalResult> SearchAsync(
            RetrievalQuery query,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null!;
        }
    }

    private sealed class NeverCompletingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null!;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
