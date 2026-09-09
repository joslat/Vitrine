// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals;
public sealed record GateResult {
    public GateResult(string name, bool? passed, double? score,
        AgentEval.Evals.Meta.ChanceFloor? chanceFloor, string evidence)
        : this(name, passed, score, chanceFloor, evidence,
            passed is null ? GateMeasurementOutcome.NotMeasured : GateMeasurementOutcome.Measured) { }
    private GateResult(string name, bool? passed, double? score,
        AgentEval.Evals.Meta.ChanceFloor? chanceFloor, string evidence,
        GateMeasurementOutcome outcome) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(evidence);
        if (outcome == GateMeasurementOutcome.Measured) {
            if (passed is null || score is null || !double.IsFinite(score.Value))
                throw new ArgumentException("A measured gate requires a finite score and a verdict.");
        }
        else if (passed is not null || score is not null) {
            throw new ArgumentException("A non-measured gate cannot carry a verdict or numeric score.");
        }
        Name = name;
        Passed = passed;
        Score = score;
        ChanceFloor = chanceFloor;
        Evidence = evidence;
        Outcome = outcome;
    }
    public string Name { get; }
    public bool? Passed { get; }
    public double? Score { get; }
    public AgentEval.Evals.Meta.ChanceFloor? ChanceFloor { get; }
    public string Evidence { get; }
    public GateMeasurementOutcome Outcome { get; }
    public AgentEvalProvenance? AgentEval { get; init; }
    public HonestyClaims? HonestInterpretation { get; init; }
    public AgentEval.Evals.Meta.MeasurementState? AgentEvalMeasurementState { get; init; }
    public static GateResult NotMeasured(string name, AgentEval.Evals.Meta.ChanceFloor? chanceFloor, string why) =>
        new(name, null, null, chanceFloor, why, GateMeasurementOutcome.NotMeasured);
    public static GateResult NotApplicable(string name, AgentEval.Evals.Meta.ChanceFloor? chanceFloor, string why) =>
        new(name, null, null, chanceFloor, why, GateMeasurementOutcome.NotApplicable);
    public static GateResult InstrumentError(string name, AgentEval.Evals.Meta.ChanceFloor? chanceFloor,
        Type exceptionType) =>
        new(name, null, null, chanceFloor,
            $"INSTRUMENT ERROR: {exceptionType.Name}; details withheld.",
            GateMeasurementOutcome.InstrumentError);
    internal static GateResult FromAdmitted(
        string name,
        AgentEval.Evals.EvalResult result,
        AgentEval.Evals.Meta.ChanceFloor floor,
        string evidence) {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(floor);
        var state = global::AgentEval.Evals.EvalScoreExtensions.CensusBucket(result.Score);
        var countsTowardAggregate = global::AgentEval.Evals.EvalScoreExtensions.CountsTowardAggregate(result.Score);
        var gate = countsTowardAggregate
            ? new GateResult(name, result.Score.Passed, result.Score.Value, floor, evidence)
            : state switch {
            global::AgentEval.Evals.Meta.MeasurementState.NotApplicable =>
                NotApplicable(name, floor, result.Details.Summary ?? "NOT APPLICABLE"),
            global::AgentEval.Evals.Meta.MeasurementState.NotMeasured =>
                NotMeasured(name, floor, result.Details.Summary ?? "NOT MEASURED"),
            _ => throw new InvalidOperationException("AgentEval returned a measured score that cannot enter an aggregate."),
        };
        return gate with {
            AgentEvalMeasurementState = state,
        };
    }
}
public enum GateMeasurementOutcome { Measured, NotApplicable, NotMeasured, InstrumentError }
public enum EvaluationExecutionProfile { OfflineDeterministic }
public sealed record EvaluationExecutionProvenance(EvaluationExecutionProfile Profile, string DemoScope,
    string SubjectEngine, string EvaluatorEngine, string? DeploymentName, int? Demo01SubjectModelCalls,
    int? Demo02SubjectModelCalls, int? JudgeModelCalls, long? Demo01SubjectTokens, long? Demo02SubjectTokens,
    long? JudgeTokens, decimal? EstimatedCostUsd, bool UsesExternalModels) {
    public int? TotalModelCalls =>
        !UsesExternalModels ? 0 :
        Demo01SubjectModelCalls is { } demo01 &&
        Demo02SubjectModelCalls is { } demo02 &&
        JudgeModelCalls is { } judge
            ? demo01 + demo02 + judge
            : null;
}
public sealed record AgentEvalObservation(string Id, string Outcome, double? Score = null, string? Surface = null,
    int? SampleCount = null);
