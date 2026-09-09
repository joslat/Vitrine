// SPDX-License-Identifier: MIT

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.Evals;

namespace AgentEval.VitrineDemo.Tests;

public sealed class ProductionVariantBenchmarkTests
{
    [Fact]
    public async Task SystemVariantsAreDistinctRunsAndDegradedArmsLoseNatively()
    {
        var root = NewOutputRoot();
        try
        {
            var judged = await ProductionVariantBenchmarks.RunJudgedQualityAsync(
                JudgedQualityProductionEval.Input(new(100, 4, 1, 1, 1), "authored request"),
                JudgedQualityProductionEval.Input(new(100, 4, 1, 1, 1), "authored request"),
                outputRoot: root);
            var injection = await ProductionVariantBenchmarks.RunInjectionAsync(
                InjectionProductionEval.Input(new(4, 4, 0, 0, 2, 2, 0, 0, 0)),
                InjectionProductionEval.Input(new(4, 0, 4, 0, 2, 0, 2, 0, 2)),
                outputRoot: root);
            var recall = await ProductionVariantBenchmarks.RunRecallAsync(
                RecallProductionEval.Input(new(1, 1, 100)),
                RecallProductionEval.Input(new(1, 1, 0)),
                outputRoot: root);

            await AssertTwoPersistedArmsAsync(judged);
            await AssertTwoPersistedArmsAsync(injection);
            await AssertTwoPersistedArmsAsync(recall);

            Assert.Equal((0, 0, 1),
                (judged.ReferenceComparison.Wins, judged.ReferenceComparison.Losses,
                    judged.ReferenceComparison.Ties));
            Assert.Equal((0, 1, 0),
                (injection.ReferenceComparison.Wins, injection.ReferenceComparison.Losses,
                    injection.ReferenceComparison.Ties));
            Assert.Equal((0, 1, 0),
                (recall.ReferenceComparison.Wins, recall.ReferenceComparison.Losses,
                    recall.ReferenceComparison.Ties));

            Assert.True(injection.Arm(ProductionVariantBenchmarks.InjectionSafeArmId).Result.Score.Passed);
            Assert.False(injection.Arm(ProductionVariantBenchmarks.InjectionVulnerableArmId).Result.Score.Passed);
            Assert.True(recall.Arm(ProductionVariantBenchmarks.RecallHealthyArmId).Result.Score.Passed);
            Assert.False(recall.Arm(ProductionVariantBenchmarks.RecallAblatedArmId).Result.Score.Passed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingAndFailedArmObservationsRemainDistinctCensusStates()
    {
        var root = NewOutputRoot();
        try
        {
            var outcome = await ProductionVariantBenchmarks.RunJudgedQualityAsync(
                JudgedQualityProductionEval.Input(null, "authored request"),
                JudgedQualityProductionEval.FailedInput("judge recorder failed", "authored request"),
                outputRoot: root);

            var absent = outcome.Arm(ProductionVariantBenchmarks.JudgedDemo01ArmId);
            var failed = outcome.Arm(ProductionVariantBenchmarks.JudgedDemo02ArmId);

            Assert.Equal(new ObservationCensus(0, 1, 0), absent.Census);
            Assert.Equal(new ObservationCensus(0, 0, 1), failed.Census);
            Assert.Equal(MeasurementState.NotApplicable, absent.Result.Score.CensusBucket());
            Assert.Equal(MeasurementState.NotMeasured, failed.Result.Score.CensusBucket());
            Assert.Equal(0, outcome.ReferenceComparison.EffectiveN);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertTwoPersistedArmsAsync(ProductionVariantBenchmarkOutcome outcome)
    {
        Assert.Single(outcome.Definition.Cases);
        var admitted = Assert.Single(outcome.Definition.Checks);
        Assert.Equal(FloorState.NotDerivable, admitted.Floor.State);
        Assert.False(string.IsNullOrWhiteSpace(admitted.Floor.Derivation));
        Assert.Equal(2, outcome.Arms.Count);
        Assert.Equal(2, outcome.Arms.Select(static arm => arm.ArmId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, outcome.Arms.Select(static arm => arm.Run.RunId).Distinct(StringComparer.Ordinal).Count());
        Assert.True(Directory.Exists(outcome.WorkspaceRoot));

        foreach (var arm in outcome.Arms)
        {
            Assert.Single(arm.Run.Observations);
            Assert.Equal(arm.ArmId, arm.Run.ArmId);
            Assert.True(Directory.Exists(arm.RunDirectory));
            Assert.Equal(new ObservationCensus(1, 0, 0), arm.Census);
            var manifest = await outcome.Store.GetRunManifestAsync(arm.Run.RunId);
            var summary = await outcome.Store.GetRunSummaryAsync(arm.Run.RunId);
            Assert.NotNull(manifest);
            Assert.NotNull(summary);
            Assert.Equal(BenchmarkRunner.RunKind, manifest!.Run.Kind);
            Assert.Equal($"BenchmarkRunner/{outcome.Definition.Key}@{outcome.Definition.Version}",
                manifest.Run.Harness);
            Assert.Equal(1, summary!.Stats.Total);
        }
    }

    private static string NewOutputRoot() =>
        Path.Combine(Path.GetTempPath(), "vitrine-production-variants", Guid.NewGuid().ToString("N"));
}
