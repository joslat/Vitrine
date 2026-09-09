// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class ProductionSeamTests
{
    [Fact]
    public void ShippedTopologyAcceptsOnlyTheExactFiveExecutorOneLoopGraph()
    {
        var topology = DiscoveryTopology.Shipped;
        var exact = new DiscoveryTopologyObservation(
            DiscoveryExecutorIds.All,
            topology.Routes.Select(static route =>
                (route.SourceExecutorId, route.TargetExecutorId)).ToArray());

        var accepted = topology.Validate(exact);
        var missingLoop = topology.Validate(exact with
        {
            Edges = exact.Edges
                .Where(edge => edge != (DiscoveryExecutorIds.CoverageReviewer, DiscoveryExecutorIds.Discovery))
                .ToArray(),
        });
        var staleOrder = topology.Validate(exact with
        {
            ExecutorIds = exact.ExecutorIds.Reverse().ToArray(),
        });

        Assert.True(accepted.IsExact, accepted.Detail);
        Assert.False(missingLoop.IsExact);
        Assert.False(staleOrder.IsExact);
        Assert.Equal(5, topology.Nodes.Count);
        Assert.Equal(5, topology.Routes.Count);
        var loop = Assert.Single(topology.Routes, static route => route.IsLoopBack);
        Assert.True(loop.IsConditional);
        Assert.Equal(DiscoveryRouteIds.ReviewToMoreDiscovery, loop.Id);
        Assert.Throws<InvalidOperationException>(() =>
            topology.RequireRoute(DiscoveryExecutorIds.Presenter, DiscoveryExecutorIds.InterestMapper));
    }

    [Fact]
    public async Task ActualPreparedWorkflowIsProjectedThroughTheShippedTopology()
    {
        await using var coordinator = new VitrineRunCoordinator();

        var outcome = await coordinator.RunAsync(new(
            VitrineRunMode.Demo02,
            Personas.MarcoUserId,
            MaxRounds: 3));

        Assert.Null(outcome.FailureKind);
        Assert.Equal(VitrineGraphSource.MafWorkflow, outcome.Graph.Source);
        Assert.Equal(
            DiscoveryTopology.Shipped.Routes.Select(static route => route.Id).Order(StringComparer.Ordinal),
            outcome.Graph.Edges.Select(static edge => edge.Id).Order(StringComparer.Ordinal));
        var loop = Assert.Single(outcome.Graph.Edges, static edge => edge.IsLoopBack);
        Assert.Equal(DiscoveryRouteIds.ReviewToMoreDiscovery, loop.Id);
        Assert.True(loop.IsConditional);
    }

    [Fact]
    public void TopologyCaseRegistryCoversEveryAuthoredPersonaInEveryConcreteSpace()
    {
        var spaces = Enum.GetValues<EmbeddingSpaceChoice>()
            .Where(static space => space != EmbeddingSpaceChoice.Auto)
            .ToArray();
        var personas = new[]
        {
            Personas.RenzoUserId,
            Personas.MarcoUserId,
            Personas.MirjamUserId,
            Personas.NadiaUserId,
            Personas.LucaUserId,
        };

        Assert.Equal(personas.Length * spaces.Length, DiscoveryTopologyCaseRegistry.All.Count);
        foreach (var persona in personas)
        foreach (var space in spaces)
        {
            var claim = Assert.Single(DiscoveryTopologyCaseRegistry.All,
                candidate => candidate.PersonaId == persona && candidate.EmbeddingSpace == space);
            Assert.Equal(claim.Rounds - 1, claim.LoopBackCount);
            Assert.Equal(
                claim.LoopBackCount,
                claim.RouteIds.Count(static route => route == DiscoveryRouteIds.ReviewToMoreDiscovery));
            Assert.Equal(DiscoveryRouteIds.MapToDiscovery, claim.RouteIds[0]);
            Assert.Equal(DiscoveryRouteIds.RankerToPresenter, claim.RouteIds[^1]);
            Assert.DoesNotContain(claim.StopReason,
                new[] { DiscoveryStopReason.None, DiscoveryStopReason.GapsRemain });
        }
    }

    [Fact]
    public async Task ActualWorkflowFactsMatchIndependentClaimAndOneFieldStaleClaimFails()
    {
        var run = await GalaxusDiscoveryLoop.RunAsync(
            Personas.NadiaUserId,
            new DiscoveryLoopOptions(Offline: true));

        var healthy = DiscoveryTopologyCaseRegistry.Assess(run);
        Assert.Equal(DiscoveryTopologyCaseOutcome.Match, healthy.Outcome);
        Assert.True(healthy.IsMatch);
        Assert.NotNull(healthy.Claim);

        var staleClaim = healthy.Claim! with { Rounds = healthy.Claim.Rounds + 1 };
        var ablated = DiscoveryTopologyCaseRegistry.Assess(run, staleClaim);

        Assert.Equal(DiscoveryTopologyCaseOutcome.Mismatch, ablated.Outcome);
        Assert.False(ablated.IsMatch);
        var difference = Assert.Single(ablated.Differences);
        Assert.StartsWith("rounds expected", difference, StringComparison.Ordinal);
        Assert.Equal(DiscoveryTopologyCaseOutcome.Match,
            DiscoveryTopologyCaseRegistry.Assess(run).Outcome);
    }

    [Fact]
    public void TopologyCaseComparisonDoesNotTurnMissingSpaceIntoSuccess()
    {
        var claim = DiscoveryTopologyCaseRegistry.All.First();
        var state = new DiscoveryState
        {
            CustomerId = claim.PersonaId,
            Market = "CH",
            Language = "en",
            DiscoveryRound = claim.Rounds,
            StopReason = claim.StopReason,
        };
        var run = new DiscoveryRunResult(state, DiscoveryExecutorIds.All, claim.RouteIds, 0, TimeSpan.Zero)
        {
            RequestedPersonaId = claim.PersonaId,
        };

        var assessment = DiscoveryTopologyCaseRegistry.Assess(run);

        Assert.Equal(DiscoveryTopologyCaseOutcome.NotMeasured, assessment.Outcome);
        Assert.False(assessment.IsMatch);
        Assert.Null(assessment.Claim);
        Assert.Equal(
            DiscoveryTopologyCaseOutcome.NotMeasured,
            DiscoveryTopologyCaseRegistry.Assess(run, claim).Outcome);
    }

    [Fact]
    public void AuthoredPersonaSelectsTopologyClaimAndReportedPersonaMustMatchIt()
    {
        var claim = DiscoveryTopologyCaseRegistry.All.Single(candidate =>
            candidate.PersonaId == Personas.NadiaUserId &&
            candidate.EmbeddingSpace == EmbeddingSpaceChoice.ConceptVectors);
        var state = new DiscoveryState
        {
            CustomerId = Personas.RenzoUserId,
            Market = "CH",
            Language = "en",
            DiscoveryRound = claim.Rounds,
            StopReason = claim.StopReason,
        };
        var run = new DiscoveryRunResult(state, DiscoveryExecutorIds.All, claim.RouteIds, 0, TimeSpan.Zero)
        {
            RequestedPersonaId = Personas.NadiaUserId,
            ResolvedEmbeddingSpace = claim.EmbeddingSpace,
        };

        var assessment = DiscoveryTopologyCaseRegistry.Assess(run);

        Assert.Equal(DiscoveryTopologyCaseOutcome.Mismatch, assessment.Outcome);
        Assert.Equal(Personas.NadiaUserId, assessment.Claim?.PersonaId);
        Assert.Contains(assessment.Differences, difference =>
            difference.StartsWith("persona expected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PortableRecommendationArtifactEmitsResolvedCatalogueFacts()
    {
        var run = await RecommendationRunEngine.RunAsync(new(
            Personas.NadiaUserId,
            Arm: RecommendationExecutionArm.ZeroModelBaseline));
        Assert.NotNull(run.Outcome);
        var recommendation = Assert.Single(run.Outcome.Cleaned.AllPresented.Take(1));

        var expectedLine = CatalogueFactRenderer.Render(recommendation, Catalogue.Default);
        var artifact = RecommendationArtifactComposer.Compose(run);

        Assert.Contains(expectedLine, artifact, StringComparison.Ordinal);
        var fact = CatalogueFactRenderer.Resolve(recommendation, Catalogue.Default);
        Assert.True(Catalogue.Default.TryGet(fact.ProductId, out var product));
        Assert.True(product!.TryGetAttributeValue(fact.AttributeKey, out var actualValue));
        Assert.Equal(actualValue, fact.AttributeValue);
    }

    [Fact]
    public void CatalogueFactRendererRejectsStaleValuesAndWrongReviews()
    {
        var product = Catalogue.Default.All.First(static item => item.ReviewIds.Count > 0);
        var key = Catalogue.Default.AttributesOf(product).First(token =>
            product.TryGetAttributeValue(token, out _));
        Assert.True(product.TryGetAttributeValue(key, out var value));
        var recommendation = new RecommendationDto(
            product.Id,
            "test",
            new EvidenceDto("test", [], key, value!, product.ReviewIds.First()),
            0.5);

        Assert.Throws<InvalidOperationException>(() => CatalogueFactRenderer.Resolve(
            recommendation with
            {
                Evidence = recommendation.Evidence with { ProductAttributeValue = "stale-value" },
            },
            Catalogue.Default));
        Assert.Throws<InvalidOperationException>(() => CatalogueFactRenderer.Resolve(
            recommendation with
            {
                Evidence = recommendation.Evidence with { ReviewId = "REV-WRONG-PRODUCT" },
            },
            Catalogue.Default));
    }

    [Fact]
    public void EveryCatalogueTagRendersAsCarriesTagAndValidatesIndependently()
    {
        var checkedTags = 0;
        foreach (var product in Catalogue.Default.All)
        foreach (var tag in product.Tags)
        {
            var citation = EvidenceRef.Attribute(tag);
            var recommendation = new RecommendationDto(
                product.Id,
                "test",
                new EvidenceDto("test", [], tag, tag, null),
                0.5);

            var expected = $"Catalogue evidence: {product.Id} · carries tag {tag} [{citation}]";
            var fact = CatalogueFactRenderer.Resolve(recommendation, Catalogue.Default);
            var rendered = CatalogueFactRenderer.Render(fact);
            var independentlyChecked = CatalogueEvidenceStatement.ValidateExact(expected, Catalogue.Default);

            Assert.Equal(CatalogueEvidenceKind.Tag, fact.Kind);
            Assert.Equal(tag, fact.AttributeKey);
            Assert.Equal(tag, fact.AttributeValue);
            Assert.Equal(expected, rendered);
            Assert.True(independentlyChecked.IsValid, independentlyChecked.Detail);
            Assert.Equal(CatalogueEvidenceKind.Tag, independentlyChecked.Evidence!.Kind);
            checkedTags++;
        }

        Assert.True(checkedTags > Catalogue.Default.All.Count);
    }

    [Fact]
    public void SpecificationRendersAsExactKeyValueAndValidatesIndependently()
    {
        var product = Catalogue.Default.All.First(static candidate => candidate.Specs.Count > 0);
        var spec = product.Specs.First();
        var citation = EvidenceRef.Attribute(spec.Key);
        var recommendation = new RecommendationDto(
            product.Id,
            "test",
            new EvidenceDto("test", [], spec.Key, spec.Value, null),
            0.5);
        var expected = $"Catalogue evidence: {product.Id} · {spec.Key}={spec.Value} [{citation}]";

        var fact = CatalogueFactRenderer.Resolve(recommendation, Catalogue.Default);
        var rendered = CatalogueFactRenderer.Render(fact);
        var independentlyChecked = CatalogueEvidenceStatement.ValidateExact(expected, Catalogue.Default);

        Assert.Equal(CatalogueEvidenceKind.Specification, fact.Kind);
        Assert.Equal(expected, rendered);
        Assert.True(independentlyChecked.IsValid, independentlyChecked.Detail);
        Assert.Equal(spec.Key, independentlyChecked.Evidence!.AttributeKey);
        Assert.Equal(spec.Value, independentlyChecked.Evidence.AttributeValue);
    }

    [Fact]
    public void ExactCatalogueEvidenceRejectsStaleAndPrefixOnlyValues()
    {
        var product = Catalogue.Default.All.First(static candidate =>
            candidate.Tags.Any(tag => tag.Length > 3) && candidate.Specs.Any(spec => spec.Value.Length > 3));
        var tag = product.Tags.First(static candidate => candidate.Length > 3);
        var spec = product.Specs.First(static candidate => candidate.Value.Length > 3);
        var tagPrefix = tag[..^1];
        var valuePrefix = spec.Value[..^1];

        var prefixTag = $"Catalogue evidence: {product.Id} · carries tag {tagPrefix} [{EvidenceRef.Attribute(tag)}]";
        var staleSpec = $"Catalogue evidence: {product.Id} · {spec.Key}={valuePrefix} [{EvidenceRef.Attribute(spec.Key)}]";

        Assert.False(CatalogueEvidenceStatement.ValidateExact(prefixTag, Catalogue.Default).IsValid);
        Assert.False(CatalogueEvidenceStatement.ValidateExact(staleSpec, Catalogue.Default).IsValid);
        Assert.Throws<InvalidOperationException>(() => CatalogueFactRenderer.Resolve(
            new RecommendationDto(
                product.Id,
                "test",
                new EvidenceDto("test", [], spec.Key, valuePrefix, null),
                0.5),
            Catalogue.Default));
    }

    [Fact]
    public async Task ProductIndexBuildInvokesTheSharedVectorContract()
    {
        var products = Catalogue.Default.All.Take(2).ToArray();
        var source = new SequenceEmbeddingSource(
            new float[] { 1f, 0f, 0f },
            new float[] { 0f, 1f });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ProductVectorIndex.BuildAsync(products, source));

        Assert.Contains(products[1].Id, error.Message, StringComparison.Ordinal);
        Assert.Contains("2-dimensional", error.Message, StringComparison.Ordinal);
        Assert.Contains("3 was expected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductVectorContractRejectsTruncatedAndNonFiniteVectors()
    {
        var accepted = ProductVectorContract.Validate(new float[] { 1f, 0f, 0f }, 3, "GLX-OK");
        var noSignal = ProductVectorContract.Validate(new float[] { 0f, 0f, 0f }, 3, "GLX-ZERO");
        var unavailable = ProductVectorContract.Validate(ReadOnlyMemory<float>.Empty, 3, "GLX-MISSING");

        Assert.True(accepted.IsUsable);
        Assert.Equal(ProductVectorDisposition.NoSignal, noSignal.Disposition);
        Assert.Equal(ProductVectorDisposition.Unavailable, unavailable.Disposition);
        Assert.Throws<InvalidOperationException>(() =>
            ProductVectorContract.Validate(new float[] { 1f, 0f }, 3, "GLX-TRUNCATED"));
        Assert.Throws<InvalidOperationException>(() =>
            ProductVectorContract.Validate(new float[] { 1f, float.NaN, 0f }, 3, "GLX-NAN"));
    }

    private sealed class SequenceEmbeddingSource(params ReadOnlyMemory<float>[] vectors) : IEmbeddingSource
    {
        private int _index;

        public string Name => "test-vectors";
        public string ModelId => "test-space-v1";
        public int Dimensions => 3;
        public bool IsOffline => true;
        public float SuggestedDenseScoreFloor => 0f;

        public ValueTask<ReadOnlyMemory<float>> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(vectors[_index++]);
        }
    }
}
