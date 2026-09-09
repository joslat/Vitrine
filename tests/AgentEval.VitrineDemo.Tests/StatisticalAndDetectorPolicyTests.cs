// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.Evals;
using AgentEval.Evals.Meta;
using Galaxus.RecommendationAgent.Evaluation;

namespace AgentEval.VitrineDemo.Tests;

public sealed class StatisticalAndDetectorPolicyTests
{
    [Fact]
    public void Broken02PolicyRequiresEveryNamedCaseClassOperand()
    {
        var healthy = CanonicalBroken02Observation();
        Assert.Equal(healthy.Firings, Broken02OperandPolicy.RequiredFirings);
        var healthyVerdict = Broken02OperandPolicy.Evaluate(healthy);

        Assert.True(healthyVerdict.Tripped);
        Assert.Empty(healthyVerdict.MissingRequiredFirings);
        Assert.Contains("required firings=3/3", healthyVerdict.Describe(), StringComparison.Ordinal);

        foreach (var required in Broken02OperandPolicy.RequiredFirings)
        {
            var broken = healthy with
            {
                Firings = healthy.Firings.Where(candidate => candidate != required).ToArray(),
            };

            var brokenVerdict = Broken02OperandPolicy.Evaluate(broken);
            Assert.False(brokenVerdict.Tripped);
            Assert.Equal(required, Assert.Single(brokenVerdict.MissingRequiredFirings));

            var restoredVerdict = Broken02OperandPolicy.Evaluate(healthy);
            Assert.True(restoredVerdict.Tripped);
        }
    }

    [Fact]
    public void Broken02PolicyRejectsGlobalCountsThatFiredOnWrongCases()
    {
        DetectorFiring[] wrongCases =
        [
            new("C-06", Broken02OperandPolicy.SuppressedSignalLeak),
            new("C-08", Broken02OperandPolicy.UnresolvableEvidence),
            new("C-10", Broken02OperandPolicy.UnauthorisedAction),
        ];

        var verdict = Broken02OperandPolicy.Evaluate(new(false, 0, wrongCases));

        Assert.False(verdict.Tripped);
        Assert.Equal(Broken02OperandPolicy.RequiredFirings, verdict.MissingRequiredFirings);
    }

