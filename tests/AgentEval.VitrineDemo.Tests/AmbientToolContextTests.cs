// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Tools;

namespace AgentEval.VitrineDemo.Tests;

public sealed class AmbientToolContextTests
{
    [Fact]
    public void NestedBindingScopeRestoresEnclosingRetrieverAndMarket()
    {
        var outer = new NeverCalledRetriever("outer");
        var inner = new NeverCalledRetriever("inner");
        GalaxusTools.Bind(outer, "DE");

        try
        {
            using (GalaxusTools.BeginBinding(inner, "CH"))
            {
                Assert.Same(inner, GalaxusTools.Retriever);
                Assert.Equal("CH", GalaxusTools.Market);
            }

            Assert.Same(outer, GalaxusTools.Retriever);
            Assert.Equal("DE", GalaxusTools.Market);
        }
        finally
        {
            GalaxusTools.Unbind();
        }
    }

    [Fact]
    public async Task BindingScopesAreIsolatedAcrossConcurrentAsyncFlows()
    {
        var first = new NeverCalledRetriever("first");
        var second = new NeverCalledRetriever("second");
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstFlow = Task.Run(() => VerifyFlowAsync(first, "DE", firstReady, secondReady.Task));
        var secondFlow = Task.Run(() => VerifyFlowAsync(second, "CH", secondReady, firstReady.Task));

        await Task.WhenAll(firstFlow, secondFlow);
        Assert.False(GalaxusTools.IsBound);
    }

    [Fact]
    public async Task ProfileScopeRestoresTheEnclosingOptOutOverride()
    {
        var optedOut = UserProfiles.Require(Personas.NadiaUserId).WithPersonalization(false);
        GalaxusTools.OverrideProfile(optedOut);

        try
        {
            var refusedBefore = await GalaxusTools.GetPurchaseHistory(Personas.NadiaUserId);
            using (GalaxusTools.BeginProfileScope())
            {
                var availableInside = await GalaxusTools.GetPurchaseHistory(Personas.NadiaUserId);
                Assert.Contains("\"status\":\"ok\"", availableInside, StringComparison.Ordinal);
            }

            var refusedAfter = await GalaxusTools.GetPurchaseHistory(Personas.NadiaUserId);
            Assert.Equal(refusedBefore, refusedAfter);
            Assert.Contains("refused", refusedAfter, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            GalaxusTools.ClearProfileOverrides();
        }
    }

    private static async Task VerifyFlowAsync(
        IProductRetriever retriever,
        string market,
        TaskCompletionSource ready,
        Task otherReady)
    {
        using var scope = GalaxusTools.BeginBinding(retriever, market);
        ready.SetResult();
        await otherReady;
        await Task.Yield();
        Assert.Same(retriever, GalaxusTools.Retriever);
        Assert.Equal(market, GalaxusTools.Market);
    }

    private sealed class NeverCalledRetriever(string name) : IProductRetriever
    {
        public string Name { get; } = name;
        public bool DenseAvailable => true;
        public int ProductCount => 0;

        public ValueTask<RetrievalResult> SearchAsync(
            RetrievalQuery query,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("This context-only retriever must not be called.");
    }
}
