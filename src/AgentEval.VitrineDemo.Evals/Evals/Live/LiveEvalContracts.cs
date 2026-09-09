// SPDX-License-Identifier: MIT

using AgentEval.Evals.Meta;
using AgentEval.Output;
using Galaxus.RecommendationAgent.Observability;

namespace AgentEval.VitrineDemo.Evals.Live;

internal static class LiveEvidenceText
{
    internal static string Bound(string? value, int maximum)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        var safe = RecommendationRuntimeEvents.SafePreview(value);
        return safe.Length <= maximum ? safe : maximum == 1 ? "…" : safe[..(maximum - 1)] + "…";
    }

    internal static string SafeIdentifier(string? value, string fallback = "unknown")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var safe = new string(value.Trim().Where(static character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.').Take(80).ToArray());
        return safe.Length == 0 ? fallback : safe;
    }
}

/// <summary>Subject architecture exercised by one live arm.</summary>
public enum LiveSubjectArchitecture { Agent, Workflow }

/// <summary>Terminal subject state. Failure and cancellation are not quality scores.</summary>
public enum LiveSubjectStatus { Completed, Abstained, Failed, Cancelled }

/// <summary>Stable terminal classification for a selected evaluation plan.</summary>
public enum LiveEvalTerminalStatus
{
    Passed = 0,
    QualityFailed = 1,
    NotMeasured = 3,
    InfrastructureError = 4,
    Cancelled = 130,
}

/// <summary>Allow-listed reason why a live measurement or session could not complete.</summary>
public enum LiveEvalFailureCode
{
    ConfigurationUnavailable,
    Cancelled,
    SubjectProviderFailure,
    SubjectExecutionFailed,
    JudgeExecutionFailed,
    BenchmarkExecutionFailed,
    SafetyExecutionFailed,
}

/// <summary>Bounded options for paid evaluation execution.</summary>
/// <param name="ScenarioIds">
/// Stable scenario ids to run. Null or empty selects all four scenarios in canonical registry order.
/// </param>
public sealed record LiveEvalOptions(
    string? WorkspaceRoot = null,
    int? Repetitions = null,
    int SubjectMaxOutputTokens = 4000,
    int JudgeMaxOutputTokens = 1200,
    double PassThreshold = 0.75,
    int ResponsePreviewCharacters = 1000,
    IReadOnlyList<string>? ScenarioIds = null,
    int SafetyMaxProbesPerAttack = 2,
    int SafetyTimeoutSeconds = 45);

/// <summary>One subject request; both paid token ceilings are explicit at their boundaries.</summary>
public sealed record LiveSubjectRequest(
    LiveUseCaseScenario Scenario,
    int Repetition,
    int MaxOutputTokens);

/// <summary>One judge request. Criteria are supplied by the stable use-case registry.</summary>
public sealed record LiveJudgeRequest(
    string ScenarioId,
    string Input,
    string Output,
    string ScenarioDescription,
    string ExpectedBehavior,
    IReadOnlyList<string> GroundTruthFacts,
    IReadOnlyList<string> Criteria,
    int MaxOutputTokens);

/// <summary>One allow-listed, bounded argument copied from a real function invocation.</summary>
public sealed record LiveToolArgumentEvidence(string Name, string Value);

/// <summary>Terminal state after reconciling one start with one terminal event by operation id.</summary>
public enum LiveToolCallStatus { Completed, Failed, Cancelled, Incomplete, Unreconciled }

/// <summary>One operation-correlated, allow-listed read-only tool invocation.</summary>
public sealed record LiveToolCallEvidence(
    string OperationId,
    string ToolName,
    LiveToolCallStatus Status,
    IReadOnlyList<LiveToolArgumentEvidence> Arguments);

/// <summary>Only allow-listed, operation-correlated tool facts cross the live result boundary.</summary>
public sealed record LiveToolEvidence(
    bool JournalObserved,
    IReadOnlyList<string> ToolNames,
    int Executed,
    int Completed,
    int Failed,
    int Cancelled,
    int UnknownNameCount,
    IReadOnlyList<LiveToolCallEvidence> Calls,
    int UnreconciledCount)
{
    public static LiveToolEvidence NotApplicable { get; } = new(false, [], 0, 0, 0, 0, 0, [], 0);
}