    [Fact]
    public void Broken02EnvelopeConditionsAreAlsoLoadBearing()
    {
        var observation = CanonicalBroken02Observation();

        Assert.False(Broken02OperandPolicy.Evaluate(observation with { OverallGatePassed = true }).Tripped);
        Assert.False(Broken02OperandPolicy.Evaluate(observation with { PhantomSkuCount = 1 }).Tripped);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Broken02OperandPolicy.Evaluate(observation with { PhantomSkuCount = -1 }));
    }

    [Fact]
    public void CommittedNextPurchaseComparisonUsesExactTwoSidedArithmetic()
    {
        var comparer = new PairedEvalComparer(RepCollapse.All);
        for (var index = 0; index < 13; index++)
        {
            var caseId = $"NP-{index + 1:00}";
            var reference = index == 3 ? 1 : 0;
            var challenger = index < 3 ? 1 : 0;
            comparer.Record(Observation.Measured(caseId, "reference", reference));
            comparer.Record(Observation.Measured(caseId, "challenger", challenger));
        }
        var result = comparer.Compare("reference", "challenger");

        Assert.Equal((3, 1, 9), (result.Wins, result.Losses, result.Ties));
        Assert.Equal(4, result.EffectiveN);
        AssertClose(0.625, result.PValue);
        AssertClose(0.125, result.MinimumAttainableP);
        Assert.Equal(new ObservationCensus(13, 0, 0), result.Census);
        Assert.Equal(new ObservationUnit(13, 26, 1, RepCollapse.All), result.Unit);
        Assert.True(result.UnderpoweredByConstruction);
        Assert.True(result.ChallengerLeads);
    }

    [Fact]
    public void ExactSignPowerBoundaryIsFiveVersusSixInformativePairs()
    {
        var fourP = ExactTests.TwoSidedSignP(4, 4);
        var fourMinimum = ExactTests.MinimumAttainableP(4);
        var fiveP = ExactTests.TwoSidedSignP(5, 5);
        var fiveMinimum = ExactTests.MinimumAttainableP(5);
        var sixP = ExactTests.TwoSidedSignP(6, 6);
        var sixMinimum = ExactTests.MinimumAttainableP(6);
        var reverseSixP = ExactTests.TwoSidedSignP(0, 6);

        AssertClose(0.125, fourP);
        AssertClose(0.125, fourMinimum);
        AssertClose(0.0625, fiveP);
        Assert.True(fiveMinimum > ExactTests.DefaultAlpha);
        Assert.True(fiveP > ExactTests.DefaultAlpha);
        AssertClose(0.03125, sixP);
        AssertClose(0.03125, sixMinimum);
        Assert.True(sixMinimum <= ExactTests.DefaultAlpha);
        Assert.True(sixP <= ExactTests.DefaultAlpha);
        AssertClose(sixP, reverseSixP);
    }

    [Fact]
    public void ArtifactSuppliedExactFieldsCannotAuthorTheHonestyInterpretation()
    {
        var evidence = HonestyEvidenceLoader.Load().Evidence
            ?? throw new InvalidOperationException("The committed honesty evidence did not load.");
        var tampered = evidence with
        {
            NextPurchase = evidence.NextPurchase with
            {
                MinimumAttainableTwoSidedP = 0.01,
            },
        };

        var claims = HonestyInterpretation.Build(tampered);

        Assert.Contains("minimum attainable two-sided p = 0.125", claims.NextPurchasePrediction,
            StringComparison.Ordinal);
        Assert.Equal("Add informative pairs before making a next-purchase prediction claim.",
            claims.NextPurchaseRemedy);
        Assert.Equal(HonestyInterpretation.NextPurchaseExactMethod, claims.NextPurchaseMethod);
        Assert.False(HonestyInterpretation.Validate(tampered, claims));
    }

    [Fact]
    public void PairedComparerDistinguishesAllTiesFromNoObservations()
    {
        var comparer = new PairedEvalComparer(RepCollapse.All);
        for (var index = 0; index < 4; index++)
        {
            var caseId = $"tie-{index}";
            comparer.Record(Observation.Measured(caseId, "reference", 1));
            comparer.Record(Observation.Measured(caseId, "challenger", 1));
        }
        var ties = comparer.Compare("reference", "challenger");
        var empty = new PairedEvalComparer(RepCollapse.All).Compare("reference", "challenger");

        Assert.True(ties.Undecidable);
        AssertClose(1.0, ties.PValue);
        AssertClose(1.0, ties.MinimumAttainableP);
        Assert.Equal(new ObservationCensus(4, 0, 0), ties.Census);
        Assert.True(empty.Undecidable);
        AssertClose(1.0, empty.PValue);
        AssertClose(1.0, empty.MinimumAttainableP);
        Assert.Equal(new ObservationCensus(0, 0, 0), empty.Census);
    }

    [Fact]
    public void NativeObservationAndCollapseRejectInvalidMeasurements()
    {
        Assert.Throws<ArgumentException>(() =>
            Observation.Measured("case", "arm", double.NaN));
        Assert.Throws<ArgumentException>(() =>
            ObservationUnit.Collapse([], RepCollapse.Majority));
    }

    [Fact]
    public void ForcedChoiceCountsAuthoredPersonaCasesDirectly()
    {
        Observation[] cases =
        [
            Observation.Measured("P1", "persona-choice", 1),
            Observation.Measured("P2", "persona-choice", 0),
            Observation.Measured("P3", "persona-choice", 1),
        ];

        var result = FloorComparison.Compute(
            cases, "persona-choice", VitrineEvalCriteria.PersonaForcedChoiceFloor);

        Assert.Equal((2, 3), (result.Successes, result.Trials));
        Assert.Equal(new ObservationCensus(3, 0, 0), result.Census);
        Assert.Equal(new[] { "P1", "P2", "P3" }, cases.Select(static item => item.CaseId));
    }

    [Fact]
    public void NativeMajorityCollapseTreatsATiedRepSetAsALoss()
    {
        Assert.Equal(1, ObservationUnit.Collapse([1, 1, 0], RepCollapse.Majority));
        Assert.Equal(0, ObservationUnit.Collapse([1, 0], RepCollapse.Majority));
    }

    [Fact]
    public void EmptyForcedChoiceDenominatorIsNotAZeroScoreOrPValue()
    {
        var missing = Observation.NotMeasured("P1", "persona-choice");
        var comparison = FloorComparison.Compute(
            [missing], "persona-choice", VitrineEvalCriteria.PersonaForcedChoiceFloor);

        Assert.Equal(MeasurementState.NotMeasured, missing.State);
        Assert.Equal(0, comparison.Trials);
        Assert.Equal(new ObservationCensus(0, 0, 1), comparison.Census);
        Assert.True(comparison.Census.Void);
        Assert.True(double.IsNaN(comparison.PValue));
        Assert.False(comparison.AboveFloor);
        Assert.Contains("1 not measured", comparison.Census.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void ForcedChoiceExactTailUsesAuthoredFloorNotWhoHappenedToRun()
    {
        var personaCase = Observation.Measured("P1", "persona-choice", 1);
        var comparison = FloorComparison.Compute(
            [personaCase], "persona-choice", VitrineEvalCriteria.PersonaForcedChoiceFloor);

        Assert.Equal(1, comparison.Trials);
        Assert.Equal(VitrineEvalCriteria.PersonaCount, VitrineEvalCriteria.PersonaForcedChoiceFloor.PoolSize);
        AssertClose(1.0 / VitrineEvalCriteria.PersonaCount, comparison.FloorUsed);
        AssertClose(1.0 / VitrineEvalCriteria.PersonaCount, comparison.PValue);
        Assert.True(comparison.UnderpoweredByConstruction);
        Assert.False(comparison.AboveFloor);
    }

    [Fact]
    public void FractionalRepMeansCannotMasqueradeAsPersonaTrials()
    {
        var repMean = Observation.Measured("P1", "persona-choice", 2.0 / 3.0);

        Assert.Throws<ArgumentException>(() => FloorComparison.Compute(
            [repMean], "persona-choice", VitrineEvalCriteria.PersonaForcedChoiceFloor));
    }

    private static Broken02DetectorObservation CanonicalBroken02Observation() => new(false, 0,
    [
        new("C-05", "D3"),
        new("C-07", "D5"),
        new("C-09", "D4"),
    ]);

    private static void AssertClose(double expected, double? actual, double tolerance = 1e-12)
    {
        Assert.True(actual.HasValue, "Expected a measured numeric result.");
        Assert.InRange(Math.Abs(expected - actual.Value), 0, tolerance);
    }
}
