// SPDX-License-Identifier: MIT

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Retrieval;

namespace AgentEval.VitrineDemo.Tests;

public sealed class CommittedVectorContentPolicyTests
{
    [Fact]
    public void CheckedInAssetMatchesAllSixPinnedCosinesOffline()
    {
        var observation = CommittedVectorContentPolicy.ObserveCommittedAsset(Catalogue.Default.All);
        var result = CommittedVectorContentPolicy.Evaluate(observation);

        Assert.True(observation.HasCompleteShape);
        Assert.Equal(CommittedVectorContentPolicy.ExpectedDimensions, observation.DeclaredDimensions);
        Assert.Equal(CommittedVectorContentPolicy.ExpectedModelId, observation.ModelId);
        Assert.Equal(6, observation.Pairs.Count);
        foreach (var pin in CommittedVectorContentPolicy.PairPins)
        {
            var pair = Assert.Single(observation.Pairs, candidate =>
                candidate.LeftProductId == pin.LeftProductId &&
                candidate.RightProductId == pin.RightProductId);
            Assert.InRange(
                Math.Abs((pair.ObservedCosine ?? double.PositiveInfinity) - pin.ExpectedCosine),
                0d,
                CommittedVectorContentPolicy.CosineTolerance);
        }
        Assert.True(result.IsMatch, result.Detail);
        Assert.Equal(result.ExpectedPairCount, result.MatchedPairCount);
    }

    [Fact]
    public void SameDimensionKeyPreservingVectorSubstitutionBreaksTheContentFingerprint()
    {
        var source = PrecomputedEmbeddingSource.Load(Catalogue.Default.All);
        var canonical = ReadRequiredVectors(source, Catalogue.Default.All);
        var perturbed = canonical.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        // One value changes; the four keys, model stamp, and all 1536 dimensions stay valid.
        perturbed["GLX-1001"] = canonical["GLX-2001"];

        Assert.Equal(canonical.Keys.Order(StringComparer.Ordinal), perturbed.Keys.Order(StringComparer.Ordinal));
        Assert.All(perturbed, pair =>
            Assert.True(ProductVectorContract.Validate(
                pair.Value,
                CommittedVectorContentPolicy.ExpectedDimensions,
                pair.Key).IsUsable));

        var observation = CommittedVectorContentPolicy.Observe(
            Catalogue.Default.All,
            perturbed,
            source.Name,
            source.ModelId,
            source.Dimensions);
        var result = CommittedVectorContentPolicy.Evaluate(observation);

        Assert.True(observation.HasCompleteShape);
        Assert.Empty(observation.MissingProductIds);
        Assert.Empty(observation.InvalidProductIds);
        Assert.Equal(CommittedVectorContentDisposition.Mismatched, result.Disposition);
        Assert.False(result.IsMatch);
        Assert.True(result.MatchedPairCount < result.ExpectedPairCount, result.Detail);
    }

    [Fact]
    public void MissingVectorIsNotRenderedAsZeroOrAsAMismatch()
    {
        var source = PrecomputedEmbeddingSource.Load(Catalogue.Default.All);
        var vectors = ReadRequiredVectors(source, Catalogue.Default.All)
            .Where(static pair => !string.Equals(pair.Key, "GLX-1001", StringComparison.Ordinal))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        var observation = CommittedVectorContentPolicy.Observe(
            Catalogue.Default.All,
            vectors,
            source.Name,
            source.ModelId,
            source.Dimensions);
        var result = CommittedVectorContentPolicy.Evaluate(observation);

        Assert.Contains("GLX-1001", observation.MissingProductIds);
        Assert.All(
            observation.Pairs.Where(static pair => pair.LeftProductId == "GLX-1001"),
            static pair => Assert.Null(pair.ObservedCosine));
        Assert.Equal(CommittedVectorContentDisposition.NotMeasured, result.Disposition);
        Assert.False(result.IsMatch);
    }

    [Fact]
    public void ObservationCannotReplaceACanonicalPairWithAFlatteringIdentity()
    {
        var observation = CommittedVectorContentPolicy.ObserveCommittedAsset(Catalogue.Default.All);
        var forgedPairs = observation.Pairs.ToArray();
        forgedPairs[0] = forgedPairs[0] with { LeftProductId = "GLX-FORGED" };

        var result = CommittedVectorContentPolicy.Evaluate(observation with { Pairs = forgedPairs });

        Assert.Equal(CommittedVectorContentDisposition.NotMeasured, result.Disposition);
        Assert.False(result.IsMatch);
    }

    private static IReadOnlyDictionary<string, ReadOnlyMemory<float>> ReadRequiredVectors(
        PrecomputedEmbeddingSource source,
        IReadOnlyList<Product> products)
    {
        var productsById = products.ToDictionary(static product => product.Id, StringComparer.Ordinal);
        var vectors = new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);

        foreach (var anchor in CommittedVectorContentPolicy.RequiredAnchors)
        {
            var document = EmbeddingDocument.ForProduct(productsById[anchor.ProductId]);
            Assert.True(source.TryGetCommitted(document, out var vector));
            vectors.Add(anchor.ProductId, vector);
        }

        return vectors;
    }
}
