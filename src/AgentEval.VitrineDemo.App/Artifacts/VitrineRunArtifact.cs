// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using System.Text.Json.Serialization;

namespace AgentEval.VitrineDemo.App.Artifacts;

public sealed record VitrineGateSnapshot(
    string Name,
    bool? Passed,
    double? Score,
    VitrineChanceFloorSnapshot? ChanceFloor,
    string Evidence,
    GateMeasurementOutcome Outcome = GateMeasurementOutcome.Measured,
    VitrineAgentEvalProvenanceSnapshot? AgentEval = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    GateAuthority Authority = GateAuthority.Mandatory)
{
    // Schema 7/8 did not serialize gate authority. A post-integrity compatibility
    // projection can supply the corrected meaning without changing the signed payload.
    [JsonIgnore]
    public GateAuthority? CompatibilityAuthority { get; init; }

    [JsonIgnore]
    public GateAuthority EffectiveAuthority => CompatibilityAuthority ?? Authority;
}

public sealed record VitrineAgentEvalObservationSnapshot(
    string Id,
    string Outcome,
    double? Score,
    string? Surface,
    int? SampleCount);

public sealed record VitrineAgentEvalProvenanceSnapshot(
    string IntegrationId,
    string LibraryType,
    string Mechanism,
    string Subject,
    string SnapshotPolicy,
    string ObservationProducer,
    string AcceptanceEvaluator,
    bool SubjectSuppliedPassFail,
    IReadOnlyList<VitrineAgentEvalObservationSnapshot> Observations);

public sealed record VitrineControlSnapshot(
    string Id,
    string Name,
    string Category,
    string Target,
    string ObservationProducer,
    string Evaluator,
    ControlScopeClass ScopeClass,
    string Tranche,
    ControlAttemptOutcome HealthyOutcome,
    ControlAttemptOutcome BrokenOutcome,
    ControlAttemptOutcome RestoredOutcome,
    bool BrokenWentRed,
    bool RestoredWentGreen,
    string Evidence);

/// <summary>Credential-free subject and judge accounting copied from the typed suite result.</summary>
public sealed record VitrineEvaluationExecutionSnapshot(
    EvaluationExecutionProfile Profile,
    string DemoScope,
    string SubjectEngine,
    string EvaluatorEngine,
    string? DeploymentName,
    int? Demo01SubjectModelCalls,
    int? Demo02SubjectModelCalls,
    int? JudgeModelCalls,
    int? TotalModelCalls,
    long? Demo01SubjectTokens,
    long? Demo02SubjectTokens,
    long? JudgeTokens,
    decimal? EstimatedCostUsd,
    bool UsesExternalModels);

public sealed record VitrineBenchmarkCaseSnapshot(string Id, string Name);

public sealed record VitrineChanceFloorSnapshot(
    string Kind,
    string State,
    double? Value,
    double? ComparisonBar,
    double? IntervalHigh,
    int Draws,
    int PoolSize,
    string Derivation);

public sealed record VitrineBenchmarkCensusSnapshot(
    int Measured,
    int NotApplicable,
    int NotMeasured,
    int Total);

public sealed record VitrineBenchmarkCheckSnapshot(
    string CheckKey,
    VitrineChanceFloorSnapshot Floor,
    VitrineBenchmarkCensusSnapshot Census,
    int Successes,
    int Trials,
    double? PValue,
    double? MinimumAttainableP,
    bool? AboveFloor,
    bool? UnderpoweredByConstruction);

public sealed record VitrineBenchmarkArmSnapshot(
    string ArmId,
    string SubjectKind,
    string SubjectName,
    IReadOnlyList<VitrineBenchmarkCheckSnapshot> Checks);

public sealed record VitrineBenchmarkRunSnapshot(
    string ArmId,
    int Repetition,
    string SubjectKind,
    string SubjectName,
    string RunId,
    string RunDirectory);

public sealed record VitrineBenchmarkReferenceSnapshot(
    string CheckKey,
    string ReferenceArmId,
    string ChallengerArmId,
    int Wins,
    int Losses,
    int Ties,
    int EffectiveN,
    double? PValue,
    double? MinimumAttainableP,
    double? MeanDelta,
    VitrineBenchmarkCensusSnapshot Census,
    int Cases,
    int TotalRepObservations,
    string RepCollapse,
    bool UnderpoweredByConstruction);

