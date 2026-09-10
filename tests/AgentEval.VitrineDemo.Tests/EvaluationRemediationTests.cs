// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Reflection;
using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.Evals;

namespace AgentEval.VitrineDemo.Tests;

public sealed class EvaluationRemediationTests
{
    [Fact]
    public void CriteriaRegistryIsImmutableUniqueAndOwnsItsDecisionPolicy()
    {
        VitrineEvalCriteria.Validate();

        Assert.Equal(4, VitrineEvalCriteria.JudgedCriteria.Length);
        Assert.Equal(4, VitrineEvalCriteria.JudgedCriteria.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(VitrineEvalCriteria.JudgedCriteria, criterion =>
        {
            Assert.Equal(VitrineEvalCriteria.JudgedPassingScore, criterion.PassingScore);
            Assert.Equal(FloorState.NotDerivable, criterion.Floor.State);
            Assert.False(string.IsNullOrWhiteSpace(criterion.Floor.Derivation));
            Assert.False(string.IsNullOrWhiteSpace(criterion.Provenance));
        });
        Assert.DoesNotContain(
            typeof(VitrineEvalCriteria).GetFields(BindingFlags.Public | BindingFlags.Static),
            field => field.FieldType.IsArray);
    }

    [Fact]
    public void NativeForcedChoiceFloorPreservesDerivationAndExactPowerLimit()
    {
        Assert.Equal(FloorState.Derived, VitrineEvalCriteria.PersonaForcedChoiceFloor.State);
        Assert.Equal(1.0 / 14, VitrineEvalCriteria.PersonaForcedChoiceFloor.Value, 12);
        Assert.Contains("uniform", VitrineEvalCriteria.PersonaForcedChoiceFloor.Derivation,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0.125, ExactTests.MinimumAttainableP(4), 12);
        Assert.True(ExactTests.MinimumAttainableP(4) > ExactTests.DefaultAlpha);
    }

    [Fact]
    public void MissingMatchedSubjectCountNeverRendersAsNumericZero()
    {
        Assert.Equal("NOT MEASURED", EvaluationSuite.DescribeOptionalCount(null));
        Assert.Equal("0", EvaluationSuite.DescribeOptionalCount(0));
        Assert.NotEqual(EvaluationSuite.DescribeOptionalCount(null), EvaluationSuite.DescribeOptionalCount(0));
    }

    [Fact]
    public void HonestyEvidenceMustBePresentUntamperedAndInternallyConsistent()
    {
        var committed = HonestyEvidenceLoader.Load();
        Assert.True(committed.Measured, committed.FailureKind);
        Assert.Equal(12, committed.Evidence!.StatedNeed.CaseIds.Count);
        Assert.Equal(4, committed.Evidence.NextPurchase.InformativePairs);
        Assert.True(HonestyInterpretation.Validate(committed.Evidence, HonestyInterpretation.Build(committed.Evidence)));
        var flatteringEvidence = committed.Evidence with
        {
            StatedNeed = committed.Evidence.StatedNeed with { LiveScore = 0.777 },
        };
        Assert.False(HonestyInterpretation.Validate(flatteringEvidence, HonestyInterpretation.Build(flatteringEvidence)));

        var originalPath = Path.Combine(AppContext.BaseDirectory, "Data", HonestyEvidenceLoader.FileName);
        var tamperedPath = Path.Combine(Path.GetTempPath(), $"vitrine-honesty-{Guid.NewGuid():N}.json");
        try
        {
            var bytes = File.ReadAllBytes(originalPath);
            bytes[^2] ^= 1;
            File.WriteAllBytes(tamperedPath, bytes);

            var tampered = HonestyEvidenceLoader.Load(tamperedPath);
            Assert.False(tampered.Measured);
            Assert.Equal("EvidenceIntegrityMismatch", tampered.FailureKind);
            Assert.False(HonestyEvidenceLoader.Load(tamperedPath + ".missing").Measured);
        }
        finally
        {
            if (File.Exists(tamperedPath)) File.Delete(tamperedPath);
        }
    }

    [Fact]
    public void SyntheticReportsNeverPrintHistoricalShownClaimsWithoutValidatedEvidence()
    {
        var suite = new SuiteResult([
            new GateResult(
                "synthetic",
                true,
                1,
                ChanceFloor.NotDerivable("the synthetic report has no comparison policy"),
                "synthetic"),
        ], []);

        var json = EvaluationReportJson.Render(suite);
        var html = EvaluationReportHtml.Render(suite);

        Assert.Contains("\"interpretationStatus\": \"notMeasured\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0.889", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0.889", html, StringComparison.Ordinal);
        Assert.Contains("NOT MEASURED", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchedJudgedGateUsesSameCriterionIdsPersonaAndKForBothSubjects()
    {
        var gate = await EvaluationSuite.JudgedGateAsync(default);

        Assert.True(gate.Passed);
        Assert.Equal(GateAuthority.Diagnostic, gate.Authority);
        Assert.False(gate.IsVerdictBearing);
        Assert.NotNull(gate.AgentEval);
        Assert.Equal("AgentEval.Evals.AtomicCodeEval", gate.AgentEval!.LibraryType);
        Assert.Contains("IEvaluator criterion observation", gate.AgentEval.Mechanism, StringComparison.Ordinal);
        Assert.Contains("direct EvalInput", gate.AgentEval.Mechanism, StringComparison.Ordinal);
        Assert.Contains("BenchmarkArm/BenchmarkRunner", gate.AgentEval.Mechanism, StringComparison.Ordinal);
        Assert.DoesNotContain("TestResult", gate.AgentEval.Mechanism, StringComparison.Ordinal);
        Assert.Contains("USR-NB-01", gate.AgentEval.Subject, StringComparison.Ordinal);
        Assert.Contains("matched k=", gate.AgentEval.Subject, StringComparison.Ordinal);
        var expectedIds = VitrineEvalCriteria.JudgedCriteria
            .SelectMany(static criterion => new[] { $"demo01:{criterion.Id}", $"demo02:{criterion.Id}" })
            .Concat([
                "binding:demo01",
                "binding:demo02",
                "reachability:judge-call-count",
                "aggregate:applicable-denominator",
            ])
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedIds, gate.AgentEval.Observations.Select(static item => item.Id).Order(StringComparer.Ordinal));
        foreach (var criterion in VitrineEvalCriteria.JudgedCriteria)
        {
            var demo01 = Assert.Single(gate.AgentEval.Observations, item => item.Id == $"demo01:{criterion.Id}");
            var demo02 = Assert.Single(gate.AgentEval.Observations, item => item.Id == $"demo02:{criterion.Id}");
            Assert.Equal(VitrineEvalCriteria.JudgedMatchedK, demo01.SampleCount);
            Assert.Equal(VitrineEvalCriteria.JudgedMatchedK, demo02.SampleCount);
        }
        Assert.Equal(2, Assert.Single(gate.AgentEval.Observations,
            static item => item.Id == "reachability:judge-call-count").Score);
        Assert.Equal(VitrineEvalCriteria.JudgedCriteria.Length * 2,
            Assert.Single(gate.AgentEval.Observations,
                static item => item.Id == "aggregate:applicable-denominator").Score);
    }

    [Fact]
    public async Task AgentEvalManifestRejectsDuplicatesWrongMechanismsAndOrder()
    {
        var suite = await EvaluationSuite.RunAsync(includeControls: false);
        var manifest = suite.Gates.Select(static gate => gate.AgentEval).ToArray();

        Assert.True(EvaluationSuite.ValidateAgentEvalManifest(manifest));
        Assert.True(EvaluationSuite.ValidateMandatoryAgentEvalManifest(suite.Gates));
        var missingDiagnosticEvidence = suite.Gates.Select(gate =>
            gate.Authority == GateAuthority.Diagnostic ? gate with { AgentEval = null } : gate).ToArray();
        Assert.True(EvaluationSuite.ValidateMandatoryAgentEvalManifest(missingDiagnosticEvidence));
        var duplicate = manifest.ToArray();
        duplicate[1] = duplicate[0];
        Assert.False(EvaluationSuite.ValidateAgentEvalManifest(duplicate));
        var reordered = manifest.Reverse().ToArray();
        Assert.False(EvaluationSuite.ValidateAgentEvalManifest(reordered));
        var wrongMechanism = manifest.ToArray();
        var source = Assert.IsType<AgentEvalProvenance>(wrongMechanism[0]);
        wrongMechanism[0] = new AgentEvalProvenance(
            source.IntegrationId,
            "Vitrine.Legacy.UnadmittedEvaluator",
            source.Mechanism,
            source.Subject,
            source.Observations,
            source.SnapshotPolicy,
            source.ObservationProducer,
            source.AcceptanceEvaluator,
            source.SubjectSuppliedPassFail);
        Assert.False(EvaluationSuite.ValidateAgentEvalManifest(wrongMechanism));
    }

    [Fact]
    public async Task GateExceptionBecomesOneTypeOnlyInstrumentCompletion()
    {
        const string secret = "https://not-for-evidence.invalid sk-not-for-evidence";
        var progress = new RecordingEvaluationProgressSink();

        var gate = await EvaluationSuite.ObserveGateForTestAsync(
            () => Task.FromException<GateResult>(new InvalidOperationException(secret)),
            progress);

        Assert.Equal(GateMeasurementOutcome.InstrumentError, gate.Outcome);
        Assert.Null(gate.Score);
        Assert.Contains(nameof(InvalidOperationException), gate.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, gate.Evidence, StringComparison.Ordinal);
        Assert.Equal([EvaluationProgressKind.GateStarted, EvaluationProgressKind.GateCompleted],
            progress.Events.Select(item => item.Kind));
    }

    [Fact]
    public async Task EnvironmentCaptureFailureMarksEveryRowAsInstrumentErrorAndContinues()
    {
        var definitions = new[]
        {
            Definition("TEST-01"),
            Definition("TEST-02"),
        };
        var progress = new RecordingEvaluationProgressSink();

        var controls = await NegativeControlRunner.RunWithEnvironmentFactoryAsync(
            definitions,
            () => throw new InvalidOperationException("withheld failure detail"),
            progress: progress);

        Assert.Equal(2, controls.Count);
        Assert.All(controls, control =>
        {
            Assert.Equal(ControlAttemptOutcome.InstrumentError, control.BrokenOutcome);
            Assert.Equal(ControlAttemptOutcome.InstrumentError, control.RestoredOutcome);
            Assert.False(control.Caught);
            Assert.DoesNotContain("withheld failure detail", control.Evidence, StringComparison.Ordinal);
        });
        Assert.Equal(2, progress.Events.Count(item => item.Kind == EvaluationProgressKind.ControlCompleted));
    }

    [Fact]
    public void CommittedControlManifestPinsEveryIdentityAndProductionTarget()
    {
        Assert.Null(CommittedControlManifest.Validate());
        Assert.Equal(43, NegativeControlCatalog.Manifest.Count);
        Assert.Equal(
            Enumerable.Range(1, 43).Select(index => $"NC-{index:00}"),
            NegativeControlCatalog.Manifest.Select(item => item.Id));
        Assert.All(NegativeControlCatalog.Manifest, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Target));
            Assert.NotEqual("test-only", item.Target);
            Assert.False(string.IsNullOrWhiteSpace(item.Mutation));
        });

        Assert.All(NegativeControlCatalog.Definitions, definition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.ObservationProducer));
            Assert.NotEqual("unspecified", definition.ObservationProducer);
            Assert.False(string.IsNullOrWhiteSpace(definition.Evaluator));
            Assert.NotEqual("unspecified", definition.Evaluator);
        });
        Assert.Equal(14, NegativeControlCatalog.Definitions.Count(definition => definition.Tranche == ControlTranche.E02A));
        Assert.Equal(7, NegativeControlCatalog.Definitions.Count(definition => definition.Tranche == ControlTranche.E02B));
        Assert.Equal(22, NegativeControlCatalog.Definitions.Count(definition => definition.Tranche == ControlTranche.E02C));
    }

    [Fact]
    public void ControlCallableBindingsMatchReviewedSnapshot()
    {
        // This fixture is intentionally literal and independent of the definition builder. It is
        // the review boundary that catches a compilable but semantically wrong callable label.
        string[] expected =
        [
            "NC-01|RetrievalDiagnostics.DenseBelowFloor|HybridRetriever.SearchAsync|CausalControlPolicies.SilentWipeoutDetectorHasBothDirections|E02A",
            "NC-02|GuardrailPipeline.Screen|RecommendationRunEngine.RunAsync|GuardrailPipeline.Screen|E02A",
            "NC-03|EvidenceRef.Resolves|RecommendationRunEngine.RunAsync|GuardrailPipeline.Screen|E02A",
            "NC-04|Broken02OperandPolicy.Evaluate|NegativeControlCatalog.ObserveBroken02Operands|Broken02OperandPolicy.Evaluate|E02C",
            "NC-05|ToolCallBudget.HasGroundedCommitOrder|ToolCallBudget.CallFacts|ToolCallBudget.HasGroundedCommitOrder|E02A",
            "NC-06|GalaxusDiscoveryLoop.RunAsync|GalaxusDiscoveryLoop.RunAsync|CausalControlPolicies.CausalLoopContrast|E02B",
            "NC-07|VitrineProductionChecks.SelfTestFailuresAsync|VitrineAdmittedChecksSelfTest.RunAsync|AdmittedCheckDiagnostics.EveryProductionAblationWentRed|E02C",
            "NC-08|GalaxusDiscoveryLoop.RunAsync|GalaxusDiscoveryLoop.RunAsync|CausalControlPolicies.CausalLoopContrast|E02B",
            "NC-09|VitrineOfflineBenchmark.RunAsync|VitrineAdmittedChecksSelfTest.RunAsync|AdmittedCheckDiagnostics.EveryBenchmarkAblationWentRed|E02C",
            "NC-10|BenchmarkScore.Census|VitrineAdmittedChecksSelfTest.RunAsync|AdmittedCheckDiagnostics.HasPositiveBenchmarkCountsBeforeValues|E02C",
            "NC-11|BenchmarkScore.AgainstReference|VitrineAdmittedChecksSelfTest.RunAsync|AdmittedCheckDiagnostics.DegradedArmLosesAgainstReference|E02C",
            "NC-12|EvaluationSuite.EvaluateGraderSanityAsync|EvaluationSuite.EvaluateGraderSanityAsync|CausalControlPolicies.MatchesGold|E02C",
            "NC-13|EvaluationReportHtml.Render|EvaluationSuite.CatalogueGateForControlAsync|CausalControlPolicies.MatchesGateState|E02C",
            "NC-14|EvaluationSuite.JoinCanonicalCriteria|EvaluationSuite.JudgedGateAsync|EvaluationSuite.JoinCanonicalCriteria|E02C",
            "NC-15|MatchedBindingPolicy.HasCanonicalMatchedK|ControlEnvironment.CaptureProductionAsync|MatchedBindingPolicy.HasCanonicalMatchedK|E02C",
            "NC-16|HonestyInterpretation.Validate|HonestyInterpretation.Build|HonestyInterpretation.Validate|E02C",
            "NC-17|EvaluationSuite.JoinCanonicalCriteria|NegativeControlCatalog.Build|EvaluationSuite.JoinCanonicalCriteria|E02C",
            "NC-18|VitrineEvalCriteria.DecideApplicability|Personas.CanonicalPromptFor|JudgedApplicabilityPolicy.ComesFromAuthoredInput|E02B",
            "NC-19|UnnameableInterestFilter.Apply|NegativeControlCatalog.Build|UnnameableInterestFilter.Apply|E02A",
            "NC-20|ToolRefusalBoundary.IsSatisfied|ToolRefusalBoundary.ObserveAsync|ToolRefusalBoundary.IsSatisfied|E02A",
            "NC-21|ToolRefusalBoundary.IsSatisfied|ToolRefusalBoundary.ObserveAsync|ToolRefusalBoundary.IsSatisfied|E02A",
            "NC-22|EvaluationReportWriter.WriteAsync|EvaluationReportWriter.WriteAsync|CausalControlPolicies.ReportWriteMatchesStore|E02A",
            "NC-23|EvaluationSuite.ValidateAgentEvalManifest|EvaluationSuite.RunAsync|EvaluationSuite.ValidateAgentEvalManifest|E02C",
            "NC-24|ExactTests.TwoSidedSignP|HonestyEvidenceLoader.Load|HonestyInterpretation.Validate|E02C",
            "NC-25|FloorComparison.Compute|ForcedChoiceCalibrationFixture.Capture|FloorComparison.Compute|E02C",
            "NC-26|CiProofPolicy.RequiredCiStepsPlanned|ControlEnvironment.ObserveCiPlanAsync|CiProofPolicy.RequiredCiStepsPlanned|E02C",
            "NC-27|ProviderUsageMeasurement.IsConsistent|GalaxusDiscoveryLoop.RunAsync|ProviderUsageMeasurement.IsConsistent|E02A",
            "NC-28|ProviderUsageMeasurement.IsConsistent|RecommendationRunEngine.RunAsync|ProviderUsageMeasurement.IsConsistent|E02A",
            "NC-29|DiscoveryCalibrationObserver.ValidateDistinctPattern|DiscoveryCalibrationObserver.Capture|DiscoveryCalibrationObserver.ValidateDistinctPattern|E02B",
            "NC-30|DiscoveryRouteIds.ReviewToMoreDiscovery|DiscoveryTerminationProbe.RunAllAsync|CausalControlPolicies.HasBothLoopDirections|E02B",
            "NC-31|DiscoveryTopologyCaseRegistry.Assess|GalaxusDiscoveryLoop.RunAsync|DiscoveryTopologyCaseRegistry.Assess|E02B",
            "NC-32|VitrineEvalCriteria.DecideApplicability|EvaluationSuite.JudgedGateAsync|JudgedApplicabilityPolicy.ComesFromAuthoredInput|E02C",
            "NC-33|AgentEvalProvenance.HasIndependentBoundary|EvaluationSuite.JudgedGateAsync|AgentEvalProvenance.HasIndependentBoundary|E02C",
            "NC-34|CatalogueEvidenceStatement.ValidateExact|RecommendationArtifactComposer.Compose|CatalogueEvidenceStatement.ValidateExact|E02A",
            "NC-35|CommittedVectorContentPolicy.Evaluate|CommittedVectorContentPolicy.Observe|CommittedVectorContentPolicy.Evaluate|E02A",
            "NC-36|CausalControlPolicies.CohortComparableOrDeclared|ControlEnvironment.CaptureProductionAsync|CausalControlPolicies.CohortComparableOrDeclared|E02B",
            "NC-37|GateResult.InstrumentError|EvaluationSuite.ObserveGateForTestAsync|EvaluationReportHtml.Render|E02C",
            "NC-38|ProviderUsageMeasurement.IsConsistent|RecommendationRunEngine.RunAsync|CausalControlPolicies.CostStatesRemainDistinct|E02A",
            "NC-39|GateResult.NotMeasured|EvaluationSuite.ObserveGateForTestAsync|EvaluationReportHtml.ReadGateRowForControl|E02C",
            "NC-40|JudgedReachabilityPolicy.HasCanonicalVerdicts|EvaluationSuite.JudgedGateAsync|JudgedReachabilityPolicy.HasCanonicalVerdicts|E02C",
            "NC-41|RecommendationArtifactComposer.ComposeScreened|RecommendationArtifactComposer.ComposeScreened|CustomerAnswerScreen.Screen|E02A",
            "NC-42|ObservationCensus.Measured|NegativeControlCatalog.Build|ObservationCensus.Measured|E02C",
            "NC-43|NegativeControlRunner.RunAsync|NegativeControlRunner.RunAsync|NegativeControlRunner.RunAsync|E02C",
        ];

        var actual = NegativeControlCatalog.Definitions.Select(static definition =>
            $"{definition.Id}|{definition.Target}|{definition.ObservationProducer}|{definition.Evaluator}|{definition.Tranche}");

        Assert.Equal(expected, actual);
        Assert.Equal(
            ["NC-02", "NC-03", "NC-07", "NC-09", "NC-10", "NC-11", "NC-12", "NC-14", "NC-15", "NC-16", "NC-18", "NC-23", "NC-24", "NC-25", "NC-26", "NC-31", "NC-32", "NC-33", "NC-40", "NC-41"],
            NegativeControlCatalog.Definitions.Where(static definition => definition.ScopeClass == ControlScopeClass.ProductionObservation).Select(static definition => definition.Id));
    }

    private static ControlDefinition Definition(string id) => new(
        id,
        "contained row",
        "meta",
        _ => new object(),
        artifact => artifact,
        (_, _) => ControlAssessment.Measured(true, "measured"),
        "synthetic mutation",
        "NegativeControlRunner.Containment");
}
