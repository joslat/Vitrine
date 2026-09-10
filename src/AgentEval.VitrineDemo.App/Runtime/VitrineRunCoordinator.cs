// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.App.Runtime;

public sealed record VitrineRunRequest(
    VitrineRunMode Mode,
    string UserId,
    bool? PersonalizationEnabled = true,
    RecommendationExecutionArm Demo01Arm = RecommendationExecutionArm.ScriptedAgent,
    int MaxRounds = DiscoveryState.DefaultMaxDiscoveryRounds,
    VitrineEvaluationPlan EvaluationPlan = VitrineEvaluationPlan.OfflineSuite,
    string? LiveScenarioId = null,
    int EvaluationRepetitions = 5,
    bool PaidExecutionConfirmed = false)
{
    public const string MultiplePersonasScope = "multiple-personas";
    public const string NotApplicablePersonaScope = "not-applicable";
}

public sealed record VitrineRunOutcome(
    Guid RunId,
    VitrineRunRequest Request,
    VitrineGraphSnapshot Graph,
    IReadOnlyList<VitrineEvent> Events,
    RecommendationRunResult? Recommendation = null,
    DiscoveryRunResult? Workflow = null,
    SuiteResult? Evaluation = null,
    LiveEvalResult? LiveEvaluation = null,
    bool Cancelled = false,
    string? FailureKind = null)
{
    public int? ProcessEquivalentExitCode => Evaluation?.ExitCode ?? LiveEvaluation?.ExitCode;
}