/// <summary>One allow-listed workflow executor and how often its start event was observed.</summary>
public sealed record LiveExecutorEvidence(string ExecutorId, int ExecutionCount);

/// <summary>Allow-listed workflow topology and bounded-run facts.</summary>
public sealed record LiveWorkflowEvidence(
    IReadOnlyList<LiveExecutorEvidence> Executors,
    IReadOnlyList<string> Routes,
    int DiscoveryRounds,
    int MaximumRounds,
    int SuperSteps,
    string StopReason,
    bool Looped,
    int FailureCount,
    int DegradationCount,
    IReadOnlyList<string> DegradationKinds,
    int UnknownExecutorCount = 0,
    int UnknownRouteCount = 0);

/// <summary>Provider usage that remains absent when the provider did not report it.</summary>
public sealed record LiveUsageEvidence(
    string Status,
    int? ModelCalls,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    double? EstimatedCostUsd)
{
    public static LiveUsageEvidence NotReported { get; } =
        new("not-reported", null, null, null, null, null);

    /// <summary>True only when the state and every reported amount describe one possible observation.</summary>
    public bool IsConsistent()
    {
        if (ModelCalls is < 0 || InputTokens is < 0 || OutputTokens is < 0 || TotalTokens is < 0 ||
            EstimatedCostUsd is < 0 || EstimatedCostUsd is { } cost && !double.IsFinite(cost))
            return false;

        if (InputTokens is { } input && OutputTokens is { } output && TotalTokens is { } total &&
            (input > long.MaxValue - output || input + output != total)) return false;
        if (InputTokens is { } knownInput && TotalTokens is { } knownTotal && knownInput > knownTotal ||
            OutputTokens is { } knownOutput && TotalTokens is { } anotherTotal && knownOutput > anotherTotal)
            return false;

        return Status switch
        {
            "not-reported" => InputTokens is null && OutputTokens is null && TotalTokens is null &&
                              EstimatedCostUsd is null,
            "measured-zero" => TotalTokens == 0 && (InputTokens ?? 0) == 0 && (OutputTokens ?? 0) == 0 &&
                               (EstimatedCostUsd ?? 0) == 0,
            "measured" => TotalTokens > 0 && ModelCalls is not 0,
            "lower-bound" => TotalTokens >= 0 && ModelCalls is not 0,
            _ => false,
        };
    }
}

/// <summary>The typed observation seam used by deterministic fakes and real subjects alike.</summary>
public sealed record LiveSubjectObservation(
    MeasurementState Measurement,
    LiveSubjectStatus Status,
    string? Response,
    LiveToolEvidence Tools,
    LiveWorkflowEvidence? Workflow,
    LiveUsageEvidence Usage,
    LiveEvalFailureCode? FailureCode = null,
    string? FailureDetail = null)
{
    public static LiveSubjectObservation NotMeasured(
        LiveSubjectStatus status = LiveSubjectStatus.Failed,
        LiveSubjectArchitecture architecture = LiveSubjectArchitecture.Agent) =>
        new(MeasurementState.NotMeasured, status, null, LiveToolEvidence.NotApplicable,
            architecture == LiveSubjectArchitecture.Workflow
                ? new LiveWorkflowEvidence([], [], 0, 0, 0, "not-measured", false, 0, 0, [])
                : null,
            LiveUsageEvidence.NotReported);
}

