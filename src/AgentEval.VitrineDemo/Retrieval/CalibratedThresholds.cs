// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>Retrieval, attribution, and confidence thresholds for one embedding space.</summary>
public sealed record SpaceThresholds(
    float DenseScoreFloor,
    double AttributionFloor,
    double ConfidencePrimary,
    double ConfidenceSecondary);

/// <summary>Central source for embedding-space-specific operating thresholds.</summary>
/// <remarks>
/// The sanitized repository commits the resulting constants, not the original calibration dataset.
/// Their limitations and observed null-tail rates are documented in
/// <c>docs/Vitrine-Retrieval-Deep-Dive.html</c>.
/// </remarks>
public static class CalibratedThresholds
{
    /// <summary>Legacy shared values retained as an explicit comparison baseline.</summary>
    public static SpaceThresholds PreCalibration { get; } = new(
        DenseScoreFloor: 0.28f,
        AttributionFloor: 0.20,
        ConfidencePrimary: 0.70,
        ConfidenceSecondary: 0.45);

    /// <summary>
    /// Operating thresholds for the authored 24-dimensional concept space. The 0.280 dense floor
    /// is intentionally disclosed as permissive; it is not represented as a safety or quality bar.
    /// </summary>
    public static SpaceThresholds Concept { get; } = new(
        DenseScoreFloor: 0.280f,
        AttributionFloor: 0.200,
        ConfidencePrimary: 0.703,
        ConfidenceSecondary: 0.455);

    /// <summary>
    /// Operating thresholds for the <c>text-embedding-3-small</c> space. They remain explicit
    /// configuration, not independently reproducible calibration evidence in this repository.
    /// </summary>
    public static SpaceThresholds RealVectors { get; } = new(
        DenseScoreFloor: 0.223f,
        AttributionFloor: 0.221,
        ConfidencePrimary: 0.520,
        ConfidenceSecondary: 0.437);

    /// <summary>The row for one space.</summary>
    /// <param name="space">A RESOLVED space — never <see cref="EmbeddingSpaceChoice.Auto"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="space"/> is not a resolved space.</exception>
    public static SpaceThresholds For(EmbeddingSpaceChoice space) => space switch
    {
        EmbeddingSpaceChoice.ConceptVectors => Concept,
        EmbeddingSpaceChoice.RealVectors    => RealVectors,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(space),
                 space,
                 "Thresholds are a property of a RESOLVED space. 'Auto' is a request, not a space.")
    };

    /// <summary>
    /// Thresholds for the resolved source. Before resolution, the credential-free concept space is
    /// the deterministic default.
    /// </summary>
    public static SpaceThresholds Current => EmbeddingSpace.Current is { } resolution
        ? For(resolution.Chosen)
        : Concept;
}
