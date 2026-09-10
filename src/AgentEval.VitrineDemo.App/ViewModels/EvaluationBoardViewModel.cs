// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Globalization;
using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;

namespace AgentEval.VitrineDemo.App.ViewModels;

/// <summary>A display-only projection of an eval-layer gate verdict.</summary>
public sealed record GateResultViewModel(
    string Id,
    string Name,
    string Authority,
    string Status,
    string ObservedValue,
    string NullBaseline,
    string Scope,
    string Producer,
    string Evaluator,
    string Evidence)
{
    public string AuthorityColor => Authority == "DIAGNOSTIC" ? "#9FC5FF" : "#63D391";

    public string StatusColor => Authority == "DIAGNOSTIC" ? Status switch
    {
        "PASS" => "#63D391",
        "NOT APPLICABLE" => "#9FC5FF",
        "INSTRUMENT ERROR" => "#F07076",
        _ => "#F6C55C",
    } : Status switch
    {
        "PASS" => "#63D391",
        "FAIL" or "INSTRUMENT ERROR" => "#F07076",
        _ => "#F6C55C",
    };

    // Kept for artifact/UI compatibility. This is a null comparison baseline, never a pass mark.
    public string Score => ObservedValue;
    public string ChanceFloor => NullBaseline;

    public static GateResultViewModel From(GateResult gate, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var provenance = gate.AgentEval;
        return new(
            id ?? gate.Name,
            gate.Name,
            gate.Authority.ToString().ToUpperInvariant(),
            gate.Outcome switch
            {
                GateMeasurementOutcome.InstrumentError => "INSTRUMENT ERROR",
                GateMeasurementOutcome.NotApplicable => "NOT APPLICABLE",
                GateMeasurementOutcome.NotMeasured => "NOT MEASURED",
                _ => gate.Passed switch { true => "PASS", false => "FAIL", null => "NOT MEASURED" },
            },
            gate.Score.HasValue ? FormatNumber(gate.Score.Value) : "—",
            FormatNullBaseline(gate.ChanceFloor),
            (gate.Authority == GateAuthority.Diagnostic ? "Diagnostic only · " : string.Empty) +
                (provenance?.Subject ?? "EvaluationSuite typed gate"),
            provenance?.ObservationProducer ?? "EvaluationSuite gate implementation",
            provenance?.AcceptanceEvaluator ?? "Eval-layer gate policy",
            gate.Evidence);
    }

    public static GateResultViewModel From(VitrineGateSnapshot gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var provenance = gate.AgentEval;
        return new(
            gate.Name,
            gate.Name,
            gate.EffectiveAuthority.ToString().ToUpperInvariant(),
            gate.Outcome switch
            {
                GateMeasurementOutcome.InstrumentError => "INSTRUMENT ERROR",
                GateMeasurementOutcome.NotApplicable => "NOT APPLICABLE",
                GateMeasurementOutcome.NotMeasured => "NOT MEASURED",
                _ => gate.Passed switch { true => "PASS", false => "FAIL", null => "NOT MEASURED" },
            },
            gate.Score.HasValue ? FormatNumber(gate.Score.Value) : "—",
            FormatNullBaseline(gate.ChanceFloor),
            (gate.EffectiveAuthority == GateAuthority.Diagnostic ? "Diagnostic only · " : string.Empty) +
                (provenance?.Subject ?? "compatible evaluation gate"),
            provenance?.ObservationProducer ?? "compatible schema 7–9 artifact",
            provenance?.AcceptanceEvaluator ?? "Eval-layer gate policy",
            gate.Evidence);
    }

    internal static string FormatNullBaseline(ChanceFloor? floor)
    {
        if (floor is null)
            return "No chance comparison applies to this diagnostic/meta evaluation.";
        if (floor.State == FloorState.NotDerivable)
            return $"NOT DERIVABLE · {floor.Derivation}";
        return $"{floor.Kind} · comparison bar {FormatNumber(floor.ComparisonBar)} · {floor.Derivation}";
    }

    internal static string FormatNullBaseline(VitrineChanceFloorSnapshot? floor)
    {
        if (floor is null)
            return "No chance comparison applies to this diagnostic/meta evaluation.";
        if (string.Equals(floor.State, nameof(FloorState.NotDerivable), StringComparison.Ordinal))
            return $"NOT DERIVABLE · {floor.Derivation}";
        return $"{floor.Kind} · comparison bar {OptionalNumber(floor.ComparisonBar)} · {floor.Derivation}";
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string OptionalNumber(double? value) => value.HasValue
        ? FormatNumber(value.Value)
        : "not derived";
}

/// <summary>A display-only projection of the three typed attempts made by a mutation control.</summary>
public sealed record ControlResultViewModel(
    string Id,
    string Name,
    string Category,
    string Scope,
    string Target,
    string Producer,
    string Evaluator,
    string Tranche,
    string BaselineHealthy,
    string DefectInjected,
    string Recovery,
    string Evidence,
    ControlAttemptOutcome HealthyOutcome,
    ControlAttemptOutcome BrokenOutcome,
    ControlAttemptOutcome RestoredOutcome)
{
    public string BaselineColor => HealthyColor(HealthyOutcome);
    public string DefectColor => DefectOutcomeColor(BrokenOutcome);
    public string RecoveryColor => HealthyColor(RestoredOutcome);

    // Compatibility aliases for existing artifact/UI consumers. New UI uses the explicit names.
    public string Broken => LegacyBrokenText(BrokenOutcome);
    public string Restored => LegacyRestoredText(RestoredOutcome);
    public string BrokenColor => DefectColor;
    public string RestoredColor => RecoveryColor;
    public static ControlResultViewModel From(ControlResult control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return new(
            control.Id,
            control.Name,
            control.Category,
            control.ScopeClass == ControlScopeClass.ProductionObservation
                ? nameof(ControlScopeClass.ProductionObservation)
                : nameof(ControlScopeClass.BoundaryCalibrationFixture),
            control.Target,
            control.ObservationProducer,
            control.Evaluator,
            control.Tranche,
            HealthyText(control.HealthyOutcome),
            DefectText(control.BrokenOutcome),
            RecoveryText(control.RestoredOutcome),
            control.Evidence,
            control.HealthyOutcome,
            control.BrokenOutcome,
            control.RestoredOutcome);
    }

    public static ControlResultViewModel From(VitrineControlSnapshot control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return new(
            control.Id,
            control.Name,
            control.Category,
            control.ScopeClass.ToString(),
            control.Target,
            control.ObservationProducer,
            control.Evaluator,
            control.Tranche,
            HealthyText(control.HealthyOutcome),
            DefectText(control.BrokenOutcome),
            RecoveryText(control.RestoredOutcome),
            control.Evidence,
            control.HealthyOutcome,
            control.BrokenOutcome,
            control.RestoredOutcome);
    }

    private static string HealthyText(ControlAttemptOutcome outcome) => outcome switch
    {
        ControlAttemptOutcome.MeasuredPass => "HEALTHY · passed",
        ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved => "UNHEALTHY · failed",
        ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        _ => "UNKNOWN",
    };

    private static string DefectText(ControlAttemptOutcome outcome) => outcome switch
    {
        ControlAttemptOutcome.MeasuredFail => "DETECTED · expected fail",
        ControlAttemptOutcome.ExpectedFaultObserved => "DETECTED · expected fault",
        ControlAttemptOutcome.MeasuredPass => "MISSED · defect passed",
        ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        _ => "UNKNOWN",
    };

    private static string RecoveryText(ControlAttemptOutcome outcome) => outcome switch
    {
        ControlAttemptOutcome.MeasuredPass => "RECOVERED · passed",
        ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved => "NOT RECOVERED · failed",
        ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        _ => "UNKNOWN",
    };

    private static string LegacyBrokenText(ControlAttemptOutcome outcome) => outcome switch
    {
        ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved => "RED · caught",
        ControlAttemptOutcome.MeasuredPass => "GREEN · wiring fault",
        ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        _ => "UNKNOWN",
    };

    private static string LegacyRestoredText(ControlAttemptOutcome outcome) => outcome switch
    {
        ControlAttemptOutcome.MeasuredPass => "GREEN · restored",
        ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved => "RED · restore fault",
        ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        _ => "UNKNOWN",
    };

    private static string HealthyColor(ControlAttemptOutcome outcome) => outcome switch
    {
        ControlAttemptOutcome.MeasuredPass => "#63D391",
        ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved or ControlAttemptOutcome.InstrumentError => "#F07076",
        _ => "#F6C55C",
    };

    private static string DefectOutcomeColor(ControlAttemptOutcome outcome) => outcome switch
    {
        ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved => "#63D391",
        ControlAttemptOutcome.MeasuredPass or ControlAttemptOutcome.InstrumentError => "#F07076",
        _ => "#F6C55C",
    };
}

/// <summary>The authored scenario selected by a paid live-evaluation plan.</summary>
public sealed record LiveScenarioPlanViewModel(
    string Id,
    string PersonaId,
    string Title,
    string Description,
    string Query,
    string ExpectedBehavior,
    IReadOnlyList<string> Criteria)
{
    public string GroundTruthFacts { get; init; } = "No ground-truth facts recorded.";
    public string AgentToolExpectation { get; init; } = "No agent tool expectation recorded.";
    public string CriteriaSummary => Criteria.Count == 0
        ? "No criteria registered."
        : string.Join("\n", Criteria);
}

/// <summary>One sanitized subject response and its typed live-evaluation facts.</summary>
public sealed record LiveTrialResultViewModel(
    string ScenarioId,
    string PersonaId,
    string ArmId,
    string Architecture,
    int Repetition,
    string Measurement,
    string Outcome,
    string SubjectStatus,
    string ResponsePreview,
    string Tools,
    string Workflow,
    string Checks,
    string Criteria)
{
    public string Failure { get; init; } = "No typed trial failure recorded.";
    public string OutcomeColor => Outcome switch
    {
        "PASS" => "#63D391",
        "FAIL" or "SUBJECT FAILED" or "CANCELLED" => "#F07076",
        _ => "#F6C55C",
    };

    public string Identity => $"{ScenarioId} · {PersonaId} · {ArmId} · repetition {Repetition}";
}

public sealed record LiveSafetyAttackViewModel(
    string Attack,
    string OwaspId,
    int Total,
    int Resisted,
    int Compromised,
    int Inconclusive,
    int Errored)
{
    public string Outcome => Compromised > 0
        ? $"VULNERABLE · {Compromised} attack(s) succeeded" +
          (Errored > 0 ? $" · {Errored} probe error(s) also made coverage incomplete" : string.Empty)
        : Errored > 0
            ? $"INCOMPLETE · {Inconclusive} inconclusive ({Errored} errored subset)"
            : Inconclusive > 0
                ? $"NOT MEASURED · {Inconclusive} inconclusive"
            : $"RESISTED · {Resisted}/{Total}";
    public string OutcomeColor => Compromised > 0 ? "#F07076"
        : Errored > 0 ? "#F07076"
        : Inconclusive > 0 ? "#F6C55C" : "#63D391";
}

public sealed record LiveSafetyProbeViewModel(
    string Attack,
    string ProbeId,
    string Outcome,
    string ErrorKind,
    string Severity,
    string Fidelity,
    string Technique,
    string Diagnostic,
    string FailureStage,
    string FailureCode,
    string FailureDetail)
{
    public bool HasFailure => FailureCode != nameof(LiveSafetyProbeErrorKind.None);
    public string OutcomeLabel => ErrorKind != nameof(LiveSafetyProbeErrorKind.None)
        ? $"ERROR · {ErrorKind.ToUpperInvariant()}"
        : Outcome == nameof(LiveSafetyProbeOutcome.Compromised)
        ? "VULNERABLE · ATTACK SUCCEEDED"
        : Outcome == nameof(LiveSafetyProbeOutcome.Resisted)
            ? "RESISTED"
            : "INCONCLUSIVE · NOT MEASURED";
    public string OutcomeColor => ErrorKind != nameof(LiveSafetyProbeErrorKind.None)
        ? "#F07076"
        : Outcome == nameof(LiveSafetyProbeOutcome.Compromised)
        ? "#F07076"
        : Outcome == nameof(LiveSafetyProbeOutcome.Resisted)
            ? "#63D391"
            : "#F6C55C";
}

/// <summary>Evaluator-owned census and Wilson facts for one check in one live arm.</summary>
public sealed record LiveCheckSummaryViewModel(
    string ArmId,
    string Architecture,
    int Repetitions,
    string Key,
    string Name,
    string Measurement,
    string Census,
    string Reliability,
    string WilsonInterval)
{
    public string MeasurementColor => Measurement == "MEASURED" ? "#63D391" : "#F6C55C";
}

/// <summary>The exact per-scenario Wilson decision used by a stochastic terminal verdict.</summary>
public sealed record LiveScenarioAcceptanceViewModel(
    string ScenarioId,
    string PersonaId,
    string ArmId,
    string Architecture,
    string Census,
    string Reliability,
    string WilsonInterval,
    string Policy,
    string Outcome)
{
    public string Identity => $"{ScenarioId} · {PersonaId} · {ArmId} · {Architecture}";
    public string OutcomeColor => Outcome switch
    {
        "PASS" => "#63D391",
        "FAIL" => "#F07076",
        _ => "#F6C55C",
    };
}

/// <summary>AgentEval's case-paired comparison facts; this projection does not create a winner.</summary>
public sealed record LiveComparisonViewModel(
    string CheckKey,
    string CheckName,
    string Arms,
    string WinsLossesTies,
    int EffectiveN,
    string Census,
    string PValue,
    string MinimumAttainableP,
    string MeanDelta,
    int Cases,
    int TotalRepObservations,
    string MeanRepetitionsPerCase,
    string RepCollapse,
    string Power)
{
    public string ObservationUnit =>
        $"{Cases} paired case(s) · {TotalRepObservations} total raw reps across both arms · " +
        $"mean {MeanRepetitionsPerCase} reps/case/arm · RepCollapse.{RepCollapse}";
}

/// <summary>One provider-usage row. Missing and measured-zero remain distinct.</summary>
public sealed record LiveUsageViewModel(
    string ScenarioId,
    string ArmId,
    int Repetition,
    string Role,
    string Status,
    string ModelCalls,
    string Tokens,
    string EstimatedCostUsd)
{
    public string Summary => $"{ScenarioId} · {ArmId} · repetition {Repetition} · {Role} · {Status} · " +
        $"model calls {ModelCalls} · tokens {Tokens} · estimated USD {EstimatedCostUsd}";
}

/// <summary>One physical AgentEval run written for an arm and repetition.</summary>
public sealed record LiveRunReferenceViewModel(
    string RunId,
    string ArmId,
    int Repetition,
    string RelativeDirectory)
{
    public string Summary => $"{ArmId} · repetition {Repetition} · run {RunId} · {RelativeDirectory}";
}

public sealed class EvaluationBoardViewModel : BindableBase
{
    public const int ExpectedProductionObservations = 20;
    public const int ExpectedBoundaryCalibrationFixtures = 23;

    private const string EmptyControlSummary =
        "No control results yet · registered panel expects 43: 20 ProductionObservation + 23 BoundaryCalibrationFixture.";

    private string _overallStatus = "NOT RUN";
    private string _controlSummary = EmptyControlSummary;
    private string _honestClaims = "NOT MEASURED · validated historical honesty evidence has not run.";
    private string _runProgress = "Awaiting an evaluation run.";
    private string _activeStage = "No active evaluation stage.";
    private string _persistenceSummary = "Pending · a completed deterministic benchmark reports its exact local AgentEval directory here.";
    private string _benchmarkSummary = "Not run · no benchmark census is available.";
    private string _benchmarkFloorDerivation = "The null/chance baseline is supplied by AgentEval, not calculated by this UI.";
    private string _evaluationScope = OfflineScope;
    private string _evaluatorEngine = OfflineEvaluator;
    private string _modelUsageSummary = OfflineUsage;
    private int _totalGates = 6;
    private int _completedGates;
    private int _expectedControls = VitrineEvalCriteria.NegativeControlCount;
    private bool _finalResultLoaded;
    private bool _hasLiveEvaluation;
    private string _livePlanTitle = "No live evaluation selected.";
    private string _livePlanDescription = "Choose an explicit paid Eval01–Eval06 plan to see its workload.";
    private string _liveWorkloadSummary = "No paid workload planned.";
    private string _liveQualityPassBar = "NOT MEASURED · no live evaluator quality threshold recorded.";
    private string _liveOutcomePath = "NOT WRITTEN";
    private string _liveSessionSummary = "No live evaluation session.";
    private string _liveConfigurationSummary = "No live evaluation configuration.";
    private string _liveFailureSummary = "No typed live failures.";
    private string _liveSafetySummary = "No safety scan selected.";
    private string _liveSafetyStatus = "NOT RUN";

    public ObservableCollection<GateResultViewModel> Gates { get; } = [];
    public ObservableCollection<ControlResultViewModel> Controls { get; } = [];
    public ObservableCollection<LiveScenarioPlanViewModel> LiveScenarios { get; } = [];
    public ObservableCollection<LiveTrialResultViewModel> LiveTrials { get; } = [];
    public ObservableCollection<LiveCheckSummaryViewModel> LiveChecks { get; } = [];
    public ObservableCollection<LiveScenarioAcceptanceViewModel> LiveScenarioAcceptances { get; } = [];
    public ObservableCollection<LiveComparisonViewModel> LiveComparisons { get; } = [];
    public ObservableCollection<LiveUsageViewModel> LiveUsage { get; } = [];
    public ObservableCollection<LiveRunReferenceViewModel> LiveRuns { get; } = [];
    public ObservableCollection<LiveSafetyAttackViewModel> LiveSafetyAttacks { get; } = [];
    public ObservableCollection<LiveSafetyProbeViewModel> LiveSafetyProbes { get; } = [];

    private const string OfflineScope =
        "Targets Demo01's scripted ChatClientAgent with real read-only tools and Demo02's zero-model five-executor workflow. " +
        "Lanes: persisted deterministic use-case benchmark, judged quality, catalogue/topology, red-team, memory/honesty, and the registered mutation-control panel.";

    private const string OfflineEvaluator =
        "AgentEval 0.35 BenchmarkRunner (Definition → Arm → Runner → Score) plus EvaluationSuite's deterministic judged, red-team, memory, and policy evaluators.";

    private const string OfflineUsage =
        "Provider LLM calls: 0 · provider tokens/cost: 0 · offline deterministic clients only. Mocked model-boundary observations are not paid provider calls.";

    public string EvaluationScope
    {
        get => _evaluationScope;
        private set => SetProperty(ref _evaluationScope, value);
    }

    public string EvaluatorEngine
    {
        get => _evaluatorEngine;
        private set => SetProperty(ref _evaluatorEngine, value);
    }

    public string ModelUsageSummary
    {
        get => _modelUsageSummary;
        private set => SetProperty(ref _modelUsageSummary, value);
    }

    public string NullBaselineHelp =>
        "NULL / CHANCE BASELINE belongs only to an admitted eval and is descriptive comparison evidence—not a pass threshold or UI scoring. Mutation diagnostics are not evals and carry no floor.";

    public bool HasLiveEvaluation
    {
        get => _hasLiveEvaluation;
        private set => SetProperty(ref _hasLiveEvaluation, value);
    }

    public string LivePlanTitle
    {
        get => _livePlanTitle;
        private set => SetProperty(ref _livePlanTitle, value);
    }

    public string LivePlanDescription
    {
        get => _livePlanDescription;
        private set => SetProperty(ref _livePlanDescription, value);
    }

    public string LiveWorkloadSummary
    {
        get => _liveWorkloadSummary;
        private set => SetProperty(ref _liveWorkloadSummary, value);
    }

    public string LiveQualityPassBar
    {
        get => _liveQualityPassBar;
        private set => SetProperty(ref _liveQualityPassBar, value);
    }

    public string LiveOutcomePath
    {
        get => _liveOutcomePath;
        private set => SetProperty(ref _liveOutcomePath, value);
    }

    public string LiveSessionSummary
    {
        get => _liveSessionSummary;
        private set => SetProperty(ref _liveSessionSummary, value);
    }

    public string LiveConfigurationSummary
    {
        get => _liveConfigurationSummary;
        private set => SetProperty(ref _liveConfigurationSummary, value);
    }

    public string LiveFailureSummary
    {
        get => _liveFailureSummary;
        private set => SetProperty(ref _liveFailureSummary, value);
    }

    public string LiveSafetySummary
    {
        get => _liveSafetySummary;
        private set => SetProperty(ref _liveSafetySummary, value);
    }

    public string LiveSafetyStatus
    {
        get => _liveSafetyStatus;
        private set
        {
            if (SetProperty(ref _liveSafetyStatus, value)) RaisePropertyChanged(nameof(LiveSafetyStatusColor));
        }
    }

    public string LiveSafetyStatusColor => LiveSafetyStatus.StartsWith("RESISTED", StringComparison.Ordinal)
        ? "#63D391"
        : LiveSafetyStatus.StartsWith("VULNERABLE", StringComparison.Ordinal)
          || LiveSafetyStatus.StartsWith("INFRASTRUCTURE", StringComparison.Ordinal)
            ? "#F07076"
            : "#F6C55C";

    public string OverallStatus
    {
        get => _overallStatus;
        private set
        {
            if (SetProperty(ref _overallStatus, value)) RaisePropertyChanged(nameof(OverallStatusColor));
        }
    }

    public string OverallStatusColor => OverallStatus.StartsWith("PASS", StringComparison.Ordinal)
        || OverallStatus.StartsWith("SELF-TEST SUCCEEDED", StringComparison.Ordinal)
        || OverallStatus.StartsWith("RESISTED", StringComparison.Ordinal)
        ? "#63D391"
        : OverallStatus.StartsWith("FAIL", StringComparison.Ordinal)
          || OverallStatus.StartsWith("INFRASTRUCTURE", StringComparison.Ordinal)
          || OverallStatus.StartsWith("VULNERABLE", StringComparison.Ordinal)
          || OverallStatus.StartsWith("SELF-TEST FAILED", StringComparison.Ordinal)
          || OverallStatus.StartsWith("SELF-TEST INFRASTRUCTURE", StringComparison.Ordinal)
            ? "#F07076"
            : "#F6C55C";

    public string ControlSummary
    {
        get => _controlSummary;
        private set => SetProperty(ref _controlSummary, value);
    }

    public string HonestClaims
    {
        get => _honestClaims;
        private set => SetProperty(ref _honestClaims, value);
    }

    public string RunProgress
    {
        get => _runProgress;
        private set => SetProperty(ref _runProgress, value);
    }

    public string ActiveStage
    {
        get => _activeStage;
        private set => SetProperty(ref _activeStage, value);
    }

    public string PersistenceSummary
    {
        get => _persistenceSummary;
        private set => SetProperty(ref _persistenceSummary, value);
    }

    public string BenchmarkSummary
    {
        get => _benchmarkSummary;
        private set => SetProperty(ref _benchmarkSummary, value);
    }

    public string BenchmarkFloorDerivation
    {
        get => _benchmarkFloorDerivation;
        private set => SetProperty(ref _benchmarkFloorDerivation, value);
    }

    public bool HasGates => Gates.Count > 0;
    public bool HasControls => Controls.Count > 0;
    public bool HasLiveScenarios => LiveScenarios.Count > 0;
    public bool HasLiveTrials => LiveTrials.Count > 0;
    public bool HasLiveChecks => LiveChecks.Count > 0;
    public bool HasLiveScenarioAcceptances => LiveScenarioAcceptances.Count > 0;
    public bool HasLiveComparisons => LiveComparisons.Count > 0;
    public bool HasLiveUsage => LiveUsage.Count > 0;
    public bool HasLiveRuns => LiveRuns.Count > 0;
    public bool HasLiveSafety => LiveSafetyAttacks.Count > 0 || LiveSafetyProbes.Count > 0
        || !LiveSafetyStatus.StartsWith("NOT RUN", StringComparison.Ordinal);
    public bool HasBenchmark => !PersistenceSummary.StartsWith("Pending", StringComparison.Ordinal)
        && !PersistenceSummary.StartsWith("Not written", StringComparison.Ordinal)
        && !PersistenceSummary.StartsWith("Running", StringComparison.Ordinal);

    public void Clear()
    {
        _finalResultLoaded = false;
        Gates.Clear();
        Controls.Clear();
        ClearLiveCollections();
        HasLiveEvaluation = false;
        LivePlanTitle = "No live evaluation selected.";
        LivePlanDescription = "Choose an explicit paid Eval01–Eval06 plan to see its workload.";
        LiveWorkloadSummary = "No paid workload planned.";
        LiveQualityPassBar = "NOT MEASURED · no live evaluator quality threshold recorded.";
        LiveOutcomePath = "NOT WRITTEN";
        LiveSessionSummary = "No live evaluation session.";
        LiveConfigurationSummary = "No live evaluation configuration.";
        LiveFailureSummary = "No typed live failures.";
        LiveSafetySummary = "No safety scan selected.";
        LiveSafetyStatus = "NOT RUN";
        _totalGates = 6;
        _completedGates = 0;
        _expectedControls = VitrineEvalCriteria.NegativeControlCount;
        OverallStatus = "NOT RUN";
        ControlSummary = EmptyControlSummary;
        HonestClaims = "NOT MEASURED · validated historical honesty evidence has not run.";
        RunProgress = "Awaiting an evaluation run.";
        ActiveStage = "No active evaluation stage.";
        PersistenceSummary = "Pending · a completed deterministic benchmark reports its exact local AgentEval directory here.";
        BenchmarkSummary = "Not run · no benchmark census is available.";
        BenchmarkFloorDerivation = "The null/chance baseline is supplied by AgentEval, not calculated by this UI.";
        EvaluationScope = OfflineScope;
        EvaluatorEngine = OfflineEvaluator;
        ModelUsageSummary = OfflineUsage;
        RaiseCollectionState();
    }

    /// <summary>Projects the selected paid plan before execution; it does not run or score anything.</summary>
    public void PrepareLive(
        VitrineEvaluationPlan plan,
        LiveEvalWorkload workload,
        IReadOnlyList<LiveUseCaseScenario>? scenarios)
    {
        ArgumentNullException.ThrowIfNull(workload);
        var descriptor = VitrineEvaluationPlans.Require(plan);
        if (!descriptor.IsLive)
            throw new ArgumentException("PrepareLive requires one of the explicit paid Eval01–Eval06 plans.", nameof(plan));

        _finalResultLoaded = false;
        Gates.Clear();
        Controls.Clear();
        ClearLiveCollections();
        PopulateLiveScenarios(scenarios ?? []);
        HasLiveEvaluation = true;
        LivePlanTitle = descriptor.Label;
        LivePlanDescription = descriptor.Description;
        LiveWorkloadSummary = WorkloadSummary(plan, workload);
        var safetyPlan = plan == VitrineEvaluationPlan.LiveEval06SafetyProbes;
        LiveQualityPassBar = safetyPlan
            ? "NOT APPLICABLE · Eval06 uses compromised/resisted/inconclusive/error safety outcomes, not the 1.000 use-case quality bar."
            : "PENDING · the completed result will disclose the evaluator-owned quality pass bar; the shipped 1.000 bar requires all four authored criteria and is not a null/chance floor.";
        LiveOutcomePath = "PENDING · no live outcome has been written yet.";
        LiveSessionSummary = "READY · execution has not started.";
        OverallStatus = "READY · paid live evaluation";
        ControlSummary = "Not part of this live plan · run Offline suite or Controls for the 43-row diagnostic panel.";
        HonestClaims = safetyPlan
            ? "Safety facts use explicit semantics: Compromised means the attack succeeded and is red; Resisted is green; incomplete or errored scans remain NOT MEASURED."
            : "Live criteria and reliability facts will be shown per use case and check; no cross-check average will be manufactured.";
        RunProgress = safetyPlan
            ? $"0/{workload.PlannedSafetyProbes} bounded safety probes · up to {workload.MaximumSafetyModelCalls} safety model calls."
            : $"0/{workload.PlannedSubjectCalls} subject trials · 0/{workload.PlannedJudgeEvaluations} planned judge evaluations.";
        ActiveStage = safetyPlan
            ? "Ready · review Robin-only target, two attack categories, bounded probes, and paid-call ceiling before running."
            : "Ready · review scenarios, arms, repetitions, and paid workload before running.";
        PersistenceSummary = safetyPlan
            ? "Pending · only the redacted safety outcome and session index will be persisted; there are no BenchmarkRunner run references."
            : "Pending · the live session outcome path and physical AgentEval runs appear after persistence.";
        BenchmarkSummary = $"Planned live workload · {WorkloadSummary(plan, workload)}";
        BenchmarkFloorDerivation = safetyPlan
            ? "NOT APPLICABLE · safety compromise is not compared with a chance floor or use-case quality threshold."
            : VitrineEvaluationPlans.IsStochastic(plan)
                ? $"Terminal acceptance requires every planned trial to be fully measured. Whole-trial successes are grouped per arm/scenario, and each group must independently clear the {VitrineEvaluationPlans.StochasticConfidenceLevel:P0} Wilson lower-bound SLO of {VitrineEvaluationPlans.StochasticMinimumWilsonLowerBound:0.000}. Pooled per-check reliability remains diagnostic."
                : "Every fully measured trial must pass. Per-check census and Wilson intervals remain explanatory evidence; the UI does not average checks or derive a winner.";
        EvaluationScope = safetyPlan
            ? $"{descriptor.Label} · Robin-only fresh target · {workload.SafetyAttackCount} attack categories × up to " +
              $"{workload.PlannedSafetyProbes / Math.Max(1, workload.SafetyAttackCount)} probes."
            : $"{descriptor.Label} · {ArmPlan(plan)} · {workload.ScenarioCount} authored scenario(s) × " +
              $"{workload.ArmCount} arm(s) × {workload.Repetitions} repetition(s).";
        EvaluatorEngine = safetyPlan
            ? "Released AgentEval RedTeamRunner with JailbreakAttack and SystemPromptExtractionAttack, fallback judge, and an in-memory canary. Raw canary, probes, responses, and system instructions never cross this board."
            : "Fresh live subjects plus the registered live use-case judge and AgentEval benchmark persistence. One judge evaluation follows each measurable subject call. Provider failure cannot count as measured; bounded internal fallbacks are disclosed. This board only projects returned facts.";
        ModelUsageSummary = safetyPlan
            ? $"Paid workload planned: {workload.PlannedSubjectCalls} Robin target call(s) + up to " +
              $"{workload.PlannedJudgeEvaluations} fallback-judge call(s) · maximum {workload.MaximumSafetyModelCalls} safety model calls. Usage is NOT MEASURED until providers report it."
            : $"Paid workload planned: {workload.PlannedSubjectCalls} subject call(s) + " +
              $"{workload.PlannedJudgeEvaluations} judge evaluation(s). Calls, tokens, and cost are NOT MEASURED until providers report them.";
        LiveConfigurationSummary = safetyPlan
            ? "Planned · Jailbreak + SystemPromptExtraction · max 2 probes/attack · 45 s/probe · fallback judge · raw evidence disabled."
            : "Configuration is recorded after execution, including safe model identity and evaluator fingerprint.";
        LiveFailureSummary = "No typed live failure yet.";
        LiveSafetyStatus = safetyPlan ? "READY · SAFETY PROBES NOT RUN" : "NOT RUN";
        LiveSafetySummary = safetyPlan
            ? "Pending · no probe outcome exists before execution."
            : "Not applicable · selected plan is a use-case evaluation.";
        RaiseCollectionState();
    }

    /// <summary>Applies allow-listed live progress as copy only; result rows arrive via LoadLive.</summary>
    public void ApplyLive(LiveEvalProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var descriptor = VitrineEvaluationPlans.Require(progress.Plan);
        if (!descriptor.IsLive)
            throw new ArgumentException("ApplyLive requires progress from an explicit paid Eval01–Eval06 plan.", nameof(progress));
        if (_finalResultLoaded && progress.Phase != LiveEvalProgressPhase.SessionStarting)
            return;

        HasLiveEvaluation = true;
        LivePlanTitle = descriptor.Label;
        LivePlanDescription = descriptor.Description;
        var context = new[]
            {
                progress.ScenarioId is null ? null : $"scenario {progress.ScenarioId}",
                progress.ArmId is null ? null : $"arm {progress.ArmId}",
                progress.Repetition is null ? null : $"rep {progress.Repetition.Value}",
                progress.CheckKey is null ? null : $"check {progress.CheckKey}",
            }
            .Where(static item => item is not null)
            .Cast<string>()
            .ToArray();
        var typed = new[]
            {
                progress.Measurement is null ? null : $"measurement {Measurement(progress.Measurement.Value)}",
                progress.Passed is null ? null : $"verdict {Verdict(progress.Passed)}",
            }
            .Where(static item => item is not null)
            .Cast<string>()
            .ToArray();
        var phase = progress.Phase switch
        {
            LiveEvalProgressPhase.SessionStarting => "SESSION STARTING",
            LiveEvalProgressPhase.TrialStarting => "TRIAL STARTING",
            LiveEvalProgressPhase.SubjectRunning => "SUBJECT RUNNING",
            LiveEvalProgressPhase.SubjectCompleted => "SUBJECT COMPLETED",
            LiveEvalProgressPhase.CheckStarting => "CHECK STARTING",
            LiveEvalProgressPhase.CheckCompleted => "CHECK COMPLETED",
            LiveEvalProgressPhase.TrialCompleted => "TRIAL COMPLETED",
            LiveEvalProgressPhase.Persisting => "PERSISTING",
            LiveEvalProgressPhase.SessionCompleted => "SESSION COMPLETED",
            _ => progress.Phase.ToString().ToUpperInvariant(),
        };
        var suffix = string.Join(" · ", context.Concat(typed));
        ActiveStage = $"{phase}{(suffix.Length == 0 ? string.Empty : $" · {suffix}")} · {progress.Detail}";
        LiveSessionSummary = $"{phase}{(suffix.Length == 0 ? string.Empty : $" · {suffix}")}";
        RunProgress = ActiveStage;

        if (progress.Phase == LiveEvalProgressPhase.SessionStarting)
        {
            _finalResultLoaded = false;
            OverallStatus = "RUNNING · paid live evaluation";
            PersistenceSummary = "Running · live outcome has not been persisted yet.";
        }
        else if (progress.Phase == LiveEvalProgressPhase.Persisting)
        {
            OverallStatus = "RUNNING · persisting live result";
            PersistenceSummary = "Persisting · waiting for the typed local outcome path.";
        }
        else if (progress.Phase == LiveEvalProgressPhase.SessionCompleted)
        {
            OverallStatus = progress.Measurement == MeasurementState.Measured
                ? progress.Passed switch
                {
                    true => "PASS · live evaluation completed",
                    false => "FAIL · live quality evaluation completed",
                    null => "NOT MEASURED · live evaluation completed",
                }
                : "NOT MEASURED · live evaluation completed";
        }
        else
        {
            OverallStatus = "RUNNING · paid live evaluation";
        }

        if (progress.Phase is LiveEvalProgressPhase.CheckStarting or LiveEvalProgressPhase.CheckCompleted)
            BenchmarkSummary = $"{phase} · {progress.CheckKey ?? "unknown check"} · " +
                $"{(progress.Measurement.HasValue ? Measurement(progress.Measurement.Value) : "measurement pending")} · " +
                $"{(progress.Passed.HasValue ? Verdict(progress.Passed) : "verdict pending")} · {progress.Detail}";
        RaiseCollectionState();
    }

    /// <summary>Projects a completed evaluator-owned live result without recalculating any verdict.</summary>
    public void LoadLive(LiveEvalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var descriptor = VitrineEvaluationPlans.Require(result.Plan);
        if (!descriptor.IsLive)
            throw new ArgumentException("LoadLive requires one of the explicit paid Eval01–Eval06 results.", nameof(result));

        _finalResultLoaded = true;
        Gates.Clear();
        Controls.Clear();
        ClearLiveCollections();
        if (result.Scenarios.Count > 0)
        {
            PopulateLiveScenarios(result.Scenarios);
        }
        else
        {
            var scenarioIds = result.Trials.Select(static trial => trial.ScenarioId)
                .Distinct(StringComparer.Ordinal).ToArray();
            var scenarios = scenarioIds.Select(id => LiveUseCaseScenarios.All.FirstOrDefault(item =>
                    string.Equals(item.Id, id, StringComparison.Ordinal)))
                .Where(static item => item is not null)
                .Cast<LiveUseCaseScenario>()
                .ToArray();
            PopulateLiveScenarios(scenarios);
        }

        foreach (var trial in result.Trials)
        {
            LiveTrials.Add(ProjectTrial(trial));
            LiveUsage.Add(ProjectUsage(trial, "SUBJECT", trial.SubjectUsage));
            LiveUsage.Add(ProjectUsage(trial, "JUDGE", trial.JudgeUsage));
        }
        foreach (var arm in result.Arms)
            foreach (var check in arm.Checks)
                LiveChecks.Add(ProjectCheck(arm, check));
        foreach (var decision in result.ScenarioAcceptances)
            LiveScenarioAcceptances.Add(ProjectScenarioAcceptance(decision));
        foreach (var comparison in result.Comparisons)
            LiveComparisons.Add(ProjectComparison(comparison));
        foreach (var run in result.Runs)
            LiveRuns.Add(new(run.RunId, run.ArmId, run.Repetition, run.RelativeDirectory));

        if (result.Plan == VitrineEvaluationPlan.LiveEval06SafetyProbes)
        {
            LoadSafety(result, descriptor);
            RaiseCollectionState();
            return;
        }

        HasLiveEvaluation = true;
        LivePlanTitle = descriptor.Label;
        LivePlanDescription = descriptor.Description;
        LiveQualityPassBar = QualityPassBar(result.PassThreshold);
        LiveWorkloadSummary = $"{WorkloadSummary(result.Plan, result.Workload)} · {LiveQualityPassBar}";
        LiveOutcomePath = result.Persistence.OutcomePath;
        LiveSessionSummary = $"{result.SessionId} · {result.StartedAtUtc:O} → {result.CompletedAtUtc:O} · " +
            $"{result.TerminalStatus} · exit {result.ExitCode}";
        OverallStatus = result.TerminalStatus switch
        {
            LiveEvalTerminalStatus.Passed => "PASS · live eval exit 0",
            LiveEvalTerminalStatus.QualityFailed => "FAIL · live quality exit 1",
            LiveEvalTerminalStatus.NotMeasured => "NOT MEASURED · live eval exit 3",
            LiveEvalTerminalStatus.InfrastructureError => "INFRASTRUCTURE FAILURE · live eval exit 4",
            LiveEvalTerminalStatus.Cancelled => "CANCELLED · live eval exit 130",
            _ => $"UNKNOWN LIVE STATUS · exit {result.ExitCode}",
        };
        ControlSummary = "Not part of this live result · the 43-row registered diagnostic panel remains in Offline suite and Controls.";
        HonestClaims = VitrineEvaluationPlans.IsStochastic(result.Plan)
            ? $"Terminal acceptance uses {result.ScenarioAcceptances.Count} per-arm/per-scenario decision(s): every planned trial must be fully measured, and whole-trial successes must independently clear the 95% Wilson lower-bound floor of 0.500. Pooled per-check rows are diagnostic."
            : result.Comparisons.Count == 0
            ? "Every fully measured trial must pass. Per-arm, per-check census and Wilson reliability are reported independently; no cross-check average was manufactured."
            : "AgentEval case-paired comparison facts are shown per check; W/L/T, effective n, p-value, minimum attainable p, case-level observation unit, rep collapse, mean delta, census, and power are not converted into a UI winner.";
        RunProgress = $"{result.Trials.Count}/{result.Workload.PlannedSubjectCalls} subject trials recorded · " +
            $"{result.Runs.Count} physical AgentEval arm × repetition run(s) · completed.";
        ActiveStage = $"Live evaluation complete · {result.TerminalStatus} · typed LiveEvalResult loaded.";
        PersistenceSummary = $"Outcome {result.Persistence.OutcomePath} · index {result.Persistence.IndexPath} · " +
            $"workspace {result.Persistence.WorkspaceRoot} · {result.Runs.Count} physical run reference(s).";
        var checkCensus = result.Arms.SelectMany(static arm => arm.Checks).ToArray();
        var checkSummary = $"{checkCensus.Sum(static item => item.Census.Measured)} measured / " +
            $"{checkCensus.Sum(static item => item.Census.NotApplicable)} not applicable / " +
            $"{checkCensus.Sum(static item => item.Census.NotMeasured)} not measured check observations";
        BenchmarkSummary = VitrineEvaluationPlans.IsStochastic(result.Plan)
            ? $"{descriptor.Label} · {result.ScenarioAcceptances.Count} terminal per-arm/scenario whole-trial Wilson decision(s) · " +
              $"{result.Workload.ScenarioCount} scenarios × {result.Workload.ArmCount} arms × " +
              $"{result.Workload.Repetitions} reps · {result.Trials.Count} trials · {checkCensus.Length} diagnostic arm/check summaries · " +
              $"{checkSummary} · {result.Comparisons.Count} paired comparisons."
            : $"{descriptor.Label} · {result.Workload.ScenarioCount} scenarios × {result.Workload.ArmCount} arms × " +
              $"{result.Workload.Repetitions} reps · {result.Trials.Count} trials · {checkCensus.Length} arm/check summaries · " +
              $"{checkSummary} · {result.Comparisons.Count} paired comparisons.";
        BenchmarkFloorDerivation = VitrineEvaluationPlans.IsStochastic(result.Plan)
            ? $"{LiveQualityPassBar} Every terminal decision requires full measurement; whole-trial success also requires response/trace checks before the 95% Wilson lower-bound floor of 0.500 is applied. Decisions are copied from LiveScenarioAcceptanceDecision; pooled per-check summaries are diagnostic."
            : $"{LiveQualityPassBar} No null/chance floor or aggregate was applied by the UI. Each displayed success count, denominator, estimate, and Wilson interval is copied from LiveCheckSummary; each comparison is copied from LiveCheckComparison.";
        EvaluationScope = $"{descriptor.Label} · {ArmPlan(result.Plan)} · {result.Workload.ScenarioCount} authored scenario(s) × " +
            $"{result.Workload.ArmCount} arm(s) × {result.Workload.Repetitions} repetition(s).";
        EvaluatorEngine =
            "Live subject observations + registered use-case criteria + evaluator-owned AgentEval census, Wilson reliability, and optional case-paired BenchmarkScore comparisons. Provider failure cannot count as measured; bounded internal fallbacks are disclosed per workflow trial. The app does not re-score.";
        ModelUsageSummary = UsageSummary(result.Trials);
        LiveConfigurationSummary = ConfigurationSummary(result.Configuration);
        LiveFailureSummary = FailureSummary(result.Failures);
        RaiseCollectionState();
    }

    /// <summary>Rehydrates the board from an integrity-verified schema 7–9 projection only.</summary>
    public void LoadLiveSnapshot(VitrineLiveEvaluationSnapshot result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Enum.TryParse<VitrineEvaluationPlan>(result.Plan, out var plan))
            throw new InvalidDataException("The live artifact contains an unknown evaluation plan.");
        var descriptor = VitrineEvaluationPlans.Require(plan);
        if (!descriptor.IsLive)
            throw new InvalidDataException("The live artifact does not identify a paid Eval01–Eval06 plan.");

        _finalResultLoaded = true;
        Gates.Clear();
        Controls.Clear();
        ClearLiveCollections();
        foreach (var scenario in result.Scenarios)
            LiveScenarios.Add(new(
                scenario.Id,
                scenario.PersonaId,
                scenario.Title,
                scenario.Description,
                scenario.Query,
                scenario.ExpectedBehavior,
                scenario.Criteria.Select(static criterion => $"{criterion.Id} · {criterion.Text}").ToArray())
            {
                GroundTruthFacts = scenario.GroundTruthFacts.Count == 0
                    ? "No ground-truth facts recorded."
                    : string.Join("\n", scenario.GroundTruthFacts),
                AgentToolExpectation = scenario.AgentToolExpectation is null
                    ? "No agent tool expectation recorded."
                    : ToolExpectation(
                        scenario.AgentToolExpectation.RequiresAbstention,
                        scenario.AgentToolExpectation.RequiredTools,
                        scenario.AgentToolExpectation.ForbiddenTools,
                        scenario.AgentToolExpectation.ForbiddenPresentedSkus),
            });
        foreach (var trial in result.Trials)
        {
            LiveTrials.Add(ProjectTrial(trial));
            LiveUsage.Add(ProjectUsage(trial, "SUBJECT", trial.SubjectUsage));
            LiveUsage.Add(ProjectUsage(trial, "JUDGE", trial.JudgeUsage));
        }
        foreach (var arm in result.Arms)
            foreach (var check in arm.Checks)
                LiveChecks.Add(ProjectCheck(arm, check));
        foreach (var decision in result.ScenarioAcceptances ?? [])
            LiveScenarioAcceptances.Add(ProjectScenarioAcceptance(decision));
        foreach (var comparison in result.Comparisons)
            LiveComparisons.Add(ProjectComparison(comparison));
        foreach (var run in result.Runs)
            LiveRuns.Add(new(run.RunId, run.ArmId, run.Repetition, run.RelativeDirectory));

        if (plan == VitrineEvaluationPlan.LiveEval06SafetyProbes)
        {
            LoadSafety(result);
            RaiseCollectionState();
            return;
        }

        HasLiveEvaluation = true;
        LivePlanTitle = result.PlanLabel;
        LivePlanDescription = result.PlanDescription;
        LiveQualityPassBar = QualityPassBar(result.PassThreshold);
        LiveWorkloadSummary = $"{WorkloadSummary(plan, result.Workload)} · {LiveQualityPassBar}";
        LiveOutcomePath = result.Persistence.OutcomePath;
        LiveSessionSummary = $"{result.SessionId} · {result.StartedAtUtc:O} → {result.CompletedAtUtc:O} · " +
            $"{result.TerminalStatus} · exit {result.ExitCode}";
        OverallStatus = result.TerminalStatus switch
        {
            nameof(LiveEvalTerminalStatus.Passed) => "PASS · live eval exit 0",
            nameof(LiveEvalTerminalStatus.QualityFailed) => "FAIL · live quality exit 1",
            nameof(LiveEvalTerminalStatus.NotMeasured) => "NOT MEASURED · live eval exit 3",
            nameof(LiveEvalTerminalStatus.InfrastructureError) => "INFRASTRUCTURE FAILURE · live eval exit 4",
            nameof(LiveEvalTerminalStatus.Cancelled) => "CANCELLED · live eval exit 130",
            _ => $"UNKNOWN LIVE STATUS · exit {result.ExitCode}",
        };
        ControlSummary = "Not part of this live result · the 43-row registered diagnostic panel remains in Offline suite and Controls.";
        HonestClaims = VitrineEvaluationPlans.IsStochastic(plan)
            ? $"Replayed terminal acceptance uses {result.ScenarioAcceptances?.Count ?? 0} integrity-verified per-arm/per-scenario decision(s): every planned trial must be fully measured, and whole-trial successes must independently clear the 95% Wilson lower-bound floor of 0.500. Pooled per-check rows are diagnostic."
            : result.Comparisons.Count == 0
            ? "Every fully measured trial must pass. Per-arm, per-check census and Wilson reliability are replayed independently; no cross-check average was manufactured."
            : "AgentEval case-paired comparison facts are replayed per check; W/L/T, effective n, p-value, minimum attainable p, case-level observation unit, rep collapse, mean delta, census, and power are not converted into a UI winner.";
        RunProgress = $"{result.Trials.Count}/{result.Workload.PlannedSubjectCalls} subject trials replayed · " +
            $"{result.Runs.Count} physical AgentEval arm × repetition run(s) · completed.";
        ActiveStage = $"Live evaluation replay · {result.TerminalStatus} · compatible schema 7–9 snapshot loaded; no execution occurred.";
        PersistenceSummary = $"Outcome {result.Persistence.OutcomePath} · index {result.Persistence.IndexPath} · " +
            $"workspace {result.Persistence.WorkspaceRoot} · {result.Runs.Count} physical run reference(s).";
        var checks = result.Arms.SelectMany(static arm => arm.Checks).ToArray();
        var replayCheckSummary = $"{checks.Sum(static item => item.Census.Measured)} measured / " +
            $"{checks.Sum(static item => item.Census.NotApplicable)} not applicable / " +
            $"{checks.Sum(static item => item.Census.NotMeasured)} not measured check observations";
        BenchmarkSummary = VitrineEvaluationPlans.IsStochastic(plan)
            ? $"{result.PlanLabel} · {result.ScenarioAcceptances?.Count ?? 0} terminal per-arm/scenario whole-trial Wilson decision(s) · " +
              $"{result.Workload.ScenarioCount} scenarios × {result.Workload.ArmCount} arms × " +
              $"{result.Workload.Repetitions} reps · {result.Trials.Count} trials · {checks.Length} diagnostic arm/check summaries · " +
              $"{replayCheckSummary} · {result.Comparisons.Count} paired comparisons."
            : $"{result.PlanLabel} · {result.Workload.ScenarioCount} scenarios × {result.Workload.ArmCount} arms × " +
              $"{result.Workload.Repetitions} reps · {result.Trials.Count} trials · {checks.Length} arm/check summaries · " +
              $"{replayCheckSummary} · {result.Comparisons.Count} paired comparisons.";
        BenchmarkFloorDerivation = VitrineEvaluationPlans.IsStochastic(plan)
            ? $"{LiveQualityPassBar} Every terminal decision requires full measurement; whole-trial success also requires response/trace checks before the 95% Wilson lower-bound floor of 0.500 is applied. Decisions and their underlying trial census are integrity-verified artifact facts; pooled per-check summaries are diagnostic."
            : $"{LiveQualityPassBar} No null/chance floor or aggregate was applied during replay. Success counts, denominators, estimates, Wilson intervals, and comparisons are the integrity-verified artifact facts.";
        EvaluationScope = $"{result.PlanLabel} · {ArmPlan(plan)} · {result.Workload.ScenarioCount} authored scenario(s) × " +
            $"{result.Workload.ArmCount} arm(s) × {result.Workload.Repetitions} repetition(s).";
        EvaluatorEngine =
            "Replay only · evaluator-owned live census, Wilson reliability, optional paired comparisons, and disclosed bounded fallback facts restored from the compatible artifact; provider failure cannot count as measured, and no model, subject, judge, tool, or evaluator ran.";
        ModelUsageSummary = UsageSummary(result.Trials);
        LiveConfigurationSummary = ConfigurationSummary(result.Configuration);
        LiveFailureSummary = FailureSummary(result.Failures);
        RaiseCollectionState();
    }

    private void LoadSafety(LiveEvalResult result, VitrineEvaluationPlanDescriptor descriptor)
    {
        if (result.Safety is { } safety)
        {
            foreach (var attack in safety.Attacks)
                LiveSafetyAttacks.Add(new(attack.Attack, attack.OwaspId, attack.Total,
                    attack.Resisted, attack.Compromised, attack.Inconclusive, attack.Errored));
            foreach (var probe in safety.Probes)
                LiveSafetyProbes.Add(ProjectSafetyProbe(probe));
            LiveUsage.Add(ProjectSafetyUsage("TARGET", safety.SubjectUsage));
            LiveUsage.Add(ProjectSafetyUsage("FALLBACK JUDGE", safety.JudgeUsage));
        }

        HasLiveEvaluation = true;
        LivePlanTitle = descriptor.Label;
        LivePlanDescription = descriptor.Description;
        LiveQualityPassBar = "NOT APPLICABLE · Eval06 does not use the use-case quality threshold.";
        LiveWorkloadSummary = WorkloadSummary(result.Plan, result.Workload);
        LiveOutcomePath = result.Persistence.OutcomePath;
        LiveSessionSummary = $"{result.SessionId} · {result.StartedAtUtc:O} → {result.CompletedAtUtc:O} · " +
            $"{result.TerminalStatus} · exit {result.ExitCode}";
        LiveSafetyStatus = SafetyStatus(result.Safety, result.TerminalStatus);
        LiveSafetySummary = SafetySummary(result.Safety, result.TerminalStatus);
        OverallStatus = $"{LiveSafetyStatus} · exit {result.ExitCode}";
        ControlSummary = "Not part of Eval06 · the 43-row registered diagnostic panel remains in Offline suite and Controls.";
        HonestClaims = "Compromised means the attack succeeded and Robin is vulnerable; Resisted means a conclusive defence. Errored is a subset of Inconclusive. A clean ambiguous campaign is NOT MEASURED; execution faults or incomplete coverage are an INFRASTRUCTURE ERROR, and neither is a safety pass.";
        RunProgress = result.Safety is { } completedSafety
            ? $"{completedSafety.Total}/{result.Workload.PlannedSafetyProbes} probes recorded · {completedSafety.Resisted} resisted · " +
              $"{completedSafety.Compromised} compromised · {completedSafety.Inconclusive} inconclusive ({completedSafety.Errored} errored subset)."
            : $"0/{result.Workload.PlannedSafetyProbes} probes recorded · no safety summary returned.";
        ActiveStage = $"Safety evaluation complete · {result.TerminalStatus} · typed redacted safety result loaded.";
        PersistenceSummary = $"Redacted outcome {result.Persistence.OutcomePath} · index {result.Persistence.IndexPath} · " +
            $"workspace {result.Persistence.WorkspaceRoot} · no BenchmarkRunner run references.";
        BenchmarkSummary = LiveSafetySummary;
        BenchmarkFloorDerivation = "NOT APPLICABLE · compromise/resistance is not a chance-floor comparison, Wilson estimate, or 1.000 use-case quality threshold.";
        EvaluationScope = $"{descriptor.Label} · Robin-only fresh target · {result.Workload.SafetyAttackCount} attack categories · " +
            $"up to {result.Workload.PlannedSafetyProbes} bounded probes.";
        EvaluatorEngine = "Released AgentEval RedTeamRunner · JailbreakAttack + SystemPromptExtractionAttack · fallback judge. Raw canary, probes, responses, and system instructions were excluded at the result boundary.";
        ModelUsageSummary = SafetyUsageSummary(result.Safety?.SubjectUsage, result.Safety?.JudgeUsage);
        LiveConfigurationSummary = ConfigurationSummary(result.Configuration);
        LiveFailureSummary = FailureSummary(result.Failures);
    }

    private void LoadSafety(VitrineLiveEvaluationSnapshot result)
    {
        if (result.Safety is { } safety)
        {
            foreach (var attack in safety.Attacks)
                LiveSafetyAttacks.Add(new(attack.Attack, attack.OwaspId, attack.Total,
                    attack.Resisted, attack.Compromised, attack.Inconclusive, attack.Errored));
            foreach (var probe in safety.Probes)
                LiveSafetyProbes.Add(ProjectSafetyProbe(probe));
            LiveUsage.Add(ProjectSafetyUsage("TARGET", safety.SubjectUsage));
            LiveUsage.Add(ProjectSafetyUsage("FALLBACK JUDGE", safety.JudgeUsage));
        }

        HasLiveEvaluation = true;
        LivePlanTitle = result.PlanLabel;
        LivePlanDescription = result.PlanDescription;
        LiveQualityPassBar = "NOT APPLICABLE · Eval06 does not use the use-case quality threshold.";
        LiveWorkloadSummary = WorkloadSummary(VitrineEvaluationPlan.LiveEval06SafetyProbes, result.Workload);
        LiveOutcomePath = result.Persistence.OutcomePath;
        LiveSessionSummary = $"{result.SessionId} · {result.StartedAtUtc:O} → {result.CompletedAtUtc:O} · " +
            $"{result.TerminalStatus} · exit {result.ExitCode}";
        LiveSafetyStatus = SafetyStatus(result.Safety, result.TerminalStatus);
        LiveSafetySummary = SafetySummary(result.Safety, result.TerminalStatus);
        OverallStatus = $"{LiveSafetyStatus} · exit {result.ExitCode}";
        ControlSummary = "Not part of Eval06 · the 43-row registered diagnostic panel remains in Offline suite and Controls.";
        HonestClaims = "Replayed semantics: Compromised means the attack succeeded and Robin is vulnerable; Resisted means a conclusive defence. Errored is a subset of Inconclusive. A clean ambiguous campaign is NOT MEASURED; execution faults or incomplete coverage are an INFRASTRUCTURE ERROR, and neither is a safety pass.";
        RunProgress = result.Safety is { } completedSafety
            ? $"{completedSafety.Total}/{result.Workload.PlannedSafetyProbes} probes replayed · {completedSafety.Resisted} resisted · " +
              $"{completedSafety.Compromised} compromised · {completedSafety.Inconclusive} inconclusive ({completedSafety.Errored} errored subset)."
            : $"0/{result.Workload.PlannedSafetyProbes} probes replayed · no safety summary returned.";
        ActiveStage = $"Safety evaluation replay · {result.TerminalStatus} · compatible schema 7–9 redacted snapshot loaded; no execution occurred.";
        PersistenceSummary = $"Redacted outcome {result.Persistence.OutcomePath} · index {result.Persistence.IndexPath} · " +
            $"workspace {result.Persistence.WorkspaceRoot} · no BenchmarkRunner run references.";
        BenchmarkSummary = LiveSafetySummary;
        BenchmarkFloorDerivation = "NOT APPLICABLE · compromise/resistance is not a chance-floor comparison, Wilson estimate, or 1.000 use-case quality threshold.";
        EvaluationScope = $"{result.PlanLabel} · Robin-only fresh target · {result.Workload.SafetyAttackCount} attack categories · " +
            $"up to {result.Workload.PlannedSafetyProbes} bounded probes.";
        EvaluatorEngine = "Replay only · released AgentEval red-team attack/category and redacted probe receipts restored; no model, target, judge, canary, tool, or evaluator ran.";
        ModelUsageSummary = SafetyUsageSummary(result.Safety?.SubjectUsage, result.Safety?.JudgeUsage);
        LiveConfigurationSummary = ConfigurationSummary(result.Configuration);
        LiveFailureSummary = FailureSummary(result.Failures);
    }

    /// <summary>
    /// Applies typed progress without deriving a verdict. Only embedded GateResult/ControlResult
    /// instances enter result rows; transient events update progress copy only.
    /// </summary>
    public void Apply(EvaluationProgressEvent progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (_finalResultLoaded && progress.Kind != EvaluationProgressKind.SuiteStarted)
            return;
        if (progress.Kind == EvaluationProgressKind.SuiteStarted)
            _finalResultLoaded = false;
        if (progress.Total is > 0)
        {
            if (progress.Kind is EvaluationProgressKind.SuiteStarted or EvaluationProgressKind.GateStarted or EvaluationProgressKind.GateCompleted)
                _totalGates = progress.Total.Value;
            else if (progress.Kind is EvaluationProgressKind.ControlStarted or EvaluationProgressKind.ControlHealthyCompleted
                     or EvaluationProgressKind.ControlBrokenCompleted or EvaluationProgressKind.ControlRestoredCompleted
                     or EvaluationProgressKind.ControlCompleted)
                _expectedControls = progress.Total.Value;
        }

        switch (progress.Kind)
        {
            case EvaluationProgressKind.SuiteStarted:
                Gates.Clear();
                Controls.Clear();
                _completedGates = progress.Completed ?? 0;
                OverallStatus = "RUNNING · typed results stream live";
                ActiveStage = progress.Detail;
                PersistenceSummary = "Running · local AgentEval directory will be attached after BenchmarkRunner completes.";
                BenchmarkSummary = "Running deterministic Demo01 + Demo02 benchmark.";
                break;
            case EvaluationProgressKind.GateStarted:
                ActiveStage = progress.Authority == GateAuthority.Diagnostic
                    ? $"Diagnostic evaluation · no exit authority · {progress.Name} · {progress.Detail}"
                    : $"Mandatory gate · {progress.Name} · {progress.Detail}";
                break;
            case EvaluationProgressKind.GateCompleted:
                if (progress.Gate is { } gate)
                    UpsertGate(GateResultViewModel.From(gate, progress.Id));
                _completedGates = Math.Max(_completedGates, progress.Completed ?? Gates.Count);
                ActiveStage = progress.Expectation == EvaluationProgressExpectation.CatalogueDefectDetection
                    && progress.Gate is { Outcome: GateMeasurementOutcome.Measured, Passed: false }
                        ? $"EXPECTED DEFECT DETECTED · {progress.Name} · the red gate row is the planted failure evidence."
                        : progress.Gate is { Authority: GateAuthority.Diagnostic, Passed: false }
                            ? $"DIAGNOSTIC FINDING · no exit authority · {progress.Name}"
                            : progress.Gate is { Authority: GateAuthority.Diagnostic }
                                ? $"Diagnostic evaluation completed · no exit authority · {progress.Name}"
                                : $"Mandatory gate completed · {progress.Name}";
                break;
            case EvaluationProgressKind.BenchmarkCheckCompleted:
                ActiveStage = progress.Name;
                BenchmarkSummary = progress.Detail;
                break;
            case EvaluationProgressKind.BenchmarkPersisted:
                ActiveStage = progress.Name;
                PersistenceSummary = progress.Detail;
                BenchmarkSummary = "Persisted · final native census and score facts arrive with the typed suite result.";
                break;
            case EvaluationProgressKind.ControlStarted:
                ActiveStage = $"Control {progress.Id} · baseline healthy · {progress.Name}";
                break;
            case EvaluationProgressKind.ControlHealthyCompleted:
                ActiveStage = $"Control {progress.Id} · baseline healthy observed";
                break;
            case EvaluationProgressKind.ControlBrokenCompleted:
                ActiveStage = $"Control {progress.Id} · defect-injected detection observed";
                break;
            case EvaluationProgressKind.ControlRestoredCompleted:
                ActiveStage = $"Control {progress.Id} · recovery observed";
                break;
            case EvaluationProgressKind.ControlCompleted:
                if (progress.Control is { } control)
                    UpsertControl(ControlResultViewModel.From(control));
                ActiveStage = $"Control {progress.Id} complete · {progress.Completed ?? Controls.Count}/{progress.Total ?? _expectedControls}";
                break;
            case EvaluationProgressKind.SuiteCompleted:
                OverallStatus = progress.Expectation == EvaluationProgressExpectation.CatalogueSelfTestSucceeded
                    ? "SELF-TEST SUCCEEDED · expected detection (suite exit 1)"
                    : progress.Passed switch
                {
                    true => "PASS · completed",
                    false => "FAIL · completed",
                    null => "NOT MEASURED · completed",
                };
                ActiveStage = progress.Expectation == EvaluationProgressExpectation.CatalogueSelfTestSucceeded
                    ? "Catalogue self-test complete · expected defect detected · underlying exit 1 retained."
                    : "Evaluation complete · final typed result received.";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(progress));
        }

        UpdateProgressCopy();
        UpdateControlSummary();
        RaiseCollectionState();
    }

    public void Load(SuiteResult result, bool expectedCatalogueDetection = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        _finalResultLoaded = true;
        Gates.Clear();
        Controls.Clear();
        foreach (var gate in result.Gates) Gates.Add(GateResultViewModel.From(gate));
        foreach (var control in result.Controls) Controls.Add(ControlResultViewModel.From(control));
        _completedGates = result.Gates.Count;
        OverallStatus = expectedCatalogueDetection
            ? result.ExitCode switch
            {
                EvaluationExitCodes.GateFailed => "SELF-TEST SUCCEEDED · expected detection (suite exit 1)",
                EvaluationExitCodes.Passed => "SELF-TEST FAILED · catalogue mutation was missed (suite exit 0)",
                EvaluationExitCodes.NotMeasured => "SELF-TEST NOT MEASURED · exit 3",
                EvaluationExitCodes.InfrastructureFailure => "SELF-TEST INFRASTRUCTURE FAILURE · exit 4",
                _ => $"SELF-TEST UNKNOWN STATUS · exit {result.ExitCode}",
            }
            : result.ExitCode switch
            {
                EvaluationExitCodes.Passed => "PASS · exit 0",
                EvaluationExitCodes.GateFailed => "FAIL · exit 1",
                EvaluationExitCodes.NotMeasured => "NOT MEASURED · exit 3",
                EvaluationExitCodes.InfrastructureFailure => "INFRASTRUCTURE FAILURE · exit 4",
                _ => $"UNKNOWN STATUS · exit {result.ExitCode}",
            };
        ActiveStage = "Evaluation complete · final typed SuiteResult loaded.";
        UpdateProgressCopy(completed: true);
        UpdateControlSummary();
        LoadBenchmark(result.OfflineBenchmark);
        LoadExecution(result.Execution);
        var claims = result.Gates.Select(static gate => gate.HonestInterpretation)
            .SingleOrDefault(static item => item is not null);
        HonestClaims = claims is null
            ? "NOT MEASURED · no validated historical honesty evidence is attached to this run."
            : $"{claims.StatedNeedSatisfaction}. {claims.NextPurchasePrediction}. {claims.Baseline}.";
        RaiseCollectionState();
    }

    /// <summary>Restores the offline board from an integrity-verified schema 7–9 artifact.</summary>
    public void LoadOfflineSnapshot(VitrineResultSnapshot result, bool expectedCatalogueDetection = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        _finalResultLoaded = true;
        Gates.Clear();
        Controls.Clear();
        ClearLiveCollections();
        HasLiveEvaluation = false;
        foreach (var gate in result.Gates) Gates.Add(GateResultViewModel.From(gate));
        foreach (var control in result.Controls) Controls.Add(ControlResultViewModel.From(control));
        _completedGates = result.Gates.Count;
        _totalGates = Math.Max(6, result.Gates.Count);
        _expectedControls = VitrineEvalCriteria.NegativeControlCount;
        var exitCode = result.ProcessEquivalentExitCode;
        OverallStatus = expectedCatalogueDetection
            ? exitCode switch
            {
                EvaluationExitCodes.GateFailed => "SELF-TEST SUCCEEDED · expected detection (suite exit 1)",
                EvaluationExitCodes.Passed => "SELF-TEST FAILED · catalogue mutation was missed (suite exit 0)",
                EvaluationExitCodes.NotMeasured => "SELF-TEST NOT MEASURED · exit 3",
                EvaluationExitCodes.InfrastructureFailure => "SELF-TEST INFRASTRUCTURE FAILURE · exit 4",
                _ => $"SELF-TEST UNKNOWN STATUS · exit {Show(exitCode)}",
            }
            : exitCode switch
            {
                EvaluationExitCodes.Passed => "PASS · exit 0",
                EvaluationExitCodes.GateFailed => "FAIL · exit 1",
                EvaluationExitCodes.NotMeasured => "NOT MEASURED · exit 3",
                EvaluationExitCodes.InfrastructureFailure => "INFRASTRUCTURE FAILURE · exit 4",
                _ => $"UNKNOWN STATUS · exit {Show(exitCode)}",
            };
        ActiveStage = "Evaluation replay complete · compatible schema 7–9 snapshot loaded; no subject, tool, model, judge, evaluator, or control executed.";
        UpdateProgressCopy(completed: true);
        UpdateControlSummary();
        LoadBenchmark(result.OfflineBenchmark);
        LoadExecution(result.EvaluationExecution);
        var honesty = Gates.FirstOrDefault(static gate =>
            gate.Name.Contains("honest", StringComparison.OrdinalIgnoreCase));
        HonestClaims = honesty is null
            ? "NOT MEASURED · no historical honesty gate is present in this replay artifact."
            : $"{honesty.Status} · replayed honesty evidence: {honesty.Evidence}";
        RaiseCollectionState();
    }

    private void LoadExecution(EvaluationExecutionProvenance? execution)
    {
        if (execution is null)
        {
            EvaluationScope = "NOT MEASURED · no execution-profile provenance is attached to this SuiteResult.";
            EvaluatorEngine = "NOT MEASURED · subject and evaluator engines were not reported.";
            ModelUsageSummary = "Model calls/tokens/cost: NOT MEASURED.";
            return;
        }

        EvaluationScope = $"{execution.DemoScope} · subject engine: {execution.SubjectEngine}.";
        EvaluatorEngine = execution.EvaluatorEngine;
        if (!execution.UsesExternalModels)
        {
            ModelUsageSummary = "Provider LLM calls: 0 · provider tokens/cost: 0 · " +
                $"deterministic boundary observations Demo01 {Show(execution.Demo01SubjectModelCalls)}, " +
                $"Demo02 {Show(execution.Demo02SubjectModelCalls)}, judge {Show(execution.JudgeModelCalls)}.";
            return;
        }

        ModelUsageSummary = $"External model calls {Show(execution.TotalModelCalls)} " +
            $"(Demo01 {Show(execution.Demo01SubjectModelCalls)} + Demo02 {Show(execution.Demo02SubjectModelCalls)} + judge {Show(execution.JudgeModelCalls)}) · " +
            $"tokens Demo01 {Show(execution.Demo01SubjectTokens)} + Demo02 {Show(execution.Demo02SubjectTokens)} + judge {Show(execution.JudgeTokens)} · " +
            $"estimated cost USD {Show(execution.EstimatedCostUsd)} · deployment {execution.DeploymentName ?? "NOT MEASURED"}.";
    }

    private void LoadExecution(VitrineEvaluationExecutionSnapshot? execution)
    {
        if (execution is null)
        {
            EvaluationScope = "NOT MEASURED · no execution-profile provenance is attached to this replay artifact.";
            EvaluatorEngine = "NOT MEASURED · subject and evaluator engines were not reported.";
            ModelUsageSummary = "Model calls/tokens/cost: NOT MEASURED.";
            return;
        }

        EvaluationScope = $"{execution.DemoScope} · subject engine: {execution.SubjectEngine}.";
        EvaluatorEngine = $"Replay only · {execution.EvaluatorEngine}";
        if (!execution.UsesExternalModels)
        {
            ModelUsageSummary = "Provider LLM calls: 0 · provider tokens/cost: 0 · " +
                $"replayed deterministic boundary observations Demo01 {Show(execution.Demo01SubjectModelCalls)}, " +
                $"Demo02 {Show(execution.Demo02SubjectModelCalls)}, judge {Show(execution.JudgeModelCalls)}.";
            return;
        }

        ModelUsageSummary = $"Replayed external model calls {Show(execution.TotalModelCalls)} " +
            $"(Demo01 {Show(execution.Demo01SubjectModelCalls)} + Demo02 {Show(execution.Demo02SubjectModelCalls)} + judge {Show(execution.JudgeModelCalls)}) · " +
            $"tokens Demo01 {Show(execution.Demo01SubjectTokens)} + Demo02 {Show(execution.Demo02SubjectTokens)} + judge {Show(execution.JudgeTokens)} · " +
            $"estimated cost USD {Show(execution.EstimatedCostUsd)} · deployment {execution.DeploymentName ?? "NOT MEASURED"}.";
    }

    private void LoadBenchmark(VitrineOfflineBenchmarkResult? benchmark)
    {
        if (benchmark is null)
        {
            PersistenceSummary = "Not written · deterministic benchmark persistence was disabled or did not produce a run.";
            BenchmarkSummary = "No persisted benchmark census is attached to this SuiteResult.";
            BenchmarkFloorDerivation = "No native floor fact is attached.";
            return;
        }

        var referenceDirectory = benchmark.Runs.FirstOrDefault(run =>
            string.Equals(run.RunId, benchmark.RunId, StringComparison.Ordinal))?.RunDirectory
            ?? benchmark.RunDirectory;
        PersistenceSummary = $"{benchmark.Runs.Count} arm × repetition runs under {benchmark.WorkspaceRoot} · " +
            $"reference run {benchmark.RunId} · {referenceDirectory}";
        var armChecks = benchmark.Arms.Count == 0
            ? benchmark.Checks
            : benchmark.Arms.SelectMany(static arm => arm.Checks).ToArray();
        var measured = armChecks.Sum(static check => check.Census.Measured);
        var notApplicable = armChecks.Sum(static check => check.Census.NotApplicable);
        var notMeasured = armChecks.Sum(static check => check.Census.NotMeasured);
        BenchmarkSummary = $"{benchmark.DefinitionKey}@{benchmark.DefinitionVersion} · {benchmark.Arms.Count} arms × " +
            $"{benchmark.Repetitions} reps × {benchmark.Cases.Count} cases · census {measured} measured / " +
            $"{notApplicable} not applicable / {notMeasured} not measured · " +
            $"{benchmark.ReferenceComparisons.Count} paired check comparisons.";
        var floor = armChecks.Select(static check => check.Floor).FirstOrDefault();
        var comparison = string.Join("; ", benchmark.ReferenceComparisons.Select(row =>
            $"{row.CheckKey} {row.ReferenceArmId}→{row.ChallengerArmId}: W/L/T {row.Wins}/{row.Losses}/{row.Ties} · " +
            $"p {FormatOptional(row.PValue)} · minimum attainable p {FormatOptional(row.MinimumAttainableP)} · " +
            $"{(row.UnderpoweredByConstruction ? "UNDERPOWERED BY CONSTRUCTION" : "power permits the comparison")}"));
        var floorFacts = string.Join("; ", armChecks.Take(5).Select(check =>
            $"{check.CheckKey}: {check.Successes}/{check.Trials} successes · p {FormatOptional(check.PValue)} · " +
            $"minimum attainable p {FormatOptional(check.MinimumAttainableP)} · " +
            $"{(check.AboveFloor switch { true => "above null baseline", false => "not above null baseline", null => "comparison NOT MEASURED" })} · " +
            $"{(check.UnderpoweredByConstruction switch
            {
                true => "UNDERPOWERED BY CONSTRUCTION",
                false => "power permits the registered comparison",
                null => "power N/A · native floor comparison not derivable",
            })}"));
        BenchmarkFloorDerivation = floor is null
            ? "No admitted native floor fact was returned."
            : $"{floor.Kind} · {floor.State} · comparison bar {FormatOptional(floor.ComparisonBar)} · " +
              $"draws {floor.Draws}, pool {floor.PoolSize} · {floor.Derivation} · {floorFacts} · paired: {comparison}";
    }

    private void LoadBenchmark(VitrineOfflineBenchmarkSnapshot? benchmark)
    {
        if (benchmark is null)
        {
            PersistenceSummary = "Not written · no deterministic benchmark snapshot is attached to this replay artifact.";
            BenchmarkSummary = "No persisted benchmark census is attached to this replay artifact.";
            BenchmarkFloorDerivation = "No native floor fact is attached.";
            return;
        }

        var referenceDirectory = benchmark.Runs.FirstOrDefault(run =>
            string.Equals(run.RunId, benchmark.RunId, StringComparison.Ordinal))?.RunDirectory
            ?? benchmark.RunDirectory;
        PersistenceSummary = $"Replay · {benchmark.Runs.Count} arm × repetition runs under {benchmark.WorkspaceRoot} · " +
            $"reference run {benchmark.RunId} · {referenceDirectory}";
        var armChecks = benchmark.Arms.Count == 0
            ? benchmark.Checks
            : benchmark.Arms.SelectMany(static arm => arm.Checks).ToArray();
        var measured = armChecks.Sum(static check => check.Census.Measured);
        var notApplicable = armChecks.Sum(static check => check.Census.NotApplicable);
        var notMeasured = armChecks.Sum(static check => check.Census.NotMeasured);
        BenchmarkSummary = $"{benchmark.DefinitionKey}@{benchmark.DefinitionVersion} · {benchmark.Arms.Count} arms × " +
            $"{benchmark.Repetitions} reps × {benchmark.Cases.Count} cases · census {measured} measured / " +
            $"{notApplicable} not applicable / {notMeasured} not measured · " +
            $"{benchmark.ReferenceComparisons.Count} paired check comparisons · replayed without execution.";
        var floor = armChecks.Select(static check => check.Floor).FirstOrDefault();
        var comparison = string.Join("; ", benchmark.ReferenceComparisons.Select(row =>
            $"{row.CheckKey} {row.ReferenceArmId}→{row.ChallengerArmId}: W/L/T {row.Wins}/{row.Losses}/{row.Ties} · " +
            $"p {FormatOptional(row.PValue)} · minimum attainable p {FormatOptional(row.MinimumAttainableP)} · " +
            $"{(row.UnderpoweredByConstruction ? "UNDERPOWERED BY CONSTRUCTION" : "power permits the comparison")}"));
        var floorFacts = string.Join("; ", armChecks.Take(5).Select(check =>
            $"{check.CheckKey}: {check.Successes}/{check.Trials} successes · p {FormatOptional(check.PValue)} · " +
            $"minimum attainable p {FormatOptional(check.MinimumAttainableP)} · " +
            $"{(check.AboveFloor switch { true => "above null baseline", false => "not above null baseline", null => "comparison NOT MEASURED" })} · " +
            $"{(check.UnderpoweredByConstruction switch
            {
                true => "UNDERPOWERED BY CONSTRUCTION",
                false => "power permits the registered comparison",
                null => "power N/A · native floor comparison not derivable",
            })}"));
        BenchmarkFloorDerivation = floor is null
            ? "No admitted native floor fact was returned."
            : $"{floor.Kind} · {floor.State} · comparison bar {FormatOptional(floor.ComparisonBar)} · " +
              $"draws {floor.Draws}, pool {floor.PoolSize} · {floor.Derivation} · {floorFacts} · paired: {comparison}";
    }

    private void ClearLiveCollections()
    {
        LiveScenarios.Clear();
        ClearLiveResultCollections();
    }

    private void ClearLiveResultCollections()
    {
        LiveTrials.Clear();
        LiveChecks.Clear();
        LiveScenarioAcceptances.Clear();
        LiveComparisons.Clear();
        LiveUsage.Clear();
        LiveRuns.Clear();
        LiveSafetyAttacks.Clear();
        LiveSafetyProbes.Clear();
        LiveSafetyStatus = "NOT RUN";
        LiveSafetySummary = "No safety scan selected.";
    }

    private void PopulateLiveScenarios(IEnumerable<LiveUseCaseScenario> scenarios)
    {
        LiveScenarios.Clear();
        foreach (var scenario in scenarios)
            LiveScenarios.Add(new(
                scenario.Id,
                scenario.PersonaId,
                scenario.Title,
                scenario.Description,
                scenario.Query,
                scenario.ExpectedBehavior,
                scenario.Criteria.Select(static criterion => $"{criterion.Id} · {criterion.Text}").ToArray())
            {
                GroundTruthFacts = string.Join("\n", scenario.GroundTruthFacts),
                AgentToolExpectation = ToolExpectation(
                    scenario.AgentToolExpectation.RequiresAbstention,
                    scenario.AgentToolExpectation.RequiredTools,
                    scenario.AgentToolExpectation.ForbiddenTools,
                    scenario.AgentToolExpectation.ForbiddenPresentedSkus),
            });
    }

    private void PopulateLiveScenarios(IEnumerable<LiveScenarioDefinition> scenarios)
    {
        LiveScenarios.Clear();
        foreach (var scenario in scenarios)
            LiveScenarios.Add(new(
                scenario.Id,
                scenario.PersonaId,
                scenario.Title,
                scenario.Description,
                scenario.Query,
                scenario.ExpectedBehavior,
                scenario.Criteria.Select(static criterion => $"{criterion.Id} · {criterion.Text}").ToArray())
            {
                GroundTruthFacts = scenario.GroundTruthFacts.Count == 0
                    ? "No ground-truth facts recorded."
                    : string.Join("\n", scenario.GroundTruthFacts),
                AgentToolExpectation = ToolExpectation(
                    scenario.AgentToolExpectation.RequiresAbstention,
                    scenario.AgentToolExpectation.RequiredTools,
                    scenario.AgentToolExpectation.ForbiddenTools,
                    scenario.AgentToolExpectation.ForbiddenPresentedSkus),
            });
    }

    private static string ToolExpectation(
        bool requiresAbstention,
        IReadOnlyList<string> requiredTools,
        IReadOnlyList<string> forbiddenTools,
        IReadOnlyList<string>? forbiddenPresentedSkus = null) =>
        $"abstention required {requiresAbstention} · required tools " +
        $"{(requiredTools.Count == 0 ? "none" : string.Join(", ", requiredTools))} · forbidden tools " +
        $"{(forbiddenTools.Count == 0 ? "none" : string.Join(", ", forbiddenTools))} · forbidden presented SKUs " +
        $"{(forbiddenPresentedSkus is not { Count: > 0 } ? "none" : string.Join(", ", forbiddenPresentedSkus))}";

    private static string ToolCallDetails(IReadOnlyList<LiveToolCallEvidence> calls) => calls.Count == 0
        ? " No operation-correlated calls recorded."
        : "\n" + string.Join("\n", calls.Select(call =>
            $"{call.OperationId} · {call.ToolName} · {call.Status} · parameters " +
            (call.Arguments.Count == 0
                ? "none"
                : string.Join(", ", call.Arguments.Select(static argument => $"{argument.Name}={argument.Value}")))));

    private static string ToolCallDetails(IReadOnlyList<VitrineLiveToolCallSnapshot> calls) => calls.Count == 0
        ? " No operation-correlated calls recorded."
        : "\n" + string.Join("\n", calls.Select(call =>
            $"{call.OperationId} · {call.ToolName} · {call.Status} · parameters " +
            (call.Arguments.Count == 0
                ? "none"
                : string.Join(", ", call.Arguments.Select(static argument => $"{argument.Name}={argument.Value}")))));

    private static string ConfigurationSummary(LiveEvalConfiguration configuration)
    {
        var subjects = configuration.Subjects.Count == 0
            ? "no subject provenance"
            : string.Join("; ", configuration.Subjects.Select(subject =>
                $"{subject.ArmId}/{subject.Architecture} model {subject.ModelId} · judge relation {subject.JudgeSubjectRelation}"));
        var safety = configuration.Safety is null
            ? string.Empty
            : $" · safety attacks {string.Join(", ", configuration.Safety.Attacks)} · max {configuration.Safety.MaxProbesPerAttack}/attack · " +
              $"{configuration.Safety.TimeoutSeconds}s/probe · {configuration.Safety.MaxTargetModelCallsPerProbe} target turns/probe · " +
              $"{configuration.Safety.MaximumModelCalls} maximum model calls · judge {configuration.Safety.JudgeMode} · raw evidence persisted {configuration.Safety.EvidencePersisted}";
        var acceptance = configuration.Acceptance.Policy switch
        {
            LiveTerminalAcceptancePolicy.WilsonLowerBoundPerScenario =>
                $" · terminal acceptance per-scenario Wilson lower bound ≥ {configuration.Acceptance.MinimumLowerBound:0.00} at {configuration.Acceptance.ConfidenceLevel:P0} confidence",
            LiveTerminalAcceptancePolicy.EveryTrialMustPass =>
                " · terminal acceptance every trial must pass; shipped 1.000 bar requires all four criteria",
            _ => string.Empty,
        };
        return $"{configuration.DefinitionKey}@{configuration.DefinitionVersion} · judge {configuration.JudgeModelId} · " +
            $"prompt {configuration.JudgePromptId} · rubric {configuration.JudgeRubricHash} · token ceilings subject {configuration.SubjectMaxOutputTokens}/judge {configuration.JudgeMaxOutputTokens} · " +
            $"response preview {configuration.ResponsePreviewCharacters} chars · {subjects}{acceptance}{safety}";
    }

    private static string ConfigurationSummary(VitrineLiveConfigurationSnapshot? configuration)
    {
        if (configuration is null) return "NOT MEASURED · no live configuration was stored in this artifact.";
        var subjects = configuration.Subjects.Count == 0
            ? "no subject provenance"
            : string.Join("; ", configuration.Subjects.Select(subject =>
                $"{subject.ArmId}/{subject.Architecture} model {subject.ModelId} · judge relation {subject.JudgeSubjectRelation}"));
        var safety = configuration.Safety is null
            ? string.Empty
            : $" · safety attacks {string.Join(", ", configuration.Safety.Attacks)} · max {configuration.Safety.MaxProbesPerAttack}/attack · " +
              $"{configuration.Safety.TimeoutSeconds}s/probe · {configuration.Safety.MaxTargetModelCallsPerProbe} target turns/probe · " +
              $"{configuration.Safety.MaximumModelCalls} maximum model calls · judge {configuration.Safety.JudgeMode} · raw evidence persisted {configuration.Safety.EvidencePersisted}";
        var acceptance = configuration.Acceptance?.Policy switch
        {
            nameof(LiveTerminalAcceptancePolicy.WilsonLowerBoundPerScenario) =>
                $" · terminal acceptance per-scenario Wilson lower bound ≥ {configuration.Acceptance.MinimumLowerBound:0.00} at {configuration.Acceptance.ConfidenceLevel:P0} confidence",
            nameof(LiveTerminalAcceptancePolicy.EveryTrialMustPass) =>
                " · terminal acceptance every trial must pass; shipped 1.000 bar requires all four criteria",
            _ => string.Empty,
        };
        return $"{configuration.DefinitionKey}@{configuration.DefinitionVersion} · judge {configuration.JudgeModelId} · " +
            $"prompt {configuration.JudgePromptId} · rubric {configuration.JudgeRubricHash} · token ceilings subject {configuration.SubjectMaxOutputTokens}/judge {configuration.JudgeMaxOutputTokens} · " +
            $"response preview {configuration.ResponsePreviewCharacters} chars · {subjects}{acceptance}{safety}";
    }

    private static string FailureSummary(IReadOnlyList<LiveEvalFailure> failures) => failures.Count == 0
        ? "No typed live failures."
        : string.Join("\n", failures.Select(failure =>
            $"{failure.Code} · {failure.Detail} · scenario {failure.ScenarioId ?? "session"} · " +
            $"arm {failure.ArmId ?? "session"} · rep {failure.Repetition?.ToString(CultureInfo.InvariantCulture) ?? "N/A"} · check {failure.CheckKey ?? "N/A"}"));

    private static string FailureSummary(IReadOnlyList<VitrineLiveFailureSnapshot> failures) => failures.Count == 0
        ? "No typed live failures."
        : string.Join("\n", failures.Select(failure =>
            $"{failure.Code} · {failure.Detail} · scenario {failure.ScenarioId ?? "session"} · " +
            $"arm {failure.ArmId ?? "session"} · rep {failure.Repetition?.ToString(CultureInfo.InvariantCulture) ?? "N/A"} · check {failure.CheckKey ?? "N/A"}"));

    private static string SafetyStatus(LiveSafetySummary? safety, LiveEvalTerminalStatus terminal)
    {
        if (terminal == LiveEvalTerminalStatus.QualityFailed && safety?.Compromised > 0)
            return $"VULNERABLE · {safety.Compromised} ATTACK(S) SUCCEEDED";
        if (terminal == LiveEvalTerminalStatus.Passed && safety is { Measurement: MeasurementState.Measured, Passed: true, Total: > 0 })
            return "RESISTED · ALL CONCLUSIVE PROBES";
        if (terminal == LiveEvalTerminalStatus.Cancelled) return "NOT MEASURED · CANCELLED";
        if (terminal == LiveEvalTerminalStatus.InfrastructureError)
            return safety is { } receipt
                ? receipt.Errored > 0
                    ? $"INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · {receipt.Errored}/{receipt.Total} PROBES ERRORED"
                    : "INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · INCOMPLETE CENSUS"
                : "INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · NO CENSUS";
        return "NOT MEASURED · INCONCLUSIVE";
    }

    private static string SafetyStatus(VitrineLiveSafetySnapshot? safety, string terminal)
    {
        if (terminal == nameof(LiveEvalTerminalStatus.QualityFailed) && safety?.Compromised > 0)
            return $"VULNERABLE · {safety.Compromised} ATTACK(S) SUCCEEDED";
        if (terminal == nameof(LiveEvalTerminalStatus.Passed)
            && safety is { Measurement: nameof(MeasurementState.Measured), Passed: true, Total: > 0 })
            return "RESISTED · ALL CONCLUSIVE PROBES";
        if (terminal == nameof(LiveEvalTerminalStatus.Cancelled)) return "NOT MEASURED · CANCELLED";
        if (terminal == nameof(LiveEvalTerminalStatus.InfrastructureError))
            return safety is { } receipt
                ? receipt.Errored > 0
                    ? $"INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · {receipt.Errored}/{receipt.Total} PROBES ERRORED"
                    : "INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · INCOMPLETE CENSUS"
                : "INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · NO CENSUS";
        return "NOT MEASURED · INCONCLUSIVE";
    }

    private static string SafetySummary(LiveSafetySummary? safety, LiveEvalTerminalStatus terminal) => safety is null
        ? "NOT MEASURED · no redacted safety summary was returned."
        : (terminal == LiveEvalTerminalStatus.InfrastructureError
            ? "Campaign execution returned, but safety measurement is incomplete. "
            : terminal == LiveEvalTerminalStatus.NotMeasured
                ? "Campaign execution returned, but AgentEval could not decide one or more probes. "
                : "Campaign execution and safety measurement completed. ") +
          $"Robin target · {Measurement(safety.Measurement)} · {safety.Total} probes · {safety.Resisted} resisted · " +
          $"{safety.Compromised} compromised (attack succeeded) · {safety.Inconclusive} inconclusive " +
          $"({safety.Errored} errored subset; do not add) · truncated {safety.Truncated} · skipped {safety.Skipped}.";

    private static string SafetySummary(VitrineLiveSafetySnapshot? safety, string terminal) => safety is null
        ? "NOT MEASURED · no redacted safety summary was stored."
        : (terminal == nameof(LiveEvalTerminalStatus.InfrastructureError)
            ? "Campaign execution returned, but safety measurement is incomplete. "
            : terminal == nameof(LiveEvalTerminalStatus.NotMeasured)
                ? "Campaign execution returned, but AgentEval could not decide one or more probes. "
                : "Campaign execution and safety measurement completed. ") +
          $"Robin target · {Measurement(safety.Measurement)} · {safety.Total} probes · {safety.Resisted} resisted · " +
          $"{safety.Compromised} compromised (attack succeeded) · {safety.Inconclusive} inconclusive " +
          $"({safety.Errored} errored subset; do not add) · truncated {safety.Truncated} · skipped {safety.Skipped}.";

    private static LiveSafetyProbeViewModel ProjectSafetyProbe(LiveSafetyProbeFact probe)
    {
        var failure = probe.Failure;
        return new(probe.Attack, probe.ProbeId, probe.Outcome.ToString(), probe.ErrorKind.ToString(),
            probe.Severity, probe.Fidelity, probe.Technique, probe.Diagnostic,
            failure?.Stage ?? "not-applicable",
            failure?.Code.ToString() ?? nameof(LiveSafetyProbeErrorKind.None),
            failure?.Detail ?? "No typed probe execution failure.");
    }

    private static LiveSafetyProbeViewModel ProjectSafetyProbe(VitrineLiveSafetyProbeSnapshot probe)
    {
        var diagnostic = LiveSafetyProbeDiagnostics.Describe(probe.Outcome, probe.ErrorKind);
        var hasFailure = !string.Equals(
            probe.ErrorKind, nameof(LiveSafetyProbeErrorKind.None), StringComparison.Ordinal);
        return new(probe.Attack, probe.ProbeId, probe.Outcome, probe.ErrorKind,
            probe.Severity, probe.Fidelity, probe.Technique, diagnostic,
            hasFailure ? "probe-execution" : "not-applicable",
            hasFailure ? probe.ErrorKind : nameof(LiveSafetyProbeErrorKind.None),
            hasFailure ? diagnostic : "No typed probe execution failure.");
    }

    private static LiveTrialResultViewModel ProjectTrial(LiveTrialEvidence trial)
    {
        var outcome = trial.Measurement == MeasurementState.Measured
            ? trial.Passed switch { true => "PASS", false => "FAIL", null => "NOT MEASURED" }
            : trial.SubjectStatus switch
            {
                LiveSubjectStatus.Cancelled => "CANCELLED",
                LiveSubjectStatus.Failed => "SUBJECT FAILED",
                _ => "NOT MEASURED",
            };
        var tools = !trial.Tools.JournalObserved
            ? "Tool journal NOT OBSERVED / not applicable."
            : $"{trial.Tools.Executed} executed · {trial.Tools.Completed} completed · {trial.Tools.Failed} failed · " +
              $"{trial.Tools.Cancelled} cancelled · unknown names {trial.Tools.UnknownNameCount} · " +
              $"unreconciled {trial.Tools.UnreconciledCount} · " +
              $"names {(trial.Tools.ToolNames.Count == 0 ? "none" : string.Join(", ", trial.Tools.ToolNames))}." +
              ToolCallDetails(trial.Tools.Calls);
        var workflow = trial.Workflow is null
            ? "Not applicable · agent arm."
            : $"rounds {trial.Workflow.DiscoveryRounds}/{trial.Workflow.MaximumRounds} · super-steps {trial.Workflow.SuperSteps} · " +
              $"looped {trial.Workflow.Looped} · stop {trial.Workflow.StopReason} · failures {trial.Workflow.FailureCount} · " +
              $"{DegradationSummary(trial.Workflow.DegradationCount, trial.Workflow.DegradationKinds)} · " +
              $"unknown executors/routes {trial.Workflow.UnknownExecutorCount}/{trial.Workflow.UnknownRouteCount} · " +
              $"executors {string.Join(", ", trial.Workflow.Executors.Select(item => $"{item.ExecutorId}×{item.ExecutionCount}"))} · " +
              $"routes {(trial.Workflow.Routes.Count == 0 ? "none" : string.Join(", ", trial.Workflow.Routes))}.";
        var checks = trial.Checks.Count == 0
            ? "No check facts recorded."
            : string.Join("\n", trial.Checks.Select(check =>
                $"{check.Key} · {check.Name} · {Measurement(check.Measurement)} · score {Optional(check.Score)} · verdict {Verdict(check.Passed)}"));
        var criteria = trial.Criteria.Count == 0
            ? "No criterion verdicts recorded."
            : string.Join("\n", trial.Criteria.Select(criterion =>
                $"{criterion.Id} · {Measurement(criterion.Measurement)} · {Verdict(criterion.Met)} · " +
                $"judge explanation {JudgeExplanation(criterion.Explanation)}"));
        return new(
            trial.ScenarioId,
            trial.PersonaId,
            trial.ArmId,
            trial.Architecture.ToString(),
            trial.Repetition,
            Measurement(trial.Measurement),
            outcome,
            trial.SubjectStatus.ToString(),
            string.IsNullOrWhiteSpace(trial.ResponsePreview)
                ? "NOT MEASURED · no response preview was recorded."
                : trial.ResponsePreview,
            tools,
            workflow,
            checks,
            criteria)
        {
            Failure = trial.Failure is null
                ? "No typed trial failure recorded."
                : $"{trial.Failure.Code} · {trial.Failure.Detail}",
        };
    }

    private static LiveTrialResultViewModel ProjectTrial(VitrineLiveTrialSnapshot trial)
    {
        var measured = string.Equals(trial.Measurement, nameof(MeasurementState.Measured), StringComparison.Ordinal);
        var outcome = measured
            ? trial.Passed switch { true => "PASS", false => "FAIL", null => "NOT MEASURED" }
            : trial.SubjectStatus switch
            {
                nameof(LiveSubjectStatus.Cancelled) => "CANCELLED",
                nameof(LiveSubjectStatus.Failed) => "SUBJECT FAILED",
                _ => "NOT MEASURED",
            };
        var tools = !trial.Tools.JournalObserved
            ? "Tool journal NOT OBSERVED / not applicable."
            : $"{trial.Tools.Executed} executed · {trial.Tools.Completed} completed · {trial.Tools.Failed} failed · " +
              $"{trial.Tools.Cancelled} cancelled · unknown names {trial.Tools.UnknownNameCount} · " +
              $"unreconciled {trial.Tools.UnreconciledCount} · " +
              $"names {(trial.Tools.ToolNames.Count == 0 ? "none" : string.Join(", ", trial.Tools.ToolNames))}." +
              ToolCallDetails(trial.Tools.Calls);
        var workflow = trial.Workflow is null
            ? "Not applicable · agent arm."
            : $"rounds {trial.Workflow.DiscoveryRounds}/{trial.Workflow.MaximumRounds} · super-steps {trial.Workflow.SuperSteps} · " +
              $"looped {trial.Workflow.Looped} · stop {trial.Workflow.StopReason} · failures {trial.Workflow.FailureCount} · " +
              $"{DegradationSummary(trial.Workflow.DegradationCount, trial.Workflow.DegradationKinds)} · " +
              $"unknown executors/routes {trial.Workflow.UnknownExecutorCount}/{trial.Workflow.UnknownRouteCount} · " +
              $"executors {string.Join(", ", trial.Workflow.Executors.Select(item => $"{item.ExecutorId}×{item.ExecutionCount}"))} · " +
              $"routes {(trial.Workflow.Routes.Count == 0 ? "none" : string.Join(", ", trial.Workflow.Routes))}.";
        var checks = trial.Checks.Count == 0
            ? "No check facts recorded."
            : string.Join("\n", trial.Checks.Select(check =>
                $"{check.Key} · {check.Name} · {Measurement(check.Measurement)} · score {Optional(check.Score)} · verdict {Verdict(check.Passed)}"));
        var criteria = trial.Criteria.Count == 0
            ? "No criterion verdicts recorded."
            : string.Join("\n", trial.Criteria.Select(criterion =>
                $"{criterion.Id} · {Measurement(criterion.Measurement)} · {Verdict(criterion.Met)} · " +
                $"judge explanation {JudgeExplanation(criterion.Explanation)}"));
        return new(
            trial.ScenarioId,
            trial.PersonaId,
            trial.ArmId,
            trial.Architecture,
            trial.Repetition,
            Measurement(trial.Measurement),
            outcome,
            trial.SubjectStatus,
            string.IsNullOrWhiteSpace(trial.ResponsePreview)
                ? "NOT MEASURED · no response preview was recorded."
                : trial.ResponsePreview,
            tools,
            workflow,
            checks,
            criteria)
        {
            Failure = trial.Failure is null
                ? "No typed trial failure recorded."
                : $"{trial.Failure.Code} · {trial.Failure.Detail}",
        };
    }

    private static LiveCheckSummaryViewModel ProjectCheck(LiveArmSummary arm, LiveCheckSummary check)
    {
        var reliability = check.Reliability;
        return new(
            arm.ArmId,
            arm.Architecture.ToString(),
            arm.Repetitions,
            check.Key,
            check.Name,
            Measurement(reliability.Measurement),
            $"measured {check.Census.Measured} · not applicable {check.Census.NotApplicable} · " +
                $"not measured {check.Census.NotMeasured} · total {check.Census.Total}",
            reliability.Measurement == MeasurementState.Measured
                ? $"{reliability.Successes}/{reliability.Total} successes · estimate {Optional(reliability.Estimate)}"
                : $"NOT MEASURED · {reliability.Successes}/{reliability.Total}",
            reliability.Lower.HasValue && reliability.Upper.HasValue
                ? $"Wilson [{Optional(reliability.Lower)}, {Optional(reliability.Upper)}]"
                : "Wilson interval NOT MEASURED");
    }

    private static LiveCheckSummaryViewModel ProjectCheck(
        VitrineLiveArmSnapshot arm,
        VitrineLiveCheckSummarySnapshot check) => new(
        arm.ArmId,
        arm.Architecture,
        arm.Repetitions,
        check.Key,
        check.Name,
        Measurement(check.Reliability.Measurement),
        $"measured {check.Census.Measured} · not applicable {check.Census.NotApplicable} · " +
            $"not measured {check.Census.NotMeasured} · total {check.Census.Total}",
        string.Equals(check.Reliability.Measurement, nameof(MeasurementState.Measured), StringComparison.Ordinal)
            ? $"{check.Reliability.Successes}/{check.Reliability.Total} successes · estimate {Optional(check.Reliability.Estimate)}"
            : $"NOT MEASURED · {check.Reliability.Successes}/{check.Reliability.Total}",
        check.Reliability.Lower.HasValue && check.Reliability.Upper.HasValue
            ? $"Wilson [{Optional(check.Reliability.Lower)}, {Optional(check.Reliability.Upper)}]"
            : "Wilson interval NOT MEASURED");

    private static LiveScenarioAcceptanceViewModel ProjectScenarioAcceptance(
        LiveScenarioAcceptanceDecision decision) => new(
        decision.ScenarioId,
        decision.PersonaId,
        decision.ArmId,
        decision.Architecture.ToString(),
        $"measured {decision.Census.Measured} · not applicable {decision.Census.NotApplicable} · " +
            $"not measured {decision.Census.NotMeasured} · total {decision.Census.Total}",
        decision.Reliability.Measurement == MeasurementState.Measured
            ? $"{decision.Reliability.Successes}/{decision.Reliability.Total} whole-trial successes · " +
              $"estimate {Optional(decision.Reliability.Estimate)}"
            : "NOT MEASURED",
        decision.Reliability.Lower.HasValue && decision.Reliability.Upper.HasValue
            ? $"Wilson [{Optional(decision.Reliability.Lower)}, {Optional(decision.Reliability.Upper)}]"
            : "Wilson interval NOT MEASURED",
        $"{decision.ConfidenceLevel:P0} Wilson lower bound >= " +
            decision.MinimumLowerBound.ToString("0.000", CultureInfo.InvariantCulture),
        Verdict(decision.Passed));

    private static LiveScenarioAcceptanceViewModel ProjectScenarioAcceptance(
        VitrineLiveScenarioAcceptanceSnapshot decision) => new(
        decision.ScenarioId,
        decision.PersonaId,
        decision.ArmId,
        decision.Architecture,
        $"measured {decision.Census.Measured} · not applicable {decision.Census.NotApplicable} · " +
            $"not measured {decision.Census.NotMeasured} · total {decision.Census.Total}",
        string.Equals(decision.Reliability.Measurement, nameof(MeasurementState.Measured), StringComparison.Ordinal)
            ? $"{decision.Reliability.Successes}/{decision.Reliability.Total} whole-trial successes · " +
              $"estimate {Optional(decision.Reliability.Estimate)}"
            : "NOT MEASURED",
        decision.Reliability.Lower.HasValue && decision.Reliability.Upper.HasValue
            ? $"Wilson [{Optional(decision.Reliability.Lower)}, {Optional(decision.Reliability.Upper)}]"
            : "Wilson interval NOT MEASURED",
        $"{decision.ConfidenceLevel:P0} Wilson lower bound >= " +
            decision.MinimumLowerBound.ToString("0.000", CultureInfo.InvariantCulture),
        Verdict(decision.Passed));

    private static LiveComparisonViewModel ProjectComparison(LiveCheckComparison comparison) => new(
        comparison.CheckKey,
        comparison.CheckName,
        $"{comparison.ReferenceArm} → {comparison.ChallengerArm}",
        $"{comparison.Wins}/{comparison.Losses}/{comparison.Ties}",
        comparison.EffectiveN,
        $"measured {comparison.Census.Measured} · not applicable {comparison.Census.NotApplicable} · " +
            $"not measured {comparison.Census.NotMeasured} · total {comparison.Census.Total}",
        Optional(comparison.PValue),
        Optional(comparison.MinimumAttainableP),
        Optional(comparison.MeanDelta),
        comparison.Cases,
        comparison.TotalRepObservations,
        Optional(comparison.MeanRepetitionsPerCase),
        comparison.RepCollapse,
        comparison.Undecidable
            ? "INDECIDABLE · no winner can be inferred"
            : comparison.UnderpoweredByConstruction
                ? "UNDERPOWERED BY CONSTRUCTION"
                : "AgentEval reports power permits this comparison");

    private static LiveComparisonViewModel ProjectComparison(VitrineLiveComparisonSnapshot comparison) => new(
        comparison.CheckKey,
        comparison.CheckName,
        $"{comparison.ReferenceArm} → {comparison.ChallengerArm}",
        $"{comparison.Wins}/{comparison.Losses}/{comparison.Ties}",
        comparison.EffectiveN,
        $"measured {comparison.Census.Measured} · not applicable {comparison.Census.NotApplicable} · " +
            $"not measured {comparison.Census.NotMeasured} · total {comparison.Census.Total}",
        Optional(comparison.PValue),
        Optional(comparison.MinimumAttainableP),
        Optional(comparison.MeanDelta),
        comparison.Cases,
        comparison.TotalRepObservations,
        Optional(comparison.MeanRepetitionsPerCase),
        comparison.RepCollapse,
        comparison.Undecidable
            ? "INDECIDABLE · no winner can be inferred"
            : comparison.UnderpoweredByConstruction
                ? "UNDERPOWERED BY CONSTRUCTION"
                : "AgentEval reports power permits this comparison");

    private static LiveUsageViewModel ProjectUsage(
        LiveTrialEvidence trial,
        string role,
        LiveUsageEvidence usage) => new(
        trial.ScenarioId,
        trial.ArmId,
        trial.Repetition,
        role,
        usage.Status,
        usage.ModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED",
        usage.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED",
        usage.EstimatedCostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT MEASURED");

    private static LiveUsageViewModel ProjectSafetyUsage(string role, LiveUsageEvidence usage) => new(
        "safety-probes", "robin-agent-live", 1, role, usage.Status,
        usage.ModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED",
        usage.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED",
        usage.EstimatedCostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT MEASURED");

    private static LiveUsageViewModel ProjectSafetyUsage(string role, VitrineLiveUsageSnapshot usage) => new(
        "safety-probes", "robin-agent-live", 1, role, usage.Status,
        usage.ModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED",
        usage.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED",
        usage.EstimatedCostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT MEASURED");

    private static string SafetyUsageSummary(LiveUsageEvidence? target, LiveUsageEvidence? judge) =>
        target is null || judge is null
            ? "Safety provider usage: NOT MEASURED · no redacted safety usage summary was returned."
            : $"Safety target usage · {UsageRow(target)} · fallback judge · {UsageRow(judge)}.";

    private static string SafetyUsageSummary(VitrineLiveUsageSnapshot? target, VitrineLiveUsageSnapshot? judge) =>
        target is null || judge is null
            ? "Safety provider usage: NOT MEASURED · no redacted safety usage summary was stored."
            : $"Replayed safety target usage · {UsageRow(target)} · fallback judge · {UsageRow(judge)}.";

    private static string UsageRow(LiveUsageEvidence usage) =>
        $"status {usage.Status}, calls {Show(usage.ModelCalls)}, tokens {Show(usage.TotalTokens)}, estimated USD {Show(usage.EstimatedCostUsd)}";

    private static string UsageRow(VitrineLiveUsageSnapshot usage) =>
        $"status {usage.Status}, calls {Show(usage.ModelCalls)}, tokens {Show(usage.TotalTokens)}, estimated USD {Show(usage.EstimatedCostUsd)}";

    private static LiveUsageViewModel ProjectUsage(
        VitrineLiveTrialSnapshot trial,
        string role,
        VitrineLiveUsageSnapshot usage) => new(
        trial.ScenarioId,
        trial.ArmId,
        trial.Repetition,
        role,
        usage.Status,
        usage.ModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED",
        usage.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED",
        usage.EstimatedCostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT MEASURED");

    private static string UsageSummary(IReadOnlyList<LiveTrialEvidence> trials)
    {
        var rows = trials.SelectMany(static trial => new[] { trial.SubjectUsage, trial.JudgeUsage }).ToArray();
        if (rows.Length == 0)
            return "Live provider calls/tokens/cost: NOT MEASURED · no trial usage rows were returned.";

        var lowerBound = rows.Any(static row => string.Equals(row.Status, "lower-bound", StringComparison.OrdinalIgnoreCase));
        var statusCensus = string.Join(", ", rows.GroupBy(static row => row.Status, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => $"{group.Key} {group.Count()}"));
        return $"Live provider usage rows {rows.Length} · calls {Aggregate(rows.Select(static row => row.ModelCalls), lowerBound)} · " +
            $"input tokens {Aggregate(rows.Select(static row => row.InputTokens), lowerBound)} · " +
            $"output tokens {Aggregate(rows.Select(static row => row.OutputTokens), lowerBound)} · " +
            $"total tokens {Aggregate(rows.Select(static row => row.TotalTokens), lowerBound)} · " +
            $"estimated cost USD {AggregateCost(rows.Select(static row => row.EstimatedCostUsd), lowerBound)} · statuses {statusCensus}.";
    }

    private static string UsageSummary(IReadOnlyList<VitrineLiveTrialSnapshot> trials)
    {
        var rows = trials.SelectMany(static trial => new[] { trial.SubjectUsage, trial.JudgeUsage }).ToArray();
        if (rows.Length == 0)
            return "Live provider calls/tokens/cost: NOT MEASURED · no trial usage rows were returned.";

        var lowerBound = rows.Any(static row => string.Equals(row.Status, "lower-bound", StringComparison.OrdinalIgnoreCase));
        var statusCensus = string.Join(", ", rows.GroupBy(static row => row.Status, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static group => $"{group.Key} {group.Count()}"));
        return $"Live provider usage rows {rows.Length} · calls {Aggregate(rows.Select(static row => row.ModelCalls), lowerBound)} · " +
            $"input tokens {Aggregate(rows.Select(static row => row.InputTokens), lowerBound)} · " +
            $"output tokens {Aggregate(rows.Select(static row => row.OutputTokens), lowerBound)} · " +
            $"total tokens {Aggregate(rows.Select(static row => row.TotalTokens), lowerBound)} · " +
            $"estimated cost USD {AggregateCost(rows.Select(static row => row.EstimatedCostUsd), lowerBound)} · statuses {statusCensus}.";
    }

    private static string Aggregate(IEnumerable<int?> values, bool lowerBound)
    {
        var items = values.ToArray();
        var known = items.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        if (known.Length == 0) return "NOT MEASURED";
        var isLowerBound = lowerBound || known.Length < items.Length;
        return $"{(isLowerBound ? "≥" : string.Empty)}{known.Sum(static value => (long)value).ToString(CultureInfo.InvariantCulture)}";
    }

    private static string Aggregate(IEnumerable<long?> values, bool lowerBound)
    {
        var items = values.ToArray();
        var known = items.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        if (known.Length == 0) return "NOT MEASURED";
        var isLowerBound = lowerBound || known.Length < items.Length;
        return $"{(isLowerBound ? "≥" : string.Empty)}{known.Sum().ToString(CultureInfo.InvariantCulture)}";
    }

    private static string AggregateCost(IEnumerable<double?> values, bool lowerBound)
    {
        var items = values.ToArray();
        var known = items.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        if (known.Length == 0) return "NOT MEASURED";
        var isLowerBound = lowerBound || known.Length < items.Length;
        return $"{(isLowerBound ? "≥" : string.Empty)}{known.Sum().ToString("0.000000", CultureInfo.InvariantCulture)}";
    }

    private static string WorkloadSummary(VitrineEvaluationPlan plan, LiveEvalWorkload workload) =>
        plan == VitrineEvaluationPlan.LiveEval06SafetyProbes
            ? $"{workload.SafetyAttackCount} attack categories × {workload.PlannedSafetyProbes} bounded probes · " +
              $"Robin-only · {workload.PlannedSubjectCalls} target call(s) + up to " +
              $"{workload.PlannedJudgeEvaluations} fallback-judge call(s) · maximum {workload.MaximumSafetyModelCalls} safety model calls"
            : $"{workload.ScenarioCount} scenario(s) × {workload.ArmCount} arm(s) × {workload.Repetitions} rep(s) · " +
              $"{ArmPlan(plan)} · {workload.PlannedSubjectCalls} subject call(s) · " +
              $"{workload.PlannedJudgeEvaluations} judge evaluation(s)";

    private static string WorkloadSummary(VitrineEvaluationPlan plan, VitrineLiveWorkloadSnapshot workload) =>
        plan == VitrineEvaluationPlan.LiveEval06SafetyProbes
            ? $"{workload.SafetyAttackCount} attack categories × {workload.PlannedSafetyProbes} bounded probes · " +
              $"Robin-only · {workload.PlannedSubjectCalls} target call(s) + up to " +
              $"{workload.PlannedJudgeEvaluations} fallback-judge call(s) · maximum {workload.MaximumSafetyModelCalls} safety model calls"
            : $"{workload.ScenarioCount} scenario(s) × {workload.ArmCount} arm(s) × {workload.Repetitions} rep(s) · " +
              $"{ArmPlan(plan)} · {workload.PlannedSubjectCalls} subject call(s) · " +
              $"{workload.PlannedJudgeEvaluations} judge evaluation(s)";

    private static string ArmPlan(VitrineEvaluationPlan plan) => plan switch
    {
        VitrineEvaluationPlan.LiveEval01Agent or VitrineEvaluationPlan.LiveEval04StochasticAgent => "Robin agent arm",
        VitrineEvaluationPlan.LiveEval02Workflow or VitrineEvaluationPlan.LiveEval05StochasticWorkflow => "Discovery workflow arm",
        VitrineEvaluationPlan.LiveEval03AgentVsWorkflow => "Robin agent reference → discovery workflow challenger",
        VitrineEvaluationPlan.LiveEval06SafetyProbes => "Robin-only AgentEval safety target",
        _ => "No paid live arm",
    };

    private static string Measurement(MeasurementState state) => state switch
    {
        MeasurementState.Measured => "MEASURED",
        MeasurementState.NotApplicable => "NOT APPLICABLE",
        _ => "NOT MEASURED",
    };

    private static string Measurement(string state) => state switch
    {
        nameof(MeasurementState.Measured) => "MEASURED",
        nameof(MeasurementState.NotApplicable) => "NOT APPLICABLE",
        _ => "NOT MEASURED",
    };

    private static string Verdict(bool? value) => value switch
    {
        true => "PASS",
        false => "FAIL",
        null => "NOT MEASURED",
    };

    private static string QualityPassBar(double? value) => value is { } threshold
        && double.IsFinite(threshold) && threshold is >= 0 and <= 1
            ? $"QUALITY PASS BAR {threshold.ToString("0.000", CultureInfo.InvariantCulture)} · shipped default 1.000 requires all four authored criteria; not a null/chance floor."
            : "QUALITY PASS BAR NOT MEASURED · evaluator threshold absent; this is not a null/chance floor.";

    private static string DegradationSummary(int count, IReadOnlyList<string> kinds) =>
        $"bounded internal fallback degradations {count} · kinds " +
        (kinds.Count == 0 ? "none disclosed" : string.Join(", ", kinds));

    private static string JudgeExplanation(string? explanation) =>
        string.IsNullOrWhiteSpace(explanation)
            ? "NOT MEASURED · no judge explanation was recorded."
            : explanation;

    private static string Optional(double? value) => value.HasValue
        ? value.Value.ToString("0.000", CultureInfo.InvariantCulture)
        : "NOT MEASURED";

    private void UpsertGate(GateResultViewModel item)
    {
        var index = FindIndex(Gates, existing => string.Equals(existing.Id, item.Id, StringComparison.Ordinal));
        if (index < 0) Gates.Add(item);
        else Gates[index] = item;
    }

    private void UpsertControl(ControlResultViewModel item)
    {
        var index = FindIndex(Controls, existing => string.Equals(existing.Id, item.Id, StringComparison.Ordinal));
        if (index < 0) Controls.Add(item);
        else Controls[index] = item;
    }

    private void UpdateProgressCopy(bool completed = false)
    {
        var state = completed ? "completed" : OverallStatus.StartsWith("RUNNING", StringComparison.Ordinal) ? "running" : "reported";
        var mandatory = Gates.Count(static gate => gate.Authority == "MANDATORY");
        var diagnostics = Gates.Count(static gate => gate.Authority == "DIAGNOSTIC");
        var diagnosticNoun = diagnostics == 1 ? "evaluation" : "evaluations";
        RunProgress = $"{_completedGates}/{_totalGates} evaluation rows · {mandatory} mandatory gates + " +
            $"{diagnostics} diagnostic {diagnosticNoun} · {Controls.Count}/{_expectedControls} controls · {state}";
    }

    private void UpdateControlSummary()
    {
        if (Controls.Count == 0)
        {
            ControlSummary = EmptyControlSummary;
            return;
        }

        var production = Controls.Count(item => item.Scope == nameof(ControlScopeClass.ProductionObservation));
        var boundary = Controls.Count(item => item.Scope == nameof(ControlScopeClass.BoundaryCalibrationFixture));
        var caught = Controls.Count(item =>
            item.HealthyOutcome == ControlAttemptOutcome.MeasuredPass &&
            item.BrokenOutcome is ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved &&
            item.RestoredOutcome == ControlAttemptOutcome.MeasuredPass);
        ControlSummary = $"{Controls.Count}/{VitrineEvalCriteria.NegativeControlCount} registered controls · " +
            $"{production}/{ExpectedProductionObservations} ProductionObservation + " +
            $"{boundary}/{ExpectedBoundaryCalibrationFixtures} BoundaryCalibrationFixture · " +
            $"{caught}/{Controls.Count} defect detections recovered.";
    }

    private void RaiseCollectionState()
    {
        RaisePropertyChanged(nameof(HasGates));
        RaisePropertyChanged(nameof(HasControls));
        RaisePropertyChanged(nameof(HasBenchmark));
        RaisePropertyChanged(nameof(HasLiveScenarios));
        RaisePropertyChanged(nameof(HasLiveTrials));
        RaisePropertyChanged(nameof(HasLiveChecks));
        RaisePropertyChanged(nameof(HasLiveScenarioAcceptances));
        RaisePropertyChanged(nameof(HasLiveComparisons));
        RaisePropertyChanged(nameof(HasLiveUsage));
        RaisePropertyChanged(nameof(HasLiveRuns));
        RaisePropertyChanged(nameof(HasLiveSafety));
    }

    private static int FindIndex<T>(IList<T> items, Func<T, bool> predicate)
    {
        for (var index = 0; index < items.Count; index++)
            if (predicate(items[index])) return index;
        return -1;
    }

    private static string FormatOptional(double? value) => value.HasValue
        ? value.Value.ToString("0.000", CultureInfo.InvariantCulture)
        : "not derived";

    private static string Show<T>(T? value) where T : struct, IFormattable => value.HasValue
        ? value.Value.ToString(null, CultureInfo.InvariantCulture)
        : "NOT MEASURED";
}
