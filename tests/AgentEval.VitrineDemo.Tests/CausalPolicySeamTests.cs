// SPDX-License-Identifier: MIT

using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.Evals;

namespace AgentEval.VitrineDemo.Tests;

public sealed class CausalPolicySeamTests
{
    [Fact]
    public void ForcedChoiceCalibrationUsesNativeCaseCensusAndFloorComparison()
    {
        var captured = ForcedChoiceCalibrationFixture.Capture();

        Assert.Equal(3, captured.Cases.Length);
        Assert.Equal(3, captured.Cases.Select(static item => item.CaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new ObservationCensus(3, 0, 0), captured.Comparison.Census);
        Assert.Equal((2, 3), (captured.Comparison.Successes, captured.Comparison.Trials));
        Assert.True(captured.Comparison.AboveFloor);
        Assert.True(double.IsFinite(captured.Comparison.PValue));
        Assert.Equal(FloorState.Derived, VitrineEvalCriteria.PersonaForcedChoiceFloor.State);
    }

    [Fact]
    public void EveryAdmittedCheckMustTurnRedUnderItsAblation()
    {
        var production = VitrineProductionChecks.ExpectedKeys
            .Select(static key => new VitrineAdmittedCheckSelfTestFact(
                key, "production", HealthyPassed: true, AblationWentRed: true, "fixture"))
            .ToArray();
        var benchmark = VitrineOfflineBenchmark.CheckKeys
            .Select(static key => new VitrineAdmittedCheckSelfTestFact(
                key, "benchmark", HealthyPassed: true, AblationWentRed: true, "fixture"))
            .ToArray();

        Assert.True(AdmittedCheckDiagnostics.EveryProductionAblationWentRed(production));
        Assert.True(AdmittedCheckDiagnostics.EveryBenchmarkAblationWentRed(benchmark));
        Assert.False(AdmittedCheckDiagnostics.EveryProductionAblationWentRed(
            AdmittedCheckDiagnostics.PlantAblationSurvival(production)));
        Assert.False(AdmittedCheckDiagnostics.EveryBenchmarkAblationWentRed(
            AdmittedCheckDiagnostics.PlantAblationSurvival(benchmark)));
    }

    [Fact]
    public void BenchmarkDiagnosticsRequirePositiveCountsAndDegradedReferenceLosses()
    {
        var benchmark = BenchmarkFixture();

        Assert.True(AdmittedCheckDiagnostics.HasPositiveBenchmarkCountsBeforeValues(benchmark));
        Assert.True(AdmittedCheckDiagnostics.DegradedArmLosesAgainstReference(benchmark));
        Assert.False(AdmittedCheckDiagnostics.HasPositiveBenchmarkCountsBeforeValues(
            AdmittedCheckDiagnostics.PlantZeroTrialCount(benchmark)));
        Assert.False(AdmittedCheckDiagnostics.DegradedArmLosesAgainstReference(
            AdmittedCheckDiagnostics.PlantDegradedTie(benchmark)));
    }

    private static VitrineOfflineBenchmarkResult BenchmarkFixture()
    {
        var floor = new VitrineChanceFloorFact(
            "not-derivable", "NotDerivable", null, null, null, 0, 0,
            "fixture has no authored random-choice population");
        IReadOnlyList<VitrineBenchmarkCheckFact> Checks(int successes) =>
            VitrineOfflineBenchmark.CheckKeys.Select(key => new VitrineBenchmarkCheckFact(
                key, floor, new(2, 0, 0), successes, 2, null, null, null, false)).ToArray();
        var cases = new[]
        {
            new VitrineBenchmarkCase(VitrineOfflineBenchmark.NadiaCaseId, "Nadia"),
            new VitrineBenchmarkCase(VitrineOfflineBenchmark.SofiaCaseId, "Sofia"),
        };
        var result = new VitrineOfflineBenchmarkResult(
            VitrineOfflineBenchmark.DefinitionKey,
            VitrineOfflineBenchmark.DefinitionVersion,
            VitrineOfflineBenchmark.Demo01ArmId,
            "reference-run",
            "fixture-root",
            "fixture-directory",
            cases,
            Checks(2));
        var arms = new[]
        {
            new VitrineBenchmarkArmFact(VitrineOfflineBenchmark.Demo01ArmId, "Agent", "Demo01", Checks(2)),
            new VitrineBenchmarkArmFact(VitrineOfflineBenchmark.Demo02ArmId, "Workflow", "Demo02", Checks(2)),
            new VitrineBenchmarkArmFact(VitrineOfflineBenchmark.DegradedArmId, "Agent", "Degraded", Checks(0)),
        };
        var runs = Enumerable.Range(1, 2).SelectMany(repetition => arms.Select(arm =>
            new VitrineBenchmarkRunFact(
                arm.ArmId, repetition, arm.SubjectKind, arm.SubjectName,
                $"{arm.ArmId}-{repetition}", $"fixture/{arm.ArmId}-{repetition}"))).ToArray();
        var comparisons = VitrineOfflineBenchmark.CheckKeys.Select(key => new VitrineBenchmarkReferenceFact(
            key,
            VitrineOfflineBenchmark.Demo01ArmId,
            VitrineOfflineBenchmark.DegradedArmId,
            Wins: 0,
            Losses: 2,
            Ties: 0,
            EffectiveN: 2,
            PValue: null,
            MinimumAttainableP: null,
            MeanDelta: -1,
            Census: new(2, 0, 0),
            Cases: 2,
            TotalRepObservations: 8,
            RepCollapse: nameof(RepCollapse.All),
            UnderpoweredByConstruction: true)).ToArray();
        return result with
        {
            Repetitions = 2,
            Arms = arms,
            Runs = runs,
            ReferenceComparisons = comparisons,
        };
    }
}
