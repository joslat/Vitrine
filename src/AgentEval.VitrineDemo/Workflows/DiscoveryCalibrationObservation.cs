// SPDX-License-Identifier: MIT

using Galaxus.RecommendationAgent.Catalog;
using System.Globalization;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>Typed facts captured from the real calibration consumers.</summary>
public sealed record DiscoveryCalibrationObservation(
    double CoverageMinimumScore,
    double RetrievalConfidenceHalfSaturation,
    double ProbeScore,
    CoverageStatus CoverageStatus,
    double RankerConfidence,
    bool SearchUsesExactDescriptor,
    bool ReviewerUsesExactDescriptor,
    bool RankerUsesExactDescriptor)
{
    public bool EveryNodeUsesExactDescriptor =>
        SearchUsesExactDescriptor && ReviewerUsesExactDescriptor && RankerUsesExactDescriptor;
}

/// <summary>Result of checking the authored distinct-binding sentinel pattern.</summary>
public sealed record DiscoveryCalibrationValidation(bool IsValid, string Detail);

/// <summary>
/// Executes the calibration sentinel through the same coverage, ranker, and composition methods
/// used by a shipped discovery run. It invokes no model and performs no retrieval.
/// </summary>
public static class DiscoveryCalibrationObserver
{
    public const double SentinelCoverageMinimumScore = 0.030;
    public const double SentinelRetrievalConfidenceHalfSaturation = 0.007;
    public const double SentinelProbeScore = 0.020;

    public static DiscoveryCalibrationObservation Capture(DiscoveryCalibrationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var coverage = new InterestCoverage
        {
            InterestId = "I-CALIBRATION",
            BestScore = SentinelProbeScore,
        };
        coverage.QueriesRun.Add("calibration sentinel");
        for (var i = 0; i < DiscoveryState.MinCandidatesForCoverage; i++)
            coverage.CandidateProductIds.Add($"GLX-CALIBRATION-{i}");

        var interest = new Interest
        {
            Id = coverage.InterestId,
            Label = "calibration sentinel",
            Kind = InterestKind.Direct,
            Origin = InterestOrigin.Mapper,
            Confidence = 0,
            EvidenceSignalIds = [],
            Rationale = "Observe independent calibration bindings.",
            QueryTerms = ["calibration sentinel"],
        };
        var candidate = new ProductCandidate(
            coverage.CandidateProductIds[0],
            "Calibration sentinel",
            [],
            new HashSet<string>(StringComparer.Ordinal),
            SentinelProbeScore,
            interest.Id,
            "calibration sentinel",
            [],
            [],
            0,
            0);

        var nodes = GalaxusDiscoveryLoop.BuildNodes(
            Catalogue.Default,
            new AlwaysFreshRetriever(Catalogue.Default),
            chatClient: null,
            NullDiscoveryProgressSink.Instance,
            calibration: policy);

        return new(
            policy.CoverageMinimumScore,
            policy.RetrievalConfidenceHalfSaturation,
            SentinelProbeScore,
            CatalogueDiscoverySearch.ClassifyCoverage(coverage, policy),
            DeterministicRanker.Confidence(interest, candidate, policy),
            nodes.Search is CatalogueDiscoverySearch search && ReferenceEquals(search.Calibration, policy),
            nodes.Reviewer is DeterministicCoverageReviewer reviewer && ReferenceEquals(reviewer.Calibration, policy),
            nodes.Ranker is DeterministicRanker ranker && ReferenceEquals(ranker.Calibration, policy));
    }

    public static DiscoveryCalibrationValidation ValidateDistinctPattern(
        DiscoveryCalibrationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var parametersMatch =
            Math.Abs(observation.CoverageMinimumScore - SentinelCoverageMinimumScore) < 1e-12 &&
            Math.Abs(observation.RetrievalConfidenceHalfSaturation -
                SentinelRetrievalConfidenceHalfSaturation) < 1e-12 &&
            Math.Abs(observation.ProbeScore - SentinelProbeScore) < 1e-12;
        var expectedRankerConfidence =
            SentinelProbeScore / (SentinelProbeScore + SentinelRetrievalConfidenceHalfSaturation) / 2.0;
        var behaviorMatches =
            observation.CoverageStatus == CoverageStatus.Uncovered &&
            Math.Abs(observation.RankerConfidence - expectedRankerConfidence) < 1e-12;
        var valid = parametersMatch && behaviorMatches && observation.EveryNodeUsesExactDescriptor;

        return new(valid, string.Create(CultureInfo.InvariantCulture,
            $"coverage={observation.CoverageMinimumScore:0.000}:{observation.CoverageStatus}; " +
            $"confidence-shape={observation.RetrievalConfidenceHalfSaturation:0.000}:" +
            $"{observation.RankerConfidence:0.000}; exact-node-bindings={observation.EveryNodeUsesExactDescriptor}"));
    }
}
