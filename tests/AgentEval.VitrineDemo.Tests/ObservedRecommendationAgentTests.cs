// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Runtime.CompilerServices;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Tools;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.VitrineDemo.Tests;

public sealed class ObservedRecommendationAgentTests
{
    [Fact]
    public async Task RecommendationEngineDefaultsToOfflineRealAgentPath()
    {
        var progress = new RecordingRecommendationRuntimeEventSink();

        var result = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions("USR-NB-01"), progress);

        Assert.Equal(RecommendationRunStatus.Completed, result.Status);
        Assert.Equal(RecommendationExecutionArm.ScriptedAgent, result.Options.Arm);
        Assert.Equal(13, result.RegisteredToolNames.Count);
        Assert.Equal(6, result.ModelCalls);
        Assert.Equal(4, result.ToolCallsUsed);
        Assert.Single(result.Presented);
        Assert.NotNull(result.Outcome);
        Assert.Contains(result.Events, e => e.Kind == RecommendationRuntimeEventKind.GuardDecision);
        Assert.DoesNotContain(result.Events, e => e.Detail.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OfflineScriptRunsSofiasGroundedGapTrajectoryThroughObservedTools()
    {
        var result = await RecommendationRunEngine.RunAsync(new RecommendationRunOptions(
            Personas.SofiaUserId,
            Arm: RecommendationExecutionArm.ScriptedAgent));

        Assert.Equal(RecommendationRunStatus.Completed, result.Status);
        Assert.Null(result.FailureKind);
        Assert.Equal(6, result.ModelCalls);
        Assert.Equal(4, result.ToolCallsUsed);
        Assert.Equal("GLX-3007", Assert.Single(result.Presented).Sku);
        Assert.Contains(result.Outcome!.Cleaned.AllPresented,
            static item => item.ProductId == "GLX-3007");
        Assert.Equal(5, result.Events.Count(static item =>
            item.Kind == RecommendationRuntimeEventKind.ToolExecutionStarted));
        Assert.Equal(5, result.Events.Count(static item =>
            item.Kind == RecommendationRuntimeEventKind.ToolCompleted));
        Assert.Contains(result.Events, static item =>
            item.Kind == RecommendationRuntimeEventKind.RunCompleted);
    }

    [Fact]
    public async Task BaselineKeepsMeasuredZeroModelCallsSeparateFromMissingToolMeasurement()
    {
        var result = await RecommendationRunEngine.RunAsync(new RecommendationRunOptions(
            "USR-NB-01",
            Arm: RecommendationExecutionArm.ZeroModelBaseline));

        Assert.Equal(RecommendationRunStatus.Completed, result.Status);
        Assert.Equal(0, result.ModelCalls);
        Assert.Null(result.ToolCallsUsed);
        Assert.NotEmpty(result.Presented);
        Assert.NotNull(result.InterestMap);
        foreach (var recommendation in result.Presented)
        {
            var signal = Assert.Single(result.InterestMap!.Signals, candidate =>
                recommendation.Reason.Contains($"\"{candidate.Label}\"", StringComparison.Ordinal));
            var interest = DiscoveryInterestMapping.ToInterest(signal, "baseline-test");
            var product = Catalogue.Default.BySku[recommendation.Sku];
            Assert.True(
                InterestAttribution.IsAttributable(Catalogue.Default, interest, product, out var attribution),
                $"{recommendation.Sku} was not attributable to {signal.Label}: {attribution}");
        }
    }

    [Fact]
    public async Task OfflineScriptRunsRealAgentAndRealToolsWithInvocationTimeEvents()
    {
        var catalogue = Catalogue.Default;
        var retriever = await HybridRetriever.BuildAsync(catalogue.All, ConceptEmbeddingSource.Instance);
        var events = new RecordingRecommendationRuntimeEventSink();
        var client = OfflineRecommendationScript.Create("USR-NB-01");

        GalaxusTools.Bind(retriever, "CH");
        try
        {
            var agent = RecommendationAgentFactory.Create(client, events);
            var session = await agent.CreateSessionAsync();
            using var budget = ToolCallBudget.BeginScope();
            using var capture = GalaxusTools.BeginRunCapture();

            var response = await agent.RunAsync(
                [new ChatMessage(ChatRole.User, "Recommend one useful product for this customer.")],
                session);

            Assert.Equal(6, client.CallCount);
            Assert.Single(GalaxusTools.PresentedInCurrentRun);
            Assert.Equal("GLX-1003", GalaxusTools.PresentedInCurrentRun[0].Sku);
            Assert.Contains("screen", response.Text, StringComparison.OrdinalIgnoreCase);

            var recorded = events.Events.ToArray();
            Assert.Equal(5, recorded.Count(e => e.Kind == RecommendationRuntimeEventKind.ToolExecutionStarted));
            Assert.Equal(5, recorded.Count(e => e.Kind == RecommendationRuntimeEventKind.ToolCompleted));
            Assert.Equal(6, recorded.Count(e => e.Kind == RecommendationRuntimeEventKind.ModelRequestStarted));
            Assert.Equal(6, recorded.Count(e => e.Kind == RecommendationRuntimeEventKind.ModelResponseReceived));

            foreach (var started in recorded.Where(e => e.Kind == RecommendationRuntimeEventKind.ToolExecutionStarted))
            {
                var startIndex = Array.IndexOf(recorded, started);
                var completionIndex = recorded
                    .Select((runtimeEvent, index) => (runtimeEvent, index))
                    .Single(pair => pair.runtimeEvent.Kind == RecommendationRuntimeEventKind.ToolCompleted
                                 && pair.runtimeEvent.OperationId == started.OperationId)
                    .index;
                Assert.True(startIndex < completionIndex);
            }

            Assert.Equal(13, RecommendationAgentFactory.BuildReadOnlyTools().Length);
            Assert.DoesNotContain(recorded, e => e.Detail.Contains("https://", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            GalaxusTools.Unbind();
            GalaxusTools.ClearProfileOverrides();
        }
    }

    [Fact]
    public void EventPreviewRedactsUrlsAndCredentialAssignments()
    {
        var preview = RecommendationRuntimeEvents.SafePreview(
            "endpoint=https://sentinel.example.test/path api_key=super-secret token=abc");

        Assert.DoesNotContain("sentinel.example.test", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", preview, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ObservationDecoratorPreservesEveryRegisteredFunctionContract()
    {
        var original = RecommendationAgentFactory.BuildReadOnlyTools().Cast<AIFunction>().ToArray();
        var wrapped = RecommendationAgentFactory.BuildReadOnlyTools(
            new RecordingRecommendationRuntimeEventSink()).Cast<AIFunction>().ToArray();

        Assert.Equal(original.Length, wrapped.Length);
        for (var index = 0; index < original.Length; index++)
        {
            Assert.Equal(original[index].Name, wrapped[index].Name);
            Assert.Equal(original[index].Description, wrapped[index].Description);
            Assert.Equal(original[index].JsonSchema.ToString(), wrapped[index].JsonSchema.ToString());
            Assert.Equal(original[index].ReturnJsonSchema.ToString(), wrapped[index].ReturnJsonSchema.ToString());
        }
    }

    [Fact]
    public async Task ModelFailureHasOneRunTerminalAndASeparateBoundaryFailure()
    {
        var result = await RecommendationRunEngine.RunAsync(new RecommendationRunOptions(
            Personas.NadiaUserId,
            Arm: RecommendationExecutionArm.ScriptedAgent,
            ChatClient: new DeterministicChatClient()));

        Assert.Equal(RecommendationRunStatus.Failed, result.Status);
        Assert.Equal(nameof(InvalidOperationException), result.FailureKind);
        Assert.Equal(1, result.ModelCalls);
        Assert.Equal(ProviderUsageStatus.Missing, result.ProviderUsage.Status);
        Assert.Equal(1, result.ProviderUsage.ModelCalls);
        Assert.Single(result.Events, item => item.Kind == RecommendationRuntimeEventKind.ModelRequestFailed);
        Assert.Single(result.Events, item => item.Kind == RecommendationRuntimeEventKind.RunFailed);
        Assert.DoesNotContain(result.Events, item => item.Kind == RecommendationRuntimeEventKind.RunCompleted);
        Assert.Equal(RecommendationRuntimeEventKind.RunFailed, result.Events[^1].Kind);
    }

    [Fact]
    public async Task CallerCancellationPreservesTheObservedModelAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = new CancellingChatClient(cancellation);

        var result = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(
                Personas.NadiaUserId,
                Arm: RecommendationExecutionArm.ScriptedAgent,
                ChatClient: client),
            cancellationToken: cancellation.Token);

        Assert.Equal(RecommendationRunStatus.Cancelled, result.Status);
        Assert.Null(result.FailureKind);
        Assert.Equal(1, result.ModelCalls);
        Assert.Equal(ProviderUsageStatus.Missing, result.ProviderUsage.Status);
        Assert.Equal(1, result.ProviderUsage.ModelCalls);
        Assert.Single(result.Events, item => item.Kind == RecommendationRuntimeEventKind.ModelRequestStarted);
        Assert.Single(result.Events, item => item.Kind == RecommendationRuntimeEventKind.ModelRequestCancelled);
        Assert.Single(result.Events, item => item.Kind == RecommendationRuntimeEventKind.RunCancelled);
        Assert.Equal(RecommendationRuntimeEventKind.RunCancelled, result.Events[^1].Kind);
    }

    [Fact]
    public async Task StreamingFailureClosesTheModelBoundaryWithoutInventingAResponse()
    {
        var events = new RecordingRecommendationRuntimeEventSink();
        using var observed = new ObservedChatClient(new DeterministicChatClient(), events);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in observed.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, "trigger the empty deterministic provider")]))
            {
            }
        });

        Assert.Single(events.Events, item => item.Kind == RecommendationRuntimeEventKind.ModelRequestStarted);
        Assert.Single(events.Events, item => item.Kind == RecommendationRuntimeEventKind.ModelRequestFailed);
        Assert.DoesNotContain(events.Events, item => item.Kind == RecommendationRuntimeEventKind.ModelResponseReceived);
    }

    private sealed class CancellingChatClient(CancellationTokenSource callerCancellation) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            callerCancellation.Cancel();
            return Task.FromCanceled<ChatResponse>(callerCancellation.Token);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            callerCancellation.Cancel();
            await Task.FromCanceled(callerCancellation.Token);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
