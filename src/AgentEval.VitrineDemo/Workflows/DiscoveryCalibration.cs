// SPDX-License-Identifier: MIT

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// The two independently calibrated discovery parameters. Passing this descriptor through the
/// real coverage and ranker call sites makes accidental aliasing executable and testable.
/// </summary>
public sealed record DiscoveryCalibrationPolicy
{
    public DiscoveryCalibrationPolicy(double coverageMinimumScore, double retrievalConfidenceHalfSaturation)
    {
        if (!double.IsFinite(coverageMinimumScore) || coverageMinimumScore < 0)
            throw new ArgumentOutOfRangeException(nameof(coverageMinimumScore));
        if (!double.IsFinite(retrievalConfidenceHalfSaturation) || retrievalConfidenceHalfSaturation <= 0)
            throw new ArgumentOutOfRangeException(nameof(retrievalConfidenceHalfSaturation));

        CoverageMinimumScore = coverageMinimumScore;
        RetrievalConfidenceHalfSaturation = retrievalConfidenceHalfSaturation;
    }

    /// <summary>The score cut used only by the coverage decision.</summary>
    public double CoverageMinimumScore { get; }

    /// <summary>The half-saturation used only to shape ranker confidence.</summary>
    public double RetrievalConfidenceHalfSaturation { get; }

    /// <summary>The descriptor used by shipped workflow composition.</summary>
    public static DiscoveryCalibrationPolicy Shipped { get; } = new(
        DiscoveryState.MinCandidateScore,
        DiscoveryState.RetrievalConfidenceHalfSaturation);

    public bool MeetsCoverage(double score) =>
        double.IsFinite(score) && score >= CoverageMinimumScore;

    public double ShapeRetrievalConfidence(double score) =>
        score <= 0 ? 0 : score / (score + RetrievalConfidenceHalfSaturation);
}

/// <summary>Compatibility façade over the shipped typed calibration descriptor.</summary>
public static class DiscoveryCalibration
{
    public static bool MeetsCoverage(
        double score,
        double minimumCandidateScore = DiscoveryState.MinCandidateScore) =>
        minimumCandidateScore == DiscoveryCalibrationPolicy.Shipped.CoverageMinimumScore
            ? DiscoveryCalibrationPolicy.Shipped.MeetsCoverage(score)
            : new DiscoveryCalibrationPolicy(
                minimumCandidateScore,
                DiscoveryCalibrationPolicy.Shipped.RetrievalConfidenceHalfSaturation)
                .MeetsCoverage(score);

    public static double ShapeRetrievalConfidence(
        double score,
        double halfSaturation = DiscoveryState.RetrievalConfidenceHalfSaturation) =>
        halfSaturation == DiscoveryCalibrationPolicy.Shipped.RetrievalConfidenceHalfSaturation
            ? DiscoveryCalibrationPolicy.Shipped.ShapeRetrievalConfidence(score)
            : new DiscoveryCalibrationPolicy(
                DiscoveryCalibrationPolicy.Shipped.CoverageMinimumScore,
                halfSaturation)
                .ShapeRetrievalConfidence(score);
}
