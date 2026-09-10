// SPDX-License-Identifier: MIT

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class DiscoveryPartialDisclosureTests
{
    private const string PartialHeading = "Partial result — some interests are not covered yet";
    private const string PartialNextStep =
        "Next step: add the missing preferences, or ask an advisor to review these gaps before relying on the recommendations.";

    [Theory]
    [InlineData(ScriptedReviewer.Mode.NeverApprove, true, 2, DiscoveryStopReason.RoundLimitReached)]
    [InlineData(ScriptedReviewer.Mode.NeverApprove, false, 3, DiscoveryStopReason.NoProgress)]
    [InlineData(ScriptedReviewer.Mode.RepeatTheSameQuery, true, 3, DiscoveryStopReason.GapsUnresolvable)]
    public async Task PartialTerminationReasonsAreDisclosedInTheDeliveredAnswer(
        ScriptedReviewer.Mode reviewerMode,
        bool alwaysFresh,
        int maximumRounds,
        DiscoveryStopReason expectedStopReason)
    {
        IProductRetriever retriever = alwaysFresh
            ? new AlwaysFreshRetriever(Catalogue.Default)
            : new AlwaysSameProductsRetriever(Catalogue.Default);
        var run = await GalaxusDiscoveryLoop.RunAsync(
            DiscoveryTerminationProbe.ProbeUserId,
            new DiscoveryLoopOptions(
                Offline: true,
                MaxRounds: maximumRounds,
                Retriever: retriever,
                Nodes: Nodes(reviewerMode)));

        Assert.False(run.Failed, string.Join("; ", run.ExecutorFailures));
        Assert.Equal(expectedStopReason, run.State.StopReason);
        Assert.True(run.State.IsPartialAnswer);
        Assert.False(string.IsNullOrWhiteSpace(run.State.FinalAnswer));
        Assert.Contains(PartialHeading,
            run.State.FinalAnswer, StringComparison.Ordinal);
        Assert.Contains($"Stop reason: {expectedStopReason}",
            run.State.FinalAnswer, StringComparison.Ordinal);
        Assert.Contains(PartialNextStep,
            run.State.FinalAnswer, StringComparison.Ordinal);

        var uncovered = run.State.UncoveredInterests();
        Assert.NotEmpty(uncovered);
        Assert.All(uncovered, interest => Assert.Contains(
            $"  · {interest.Label}", run.State.FinalAnswer, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CoverageSufficientAnswerOmitsThePartialDisclosure()
    {
        var run = await GalaxusDiscoveryLoop.RunAsync(
            DiscoveryTerminationProbe.ProbeUserId,
            new DiscoveryLoopOptions(
                Offline: true,
                MaxRounds: 3,
                Retriever: new AlwaysFreshRetriever(Catalogue.Default),
                Nodes: Nodes(ScriptedReviewer.Mode.AlwaysApprove)));

        Assert.False(run.Failed, string.Join("; ", run.ExecutorFailures));
        Assert.Equal(DiscoveryStopReason.CoverageSufficient, run.State.StopReason);
        Assert.False(run.State.IsPartialAnswer);
        Assert.DoesNotContain(PartialHeading,
            run.State.FinalAnswer, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop reason:", run.State.FinalAnswer, StringComparison.Ordinal);
        Assert.DoesNotContain(PartialNextStep,
            run.State.FinalAnswer, StringComparison.Ordinal);
    }

    private static DiscoveryNodeOverrides Nodes(ScriptedReviewer.Mode reviewerMode) => new(
        Reviewer: new ScriptedReviewer(reviewerMode),
        Presenter: new DeterministicPresenter(
            Catalogue.Default,
            NullDiscoveryProgressSink.Instance,
            print: false));
}