/// <summary>Allow-listed projection of the canonical AgentEval file-system run.</summary>
public sealed record VitrineOfflineBenchmarkSnapshot(
    string DefinitionKey,
    string DefinitionVersion,
    string ArmId,
    string RunId,
    string WorkspaceRoot,
    string RunDirectory,
    IReadOnlyList<VitrineBenchmarkCaseSnapshot> Cases,
    IReadOnlyList<VitrineBenchmarkCheckSnapshot> Checks)
{
    public int Repetitions { get; init; }
    public IReadOnlyList<VitrineBenchmarkArmSnapshot> Arms { get; init; } = [];
    public IReadOnlyList<VitrineBenchmarkRunSnapshot> Runs { get; init; } = [];
    public IReadOnlyList<VitrineBenchmarkReferenceSnapshot> ReferenceComparisons { get; init; } = [];
}

public sealed record VitrineLiveCriterionDefinitionSnapshot(string Id, string Text);

public sealed record VitrineLiveScenarioSnapshot(
    string Id,
    string PersonaId,
    string Title,
    string Description,
    string Query,
    string ExpectedBehavior,
    IReadOnlyList<VitrineLiveCriterionDefinitionSnapshot> Criteria)
{
    public IReadOnlyList<string> GroundTruthFacts { get; init; } = [];
    public VitrineLiveToolExpectationSnapshot? AgentToolExpectation { get; init; }
}

public sealed record VitrineLiveToolExpectationSnapshot(
    bool RequiresAbstention,
    IReadOnlyList<string> RequiredTools,
    IReadOnlyList<string> ForbiddenTools,
    IReadOnlyList<string> ForbiddenPresentedSkus);

public sealed record VitrineLiveWorkloadSnapshot(
    int ScenarioCount,
    int ArmCount,
    int Repetitions,
    int PlannedSubjectCalls,
    int PlannedJudgeEvaluations)
{
    public int SafetyAttackCount { get; init; }
    public int PlannedSafetyProbes { get; init; }
    public int MaximumSafetyModelCalls { get; init; }
}

public sealed record VitrineLiveRunSnapshot(
    string RunId,
    string ArmId,
    int Repetition,
    string RelativeDirectory);

public sealed record VitrineLiveToolArgumentSnapshot(string Name, string Value);

public sealed record VitrineLiveToolCallSnapshot(
    string OperationId,
    string ToolName,
    string Status,
    IReadOnlyList<VitrineLiveToolArgumentSnapshot> Arguments);

public sealed record VitrineLiveToolSnapshot(
    bool JournalObserved,
    IReadOnlyList<string> ToolNames,
    int Executed,
    int Completed,
    int Failed,
    int Cancelled,
    int UnknownNameCount)
{
    public IReadOnlyList<VitrineLiveToolCallSnapshot> Calls { get; init; } = [];
    public int UnreconciledCount { get; init; }
}

public sealed record VitrineLiveExecutorSnapshot(string ExecutorId, int ExecutionCount);

public sealed record VitrineLiveWorkflowProviderStageSnapshot(
    string ExecutorId,
    int AttemptCount,
    int ResponseCount,
    int UnusableAttemptCount,
    int FailedAttemptCount,
    int CancelledAttemptCount,
    string Status)
{
    public int LastUnusableAttemptNumber { get; init; }
    public int LastUsableResponseAttemptNumber { get; init; }
}

public sealed record VitrineLiveWorkflowSnapshot(
    IReadOnlyList<VitrineLiveExecutorSnapshot> Executors,
    IReadOnlyList<string> Routes,
    int DiscoveryRounds,
    int MaximumRounds,
    int SuperSteps,
    string StopReason,
    bool Looped,
    int FailureCount,
    int DegradationCount,
    IReadOnlyList<string> DegradationKinds,
    int UnknownExecutorCount,
    int UnknownRouteCount)
{
    public IReadOnlyList<VitrineLiveWorkflowProviderStageSnapshot> ProviderStages { get; init; } = [];
    public int ProviderFailedAttemptCount { get; init; }
    public int RecoveredProviderFailedAttemptCount { get; init; }
    public int TerminalProviderStageCount { get; init; }
}

public sealed record VitrineLiveUsageSnapshot(
    string Status,
    int? ModelCalls,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    double? EstimatedCostUsd);

public sealed record VitrineLiveCheckFactSnapshot(
    string Key,
    string Name,
    string Measurement,
    double? Score,
    bool? Passed);

public sealed record VitrineLiveCriterionVerdictSnapshot(
    string Id,
    string Measurement,
    bool? Met,
    string Explanation);

