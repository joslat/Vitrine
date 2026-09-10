// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.App.Controls;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using Avalonia;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class AgenticRagGraphTests
{
    [Theory]
    [InlineData(600d)]
    [InlineData(1100d)]
    public void GroupBoundsContainRetrieveCritiqueCardsAndFeedbackRouteAtCompactAndWideWidths(
        double width)
    {
        var graph = VitrineGraphFactory.FromDiscoveryTopologyContract();
        var projected = new GraphViewModel();
        projected.Load(graph);
        var height = RuntimeGraphControl.RequiredHeightForLayout(projected, width);
        var positions = RuntimeGraphControl.Layout(projected, width, height);

        var candidate = AgenticRagGroupLayout.TryCreate(projected, positions, width);

        Assert.True(candidate.HasValue);
        var group = candidate.Value;
        Assert.True(group.Bounds.Width >= 310);
        Assert.InRange(group.Bounds.Left, 0, width);
        Assert.InRange(group.Bounds.Right, 0, width);
        Assert.InRange(group.LoopY, group.Bounds.Top, group.Bounds.Bottom);
        Assert.InRange(group.LoopLabelOrigin.X, group.Bounds.Left, group.Bounds.Right);
        Assert.InRange(group.LoopLabelOrigin.Y, group.Bounds.Top, group.Bounds.Bottom);
        Assert.True(group.LoopLabelOrigin.Y - group.LabelOrigin.Y >= 18);
        foreach (var executorId in new[]
                 {
                     DiscoveryExecutorIds.Discovery,
                     DiscoveryExecutorIds.CoverageReviewer,
                 })
        {
            var centre = positions[executorId];
            Assert.True(group.Bounds.Left <= centre.X - 83);
            Assert.True(group.Bounds.Right >= centre.X + 83);
            Assert.True(group.Bounds.Top <= centre.Y - 25);
            Assert.True(group.Bounds.Bottom >= centre.Y + 25);
        }
    }

    [Fact]
    public void GroupIsRenderOnlyAndIsNotProjectedOntoUnrelatedGraphs()
    {
        var workflow = VitrineGraphFactory.FromDiscoveryTopologyContract();
        Assert.Equal(5, workflow.Nodes.Count);
        Assert.Equal(5, workflow.Edges.Count);
        Assert.DoesNotContain(workflow.Nodes, static node =>
            node.Id.Contains("agentic-rag", StringComparison.OrdinalIgnoreCase));

        var agent = VitrineGraphFactory.FromRegisteredDemo01Functions();
        var projected = new GraphViewModel();
        projected.Load(agent);
        var height = RuntimeGraphControl.RequiredHeightForLayout(projected, 1100);
        var positions = RuntimeGraphControl.Layout(projected, 1100, height);

        Assert.Null(AgenticRagGroupLayout.TryCreate(projected, positions, 1100));
    }

    [Fact]
    public void CompactFeedbackRouteDetoursOutsideBothCardsAndTheForwardEdge()
    {
        const double width = 600;
        var projected = new GraphViewModel();
        projected.Load(VitrineGraphFactory.FromDiscoveryTopologyContract());
        var height = RuntimeGraphControl.RequiredHeightForLayout(projected, width);
        var positions = RuntimeGraphControl.Layout(projected, width, height);
        var discovery = positions[DiscoveryExecutorIds.Discovery];
        var reviewer = positions[DiscoveryExecutorIds.CoverageReviewer];
        var group = Assert.IsType<AgenticRagGroupGeometry>(
            AgenticRagGroupLayout.TryCreate(projected, positions, width));
        var discoveryCard = CardAt(discovery);
        var reviewerCard = CardAt(reviewer);
        var forward = RuntimeGraphControl.TrimmedSegment(discovery, reviewer, 166, 50, 166, 50);
        var forwardLeg = (forward.Start, forward.End);

        Assert.Equal(discovery.X, reviewer.X);
        Assert.Equal(4, group.FeedbackRoute.Count);
        Assert.All(group.FeedbackRoute, point =>
        {
            Assert.InRange(point.X, group.Bounds.Left, group.Bounds.Right);
            Assert.InRange(point.Y, group.Bounds.Top, group.Bounds.Bottom);
        });
        Assert.True(group.FeedbackRoute[1].X < Math.Min(discoveryCard.Left, reviewerCard.Left)
                    || group.FeedbackRoute[1].X > Math.Max(discoveryCard.Right, reviewerCard.Right));

        foreach (var leg in Legs(group.FeedbackRoute))
        {
            Assert.False(CrossesCardInterior(leg, discoveryCard));
            Assert.False(CrossesCardInterior(leg, reviewerCard));
            Assert.False(AxisAlignedSegmentsIntersect(leg, forwardLeg));
        }
    }

    [Fact]
    public void HtmlGraphExportsDashedAccessibleAgenticRagGroupWithoutChangingTopology()
    {
        var graph = VitrineGraphFactory.FromDiscoveryTopologyContract();
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            Guid.NewGuid(),
            new(VitrineRunMode.Demo02, Personas.NadiaUserId),
            graph,
            []));

        var html = VitrineHtmlReport.Render(artifact);

        Assert.Equal(5, artifact.Graph.Nodes.Count);
        Assert.Equal(5, artifact.Graph.Edges.Count);
        Assert.Contains("stroke-dasharray:8 6", html, StringComparison.Ordinal);
        Assert.Contains("class=\"agentic-rag-group\" role=\"group\"", html, StringComparison.Ordinal);
        Assert.Contains("data-agentic-rag-group=\"bounded\"", html, StringComparison.Ordinal);
        Assert.Contains(AgenticRagGroupLayout.AccessibleDescription, html, StringComparison.Ordinal);
        Assert.Contains("BOUNDED AGENTIC RAG", html, StringComparison.Ordinal);
        Assert.Contains("RETRIEVE", html, StringComparison.Ordinal);
        Assert.Contains("CRITIQUE", html, StringComparison.Ordinal);
    }

    private static Rect CardAt(Point centre) =>
        new(centre.X - 83, centre.Y - 25, 166, 50);

    private static IEnumerable<(Point Start, Point End)> Legs(IReadOnlyList<Point> route)
    {
        for (var index = 1; index < route.Count; index++)
            yield return (route[index - 1], route[index]);
    }

    private static bool CrossesCardInterior((Point Start, Point End) leg, Rect card)
    {
        if (leg.Start.X == leg.End.X)
            return leg.Start.X > card.Left && leg.Start.X < card.Right
                   && Math.Max(Math.Min(leg.Start.Y, leg.End.Y), card.Top)
                   < Math.Min(Math.Max(leg.Start.Y, leg.End.Y), card.Bottom);
        if (leg.Start.Y == leg.End.Y)
            return leg.Start.Y > card.Top && leg.Start.Y < card.Bottom
                   && Math.Max(Math.Min(leg.Start.X, leg.End.X), card.Left)
                   < Math.Min(Math.Max(leg.Start.X, leg.End.X), card.Right);
        return true;
    }

    private static bool AxisAlignedSegmentsIntersect(
        (Point Start, Point End) first,
        (Point Start, Point End) second)
    {
        var firstVertical = first.Start.X == first.End.X;
        var secondVertical = second.Start.X == second.End.X;
        if (firstVertical == secondVertical)
        {
            if (firstVertical && first.Start.X != second.Start.X) return false;
            if (!firstVertical && first.Start.Y != second.Start.Y) return false;
            var firstMin = firstVertical ? Math.Min(first.Start.Y, first.End.Y) : Math.Min(first.Start.X, first.End.X);
            var firstMax = firstVertical ? Math.Max(first.Start.Y, first.End.Y) : Math.Max(first.Start.X, first.End.X);
            var secondMin = firstVertical ? Math.Min(second.Start.Y, second.End.Y) : Math.Min(second.Start.X, second.End.X);
            var secondMax = firstVertical ? Math.Max(second.Start.Y, second.End.Y) : Math.Max(second.Start.X, second.End.X);
            return Math.Max(firstMin, secondMin) <= Math.Min(firstMax, secondMax);
        }

        var vertical = firstVertical ? first : second;
        var horizontal = firstVertical ? second : first;
        return vertical.Start.X >= Math.Min(horizontal.Start.X, horizontal.End.X)
               && vertical.Start.X <= Math.Max(horizontal.Start.X, horizontal.End.X)
               && horizontal.Start.Y >= Math.Min(vertical.Start.Y, vertical.End.Y)
               && horizontal.Start.Y <= Math.Max(vertical.Start.Y, vertical.End.Y);
    }

    [Fact]
    public async Task BoundedPartialExitUsesAStaticLabelThatDoesNotClaimCoverageWasSufficient()
    {
        var roundLimit = Assert.Single(
            await DiscoveryTerminationProbe.RunAllAsync(),
            static result => result.Name == "1. Round cap stops the loop");
        var exit = Assert.Single(
            DiscoveryTopology.Shipped.Routes,
            static route => route.Id == DiscoveryRouteIds.ReviewToRanker);

        Assert.True(roundLimit.Passed, roundLimit.Actual);
        Assert.Contains("PARTIAL answer", roundLimit.Expected, StringComparison.Ordinal);
        Assert.Equal("approved / bounded partial", exit.Label);
        Assert.DoesNotContain("coverage sufficient", exit.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoverageReviewerCannotApproveWhileOmittingAMappedInterest()
    {
        var state = new DiscoveryState
        {
            CustomerId = Personas.NadiaUserId,
            Market = "CH",
            Language = "en",
            PersonalizationConsent = true,
            SessionRequest = "Help me prepare for a multi-day photography trip."
        };
        state.Interests.Add(new Interest
        {
            Id = "I-1",
            Label = "multi-day photography",
            Kind = InterestKind.Direct,
            Origin = InterestOrigin.Mapper,
            Confidence = 0.8,
            EvidenceSignalIds = ["PUR-NB-01"],
            Rationale = "stated and purchase-backed",
            QueryTerms = ["photography"]
        });
        var coverage = state.CoverageFor("I-1");
        coverage.QueriesRun.Add("photography");
        coverage.CandidateProductIds.Add("GLX-1001");
        coverage.BestScore = 0.5;

        CoverageVerdictProjection.Project(
            state,
            new CoverageVerdict([], [], null, CoverageVerdict.CoverageSufficient,
                "Everything is covered."),
            Catalogue.Default,
            NullDiscoveryProgressSink.Instance);

        Assert.False(state.CoverageApproved);
        Assert.Contains("I-1", state.ReviewNotes, StringComparison.Ordinal);
        Assert.Contains("omitted", state.ReviewNotes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CoverageReviewerCannotApproveALatentInterestWithNoCandidate()
    {
        var state = new DiscoveryState
        {
            CustomerId = Personas.MarcoUserId,
            Market = "CH",
            Language = "it",
            PersonalizationConsent = true,
            SessionRequest = "Help me improve home espresso."
        };
        state.Interests.Add(new Interest
        {
            Id = "I-1",
            Label = "decaffeinated home espresso",
            Kind = InterestKind.Latent,
            Origin = InterestOrigin.Mapper,
            Confidence = 0.55,
            EvidenceSignalIds = [],
            Rationale = "inferred from the session",
            QueryTerms = ["decaf espresso"]
        });
        state.CoverageFor("I-1").QueriesRun.Add("decaf espresso");

        CoverageVerdictProjection.Project(
            state,
            new CoverageVerdict(["I-1"], [], null, CoverageVerdict.CoverageSufficient,
                "Everything is covered."),
            Catalogue.Default,
            NullDiscoveryProgressSink.Instance);

        Assert.False(state.CoverageApproved);
        Assert.Contains("I-1", state.ReviewNotes, StringComparison.Ordinal);
        Assert.Contains("not structurally covered", state.ReviewNotes, StringComparison.OrdinalIgnoreCase);
    }
}