public sealed record AgentEvalProvenance {
    private static readonly object IndependentBoundaryToken = new();
    public AgentEvalProvenance(string integrationId, string libraryType, string mechanism, string subject,
        IReadOnlyList<AgentEvalObservation> observations, string snapshotPolicy, string observationProducer,
        string acceptanceEvaluator, bool subjectSuppliedPassFail) {
        IntegrationId = integrationId;
        LibraryType = libraryType;
        Mechanism = mechanism;
        Subject = subject;
        Observations = Array.AsReadOnly((observations ?? throw new ArgumentNullException(nameof(observations))).ToArray());
        SnapshotPolicy = snapshotPolicy;
        ObservationProducer = observationProducer;
        AcceptanceEvaluator = acceptanceEvaluator;
        SubjectSuppliedPassFail = subjectSuppliedPassFail;
    }
    public string IntegrationId { get; }
    public string LibraryType { get; }
    public string Mechanism { get; }
    public string Subject { get; }
    public IReadOnlyList<AgentEvalObservation> Observations { get; }
    public string SnapshotPolicy { get; }
    public string ObservationProducer { get; }
    public string AcceptanceEvaluator { get; }
    public bool SubjectSuppliedPassFail { get; }
    internal object? BoundaryAttestation { get; init; }
    internal static AgentEvalProvenance Independent(string integrationId, string libraryType, string mechanism,
        string subject, IReadOnlyList<AgentEvalObservation> observations, string snapshotPolicy,
        string observationProducer, string acceptanceEvaluator) =>
        new(integrationId, libraryType, mechanism, subject, observations, snapshotPolicy,
            observationProducer, acceptanceEvaluator, subjectSuppliedPassFail: false) {
            BoundaryAttestation = IndependentBoundaryToken,
        };
    internal bool HasIndependentBoundary =>
        ReferenceEquals(BoundaryAttestation, IndependentBoundaryToken) &&
        !string.IsNullOrWhiteSpace(IntegrationId) &&
        !SubjectSuppliedPassFail &&
        !string.IsNullOrWhiteSpace(SnapshotPolicy) &&
        !string.IsNullOrWhiteSpace(ObservationProducer) &&
        !string.IsNullOrWhiteSpace(AcceptanceEvaluator);
    internal AgentEvalProvenance WithoutSnapshotPolicyForControl() =>
        new(IntegrationId, LibraryType, Mechanism, Subject, Observations, string.Empty,
            ObservationProducer, AcceptanceEvaluator, SubjectSuppliedPassFail) {
            BoundaryAttestation = BoundaryAttestation,
        };
    internal AgentEvalProvenance WithSubjectPassFailForControl() =>
        new(IntegrationId, LibraryType, Mechanism, Subject, Observations, SnapshotPolicy,
            ObservationProducer, AcceptanceEvaluator, subjectSuppliedPassFail: true) {
            BoundaryAttestation = BoundaryAttestation,
        };
}
public sealed record HonestyClaims(string StatedNeedSatisfaction, string NextPurchasePrediction, string Baseline,
    string MeasurementId, string Source) {
    public string NextPurchaseRemedy { get; init; } = "Add informative pairs before making a next-purchase prediction claim.";
    public string NextPurchaseMethod { get; init; } = HonestyInterpretation.NextPurchaseExactMethod;
}
public enum ControlAttemptOutcome { MeasuredPass, MeasuredFail, NotMeasured, InstrumentError, ExpectedFaultObserved }
public enum ControlScopeClass { ProductionObservation, BoundaryCalibrationFixture }
public sealed record ControlResult(string Id, string Name, string Category, bool BrokenWentRed,
    bool RestoredWentGreen, string Evidence) {
    public string Target { get; init; } = "unspecified";
    public string ObservationProducer { get; init; } = "unspecified";
    public string Evaluator { get; init; } = "unspecified";
    public string Tranche { get; init; } = "unspecified";
    public ControlScopeClass ScopeClass { get; init; } = ControlScopeClass.BoundaryCalibrationFixture;
    public ControlAttemptOutcome HealthyOutcome { get; init; } = RestoredWentGreen
        ? ControlAttemptOutcome.MeasuredPass
        : ControlAttemptOutcome.NotMeasured;
    public ControlAttemptOutcome BrokenOutcome { get; init; } = BrokenWentRed
        ? ControlAttemptOutcome.MeasuredFail
        : ControlAttemptOutcome.MeasuredPass;
    public ControlAttemptOutcome RestoredOutcome { get; init; } = RestoredWentGreen
        ? ControlAttemptOutcome.MeasuredPass
        : ControlAttemptOutcome.MeasuredFail;
    public bool Caught =>
        HealthyOutcome == ControlAttemptOutcome.MeasuredPass &&
        (BrokenOutcome is ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved) &&
        RestoredOutcome == ControlAttemptOutcome.MeasuredPass;
    public bool HasMeasuredFailure =>
        HealthyOutcome == ControlAttemptOutcome.MeasuredFail ||
        BrokenOutcome == ControlAttemptOutcome.MeasuredPass ||
        RestoredOutcome is ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved;
    public bool HasMissingMeasurement =>
        HealthyOutcome == ControlAttemptOutcome.NotMeasured ||
        BrokenOutcome == ControlAttemptOutcome.NotMeasured ||
        RestoredOutcome == ControlAttemptOutcome.NotMeasured;
    public bool HasInfrastructureFailure =>
        HealthyOutcome == ControlAttemptOutcome.InstrumentError ||
        BrokenOutcome == ControlAttemptOutcome.InstrumentError ||
        RestoredOutcome == ControlAttemptOutcome.InstrumentError;
    internal ControlExecutionAttestation? ExecutionAttestation { get; init; }
}
internal sealed class ControlExecutionAttestation {
    private readonly string _evidence;
    private readonly bool _brokenWentRed;
    private readonly bool _restoredWentGreen;
    private readonly ControlAttemptOutcome _healthy;
    private readonly ControlAttemptOutcome _broken;
    private readonly ControlAttemptOutcome _restored;
    public ControlExecutionAttestation(ControlDefinition definition, ControlResult result) {
        Definition = definition;
        _evidence = result.Evidence;
        _brokenWentRed = result.BrokenWentRed;
        _restoredWentGreen = result.RestoredWentGreen;
        _healthy = result.HealthyOutcome;
        _broken = result.BrokenOutcome;
        _restored = result.RestoredOutcome;
    }
    public ControlDefinition Definition { get; }
    public bool Matches(ControlResult result) =>
        string.Equals(_evidence, result.Evidence, StringComparison.Ordinal) &&
        _brokenWentRed == result.BrokenWentRed &&
        _restoredWentGreen == result.RestoredWentGreen &&
        _healthy == result.HealthyOutcome &&
        _broken == result.BrokenOutcome &&
        _restored == result.RestoredOutcome;
}
public static class EvaluationExitCodes {
    public const int Passed = 0;
    public const int GateFailed = 1;
    public const int InvalidArguments = 2;
    public const int NotMeasured = 3;
    public const int InfrastructureFailure = 4;
}
public sealed record SuiteResult(IReadOnlyList<GateResult> Gates, IReadOnlyList<ControlResult> Controls) {
    public VitrineOfflineBenchmarkResult? OfflineBenchmark { get; init; }
    public EvaluationExecutionProvenance? Execution { get; init; }
    public bool RequireCanonicalControlPanel { get; init; }
    public int CaughtControls => Controls.Count(control => control.Caught);
    public int ExitCode => RequireCanonicalControlPanel && !NegativeControlCatalog.HasCanonicalExecutionScope(Controls) ||
                           Gates.Any(gate => gate.Outcome == GateMeasurementOutcome.InstrumentError) ||
                           Controls.Any(control => control.HasInfrastructureFailure)
        ? EvaluationExitCodes.InfrastructureFailure
        : Gates.Any(gate => gate.Outcome == GateMeasurementOutcome.NotMeasured) ||
          Controls.Any(control => control.HasMissingMeasurement)
            ? EvaluationExitCodes.NotMeasured
            : Gates.Any(gate => gate.Passed == false) || Controls.Any(control => control.HasMeasuredFailure)
                ? EvaluationExitCodes.GateFailed
                : EvaluationExitCodes.Passed;
}