/// <summary>Runs one architecture without exposing a model object to benchmark metadata.</summary>
public interface ILiveEvalSubject
{
    string ArmId { get; }
    string ModelId { get; }
    LiveSubjectArchitecture Architecture { get; }
    Task<LiveSubjectObservation> RunAsync(
        LiveSubjectRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Judge seam; the default implementation creates a configured ChatClientEvaluator.</summary>
public interface ILiveEvalJudge
{
    string ModelId { get; }
    Task<AgentEval.Core.EvaluationResult> EvaluateAsync(
        LiveJudgeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Configuration readiness without endpoint, credential, or environment-value disclosure.</summary>
public sealed record LiveEvalReadiness(bool IsReady, string Detail);

/// <summary>Safe progress phases emitted while paid calls and checks are in flight.</summary>
public enum LiveEvalProgressPhase
{
    SessionStarting,
    TrialStarting,
    SubjectRunning,
    SubjectCompleted,
    CheckStarting,
    CheckCompleted,
    TrialCompleted,
    SafetyTargetRunning,
    SafetyFindingsCompleted,
    Persisting,
    SessionCompleted,
}

/// <summary>Allow-listed progress suitable for a UI; Detail never contains model text.</summary>
public sealed record LiveEvalProgress(
    VitrineEvaluationPlan Plan,
    LiveEvalProgressPhase Phase,
    string? ScenarioId,
    string? ArmId,
    int? Repetition,
    string? CheckKey,
    MeasurementState? Measurement,
    bool? Passed,
    string Detail);

/// <summary>Measurement census copied from AgentEval's benchmark meta lane.</summary>
public sealed record LiveObservationCensus(int Measured, int NotApplicable, int NotMeasured)
{
    public int Total => Measured + NotApplicable + NotMeasured;
}

/// <summary>A zero-denominator interval is explicitly NotMeasured and carries no numeric bounds.</summary>
public sealed record LiveReliability(
    MeasurementState Measurement,
    int Successes,
    int Total,
    double? Estimate,
    double? Lower,
    double? Upper);

/// <summary>One admitted check result; operational absence is never represented as score zero.</summary>
public sealed record LiveCheckFact(
    string Key,
    string Name,
    MeasurementState Measurement,
    double? Score,
    bool? Passed);

/// <summary>One expected use-case criterion and the judge's individual verdict.</summary>
public sealed record LiveCriterionVerdict(
    string Id,
    MeasurementState Measurement,
    bool? Met,
    string Explanation);

/// <summary>Sanitized evidence for one scenario, arm and repetition.</summary>
public sealed record LiveTrialEvidence(
    string ScenarioId,
    string PersonaId,
    string ArmId,
    LiveSubjectArchitecture Architecture,
    int Repetition,
    MeasurementState Measurement,
    bool? Passed,
    LiveSubjectStatus SubjectStatus,
    string ResponsePreview,
    LiveToolEvidence Tools,
    LiveWorkflowEvidence? Workflow,
    IReadOnlyList<LiveCheckFact> Checks,
    IReadOnlyList<LiveCriterionVerdict> Criteria,
    LiveUsageEvidence SubjectUsage,
    LiveUsageEvidence JudgeUsage,
    LiveEvalFailure? Failure);

/// <summary>One physical AgentEval run directory: exactly one arm and one repetition.</summary>
public sealed record LiveEvalRunReference(
    string RunId,
    string ArmId,
    int Repetition,
    string RelativeDirectory);

/// <summary>Census and stochastic reliability for one admitted check in one arm.</summary>
public sealed record LiveCheckSummary(
    string Key,
    string Name,
    LiveObservationCensus Census,
    LiveReliability Reliability);

/// <summary>All check facts for one arm; no cross-check average is manufactured.</summary>
public sealed record LiveArmSummary(
    string ArmId,
    LiveSubjectArchitecture Architecture,
    int Repetitions,
    IReadOnlyList<LiveCheckSummary> Checks);

/// <summary>Case-paired BenchmarkScore fact for one check, workflow as challenger.</summary>
public sealed record LiveCheckComparison(
    string CheckKey,
    string CheckName,
    string ReferenceArm,
    string ChallengerArm,
    int Wins,
    int Losses,
    int Ties,
    int EffectiveN,
    double? PValue,
    double? MinimumAttainableP,
    double? MeanDelta,
    int Cases,
    int TotalRepObservations,
    double? MeanRepetitionsPerCase,
    string RepCollapse,
    LiveObservationCensus Census,
    bool UnderpoweredByConstruction,
    bool Undecidable);

/// <summary>Runtime paths. The sanitized outcome document persists only relative run references.</summary>
public sealed record LiveEvalPersistence(
    string WorkspaceRoot,
    string SessionDirectory,
    string OutcomePath,
    string IndexPath);

/// <summary>Paid workload planned before execution; one judge evaluation follows each measurable subject call.</summary>
public sealed record LiveEvalWorkload(
    int ScenarioCount,
    int ArmCount,
    int Repetitions,
    int PlannedSubjectCalls,
    int PlannedJudgeEvaluations,
    int SafetyAttackCount = 0,
    int PlannedSafetyProbes = 0,
    int MaximumSafetyModelCalls = 0);

/// <summary>Allow-listed outcome copied from AgentEval's released red-team result.</summary>
public enum LiveSafetyProbeOutcome { Compromised, Resisted, Inconclusive }

/// <summary>Allow-listed reason a red-team probe was inconclusive.</summary>
public enum LiveSafetyProbeErrorKind { None, Timeout, Transport, Execution }

/// <summary>
/// Produces the complete allow-listed diagnostic vocabulary for redacted safety receipts.
/// The diagnostic is derived from typed outcome facts; raw provider or probe evidence is
/// never accepted as an input.
/// </summary>
public static class LiveSafetyProbeDiagnostics
{
    public const string NoExecutionError = "No probe execution error.";
    public const string Inconclusive = "AgentEval could not decide this probe.";
    public const string Timeout = "The probe reached its configured timeout.";
    public const string Transport = "The remote model transport failed.";
    public const string Execution =
        "An unexpected probe execution fault occurred; underlying provider and exception detail was deliberately suppressed because IncludeEvidence=false.";

    public static string Describe(
        LiveSafetyProbeOutcome outcome,
        LiveSafetyProbeErrorKind errorKind) => errorKind switch
    {
        LiveSafetyProbeErrorKind.Timeout => Timeout,
        LiveSafetyProbeErrorKind.Transport => Transport,
        LiveSafetyProbeErrorKind.Execution => Execution,
        _ when outcome == LiveSafetyProbeOutcome.Inconclusive => Inconclusive,
        _ => NoExecutionError,
    };

    public static string Describe(string? outcome, string? errorKind)
    {
        var parsedOutcome = Enum.TryParse<LiveSafetyProbeOutcome>(outcome, ignoreCase: true, out var knownOutcome)
            && Enum.IsDefined(knownOutcome)
            ? knownOutcome
            : LiveSafetyProbeOutcome.Inconclusive;
        var parsedError = Enum.TryParse<LiveSafetyProbeErrorKind>(errorKind, ignoreCase: true, out var knownError)
            && Enum.IsDefined(knownError)
            ? knownError
            : LiveSafetyProbeErrorKind.Execution;
        return Describe(parsedOutcome, parsedError);
    }

    public static bool IsKnown(string? outcome, string? errorKind) =>
        Enum.TryParse<LiveSafetyProbeOutcome>(outcome, ignoreCase: true, out var knownOutcome)
        && Enum.IsDefined(knownOutcome)
        && Enum.TryParse<LiveSafetyProbeErrorKind>(errorKind, ignoreCase: true, out var knownError)
        && Enum.IsDefined(knownError);

    public static bool IsCanonical(string? diagnostic, string? outcome, string? errorKind) =>
        IsKnown(outcome, errorKind) && diagnostic is not null && string.Equals(
            diagnostic,
            Describe(outcome, errorKind),
            StringComparison.Ordinal);
}

/// <summary>A typed, allow-listed probe failure that cannot carry raw evaluator or provider text.</summary>
public sealed record LiveSafetyProbeFailure(
    string Stage,
    LiveSafetyProbeErrorKind Code,
    string Detail);

/// <summary>One redacted probe receipt. Raw prompts, responses, reasons and canaries never cross this boundary.</summary>
public sealed record LiveSafetyProbeFact(
    string Attack,
    string ProbeId,
    LiveSafetyProbeOutcome Outcome,
    LiveSafetyProbeErrorKind ErrorKind,
    string Severity,
    string Fidelity,
    string Technique)
{
    /// <summary>Safe, deterministic explanation; serialized without retaining raw evaluator evidence.</summary>
    public string Diagnostic => LiveSafetyProbeDiagnostics.Describe(Outcome, ErrorKind);

    /// <summary>Present only when AgentEval classified a probe execution error.</summary>
    public LiveSafetyProbeFailure? Failure => ErrorKind == LiveSafetyProbeErrorKind.None
        ? null
        : new("probe-execution", ErrorKind, Diagnostic);
}

/// <summary>One allow-listed AgentEval attack category and its outcome census.</summary>
public sealed record LiveSafetyAttackSummary(
    string Attack,
    string OwaspId,
    int Total,
    int Resisted,
    int Compromised,
    int Inconclusive,
    int Errored);

/// <summary>Bounded paid red-team configuration persisted without the in-memory extraction canary.</summary>
public sealed record LiveSafetyConfiguration(
    IReadOnlyList<string> Attacks,
    int MaxProbesPerAttack,
    int TimeoutSeconds,
    int MaxTargetModelCallsPerProbe,
    int MaximumModelCalls,
    string JudgeMode,
    bool EvidencePersisted);

/// <summary>Sanitized result of the real AgentEval red-team scan against Robin only.</summary>
public sealed record LiveSafetySummary(
    string Target,
    MeasurementState Measurement,
    bool? Passed,
    int Total,
    int Resisted,
    int Compromised,
    int Inconclusive,
    int Errored,
    bool Truncated,
    int Skipped,
    IReadOnlyList<LiveSafetyAttackSummary> Attacks,
    IReadOnlyList<LiveSafetyProbeFact> Probes,
    LiveUsageEvidence SubjectUsage,
    LiveUsageEvidence JudgeUsage);

/// <summary>Deterministic tool contract authored for one live use case.</summary>
public sealed record LiveAgentToolExpectation(
    bool RequiresAbstention,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<string> ForbiddenTools,
    IReadOnlyList<string> ForbiddenPresentedSkus);

/// <summary>Sanitized evaluator definition persisted with the session that used it.</summary>
public sealed record LiveScenarioDefinition(
    string Id,
    string PersonaId,
    string Title,
    string Description,
    string Query,
    string ExpectedBehavior,
    IReadOnlyList<string> GroundTruthFacts,
    LiveAgentToolExpectation AgentToolExpectation,
    IReadOnlyList<LiveUseCaseCriterion> Criteria);

/// <summary>Safe model identity and native AgentEval judge relation for one selected arm.</summary>
public sealed record LiveSubjectProvenance(
    string ArmId,
    LiveSubjectArchitecture Architecture,
    string ModelId,
    JudgeSubjectRelation JudgeSubjectRelation);

/// <summary>Self-contained paid-run configuration; no endpoint or credential may enter this record.</summary>
public sealed record LiveEvalConfiguration(
    string DefinitionKey,
    string DefinitionVersion,
    string JudgeModelId,
    string JudgePromptId,
    string JudgeRubricHash,
    int SubjectMaxOutputTokens,
    int JudgeMaxOutputTokens,
    int ResponsePreviewCharacters,
    IReadOnlyList<LiveSubjectProvenance> Subjects)
{
    public LiveSafetyConfiguration? Safety { get; init; }
}

/// <summary>One sanitized failure fact, optionally scoped to a trial/check.</summary>
public sealed record LiveEvalFailure(
    LiveEvalFailureCode Code,
    string Detail,
    string? ScenarioId = null,
    string? ArmId = null,
    int? Repetition = null,
    string? CheckKey = null);

/// <summary>Complete selected-plan outcome, including every admitted check fact.</summary>
public sealed record LiveEvalResult(
    VitrineEvaluationPlan Plan,
    LiveEvalTerminalStatus TerminalStatus,
    string SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    LiveEvalWorkload Workload,
    double PassThreshold,
    IReadOnlyList<LiveScenarioDefinition> Scenarios,
    LiveEvalConfiguration Configuration,
    IReadOnlyList<LiveEvalRunReference> Runs,
    IReadOnlyList<LiveTrialEvidence> Trials,
    IReadOnlyList<LiveArmSummary> Arms,
    IReadOnlyList<LiveCheckComparison> Comparisons,
    IReadOnlyList<LiveEvalFailure> Failures,
    LiveEvalPersistence Persistence)
{
    public int ExitCode => (int)TerminalStatus;
    public LiveSafetySummary? Safety { get; init; }
}
