// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>How one product vector may participate in the dense index.</summary>
public enum ProductVectorDisposition
{
    Usable,
    Unavailable,
    NoSignal,
}

/// <summary>Code-owned observation of one vector at the index boundary.</summary>
public readonly record struct ProductVectorValidation(
    ProductVectorDisposition Disposition,
    int ExpectedDimensions,
    int ActualDimensions)
{
    public bool IsUsable => Disposition == ProductVectorDisposition.Usable;
}

/// <summary>
/// The shape and numeric contract enforced whenever a vector enters <see cref="ProductVectorIndex"/>.
/// </summary>
public static class ProductVectorContract
{
    /// <summary>
    /// Validates a vector against the source-declared or first-observed dimensionality.
    /// Empty and all-zero vectors are explicit non-usable states; stale dimensions and non-finite
    /// components are wiring faults and fail closed.
    /// </summary>
    public static ProductVectorValidation Validate(
        ReadOnlyMemory<float> vector,
        int expectedDimensions,
        string productId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        if (expectedDimensions < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedDimensions), "Vector dimensions cannot be negative.");
        if (vector.IsUnavailable())
            return new(ProductVectorDisposition.Unavailable, expectedDimensions, 0);

        var resolvedDimensions = expectedDimensions == 0 ? vector.Length : expectedDimensions;
        if (vector.Length != resolvedDimensions)
        {
            throw new InvalidOperationException(
                $"Product '{productId}' has a {vector.Length}-dimensional vector but {resolvedDimensions} was expected. " +
                "A mixed-dimension index cannot be searched; rebuild the embedding assets against one model.");
        }

        foreach (var component in vector.Span)
        {
            if (!float.IsFinite(component))
                throw new InvalidOperationException($"Product '{productId}' has a non-finite embedding component.");
        }

        return new(
            EmbeddingVectors.IsAllZero(vector.Span)
                ? ProductVectorDisposition.NoSignal
                : ProductVectorDisposition.Usable,
            resolvedDimensions,
            vector.Length);
    }
}