public sealed record VitrineLiveTrialSnapshot(
    string ScenarioId,
    string PersonaId,
    string ArmId,
    string Architecture,
    int Repetition,
    string Measurement,
    bool? Passed,
    string SubjectStatus,
    string ResponsePreview,
    VitrineLiveToolSnapshot Tools,
    VitrineLiveWorkflowSnapshot? Workflow,
    IReadOnlyList<VitrineLiveCheckFactSnapshot> Checks,
    IReadOnlyList<VitrineLiveCriterionVerdictSnapshot> Criteria,
    VitrineLiveUsageSnapshot SubjectUsage,
    VitrineLiveUsageSnapshot JudgeUsage)
{
    public VitrineLiveFailureSnapshot? Failure { get; init; }
}

public sealed record VitrineLiveCensusSnapshot(
    int Measured,
    int NotApplicable,
    int NotMeasured,
    int Total);

public sealed record VitrineLiveReliabilitySnapshot(
    string Measurement,
    int Successes,
    int Total,
    double? Estimate,
    double? Lower,
    double? Upper);

public sealed record VitrineLiveCheckSummarySnapshot(
    string Key,
    string Name,
    VitrineLiveCensusSnapshot Census,
    VitrineLiveReliabilitySnapshot Reliability);

public sealed record VitrineLiveScenarioAcceptanceSnapshot(
    string ScenarioId,
    string PersonaId,
    string ArmId,
    string Architecture,
    VitrineLiveCensusSnapshot Census,
    VitrineLiveReliabilitySnapshot Reliability,
    double ConfidenceLevel,
    double MinimumLowerBound,
    bool? Passed);

public sealed record VitrineLiveArmSnapshot(
    string ArmId,
    string Architecture,
    int Repetitions,
    IReadOnlyList<VitrineLiveCheckSummarySnapshot> Checks);

public sealed record VitrineLiveComparisonSnapshot(
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
    VitrineLiveCensusSnapshot Census,
    bool UnderpoweredByConstruction)
{
    public bool Undecidable { get; init; }
}

public sealed record VitrineLivePersistenceSnapshot(
    string WorkspaceRoot,
    string SessionDirectory,
    string OutcomePath,
    string IndexPath);

public sealed record VitrineLiveSubjectProvenanceSnapshot(
    string ArmId,
    string Architecture,
    string ModelId,
    string JudgeSubjectRelation);

public sealed record VitrineLiveSafetyConfigurationSnapshot(
    IReadOnlyList<string> Attacks,
    int MaxProbesPerAttack,
    int TimeoutSeconds,
    int MaxTargetModelCallsPerProbe,
    int MaximumModelCalls,
    string JudgeMode,
    bool EvidencePersisted);

