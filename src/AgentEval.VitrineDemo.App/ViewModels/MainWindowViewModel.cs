// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Avalonia.Threading;
using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Demos;

namespace AgentEval.VitrineDemo.App.ViewModels;

public sealed class MainWindowViewModel : BindableBase, IAsyncDisposable
{
    private readonly VitrineRunCoordinator _coordinator;
    private readonly IArtifactSaveService _saveService;
    private readonly Lock _presentationGate = new();
    private VitrineRunMode _selectedMode = VitrineRunMode.Demo01;
    private VitrineEventProjection? _projection;
    private VitrineEventStore? _store;
    private Action<VitrineEvent>? _storeHandler;
    private bool _isRunning;
    private bool _isPresentationActive;
    private string _status = "Offline-ready · choose a mode and run";
    private string _resultSummary = "No run yet. The graph will be captured from the actual runtime surface.";
    private string _outcomeTitle = "SCREENED RUN OUTCOME · no run yet";
    private string _outcomeDetails = "The exact customer-facing artifact, routes, degradations, and ledger will appear here after a run.";
    private VitrineRunOutcome? _lastOutcome;
    private VitrineRunArtifact? _artifact;
    private VitrineReplaySession? _replay;
    private string _replayStatus = "Replay inactive";
    private bool _isSetupExpanded = true;
    private bool _isOutcomeExpanded;
    private Guid? _presentationRunId;
    private long _presentationGeneration;
    private Task _presentationCompletion = Task.CompletedTask;
    private bool _disposed;

    public MainWindowViewModel() : this(new VitrineRunCoordinator(), NullArtifactSaveService.Instance) { }

    internal MainWindowViewModel(VitrineRunCoordinator coordinator, IArtifactSaveService? saveService = null)
    {
        _coordinator = coordinator;
        _saveService = saveService ?? NullArtifactSaveService.Instance;
        _coordinator.RunPrepared += OnRunPrepared;
        _coordinator.GraphPrepared += OnGraphPrepared;
        _coordinator.EvaluationProgressed += OnEvaluationProgressed;
        _coordinator.LiveEvaluationProgressed += OnLiveEvaluationProgressed;
        Setup.PropertyChanged += OnSetupPropertyChanged;
        RunCommand = new AsyncRelayCommand(RunAsync, CanRun);
        CancelCommand = new RelayCommand(_coordinator.Cancel, () => IsRunning);
        ClearCommand = new RelayCommand(Clear, CanClear);
        ExportJsonCommand = new AsyncRelayCommand(() => ExportAsync(html: false), () => !IsRunning && _artifact is not null);
        ExportHtmlCommand = new AsyncRelayCommand(() => ExportAsync(html: true), () => !IsRunning && _artifact is not null);
        StartReplayCommand = new RelayCommand(StartReplay, () => !IsRunning && _artifact is not null);
        ReplayPreviousCommand = new RelayCommand(ReplayPrevious, () => !IsRunning && _replay is not null && _replay.CanMovePrevious);
        ReplayNextCommand = new RelayCommand(ReplayNext, () => !IsRunning && _replay is not null && _replay.CanMoveNext);
        Graph.Load(PreviewGraph(SelectedMode, Setup.SelectedEvaluationPlan.Plan));
    }

    public string Title => "VITRINE · AgentEval control room";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string EvidenceBoundary =>
        "The UI observes execution. Criteria, floors, scoring, controls, and verdicts remain in AgentEval.VitrineDemo.Evals.";

    public string LiveReadiness => Config.Readiness.SafeSummary;

    public IReadOnlyList<VitrineRunMode> Modes { get; } = Enum.GetValues<VitrineRunMode>();

    public RunSetupViewModel Setup { get; } = new();
    public GraphViewModel Graph { get; } = new();
    public TimelineViewModel Timeline { get; } = new();
    public EvaluationBoardViewModel Evaluation { get; } = new();

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncRelayCommand ExportJsonCommand { get; }
    public AsyncRelayCommand ExportHtmlCommand { get; }
    public RelayCommand StartReplayCommand { get; }
    public RelayCommand ReplayPreviousCommand { get; }
    public RelayCommand ReplayNextCommand { get; }

