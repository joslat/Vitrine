// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Collections.ObjectModel;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>A catalogue identity whose committed vector participates in the content fingerprint.</summary>
public sealed record CommittedVectorAnchor(string ProductId, string ProductName);

/// <summary>An immutable expected cosine between two named committed product vectors.</summary>
public sealed record CommittedVectorCosinePin(
    string LeftProductId,
    string RightProductId,
    double ExpectedCosine);

/// <summary>One independently calculated pair observation. Null means the pair was not measurable.</summary>
public sealed record CommittedVectorPairObservation(
    string LeftProductId,
    string RightProductId,
    double? ObservedCosine);

/// <summary>Typed evidence captured from the committed vector asset before evaluating its content.</summary>
public sealed record CommittedVectorContentObservation(
    string SourceName,
    string ModelId,
    int DeclaredDimensions,
    IReadOnlyList<CommittedVectorPairObservation> Pairs,
    IReadOnlyList<string> MissingProductIds,
    IReadOnlyList<string> InvalidProductIds)
{
    /// <summary>True only when every required key carries a finite, non-zero vector of the declared shape.</summary>
    public bool HasCompleteShape =>
        DeclaredDimensions == CommittedVectorContentPolicy.ExpectedDimensions &&
        MissingProductIds.Count == 0 &&
        InvalidProductIds.Count == 0 &&
        Pairs.Count == CommittedVectorContentPolicy.PairPins.Count &&
        Pairs.All(static pair => pair.ObservedCosine.HasValue);
}

/// <summary>The three possible outcomes of checking the committed vector content fingerprint.</summary>
public enum CommittedVectorContentDisposition
{
    Matched,
    Mismatched,
    NotMeasured,
}

/// <summary>Evaluation of a committed-vector observation against the code-owned cosine pins.</summary>
public sealed record CommittedVectorContentResult(
    CommittedVectorContentDisposition Disposition,
    int MatchedPairCount,
    int ExpectedPairCount,
    string Detail)
{
    public bool IsMatch => Disposition == CommittedVectorContentDisposition.Matched;
}

/// <summary>
/// Proves that the checked-in product vectors contain the expected numbers, not merely the expected
/// keys and dimensions. Six pairwise cosine pins form a compact, rotation-sensitive fingerprint over
/// four stable catalogue products. Cosines are calculated here directly in double precision rather
/// than delegated to the index implementation that this policy independently guards.
/// </summary>
public static class CommittedVectorContentPolicy
{
    public const string ExpectedModelId = "text-embedding-3-small";
    public const int ExpectedDimensions = 1536;
    public const double CosineTolerance = 0.000001d;

    private static readonly ReadOnlyCollection<CommittedVectorAnchor> RequiredAnchorValues =
        Array.AsReadOnly<CommittedVectorAnchor>(
        [
            new("GLX-1001", "Sony Alpha 7 IV (ILCE-7M4) body"),
            new("GLX-1002", "Sony FE 16-35 mm F4 PZ G"),
            new("GLX-2001", "Osprey Kestrel 38 trekking pack"),
            new("GLX-2002", "Petzl Actik Core headlamp"),
        ]);

    private static readonly ReadOnlyCollection<CommittedVectorCosinePin> PairPinValues =
        Array.AsReadOnly<CommittedVectorCosinePin>(
        [
            new("GLX-1001", "GLX-1002", 0.6438093107856739d),
            new("GLX-1001", "GLX-2001", 0.23964030232974579d),
            new("GLX-1001", "GLX-2002", 0.31325302608806443d),
            new("GLX-1002", "GLX-2001", 0.3292800248769647d),
            new("GLX-1002", "GLX-2002", 0.2984261135200159d),
            new("GLX-2001", "GLX-2002", 0.3467155336568779d),
        ]);

    /// <summary>The four exact catalogue identities covered by the fingerprint.</summary>
    public static IReadOnlyList<CommittedVectorAnchor> RequiredAnchors => RequiredAnchorValues;

    /// <summary>The six immutable cosine pins derived from the checked-in asset.</summary>
    public static IReadOnlyList<CommittedVectorCosinePin> PairPins => PairPinValues;

    /// <summary>Loads the canonical committed asset and observes it without making any model call.</summary>
    public static CommittedVectorContentObservation ObserveCommittedAsset(
        IReadOnlyList<Product> products,
        IEnumerable<string>? assetPaths = null)
    {
        ArgumentNullException.ThrowIfNull(products);

        var source = PrecomputedEmbeddingSource.Load(
            products,
            liveFallback: null,
            assetPaths: assetPaths);
        return Observe(source, products);
    }

    /// <summary>Reads the four product vectors directly from an already-loaded committed source.</summary>
    public static CommittedVectorContentObservation Observe(
        PrecomputedEmbeddingSource source,
        IReadOnlyList<Product> products)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(products);

