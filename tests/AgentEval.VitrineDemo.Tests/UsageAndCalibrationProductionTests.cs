// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.ViewModels;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Extensions.AI;

namespace AgentEval.VitrineDemo.Tests;

public sealed class UsageAndCalibrationProductionTests
{
    [Fact]
    public void Demo01UsageKeepsAbsenceZeroMeasurementAndLowerBoundDistinct()
    {
        var missing = ProviderUsageMeasurement.FromDemo01(1, null);
        var zero = ProviderUsageMeasurement.FromDemo01(0, null);
        var measured = ProviderUsageMeasurement.FromDemo01(1, new UsageDetails
        {
            InputTokenCount = 12,
            OutputTokenCount = 3,
        });
        var lowerBound = ProviderUsageMeasurement.FromDemo01(1, new UsageDetails
        {
            InputTokenCount = 12,
        });

        Assert.Equal(ProviderUsageStatus.Missing, missing.Status);
        Assert.Null(missing.TotalTokens);
        Assert.Equal(ProviderUsageStatus.MeasuredZero, zero.Status);
        Assert.Equal(0, zero.TotalTokens);
        Assert.Equal(ProviderUsageStatus.Measured, measured.Status);
        Assert.Equal(15, measured.TotalTokens);
        Assert.Equal(ProviderUsageStatus.LowerBound, lowerBound.Status);
        Assert.Equal(12, lowerBound.TotalTokens);
        Assert.Null(lowerBound.CompletionTokens);
        Assert.Equal("NOT MEASURED", missing.ToDisplayString());
        Assert.Contains("lower bound", lowerBound.ToDisplayString(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => ProviderUsageMeasurement.FromDemo01(0,
            new UsageDetails { TotalTokenCount = 1 }));
        Assert.Throws<InvalidOperationException>(() => ProviderUsageMeasurement.FromDemo01(1,
            new UsageDetails { InputTokenCount = 4, OutputTokenCount = 2, TotalTokenCount = 1 }));
    }

    [Fact]
    public void Demo02UsageKeepsAbsenceZeroMeasurementAndLowerBoundDistinct()
    {
        var missing = ProviderUsageMeasurement.FromDemo02(new(0, 0, 2, 0, 0));
        var zeroCalls = ProviderUsageMeasurement.FromDemo02(new(0, 0, 0, 0, 0));
        var measuredZero = ProviderUsageMeasurement.FromDemo02(new(1, 0, 0, 0, 0));
        var measured = ProviderUsageMeasurement.FromDemo02(new(1, 0, 0, 20, 5));
        var lowerBound = ProviderUsageMeasurement.FromDemo02(new(1, 1, 1, 20, 5));

        Assert.Equal(ProviderUsageStatus.Missing, missing.Status);
        Assert.Null(missing.TotalTokens);
        Assert.Equal(ProviderUsageStatus.MeasuredZero, zeroCalls.Status);
        Assert.Equal(ProviderUsageStatus.MeasuredZero, measuredZero.Status);
        Assert.Equal(ProviderUsageStatus.Measured, measured.Status);
        Assert.Equal(25, measured.TotalTokens);
        Assert.Equal(ProviderUsageStatus.LowerBound, lowerBound.Status);
        Assert.Equal(25, lowerBound.TotalTokens);
        Assert.Throws<InvalidOperationException>(() =>
            ProviderUsageMeasurement.FromDemo02(new(0, 0, 0, 1, 0)));
    }

    [Fact]
    public async Task AppSummaryConsumesTheSameUsageProjectionAsTheTypedRunResult()
    {
        await using var viewModel = new MainWindowViewModel();
        viewModel.Setup.SelectedArm = viewModel.Setup.Arms.Single(
            static arm => arm.Arm == Galaxus.RecommendationAgent.Demos.RecommendationExecutionArm.ZeroModelBaseline);
        viewModel.Setup.AudiencePacingMilliseconds = 0;

        await viewModel.RunSelectedModeAsync();

        var run = viewModel.LastOutcome?.Recommendation;
        Assert.NotNull(run);
        Assert.Equal(ProviderUsageStatus.MeasuredZero, run.ProviderUsage.Status);
        Assert.Contains($"usage {run.ProviderUsage.ToDisplayString()}", viewModel.ResultSummary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctCalibrationBindingsFlowThroughActualCoverageAndRankerCallSites()
    {
        var distinct = new DiscoveryCalibrationPolicy(
            coverageMinimumScore: 0.030,
            retrievalConfidenceHalfSaturation: 0.007);
        var aliased = new DiscoveryCalibrationPolicy(
            coverageMinimumScore: 0.030,
            retrievalConfidenceHalfSaturation: 0.030);
        var nodes = GalaxusDiscoveryLoop.BuildNodes(
            Catalogue.Default,
            new AlwaysFreshRetriever(Catalogue.Default),
            chatClient: null,
            NullDiscoveryProgressSink.Instance,
            calibration: distinct);
        var coverage = new InterestCoverage
        {
            InterestId = "I-1",
            BestScore = 0.020,
        };
        coverage.QueriesRun.Add("sentinel query");
        for (var i = 0; i < DiscoveryState.MinCandidatesForCoverage; i++)
            coverage.CandidateProductIds.Add($"GLX-{i}");
        var interest = new Interest
        {
            Id = "I-1",
            Label = "sentinel",
            Kind = InterestKind.Direct,
            Origin = InterestOrigin.Mapper,
            Confidence = 0,
            EvidenceSignalIds = [],
            Rationale = "calibration binding test",
            QueryTerms = ["sentinel"],
        };
        var candidate = new ProductCandidate(
            "GLX-0", "sentinel", [], new HashSet<string>(StringComparer.Ordinal),
            0.020, "I-1", "sentinel", [], [], 0, 0);

        var coverageStatus = CatalogueDiscoverySearch.ClassifyCoverage(coverage, distinct);
        var distinctConfidence = DeterministicRanker.Confidence(interest, candidate, distinct);
        var aliasedConfidence = DeterministicRanker.Confidence(interest, candidate, aliased);

        Assert.Equal(CoverageStatus.Uncovered, coverageStatus);
        Assert.True(distinctConfidence > 0.35, $"distinct confidence was {distinctConfidence}");
        Assert.True(aliasedConfidence < 0.25, $"aliased confidence was {aliasedConfidence}");
        Assert.NotEqual(distinctConfidence, aliasedConfidence);
        Assert.Same(distinct, ((CatalogueDiscoverySearch)nodes.Search).Calibration);
        Assert.Same(distinct, ((DeterministicCoverageReviewer)nodes.Reviewer).Calibration);
        Assert.Same(distinct, ((DeterministicRanker)nodes.Ranker).Calibration);
    }

    [Fact]
    public void CompactCalibrationObservationAcceptsDistinctBindingsAndRejectsAliasing()
    {
        var distinct = new DiscoveryCalibrationPolicy(
            DiscoveryCalibrationObserver.SentinelCoverageMinimumScore,
            DiscoveryCalibrationObserver.SentinelRetrievalConfidenceHalfSaturation);
        var aliased = new DiscoveryCalibrationPolicy(
            DiscoveryCalibrationObserver.SentinelCoverageMinimumScore,
            DiscoveryCalibrationObserver.SentinelCoverageMinimumScore);

        var healthy = DiscoveryCalibrationObserver.Capture(distinct);
        var broken = DiscoveryCalibrationObserver.Capture(aliased);
        var healthyValidation = DiscoveryCalibrationObserver.ValidateDistinctPattern(healthy);
        var brokenValidation = DiscoveryCalibrationObserver.ValidateDistinctPattern(broken);

        Assert.True(healthyValidation.IsValid, healthyValidation.Detail);
        Assert.False(brokenValidation.IsValid, brokenValidation.Detail);
        Assert.True(healthy.EveryNodeUsesExactDescriptor);
        Assert.True(broken.EveryNodeUsesExactDescriptor);
        Assert.NotEqual(healthy.RankerConfidence, broken.RankerConfidence);
    }
}