public sealed record VitrineLiveConfigurationSnapshot(
    string DefinitionKey,
    string DefinitionVersion,
    string JudgeModelId,
    string JudgePromptId,
    string JudgeRubricHash,
    int SubjectMaxOutputTokens,
    int JudgeMaxOutputTokens,
    int ResponsePreviewCharacters,
    IReadOnlyList<VitrineLiveSubjectProvenanceSnapshot> Subjects)
{
    public VitrineLiveSafetyConfigurationSnapshot? Safety { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VitrineLiveTerminalAcceptanceSnapshot? Acceptance { get; init; }
}

public sealed record VitrineLiveTerminalAcceptanceSnapshot(
    string Policy,
    double? ConfidenceLevel,
    double? MinimumLowerBound);

public sealed record VitrineLiveFailureSnapshot(
    string Code,
    string Detail,
    string? ScenarioId,
    string? ArmId,
    int? Repetition,
    string? CheckKey);

public sealed record VitrineLiveSafetyAttackSnapshot(
    string Attack,
    string OwaspId,
    int Total,
    int Resisted,
    int Compromised,
    int Inconclusive,
    int Errored);

public sealed record VitrineLiveSafetyProbeSnapshot(
    string Attack,
    string ProbeId,
    string Outcome,
    string ErrorKind,
    string Severity,
    string Fidelity,
    string Technique,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Diagnostic = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    VitrineLiveSafetyProbeFailureSnapshot? Failure = null);

public sealed record VitrineLiveSafetyProbeFailureSnapshot(
    string Stage,
    string Code,
    string Detail);

public sealed record VitrineLiveSafetySnapshot(
    string Target,
    string Measurement,
    bool? Passed,
    int Total,
    int Resisted,
    int Compromised,
    int Inconclusive,
    int Errored,
    bool Truncated,
    int Skipped,
    IReadOnlyList<VitrineLiveSafetyAttackSnapshot> Attacks,
    IReadOnlyList<VitrineLiveSafetyProbeSnapshot> Probes,
    VitrineLiveUsageSnapshot SubjectUsage,
    VitrineLiveUsageSnapshot JudgeUsage);

/// <summary>Allow-listed, sanitized projection of a paid use-case evaluation session.</summary>
public sealed record VitrineLiveEvaluationSnapshot(
    string Plan,
    string PlanLabel,
    string PlanDescription,
    string TerminalStatus,
    int ExitCode,
    string SessionId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    VitrineLiveWorkloadSnapshot Workload,
    double? PassThreshold,
    IReadOnlyList<VitrineLiveScenarioSnapshot> Scenarios,
    IReadOnlyList<VitrineLiveRunSnapshot> Runs,
    IReadOnlyList<VitrineLiveTrialSnapshot> Trials,
    IReadOnlyList<VitrineLiveArmSnapshot> Arms,
    IReadOnlyList<VitrineLiveComparisonSnapshot> Comparisons,
    VitrineLivePersistenceSnapshot Persistence)
{
    public VitrineLiveConfigurationSnapshot? Configuration { get; init; }
    public IReadOnlyList<VitrineLiveFailureSnapshot> Failures { get; init; } = [];
    public VitrineLiveSafetySnapshot? Safety { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<VitrineLiveScenarioAcceptanceSnapshot>? ScenarioAcceptances { get; init; }
}

public sealed record VitrineRecommendationSnapshot(
    string Sku,
    string Name,
    string Tray,
    string Reason,
    string Evidence,
    double Confidence,
    decimal? VerifiedPriceChf,
    int? StockUnits,
    int? DeliveryEstimateDays);

public sealed record VitrineLedgerEntrySnapshot(
    string Stage,
    string Action,
    string Reason,
    string Subject,
    string Detail);

public sealed record VitrineLedgerSnapshot(
    int InputCount,
    int OutputCount,
    int DroppedCount,
    int DemotedCount,
    int NotedCount,
    int? GiftExcluded,
    int PriceStockRequested,
    int PriceStockVerified,
    int? ToolCallsUsed,
    int? ToolCallCap,
    IReadOnlyList<VitrineLedgerEntrySnapshot> Entries);

public sealed record VitrineDemo01Snapshot(
    string CustomerFacingAnswer,
    string? Retriever,
    string? BudgetSummary,
    IReadOnlyList<string> RegisteredTools,
    IReadOnlyList<VitrineRecommendationSnapshot> Recommendations,
    VitrineLedgerSnapshot? Ledger);

public sealed record VitrineWorkflowInterestSnapshot(
    string Id,
    string Label,
    string Kind,
    string Origin,
    double Confidence);

public sealed record VitrineDemo02Snapshot(
    string CustomerFacingAnswer,
    bool CoverageApproved,
    bool PartialAnswer,
    int DiscoveryRounds,
    int MaxRounds,
    int SearchesRun,
    int ModelCalls,
    bool SelectionWasDeterministic,
    IReadOnlyList<string> ExecutorIds,
    IReadOnlyList<string> RoutesTaken,
    IReadOnlyList<VitrineWorkflowInterestSnapshot> Interests,
    IReadOnlyList<VitrineRecommendationSnapshot> Recommendations,
    IReadOnlyList<string> OpenGaps,
    IReadOnlyList<string> DroppedSkus,
    IReadOnlyList<string> DegradedNotes,
    IReadOnlyList<string> ExecutorFailures,
    VitrineLedgerSnapshot? Ledger);

public sealed record VitrineResultSnapshot(
    string Status,
    string? FailureKind,
    int? ProcessEquivalentExitCode,
    int? ModelCalls,
    int? ToolCalls,
    int? Presented,
    int? Survived,
    bool? WorkflowLooped,
    int? WorkflowSuperSteps,
    string? WorkflowStopReason,
    IReadOnlyList<VitrineGateSnapshot> Gates,
    IReadOnlyList<VitrineControlSnapshot> Controls,
    VitrineDemo01Snapshot? Demo01 = null,
    VitrineDemo02Snapshot? Demo02 = null,
    VitrineOfflineBenchmarkSnapshot? OfflineBenchmark = null,
    VitrineEvaluationExecutionSnapshot? EvaluationExecution = null,
    VitrineLiveEvaluationSnapshot? LiveEvaluation = null);

/// <summary>Versioned replay/export shape containing only allow-listed, sanitized fields.</summary>
public sealed record VitrineRunArtifact(
    int SchemaVersion,
    Guid RunId,
    DateTimeOffset CreatedAtUtc,
    VitrineRunMode Mode,
    string PersonaId,
    string ExecutionArm,
    bool? PersonalizationDisabled,
    VitrineGraphSnapshot Graph,
    IReadOnlyList<VitrineEvent> Events,
    VitrineResultSnapshot Result,
    string IntegritySha256)
{
    public const int MinimumSupportedSchemaVersion = 7;
    public const int CurrentSchemaVersion = 10;
}