        var vectors = new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);
        foreach (var anchor in RequiredAnchorValues)
        {
            var product = products.FirstOrDefault(candidate =>
                candidate is not null && string.Equals(candidate.Id, anchor.ProductId, StringComparison.Ordinal));
            if (product is null) continue;

            var document = EmbeddingDocument.ForProduct(product);
            if (source.TryGetCommitted(document, out var vector))
                vectors.Add(anchor.ProductId, vector);
        }

        return Observe(
            products,
            vectors,
            source.Name,
            source.ModelId,
            source.Dimensions);
    }

    /// <summary>
    /// Observes supplied committed vectors. This overload is the causal-ablation seam: callers can
    /// replace one vector while keeping every key and dimension valid, then run the same evaluator.
    /// The vectors are reduced immediately to scalar observations and are never retained.
    /// </summary>
    public static CommittedVectorContentObservation Observe(
        IReadOnlyList<Product> products,
        IReadOnlyDictionary<string, ReadOnlyMemory<float>> vectorsByProductId,
        string sourceName,
        string modelId,
        int declaredDimensions)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(vectorsByProductId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var productsById = products
            .Where(static product => product is not null)
            .ToDictionary(static product => product.Id, StringComparer.Ordinal);
        var usableVectors = new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);
        var missing = new List<string>();
        var invalid = new List<string>();

        foreach (var anchor in RequiredAnchorValues)
        {
            if (!productsById.TryGetValue(anchor.ProductId, out var product) ||
                !string.Equals(product.Name, anchor.ProductName, StringComparison.Ordinal))
            {
                missing.Add(anchor.ProductId);
                continue;
            }

            if (!vectorsByProductId.TryGetValue(anchor.ProductId, out var vector) || vector.IsEmpty)
            {
                missing.Add(anchor.ProductId);
                continue;
            }

            if (!IsUsableVector(vector.Span, declaredDimensions))
            {
                invalid.Add(anchor.ProductId);
                continue;
            }

            usableVectors.Add(anchor.ProductId, vector);
        }

        var pairs = new List<CommittedVectorPairObservation>(PairPinValues.Count);
        foreach (var pin in PairPinValues)
        {
            double? cosine = null;
            if (usableVectors.TryGetValue(pin.LeftProductId, out var left) &&
                usableVectors.TryGetValue(pin.RightProductId, out var right))
            {
                cosine = CalculateCosine(left.Span, right.Span);
            }

            pairs.Add(new(
                pin.LeftProductId,
                pin.RightProductId,
                cosine));
        }

        return new(
            sourceName,
            modelId,
            declaredDimensions,
            pairs.AsReadOnly(),
            missing.AsReadOnly(),
            invalid.AsReadOnly());
    }

    /// <summary>Compares a complete observation with the code-owned model, shape, and cosine pins.</summary>
    public static CommittedVectorContentResult Evaluate(CommittedVectorContentObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!observation.HasCompleteShape ||
            !string.Equals(observation.ModelId, ExpectedModelId, StringComparison.Ordinal))
        {
            return new(
                CommittedVectorContentDisposition.NotMeasured,
                0,
                PairPinValues.Count,
                "The committed vector fingerprint was not completely measurable in the expected embedding space.");
        }

        var observedByPair = new Dictionary<(string Left, string Right), double?>();
        foreach (var pair in observation.Pairs)
        {
            if (!observedByPair.TryAdd((pair.LeftProductId, pair.RightProductId), pair.ObservedCosine))
            {
                return new(
                    CommittedVectorContentDisposition.NotMeasured,
                    0,
                    PairPinValues.Count,
                    "The committed vector fingerprint contained a duplicate pair observation.");
            }
        }

        if (PairPinValues.Any(pin =>
                !observedByPair.ContainsKey((pin.LeftProductId, pin.RightProductId))))
        {
            return new(
                CommittedVectorContentDisposition.NotMeasured,
                0,
                PairPinValues.Count,
                "The committed vector fingerprint did not contain the exact six required pair observations.");
        }

        var matched = 0;
        foreach (var pin in PairPinValues)
        {
            if (observedByPair.TryGetValue((pin.LeftProductId, pin.RightProductId), out var observed) &&
                observed is { } cosine &&
                double.IsFinite(cosine) &&
                Math.Abs(cosine - pin.ExpectedCosine) <= CosineTolerance)
            {
                matched++;
            }
        }
        var disposition = matched == PairPinValues.Count
            ? CommittedVectorContentDisposition.Matched
            : CommittedVectorContentDisposition.Mismatched;

        return new(
            disposition,
            matched,
            PairPinValues.Count,
            disposition == CommittedVectorContentDisposition.Matched
                ? $"All {PairPinValues.Count} committed-vector cosine pins matched."
                : $"Only {matched} of {PairPinValues.Count} committed-vector cosine pins matched.");
    }

    private static bool IsUsableVector(ReadOnlySpan<float> vector, int declaredDimensions)
    {
        if (declaredDimensions != ExpectedDimensions || vector.Length != declaredDimensions)
            return false;

        double normSquared = 0d;
        foreach (var component in vector)
        {
            if (!float.IsFinite(component)) return false;
            normSquared += (double)component * component;
        }

        return normSquared > 0d && double.IsFinite(normSquared);
    }

    private static double CalculateCosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length || left.IsEmpty) return double.NaN;

        double dot = 0d;
        double leftNormSquared = 0d;
        double rightNormSquared = 0d;

        for (int index = 0; index < left.Length; index++)
        {
            var leftValue = (double)left[index];
            var rightValue = (double)right[index];
            dot += leftValue * rightValue;
            leftNormSquared += leftValue * leftValue;
            rightNormSquared += rightValue * rightValue;
        }

        var denominator = Math.Sqrt(leftNormSquared * rightNormSquared);
        return denominator > 0d && double.IsFinite(denominator)
            ? dot / denominator
            : double.NaN;
    }
}