    public VitrineRunMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!SetProperty(ref _selectedMode, value)) return;
            // Paid consent is deliberately one-shot and mode-scoped. Returning to the same
            // paid plan must never reuse an acknowledgement made in a different UI context.
            Setup.PaidExecutionAcknowledged = false;
            if (value == VitrineRunMode.Demo02) Setup.SelectWorkflowDemonstrationPersona();
            if (value == VitrineRunMode.Demo01) Setup.SelectRecommendationDemonstrationPersona();
            if (value == VitrineRunMode.Ablation)
            {
                Setup.SelectOfflineEvaluationPlan();
            }
            Timeline.SelectedFilter = value is VitrineRunMode.Evals or VitrineRunMode.Ablation
                ? TimelineFilter.Evals
                : TimelineFilter.Debug;
            ResetForModeSelection();
            RaiseModeProperties();
            RunCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsDemo01Mode => SelectedMode == VitrineRunMode.Demo01;
    public bool IsDemo02Mode => SelectedMode == VitrineRunMode.Demo02;
    public bool IsDemoMode => IsDemo01Mode || IsDemo02Mode;
    public bool IsEvaluationMode => SelectedMode is VitrineRunMode.Evals or VitrineRunMode.Ablation;
    public bool IsCatalogueSelfTestMode => SelectedMode == VitrineRunMode.Ablation;
    public bool IsProfiledEvaluationMode => SelectedMode == VitrineRunMode.Evals;
    public bool IsLiveEvaluationMode => IsProfiledEvaluationMode && Setup.IsLiveEvaluationPlan;
    public bool IsPaidExecutionMode => IsLiveEvaluationMode
        || (IsDemoMode && Setup.SelectedArm.Arm == RecommendationExecutionArm.LiveAzure);
    public bool IsLiveScenarioEvaluationMode => IsLiveEvaluationMode && Setup.SupportsLiveScenarioSelection;
    public bool IsSafetyEvaluationMode => IsLiveEvaluationMode && Setup.IsSafetyEvaluationPlan;
    public bool IsStochasticEvaluationMode => IsLiveEvaluationMode && Setup.SupportsEvaluationRepetitions;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            RunCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ClearCommand.RaiseCanExecuteChanged();
            RaiseArtifactCommands();
            RaisePropertyChanged(nameof(RunButtonText));
            RaisePropertyChanged(nameof(SetupPanelAction));
        }
    }

    /// <summary>
    /// True only while already-recorded facts are being paced onto the screen. It never means that
    /// the subject, judge, tools, or evaluation suite are still executing.
    /// </summary>
    public bool IsPresentationActive
    {
        get => _isPresentationActive;
        private set
        {
            if (!SetProperty(ref _isPresentationActive, value)) return;
            ClearCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Keeps run configuration discoverable before the first run while allowing the live graph and
    /// timeline to reclaim the full vertical workspace during execution.
    /// </summary>
    public bool IsSetupExpanded
    {
        get => _isSetupExpanded;
        set
        {
            if (!SetProperty(ref _isSetupExpanded, value)) return;
            RaisePropertyChanged(nameof(SetupPanelAction));
        }
    }

    public string SetupPanelAction => IsRunning
        ? "RUNNING · OPEN TO REVIEW"
        : IsSetupExpanded
            ? "COLLAPSE TO ENLARGE CONTROL ROOM"
            : "EDIT RUN CONFIGURATION";

    public bool IsOutcomeExpanded
    {
        get => _isOutcomeExpanded;
        set => SetProperty(ref _isOutcomeExpanded, value);
    }

    public string SetupSelectionSummary
    {
        get
        {
            var pace = $"pace {Setup.AudiencePacingMilliseconds} ms";
            if (!IsDemoMode)
            {
                var profile = SelectedMode == VitrineRunMode.Ablation
                    ? "offline · no provider LLM · expected exit 1"
                    : Setup.SelectedEvaluationPlan.Label;
                var workload = IsLiveEvaluationMode
                    ? Setup.IsSafetyEvaluationPlan
                        ? $" · Robin-only safety · {RunSetupViewModel.SafetyAttackCategories} attacks × {RunSetupViewModel.SafetyMaxProbesPerAttack} probes · " +
                          $"{Setup.PlannedLiveSubjectRuns} target probes · max {RunSetupViewModel.SafetyMaximumModelCalls} safety model calls"
                        : $" · {Setup.SelectedLiveScenario.Label} · {Setup.EffectiveEvaluationRepetitions} rep(s) · " +
                          $"{Setup.PlannedLiveSubjectRuns} subject + {Setup.PlannedLiveJudgeCalls} judge calls"
                    : string.Empty;
                return $"{DisplayMode(SelectedMode)} · {profile}{workload} · {pace}";
            }

            var personalization = Setup.PersonalizationEnabled
                ? "personalization enabled"
                : "personalization disabled";
            var rounds = IsDemo02Mode ? $" · max {Setup.MaxRounds} rounds" : string.Empty;
            return $"{SelectedMode} · {Setup.SelectedPersona.Label} · {Setup.SelectedArm.Label} · {personalization}{rounds} · {pace}";
        }
    }

    public string RunButtonText => IsRunning
        ? "RUNNING…"
        : SelectedMode == VitrineRunMode.Ablation
            ? "RUN CATALOGUE SELF-TEST"
            : SelectedMode == VitrineRunMode.Evals
                ? Setup.IsLiveEvaluationPlan
                    ? !Setup.IsSelectedLivePlanConfigured
                        ? "LIVE EVAL UNAVAILABLE"
                        : !Setup.PaidExecutionAcknowledged
                            ? "CONFIRM PAID EVAL"
                            : $"RUN {Setup.SelectedEvaluationPlan.Label.ToUpperInvariant()}"
                    : "RUN OFFLINE EVALS"
            : IsDemo01Mode
              && Setup.SelectedArm.Arm == RecommendationExecutionArm.ScriptedAgent
              && !OfflineRecommendationScript.Supports(Setup.SelectedPersona.Id)
                ? "SCRIPT UNAVAILABLE"
                : IsDemoMode && Setup.SelectedArm.Arm == RecommendationExecutionArm.LiveAzure
                    ? !Config.IsConfigured
                        ? "LIVE UNAVAILABLE"
                        : !Setup.PaidExecutionAcknowledged
                            ? "CONFIRM PAID LIVE"
                            : "RUN LIVE AZURE"
                    : IsDemoMode && Setup.SelectedArm.Arm == RecommendationExecutionArm.ScriptedAgent
                        ? "RUN MOCKED MODEL"
                        : IsDemoMode && Setup.SelectedArm.Arm == RecommendationExecutionArm.ZeroModelBaseline
                            ? "RUN ZERO-MODEL"
                            : "RUN OFFLINE";

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetProperty(ref _resultSummary, value);
    }

    public string OutcomeTitle
    {
        get => _outcomeTitle;
        private set => SetProperty(ref _outcomeTitle, value);
    }

    public string OutcomeDetails
    {
        get => _outcomeDetails;
        private set => SetProperty(ref _outcomeDetails, value);
    }

    public string ReplayStatus
    {
        get => _replayStatus;
        private set => SetProperty(ref _replayStatus, value);
    }

    public bool HasArtifact => _artifact is not null;

    public string ModeExplanation => SelectedMode switch
    {
        VitrineRunMode.Demo01 when Setup.SelectedArm.Arm == RecommendationExecutionArm.ScriptedAgent
            && !OfflineRecommendationScript.Supports(Setup.SelectedPersona.Id)
            => "No committed scripted-agent trajectory exists for this persona. Select Nadia, the zero-model baseline, or an explicitly configured live model.",
        VitrineRunMode.Demo01 => "The scripted arm drives the real ChatClientAgent and 13 real read-only tools. The zero-model retrieval baseline and paid live arm stay separately labelled.",
        VitrineRunMode.Demo02 => "The pre-run graph comes from the typed DiscoveryTopology contract, then the exact prepared MAF graph replaces it. ACTIONS reports executor, model-call, and Search observations; these are not AIFunction tools.",
        VitrineRunMode.Evals when Setup.IsLiveEvaluationPlan
            => Setup.IsSafetyEvaluationPlan
                ? $"Explicit paid safety evaluation: {Setup.SelectedEvaluationPlan.Description} Robin only; " +
                  $"{RunSetupViewModel.SafetyAttackCategories} attack categories × up to {RunSetupViewModel.SafetyMaxProbesPerAttack} probes, " +
                  $"{Setup.PlannedLiveSubjectRuns} target probe invocations · up to {RunSetupViewModel.SafetyMaximumModelCalls} safety model calls. " +
                  "The extraction canary exists in memory only; raw probes, responses, canary text, and system instructions are never projected or persisted."
                : $"Explicit paid use-case evaluation: {Setup.SelectedEvaluationPlan.Description} " +
               $"{Setup.SelectedLiveScenario.CaseCount} stable case(s), {Setup.EffectiveEvaluationRepetitions} repetition(s), " +
               $"{Setup.PlannedLiveSubjectRuns} subject execution(s), and {Setup.PlannedLiveJudgeCalls} judge evaluation(s). " +
               "Every arm/repetition is persisted locally. Provider failure cannot count as measured; bounded internal workflow fallbacks are disclosed.",
        VitrineRunMode.Evals => "Offline Evals targets Demo01 + Demo02: a deterministic AgentEval benchmark plus judged-quality, red-team, memory/honesty, topology/catalogue, and 43 registered mutation controls (20 production-observation rows + 23 boundary/calibration fixtures). Provider LLM calls and cost are exactly zero.",
        VitrineRunMode.Ablation => "Catalogue integrity self-test removes one row only from an isolated catalogue snapshot. Expected exit 1 proves detection; state is restored afterward and no provider LLM is used.",
        _ => string.Empty,
    };

    private bool CanRun() => !IsRunning
        && (!IsDemo01Mode
            || Setup.SelectedArm.Arm != RecommendationExecutionArm.ScriptedAgent
            || OfflineRecommendationScript.Supports(Setup.SelectedPersona.Id))
        && (!IsDemoMode
            || Setup.SelectedArm.Arm != RecommendationExecutionArm.LiveAzure
            || Config.IsConfigured && Setup.PaidExecutionAcknowledged)
        && (!IsProfiledEvaluationMode || Setup.CanRunSelectedEvaluationPlan);

    private bool CanClear() => !IsRunning &&
        (IsPresentationActive || _artifact is not null || _lastOutcome is not null ||
         _replay is not null || Timeline.Events.Count > 0);

    internal VitrineRunOutcome? LastOutcome => _lastOutcome;

    private async Task RunAsync()
    {
        if (!CanRun()) return;
        var request = CaptureRunRequestForExecution();
        var generation = Interlocked.Increment(ref _presentationGeneration);
        IsRunning = true;
        IsSetupExpanded = false;
        await StopPresentationAsync();
        Timeline.Clear();
        PrepareEvaluationBoard();
        _lastOutcome = null;
        _artifact = null;
        _replay = null;
        ReplayStatus = "Run in progress · no artifact available yet";
        IsOutcomeExpanded = false;
        OutcomeTitle = $"SCREENED {DisplayMode(request.Mode).ToUpperInvariant()} OUTCOME · awaiting completion";
        OutcomeDetails = "The inspector will populate from the sanitized run artifact after the authoritative run completes.";
        RaiseArtifactCommands();
        ResultSummary = "Execution is authoritative; audience pacing only delays this visual projection.";
        Status = $"Running {DisplayMode(request.Mode)}…";

        try
        {
            _lastOutcome = await _coordinator.RunAsync(request);
            if (!IsGenerationCurrent(generation)) return;

            _artifact = VitrineArtifactSerializer.Create(_lastOutcome);
            (OutcomeTitle, OutcomeDetails) = VitrineOutcomeInspector.Describe(_artifact);
            IsOutcomeExpanded = true;
            _replay = null;
            ReplayStatus = $"Artifact ready · schema {_artifact.SchemaVersion} · {_artifact.Events.Count} events";
            RaiseArtifactCommands();

            if (_lastOutcome.Evaluation is { } evaluation)
                Evaluation.Load(evaluation, expectedCatalogueDetection: request.Mode == VitrineRunMode.Ablation);
            else if (_lastOutcome.LiveEvaluation is { } liveEvaluation)
                Evaluation.LoadLive(liveEvaluation);
            ResultSummary = Describe(_lastOutcome);
            var terminalStatus = DescribeStatus(_lastOutcome);
            IsRunning = false;
            StartPresentationCompletion(generation, _lastOutcome.RunId, terminalStatus);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await StopPresentationAsync();
            if (IsGenerationCurrent(generation))
            {
                Status = $"Failed safely · {exception.GetType().Name}";
                ResultSummary = "The run did not produce a replayable artifact; exception details were not retained.";
                OutcomeTitle = "RUN OUTCOME · unavailable";
                OutcomeDetails = "Artifact construction failed safely; no prior run is presented as the current result.";
                IsOutcomeExpanded = true;
                ReplayStatus = "No replayable artifact was produced";
                RaiseArtifactCommands();
            }
        }
        finally
        {
            if (IsGenerationCurrent(generation)) IsRunning = false;
        }
    }

    internal VitrineRunRequest CaptureRunRequestForExecution()
    {
        var isDemo = SelectedMode is VitrineRunMode.Demo01 or VitrineRunMode.Demo02;
        var personaScope = isDemo
            ? Setup.SelectedPersona.Id
            : Setup.IsSafetyEvaluationPlan
                ? VitrineRunRequest.NotApplicablePersonaScope
                : Setup.SupportsLiveScenarioSelection && Setup.SelectedLiveScenario.Id is { } scenarioId
                    ? LiveUseCaseScenarios.Require(scenarioId).PersonaId
                    : VitrineRunRequest.MultiplePersonasScope;
        var request = new VitrineRunRequest(
            SelectedMode,
            personaScope,
            isDemo ? Setup.PersonalizationEnabled : null,
            Setup.SelectedArm.Arm,
            Setup.MaxRounds,
            SelectedMode == VitrineRunMode.Evals
                ? Setup.SelectedEvaluationPlan.Plan
                : VitrineEvaluationPlan.OfflineSuite,
            SelectedMode == VitrineRunMode.Evals && Setup.SupportsLiveScenarioSelection
                ? Setup.SelectedLiveScenario.Id
                : null,
            SelectedMode == VitrineRunMode.Evals ? Setup.EffectiveEvaluationRepetitions : 1,
            IsPaidExecutionMode && Setup.PaidExecutionAcknowledged);
        if (request.PaidExecutionConfirmed)
        {
            // Capture consent into this immutable request, then consume it before any paid work.
            // A retry, even after a safe failure, therefore requires an explicit fresh click.
            Setup.PaidExecutionAcknowledged = false;
        }
        return request;
    }

    internal Task RunSelectedModeAsync() => RunAsync();

    internal VitrineRunArtifact? CurrentArtifact => _artifact;

    internal async Task DrainPresentationAsync()
    {
        Task completion;
        lock (_presentationGate) completion = _presentationCompletion;
        await completion;
    }

    private async Task ExportAsync(bool html)
    {
        if (_artifact is null) return;
        try
        {
            var content = html ? VitrineHtmlReport.Render(_artifact) : VitrineArtifactSerializer.Serialize(_artifact);
            var extension = html ? "html" : "json";
            var name = $"vitrine-{_artifact.Mode.ToString().ToLowerInvariant()}-{_artifact.RunId:N}.{extension}";
            var saved = await _saveService.SaveAsync(name, content, html ? "text/html" : "application/json");
            Status = saved is null ? "Export cancelled" : "Exported sanitized evidence";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status = $"Export failed safely · {exception.GetType().Name}";
        }
    }

    private void StartReplay()
    {
        if (_artifact is null) return;
        InvalidateAndDetachPresentation();
        _replay = new VitrineReplaySession(_artifact);
        Timeline.Clear();
        LoadReplayEvaluation();
        Graph.Load(_artifact.Graph);
        IsOutcomeExpanded = true;
        ReplayStatus = $"Replay 0/{_artifact.Events.Count} · zero execution operations";
        RaiseArtifactCommands();
    }

    private void ReplayNext()
    {
        if (_replay?.MoveNext() is not { } item) return;
        Timeline.Add(item);
        Graph.Apply(item);
        UpdateReplayStatus();
    }

    private void ReplayPrevious()
    {
        if (_replay is null) return;
        _replay.MovePrevious();
        RebuildReplayProjection();
    }

    private void RebuildReplayProjection()
    {
        if (_replay is null) return;
        var position = _replay.Position;
        Timeline.Clear();
        LoadReplayEvaluation();
        Graph.Load(_replay.Artifact.Graph);
        for (var index = 0; index <= position; index++)
        {
            var item = _replay.Artifact.Events[index];
            Timeline.Add(item);
            Graph.Apply(item);
        }
        UpdateReplayStatus();
    }

    private void LoadReplayEvaluation()
    {
        Evaluation.Clear();
        if (_replay?.Artifact.Result.LiveEvaluation is { } live)
            Evaluation.LoadLiveSnapshot(live);
        else if (_replay?.Artifact is { Mode: VitrineRunMode.Evals or VitrineRunMode.Ablation } artifact)
            Evaluation.LoadOfflineSnapshot(
                artifact.Result,
                expectedCatalogueDetection: artifact.Mode == VitrineRunMode.Ablation);
    }

    private void UpdateReplayStatus()
    {
        if (_replay is null) return;
        ReplayStatus = $"Replay {Math.Max(0, _replay.Position + 1)}/{_replay.Artifact.Events.Count} · zero execution operations";
        RaiseArtifactCommands();
    }

    private void OnRunPrepared(VitrineEventStore store, VitrineGraphSnapshot graph)
    {
        var generation = Volatile.Read(ref _presentationGeneration);
        var runId = store.RunId;
        VitrineEventProjection? displacedProjection;
        var projection = new VitrineEventProjection(
            () => Setup.AudiencePacingMilliseconds,
            item => PresentEventAsync(generation, runId, item));
        Action<VitrineEvent> handler = item =>
        {
            if (item.RunId == runId) projection.TryPublish(item);
        };

        lock (_presentationGate)
        {
            if (_disposed || generation != _presentationGeneration)
            {
                _ = projection.DisposeAsync().AsTask();
                return;
            }

            if (_store is not null && _storeHandler is not null)
                _store.EventAppended -= _storeHandler;
            displacedProjection = _projection;
            _store = store;
            _projection = projection;
            _storeHandler = handler;
            _presentationRunId = runId;
            store.EventAppended += handler;
        }

        if (displacedProjection is not null) _ = displacedProjection.DisposeAsync().AsTask();
        _ = InvokeOnUiAsync(() =>
        {
            if (IsPresentationCurrent(generation, runId)) Graph.Load(graph);
        });
    }

    private void OnGraphPrepared(Guid runId, VitrineGraphSnapshot graph)
    {
        var generation = Volatile.Read(ref _presentationGeneration);
        _ = InvokeOnUiAsync(() =>
        {
            if (IsPresentationCurrent(generation, runId)) Graph.Load(graph);
        });
    }

    private void OnEvaluationProgressed(EvaluationProgressEvent progress)
    {
        var generation = Volatile.Read(ref _presentationGeneration);
        _ = InvokeOnUiAsync(() =>
        {
            if (IsGenerationCurrent(generation) && IsEvaluationMode) Evaluation.Apply(progress);
        });
    }

    private void OnLiveEvaluationProgressed(LiveEvalProgress progress)
    {
        var generation = Volatile.Read(ref _presentationGeneration);
        _ = InvokeOnUiAsync(() =>
        {
            if (IsGenerationCurrent(generation) && IsLiveEvaluationMode) Evaluation.ApplyLive(progress);
        });
    }

    internal static string Describe(VitrineRunOutcome outcome)
    {
        if (outcome.Recommendation is { } recommendation)
        {
            var survived = recommendation.Outcome?.Cleaned.PresentedCount;
            var survivedText = survived.HasValue ? survived.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";
            var calls = recommendation.ModelCalls.HasValue ? recommendation.ModelCalls.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "NOT MEASURED";
            return $"Demo01 {recommendation.Options.Arm}: {recommendation.Presented.Count} presented, {survivedText} survived; model calls {calls}; usage {recommendation.ProviderUsage.ToDisplayString()}.";
        }
        if (outcome.Workflow is { } workflow)
            return $"Demo02: {workflow.ExecutorIds.Count} executors, {workflow.RoutesTaken.Count} route observations, loop-back {(workflow.Looped ? "observed" : "not taken")}, stop {workflow.State.StopReason}; usage {workflow.State.ProviderUsage.ToDisplayString()}.";
        if (outcome.Evaluation is { } evaluation)
        {
            var mandatory = evaluation.Gates.Count(static gate => gate.IsVerdictBearing);
            var diagnostics = evaluation.Gates.Count - mandatory;
            return $"Evaluation: process-equivalent exit {evaluation.ExitCode}; {evaluation.CaughtControls}/{evaluation.Controls.Count} registered control mutations detected and recovered; " +
                $"{mandatory} mandatory gates and {diagnostics} diagnostic evaluations; local AgentEval run {evaluation.OfflineBenchmark?.RunDirectory ?? "NOT WRITTEN"}.";
        }
        if (outcome.LiveEvaluation is { } live)
            return $"{VitrineEvaluationPlans.Require(live.Plan).Label}: {live.Trials.Count} scenario trial(s), " +
                   $"{live.Runs.Count} AgentEval run director{(live.Runs.Count == 1 ? "y" : "ies")}, " +
                   $"status {live.TerminalStatus}, exit {live.ExitCode}; local outcome {live.Persistence.OutcomePath}.";
        if (outcome.FailureKind == VitrineRunCoordinator.PaidExecutionConfirmationRequiredFailureKind)
            return "Paid live execution was not started. Confirm the one-shot Paid Execution acknowledgement, " +
                   "then run again. No provider request was made.";
        return outcome.Cancelled ? "Run cancelled." : $"Run failed safely: {outcome.FailureKind ?? "unknown failure"}.";
    }

    internal static string DescribeStatus(VitrineRunOutcome outcome)
    {
        if (outcome.Cancelled) return "Cancelled · partial events retained";
        if (outcome.FailureKind == VitrineRunCoordinator.PaidExecutionConfirmationRequiredFailureKind)
            return "Paid live execution not started · confirmation required · no provider request made";
        if (outcome.FailureKind is not null) return $"Failed safely · {outcome.FailureKind}";
        if (outcome.Evaluation is { ExitCode: 0 })
            return $"Evaluation passed · {outcome.Events.Count} authoritative events";
        if (outcome.Evaluation is { ExitCode: 1 })
            return outcome.Request.Mode == VitrineRunMode.Ablation
                ? "Catalogue integrity self-test detected the planted defect · expected exit 1 · isolated state restored"
                : "One or more mandatory evaluation gates or registered controls failed · process-equivalent exit 1";
        if (outcome.Evaluation is { ExitCode: EvaluationExitCodes.NotMeasured })
            return "Evaluation NOT MEASURED · process-equivalent exit 3";
        if (outcome.Evaluation is { ExitCode: EvaluationExitCodes.InfrastructureFailure })
            return "Evaluation infrastructure failed · process-equivalent exit 4";
        if (outcome.Evaluation is not null)
            return $"Evaluation ended with process-equivalent exit {outcome.Evaluation.ExitCode}";
        if (outcome.LiveEvaluation is { TerminalStatus: LiveEvalTerminalStatus.Passed } livePassed)
            return $"{VitrineEvaluationPlans.Require(livePassed.Plan).Label} passed · {livePassed.Trials.Count} measured trial(s) · evidence saved locally";
        if (outcome.LiveEvaluation is { TerminalStatus: LiveEvalTerminalStatus.QualityFailed } liveFailed)
            return $"{VitrineEvaluationPlans.Require(liveFailed.Plan).Label} found quality failures · process-equivalent exit 1";
        if (outcome.LiveEvaluation is { TerminalStatus: LiveEvalTerminalStatus.NotMeasured })
            return "Live evaluation NOT MEASURED · process-equivalent exit 3";
        if (outcome.LiveEvaluation is { TerminalStatus: LiveEvalTerminalStatus.InfrastructureError })
            return "Live evaluation infrastructure failed · process-equivalent exit 4";
        return $"Completed · {outcome.Events.Count} authoritative events";
    }

    private void Clear()
    {
        InvalidateAndDetachPresentation();
        Timeline.Clear();
        PrepareEvaluationBoard();
        Graph.Load(PreviewGraph(SelectedMode, Setup.SelectedEvaluationPlan.Plan));
        _lastOutcome = null;
        _artifact = null;
        _replay = null;
        ReplayStatus = "Replay inactive";
        ResultSummary = "No run yet. The graph will be captured from the actual runtime surface.";
        OutcomeTitle = "SCREENED RUN OUTCOME · no run yet";
        OutcomeDetails = "The exact customer-facing artifact, routes, degradations, and ledger will appear here after a run.";
        IsOutcomeExpanded = false;
        Status = "Offline-ready · choose a mode and run";
        RaiseArtifactCommands();
    }

    private void ResetForModeSelection()
    {
        InvalidateAndDetachPresentation();
        Timeline.Clear();
        PrepareEvaluationBoard();
        Graph.Load(PreviewGraph(SelectedMode, Setup.SelectedEvaluationPlan.Plan));
        _lastOutcome = null;
        _artifact = null;
        _replay = null;
        IsOutcomeExpanded = false;
        ReplayStatus = "Replay inactive · run first to create a sanitized artifact";
        ResultSummary = "Pre-run topology is visible now; no execution event or outcome is claimed yet.";
        OutcomeTitle = $"SCREENED {DisplayMode(SelectedMode).ToUpperInvariant()} OUTCOME · no run yet";
        OutcomeDetails = "Run the selected mode to reveal its sanitized final response, routes, degradations, and ledger.";
        Status = $"Offline-ready · {DisplayMode(SelectedMode)} preview loaded";
        RaiseArtifactCommands();
    }

    private static VitrineGraphSnapshot PreviewGraph(
        VitrineRunMode mode,
        VitrineEvaluationPlan evaluationPlan) => mode switch
    {
        VitrineRunMode.Demo01 => VitrineGraphFactory.FromRegisteredDemo01Functions(),
        VitrineRunMode.Demo02 => VitrineGraphFactory.FromDiscoveryTopologyContract(),
        VitrineRunMode.Evals when evaluationPlan != VitrineEvaluationPlan.OfflineSuite =>
            VitrineGraphFactory.ForLiveEvaluation(evaluationPlan),
        VitrineRunMode.Evals or VitrineRunMode.Ablation => VitrineGraphFactory.ForEvaluationSuite(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private void RaiseModeProperties()
    {
        RaisePropertyChanged(nameof(IsDemo01Mode));
        RaisePropertyChanged(nameof(IsDemo02Mode));
        RaisePropertyChanged(nameof(IsDemoMode));
        RaisePropertyChanged(nameof(IsEvaluationMode));
        RaisePropertyChanged(nameof(IsCatalogueSelfTestMode));
        RaisePropertyChanged(nameof(IsProfiledEvaluationMode));
        RaisePropertyChanged(nameof(IsLiveEvaluationMode));
        RaisePropertyChanged(nameof(IsPaidExecutionMode));
        RaisePropertyChanged(nameof(IsLiveScenarioEvaluationMode));
        RaisePropertyChanged(nameof(IsSafetyEvaluationMode));
        RaisePropertyChanged(nameof(IsStochasticEvaluationMode));
        RaisePropertyChanged(nameof(ModeExplanation));
        RaisePropertyChanged(nameof(RunButtonText));
        RaisePropertyChanged(nameof(SetupSelectionSummary));
    }

    private void OnSetupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        RaisePropertyChanged(nameof(SetupSelectionSummary));
        if (eventArgs.PropertyName is nameof(RunSetupViewModel.SelectedArm)
            or nameof(RunSetupViewModel.SelectedPersona)
            or nameof(RunSetupViewModel.SelectedEvaluationPlan)
            or nameof(RunSetupViewModel.SelectedLiveScenario)
            or nameof(RunSetupViewModel.EvaluationRepetitions)
            or nameof(RunSetupViewModel.PaidExecutionAcknowledged))
        {
            if (!IsRunning && IsProfiledEvaluationMode
                && eventArgs.PropertyName is nameof(RunSetupViewModel.SelectedEvaluationPlan)
                    or nameof(RunSetupViewModel.SelectedLiveScenario)
                    or nameof(RunSetupViewModel.EvaluationRepetitions))
            {
                Graph.Load(PreviewGraph(SelectedMode, Setup.SelectedEvaluationPlan.Plan));
                PrepareEvaluationBoard();
            }
            RaisePropertyChanged(nameof(ModeExplanation));
            RaisePropertyChanged(nameof(RunButtonText));
            RaisePropertyChanged(nameof(IsLiveEvaluationMode));
            RaisePropertyChanged(nameof(IsPaidExecutionMode));
            RaisePropertyChanged(nameof(IsLiveScenarioEvaluationMode));
            RaisePropertyChanged(nameof(IsSafetyEvaluationMode));
            RaisePropertyChanged(nameof(IsStochasticEvaluationMode));
            RunCommand.RaiseCanExecuteChanged();
        }
    }

    private void PrepareEvaluationBoard()
    {
        if (IsProfiledEvaluationMode && Setup.IsLiveEvaluationPlan)
        {
            var scenarios = Setup.IsSafetyEvaluationPlan
                ? []
                : Setup.SelectedLiveScenario.Id is { } id
                    ? new[] { LiveUseCaseScenarios.Require(id) }
                    : LiveUseCaseScenarios.All;
            var workload = Setup.IsSafetyEvaluationPlan
                ? new LiveEvalWorkload(
                    0, 1, 1,
                    Setup.PlannedLiveSubjectRuns,
                    Setup.PlannedLiveJudgeCalls,
                    RunSetupViewModel.SafetyAttackCategories,
                    Setup.PlannedLiveSubjectRuns,
                    RunSetupViewModel.SafetyMaximumModelCalls)
                : new LiveEvalWorkload(
                    Setup.SelectedLiveScenario.CaseCount,
                    Setup.SelectedEvaluationPlan.IsComparison ? 2 : 1,
                    Setup.EffectiveEvaluationRepetitions,
                    Setup.PlannedLiveSubjectRuns,
                    Setup.PlannedLiveJudgeCalls);
            Evaluation.PrepareLive(
                Setup.SelectedEvaluationPlan.Plan,
                workload,
                scenarios);
            return;
        }

        Evaluation.Clear();
    }

    private void RaiseArtifactCommands()
    {
        ExportJsonCommand.RaiseCanExecuteChanged();
        ExportHtmlCommand.RaiseCanExecuteChanged();
        StartReplayCommand.RaiseCanExecuteChanged();
        ReplayPreviousCommand.RaiseCanExecuteChanged();
        ReplayNextCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(HasArtifact));
    }

    private async Task PresentEventAsync(long generation, Guid runId, VitrineEvent item)
    {
        await InvokeOnUiAsync(() =>
        {
            if (!IsPresentationCurrent(generation, runId) || item.RunId != runId) return;
            Timeline.Add(item);
            Graph.Apply(item);
        });
    }

    private void StartPresentationCompletion(long generation, Guid runId, string terminalStatus)
    {
        VitrineEventProjection? projection;
        lock (_presentationGate)
        {
            if (_presentationRunId != runId)
            {
                _presentationCompletion = Task.CompletedTask;
                Status = terminalStatus;
                return;
            }
            if (_store is not null && _storeHandler is not null)
                _store.EventAppended -= _storeHandler;
            _store = null;
            _storeHandler = null;
            projection = _projection;
        }

        if (projection is null)
        {
            lock (_presentationGate) _presentationCompletion = Task.CompletedTask;
            Status = terminalStatus;
            return;
        }

        var presentingStatus = $"{terminalStatus} · presenting recorded events";
        IsPresentationActive = true;
        Status = presentingStatus;
        var completion = FinishPresentationAsync(
            generation, runId, projection, terminalStatus, presentingStatus);
        lock (_presentationGate) _presentationCompletion = completion;
    }

    private async Task FinishPresentationAsync(
        long generation,
        Guid runId,
        VitrineEventProjection projection,
        string terminalStatus,
        string presentingStatus)
    {
        await projection.CompleteAsync();
        await projection.DisposeAsync();
        lock (_presentationGate)
        {
            if (ReferenceEquals(_projection, projection))
            {
                _projection = null;
                _presentationRunId = null;
            }
        }
        await InvokeOnUiAsync(() =>
        {
            if (!IsGenerationCurrent(generation)) return;
            IsPresentationActive = false;
            if (string.Equals(Status, presentingStatus, StringComparison.Ordinal))
                Status = terminalStatus;
        });
    }

    private async Task StopPresentationAsync()
    {
        VitrineEventProjection? projection;
        lock (_presentationGate)
        {
            if (_store is not null && _storeHandler is not null)
                _store.EventAppended -= _storeHandler;
            _store = null;
            _storeHandler = null;
            projection = _projection;
            _projection = null;
            _presentationRunId = null;
        }

        if (projection is not null) await projection.DisposeAsync();
        IsPresentationActive = false;
        lock (_presentationGate) _presentationCompletion = Task.CompletedTask;
    }

    private void InvalidateAndDetachPresentation()
    {
        Interlocked.Increment(ref _presentationGeneration);
        VitrineEventProjection? projection;
        lock (_presentationGate)
        {
            if (_store is not null && _storeHandler is not null)
                _store.EventAppended -= _storeHandler;
            _store = null;
            _storeHandler = null;
            projection = _projection;
            _projection = null;
            _presentationRunId = null;
        }
        if (projection is not null) _ = projection.DisposeAsync().AsTask();
        IsPresentationActive = false;
        lock (_presentationGate) _presentationCompletion = Task.CompletedTask;
    }

    private bool IsPresentationCurrent(long generation, Guid runId)
    {
        lock (_presentationGate)
            return !_disposed
                && generation == _presentationGeneration
                && _presentationRunId == runId;
    }

    private bool IsGenerationCurrent(long generation)
    {
        lock (_presentationGate) return !_disposed && generation == _presentationGeneration;
    }

    private static Task InvokeOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private static string DisplayMode(VitrineRunMode mode) => mode == VitrineRunMode.Ablation
        ? "Catalogue integrity self-test"
        : mode.ToString();

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _presentationGeneration);
        lock (_presentationGate) _disposed = true;
        _coordinator.RunPrepared -= OnRunPrepared;
        _coordinator.GraphPrepared -= OnGraphPrepared;
        _coordinator.EvaluationProgressed -= OnEvaluationProgressed;
        _coordinator.LiveEvaluationProgressed -= OnLiveEvaluationProgressed;
        Setup.PropertyChanged -= OnSetupPropertyChanged;
        await StopPresentationAsync();
        await _coordinator.DisposeAsync();
    }
}

public enum VitrineRunMode
{
    Demo01,
    Demo02,
    Evals,
    Ablation,
}