/// <summary>Serializes authoritative runs, owns cancellation, and adapts typed source events.</summary>
public sealed class VitrineRunCoordinator : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Func<VitrineRunRequest, RecommendationToolSet?, VitrineGraphSnapshot> _initialGraphFactory;
    private readonly Func<VitrineEvaluationPlan, bool, LiveEvalOptions, IProgress<LiveEvalProgress>?,
        CancellationToken, Task<LiveEvalResult>> _liveEvaluationRunner;
    private CancellationTokenSource? _activeCancellation;
    private Task<VitrineRunOutcome>? _activeRun;
    private bool _disposed;

    public VitrineRunCoordinator() : this(CreateInitialGraph, RunLiveEvaluationAsync) { }

    internal VitrineRunCoordinator(Func<VitrineRunRequest, VitrineGraphSnapshot> initialGraphFactory)
        : this((request, _) => initialGraphFactory(request), RunLiveEvaluationAsync)
    {
    }

    internal VitrineRunCoordinator(
        Func<VitrineRunRequest, RecommendationToolSet?, VitrineGraphSnapshot> initialGraphFactory)
        : this(initialGraphFactory, RunLiveEvaluationAsync)
    {
    }

    internal VitrineRunCoordinator(
        Func<VitrineRunRequest, RecommendationToolSet?, VitrineGraphSnapshot> initialGraphFactory,
        Func<VitrineEvaluationPlan, bool, LiveEvalOptions, IProgress<LiveEvalProgress>?, CancellationToken,
            Task<LiveEvalResult>> liveEvaluationRunner)
    {
        _initialGraphFactory = initialGraphFactory
            ?? throw new ArgumentNullException(nameof(initialGraphFactory));
        _liveEvaluationRunner = liveEvaluationRunner
            ?? throw new ArgumentNullException(nameof(liveEvaluationRunner));
    }

    public event Action<VitrineEventStore, VitrineGraphSnapshot>? RunPrepared;

    public event Action<Guid, VitrineGraphSnapshot>? GraphPrepared;

    /// <summary>
    /// Carries the eval layer's typed progress directly to observers. Consumers may project the
    /// supplied GateResult/ControlResult; they must not derive a second verdict.
    /// </summary>
    public event Action<EvaluationProgressEvent>? EvaluationProgressed;

    /// <summary>Allow-listed paid-plan progress; it never contains prompts or model response text.</summary>
    public event Action<LiveEvalProgress>? LiveEvaluationProgressed;

    public bool IsRunning
    {
        get { lock (_gate) return _activeRun is { IsCompleted: false }; }
    }

    public Task<VitrineRunOutcome> RunAsync(VitrineRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationTokenSource runCancellation;
        TaskCompletionSource<VitrineRunOutcome> completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRun is { IsCompleted: false })
                throw new InvalidOperationException("Only one VITRINE run may execute at a time.");
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            completion = new TaskCompletionSource<VitrineRunOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeCancellation = runCancellation;
            _activeRun = completion.Task;
        }

        // Start only after the active task has been reserved. RunPrepared observers may re-enter
        // RunAsync, and must observe the reservation instead of starting a second execution.
        _ = CompleteReservedRunAsync(request, runCancellation, completion);
        return completion.Task;
    }

    public void Cancel()
    {
        lock (_gate) _activeCancellation?.Cancel();
    }

    public async Task CancelAndWaitAsync()
    {
        Task<VitrineRunOutcome>? running;
        lock (_gate)
        {
            _activeCancellation?.Cancel();
            running = _activeRun;
        }
        if (running is not null)
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<VitrineRunOutcome> RunCoreAsync(VitrineRunRequest request, CancellationToken cancellationToken)
    {
        var store = new VitrineEventStore();
        var graph = VitrineGraphSnapshot.Empty("Run preparation did not complete");
        CallbackRecommendationRuntimeEventSink? recommendationSink = null;
        RecommendationToolSet? recommendationTools = null;

        try
        {
            var isPaidSubject = (request.Mode is VitrineRunMode.Demo01 or VitrineRunMode.Demo02)
                && request.Demo01Arm == RecommendationExecutionArm.LiveAzure;
            var isPaidEvaluation = request.Mode == VitrineRunMode.Evals
                && request.EvaluationPlan != VitrineEvaluationPlan.OfflineSuite;
            if ((isPaidSubject || isPaidEvaluation) && !request.PaidExecutionConfirmed)
                throw new InvalidOperationException("Paid live execution requires explicit one-shot confirmation.");

            if (request.Mode == VitrineRunMode.Demo01)
            {
                recommendationSink = new CallbackRecommendationRuntimeEventSink(item =>
                    store.Append(VitrineEventAdapters.FromRecommendation(item)));
                recommendationTools = RecommendationAgentFactory.PrepareReadOnlyTools(recommendationSink);
            }

            graph = _initialGraphFactory(request, recommendationTools);
            NotifyRunPrepared(store, graph);

            switch (request.Mode)
            {
                case VitrineRunMode.Demo01:
                {
                    var result = await RecommendationRunEngine.RunAsync(
                        new RecommendationRunOptions(
                            request.UserId,
                            PersonalizationDisabled: request.PersonalizationEnabled != true,
                            request.Demo01Arm,
                            RegisteredTools: recommendationTools),
                        recommendationSink,
                        cancellationToken).ConfigureAwait(false);
                    return new(store.RunId, request, graph, store.Snapshot(), Recommendation: result,
                        Cancelled: result.Status == RecommendationRunStatus.Cancelled,
                        FailureKind: result.FailureKind);
                }
                case VitrineRunMode.Demo02:
                {
                    var sink = new CallbackDiscoveryProgressSink(item =>
                        store.Append(VitrineEventAdapters.FromDiscovery(item)));
                    using var workflowClient = request.Demo01Arm == RecommendationExecutionArm.ScriptedAgent
                        ? new DeterministicDiscoveryChatClient()
                        : null;
                    var result = await GalaxusDiscoveryLoop.RunAsync(
                        request.UserId,
                        new DiscoveryLoopOptions(
                            Offline: request.Demo01Arm == RecommendationExecutionArm.ZeroModelBaseline,
                            PersonalizationDisabled: request.PersonalizationEnabled != true,
                            MaxRounds: request.MaxRounds,
                            ChatClient: workflowClient,
                            Progress: sink,
                            WorkflowPrepared: (workflow, ids) =>
                            {
                                graph = VitrineGraphFactory.FromPreparedWorkflow(workflow, ids);
                                NotifyGraphPrepared(store.RunId, graph);
                            }),
                        cancellationToken).ConfigureAwait(false);
                    return new(store.RunId, request, graph, store.Snapshot(), Workflow: result,
                        FailureKind: result.Failed ? "WorkflowExecutorFailure" : null);
                }
                case VitrineRunMode.Evals:
                case VitrineRunMode.Ablation:
                {
                    if (request.Mode == VitrineRunMode.Evals
                        && request.EvaluationPlan != VitrineEvaluationPlan.OfflineSuite)
                    {
                        VitrineEventDraft? deferredTerminal = null;
                        var liveProgress = new CallbackProgress<LiveEvalProgress>(item =>
                        {
                            var draft = VitrineEventAdapters.FromLiveEvaluation(item);
                            if (item.Phase == LiveEvalProgressPhase.SessionCompleted)
                                deferredTerminal = draft;
                            else
                                store.Append(draft);
                            NotifyLiveEvaluationProgress(item);
                        });
                        var repetitions = VitrineEvaluationPlans.Require(request.EvaluationPlan).SupportsRepetitions
                            ? request.EvaluationRepetitions
                            : 1;
                        var options = new LiveEvalOptions(
                            Repetitions: repetitions,
                            ScenarioIds: string.IsNullOrWhiteSpace(request.LiveScenarioId)
                                ? null
                                : [request.LiveScenarioId]);
                        var liveResult = await _liveEvaluationRunner(
                            request.EvaluationPlan, request.PaidExecutionConfirmed, options, liveProgress,
                            cancellationToken).ConfigureAwait(false);
                        foreach (var observation in VitrineEventAdapters.FromLiveSafetyResult(liveResult))
                            store.Append(observation);
                        if (deferredTerminal is not null) store.Append(deferredTerminal);
                        return new(store.RunId, request, graph, store.Snapshot(), LiveEvaluation: liveResult);
                    }

                    var sink = new CallbackEvaluationProgressSink(item =>
                    {
                        store.Append(VitrineEventAdapters.FromEvaluation(item));
                        NotifyEvaluationProgress(item);
                    });
                    var result = await EvaluationSuite.RunAsync(
                        leaveCatalogueAblated: request.Mode == VitrineRunMode.Ablation,
                        cancellationToken,
                        sink).ConfigureAwait(false);
                    return new(store.RunId, request, graph, store.Snapshot(), Evaluation: result);
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(request));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            store.Append(new(VitrineEventCategory.System, "RunCancelled", VitrineEventDisposition.Warning,
                "coordinator", "ui", "Run cancelled", "The active operation observed cancellation."));
            return new(store.RunId, request, graph, store.Snapshot(), Cancelled: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            store.Append(new(VitrineEventCategory.System, "RunFailed", VitrineEventDisposition.Failed,
                "coordinator", "ui", "Run failed",
                $"Failure type {exception.GetType().Name}; message intentionally not stored."));
            return new(store.RunId, request, graph, store.Snapshot(), FailureKind: exception.GetType().Name);
        }
    }

    private async Task CompleteReservedRunAsync(
        VitrineRunRequest request,
        CancellationTokenSource runCancellation,
        TaskCompletionSource<VitrineRunOutcome> completion)
    {
        try
        {
            var outcome = await RunCoreAsync(request, runCancellation.Token).ConfigureAwait(false);
            ReleaseReservation(runCancellation);
            completion.TrySetResult(outcome);
        }
        catch (Exception exception)
        {
            ReleaseReservation(runCancellation);
            completion.TrySetException(exception);
        }
    }

    private void ReleaseReservation(CancellationTokenSource runCancellation)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeCancellation, runCancellation))
            {
                _activeCancellation = null;
                _activeRun = null;
            }
        }
        runCancellation.Dispose();
    }

    private static VitrineGraphSnapshot CreateInitialGraph(
        VitrineRunRequest request,
        RecommendationToolSet? recommendationTools) => request.Mode switch
    {
        VitrineRunMode.Demo01 => VitrineGraphFactory.FromRegisteredDemo01Functions(
            recommendationTools ?? throw new InvalidOperationException("Demo01 tool preparation was absent.")),
        VitrineRunMode.Evals when request.EvaluationPlan != VitrineEvaluationPlan.OfflineSuite =>
            VitrineGraphFactory.ForRunningLiveEvaluation(request.EvaluationPlan),
        VitrineRunMode.Evals or VitrineRunMode.Ablation => VitrineGraphFactory.ForRunningEvaluationSuite(),
        VitrineRunMode.Demo02 => VitrineGraphFactory.FromDiscoveryTopologyContract(),
        _ => throw new ArgumentOutOfRangeException(nameof(request)),
    };

    private static Task<LiveEvalResult> RunLiveEvaluationAsync(
        VitrineEvaluationPlan plan,
        bool paidExecutionConfirmed,
        LiveEvalOptions options,
        IProgress<LiveEvalProgress>? progress,
        CancellationToken cancellationToken) =>
        LiveEvaluationPlanRunner.RunAsync(plan, paidExecutionConfirmed, options, progress: progress,
            cancellationToken: cancellationToken);

    private void NotifyRunPrepared(VitrineEventStore store, VitrineGraphSnapshot graph)
    {
        var handlers = RunPrepared;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<VitrineEventStore, VitrineGraphSnapshot>>())
        {
            try { handler(store, graph); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private void NotifyGraphPrepared(Guid runId, VitrineGraphSnapshot graph)
    {
        var handlers = GraphPrepared;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<Guid, VitrineGraphSnapshot>>())
        {
            try { handler(runId, graph); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private void NotifyEvaluationProgress(EvaluationProgressEvent progress)
    {
        var handlers = EvaluationProgressed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<EvaluationProgressEvent>>())
        {
            try { handler(progress); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    private void NotifyLiveEvaluationProgress(LiveEvalProgress progress)
    {
        var handlers = LiveEvaluationProgressed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<LiveEvalProgress>>())
        {
            try { handler(progress); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await CancelAndWaitAsync().ConfigureAwait(false);
    }
}

internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    private readonly Action<T> _callback = callback ?? throw new ArgumentNullException(nameof(callback));
    public void Report(T value) => _callback(value);
}
