// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.Evals;

namespace AgentEval.VitrineDemo.App.ViewModels;

public enum GraphNodeState
{
    Idle,
    Active,
    Succeeded,
    Warning,
    Blocked,
    Failed,
    NotMeasured,
    NotApplicable,
    ExpectedDefectDetected,
    SelfTestSucceeded,
}

public sealed class GraphNodeViewModel(VitrineGraphNode node) : BindableBase
{
    private GraphNodeState _state;
    private int _executionCount;

    public string Id => node.Id;
    public string Label => node.Label;
    public string Kind => node.Kind;
    public string Description => node.Description ?? string.Empty;

    public GraphNodeState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                RaisePropertyChanged(nameof(StateText));
                RaisePropertyChanged(nameof(CanvasStateText));
                RaisePropertyChanged(nameof(StateColor));
            }
        }
    }

    public string StateText => State switch
    {
        GraphNodeState.Idle => "READY",
        GraphNodeState.Active => "RUNNING",
        GraphNodeState.Succeeded => "DONE",
        GraphNodeState.NotMeasured => "NOT MEASURED",
        GraphNodeState.NotApplicable => "NOT APPLICABLE",
        GraphNodeState.ExpectedDefectDetected => "EXPECTED DEFECT DETECTED · 99→98",
        GraphNodeState.SelfTestSucceeded => "SELF-TEST SUCCEEDED · 99→98 DETECTED",
        _ => State.ToString().ToUpperInvariant(),
    };

    public string CanvasStateText => State switch
    {
        GraphNodeState.ExpectedDefectDetected => "DETECTED · 99→98",
        GraphNodeState.SelfTestSucceeded => "SELF-TEST OK · 99→98",
        _ => StateText,
    };

    public int ExecutionCount
    {
        get => _executionCount;
        private set
        {
            if (SetProperty(ref _executionCount, value))
                RaisePropertyChanged(nameof(ExecutionText));
        }
    }

    public string ExecutionText => node.Kind == "evaluation-controls"
        ? $"{ExecutionCount}/{VitrineEvalCriteria.NegativeControlCount} controls checked"
        : ExecutionCount == 0 ? "not run" : $"runs ×{ExecutionCount}";

    public string ExecutionBadgeText => node.Kind == "evaluation-controls"
        ? $"{ExecutionCount}/{VitrineEvalCriteria.NegativeControlCount}"
        : $"×{ExecutionCount}";

    internal void RecordExecution()
    {
        ExecutionCount++;
        RaisePropertyChanged(nameof(ExecutionBadgeText));
    }
    public string StateColor => State switch
    {
        GraphNodeState.Succeeded => "#63D391",
        GraphNodeState.Warning or GraphNodeState.NotMeasured => "#F6C55C",
        GraphNodeState.NotApplicable => "#9FC5FF",
        GraphNodeState.ExpectedDefectDetected or GraphNodeState.SelfTestSucceeded => "#63D391",
        GraphNodeState.Blocked => "#F3A85D",
        GraphNodeState.Failed => "#F07076",
        GraphNodeState.Active => "#5AE4D2",
        _ => "#7184A1",
    };
}

public sealed class GraphEdgeViewModel(VitrineGraphEdge edge) : BindableBase
{
    private bool _isActive;
    private int _traversalCount;
    public string Id => edge.Id;
    public string SourceId => edge.SourceId;
    public string TargetId => edge.TargetId;
    public string Label => edge.Label;
    public bool IsConditional => edge.IsConditional;
    public bool IsLoopBack => edge.IsLoopBack;

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    /// <summary>
    /// Count of directed route observations projected from typed events. Demo02 route producers
    /// may emit the same predicate more than once, so the UI calls these observations, not rounds.
    /// </summary>
    public int TraversalCount
    {
        get => _traversalCount;
        private set
        {
            if (SetProperty(ref _traversalCount, value))
                RaisePropertyChanged(nameof(TraversalText));
        }
    }

    public string TraversalText => TraversalCount == 0 ? "not observed" : $"observed ×{TraversalCount}";

    internal void RecordTraversal() => TraversalCount++;
}

/// <summary>One truthfully named Demo02 activity class derived from typed workflow events.</summary>
public sealed class GraphActionViewModel(string id, string kind, string label) : BindableBase
{
    private int _count;
    private string _lastDetail = "Not observed yet.";

    public string Id { get; } = id;
    public string Kind { get; } = kind;
    public string Label { get; } = label;

    public int Count
    {
        get => _count;
        private set
        {
            if (SetProperty(ref _count, value)) RaisePropertyChanged(nameof(CountText));
        }
    }

    public string CountText => $"×{Count}";

    public string LastDetail
    {
        get => _lastDetail;
        private set => SetProperty(ref _lastDetail, value);
    }

    internal void Observe(int count, string detail)
    {
        Count += Math.Max(0, count);
        if (!string.IsNullOrWhiteSpace(detail)) LastDetail = detail;
    }
}

public sealed class GraphViewModel : BindableBase
{
    private VitrineGraphSnapshot _snapshot = VitrineGraphSnapshot.Empty("Waiting for a run");
    private string _provenance = "No topology captured yet.";
    private int _graphRevision;
    private readonly Dictionary<string, string> _activeOperationNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GraphNodeState> _liveAggregateStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _countedSequences = [];

    public ObservableCollection<GraphNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<GraphEdgeViewModel> Edges { get; } = [];
    public ObservableCollection<GraphActionViewModel> Actions { get; } = [];

    public VitrineGraphSnapshot Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public string Title => Snapshot.Name;

    public string Provenance
    {
        get => _provenance;
        private set => SetProperty(ref _provenance, value);
    }

    public bool IsPreview => Snapshot.Source is VitrineGraphSource.RegisteredFunctionsPreview
        or VitrineGraphSource.DiscoveryTopologyPreview
        or VitrineGraphSource.EvaluationServicePreview;

    public bool ShowActionShelf => Snapshot.Source is VitrineGraphSource.DiscoveryTopologyPreview
        or VitrineGraphSource.MafWorkflow;

    public string BadgeText => IsPreview ? "PRE-RUN PREVIEW" : "RUNTIME FACTS";

    public string BadgeColor => IsPreview ? "#F6C55C" : "#5AE4D2";

    public string ActionsSummary => Actions.Count == 0
        ? "No actions observed yet. Executor, model-call, and Search activity appears here during the run."
        : $"{Actions.Count} typed action class(es) observed. These are workflow actions, not registered AIFunction tools.";

    public void Load(VitrineGraphSnapshot graph)
    {
        Snapshot = graph;
        Nodes.Clear();
        Edges.Clear();
        Actions.Clear();
        _activeOperationNodes.Clear();
        _liveAggregateStates.Clear();
        _countedSequences.Clear();
        foreach (var node in graph.Nodes) Nodes.Add(new(node));
        foreach (var edge in graph.Edges) Edges.Add(new(edge));
        Provenance = graph.Source switch
        {
            VitrineGraphSource.RegisteredFunctionsPreview =>
                $"Pre-run preview assembled from the {Nodes.Count(node => node.Kind == "tool")} read-only functions currently returned by RecommendationAgentFactory; the run replaces it with its identity-bearing tool set.",
            VitrineGraphSource.RegisteredFunctions => $"Derived from {Nodes.Count(node => node.Kind == "tool")} functions returned by RecommendationAgentFactory.",
            VitrineGraphSource.DiscoveryTopologyPreview =>
                $"Pre-run preview from the subject-owned DiscoveryTopology contract: {Nodes.Count} executors and {Edges.Count} directed routes. It is replaced by the exact prepared graph before execution.",
            VitrineGraphSource.MafWorkflow => $"Extracted by AgentEval.MAF from the exact prepared workflow: {Nodes.Count} nodes, {Edges.Count} edges.",
            VitrineGraphSource.EvaluationServicePreview when Nodes.Any(node => node.Id == "live-session") =>
                "Pre-run paid evaluation plan derived from the selected Eval01–Eval06 descriptor and its admitted live checks. Runtime state starts only after explicit confirmation.",
            VitrineGraphSource.EvaluationServicePreview =>
                "Pre-run plan preview. Runtime state is driven only by typed EvaluationSuite progress.",
            VitrineGraphSource.EvaluationService when Nodes.Any(node => node.Id == "live-session") =>
                "Driven by typed LiveEvalProgress from fresh subject, judge, benchmark, and persistence stages.",
            _ => "Driven by typed EvaluationSuite progress.",
        };
        RaisePropertyChanged(nameof(Title));
        RaisePropertyChanged(nameof(IsPreview));
        RaisePropertyChanged(nameof(ShowActionShelf));
        RaisePropertyChanged(nameof(BadgeText));
        RaisePropertyChanged(nameof(BadgeColor));
        RaisePropertyChanged(nameof(ActionsSummary));
        AdvanceRevision();
    }

    /// <summary>A monotonic invalidation token; equal aggregate state must still redraw a new route.</summary>
    public int GraphRevision => _graphRevision;

    public GraphNodeState StateOf(string nodeId) =>
        Nodes.FirstOrDefault(node => string.Equals(node.Id, nodeId, StringComparison.OrdinalIgnoreCase))?.State
        ?? GraphNodeState.Idle;

    public void Apply(VitrineEvent item)
    {
        foreach (var edge in Edges) edge.IsActive = false;

        var isNewEvent = _countedSequences.Add(item.Sequence);
        var nodeId = ResolveNodeId(item);
        var node = Nodes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, nodeId, StringComparison.OrdinalIgnoreCase));
        if (node is not null)
        {
            if (item.Disposition == VitrineEventDisposition.Active && isNewEvent)
            {
                if (ShouldRecordExecution(item, node)) node.RecordExecution();
                if (!string.IsNullOrWhiteSpace(item.OperationId))
                    _activeOperationNodes[item.OperationId] = node.Id;
            }
            else if (isNewEvent && item.Kind is "BenchmarkCheckCompleted" or "BenchmarkPersisted"
                or "OfflineBenchmarkPersisted" or "ControlCompleted" or "SuiteCompleted"
                or "LiveSessionCompleted")
            {
                node.RecordExecution();
            }
            var observedState = item.Disposition switch
            {
                VitrineEventDisposition.Active => GraphNodeState.Active,
                VitrineEventDisposition.Succeeded => GraphNodeState.Succeeded,
                VitrineEventDisposition.Warning => GraphNodeState.Warning,
                VitrineEventDisposition.Blocked => GraphNodeState.Blocked,
                VitrineEventDisposition.Failed => GraphNodeState.Failed,
                VitrineEventDisposition.NotMeasured => GraphNodeState.NotMeasured,
                VitrineEventDisposition.NotApplicable => GraphNodeState.NotApplicable,
                VitrineEventDisposition.ExpectedDefectDetected => GraphNodeState.ExpectedDefectDetected,
                VitrineEventDisposition.SelfTestSucceeded => GraphNodeState.SelfTestSucceeded,
                VitrineEventDisposition.Neutral when item.Kind == "GateCompleted"
                    && node.Kind == "evaluation-diagnostic" => GraphNodeState.Warning,
                _ => node.State,
            };
            if (item.Kind is "LiveCheckCompleted" or "LiveTrialCompleted")
            {
                observedState = _liveAggregateStates.TryGetValue(node.Id, out var prior)
                    ? AggregateLiveState(prior, observedState)
                    : observedState;
                _liveAggregateStates[node.Id] = observedState;
            }
            node.State = observedState;
        }

        if (item.Disposition != VitrineEventDisposition.Active
            && !string.IsNullOrWhiteSpace(item.OperationId))
        {
            _activeOperationNodes.Remove(item.OperationId);
        }

        if (item.SourceId.Length > 0 && item.TargetId.Length > 0)
        {
            var edge = Edges.FirstOrDefault(candidate =>
                string.Equals(candidate.SourceId, item.SourceId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.TargetId, item.TargetId, StringComparison.OrdinalIgnoreCase));
            if (edge is not null)
            {
                edge.IsActive = true;
                if (isNewEvent && IsTraversalObservation(item)) edge.RecordTraversal();
            }
        }


        if (IsRunTerminal(item)) CompleteOutstandingNodes(item.Disposition);
        if (isNewEvent) ObserveWorkflowAction(item);
        AdvanceRevision();
    }

    private string ResolveNodeId(VitrineEvent item)
    {
        if (item.Kind is "BenchmarkCheckCompleted" or "BenchmarkPersisted" or "OfflineBenchmarkPersisted"
            or "SuiteCompleted" or "LiveSessionCompleted"
            or "LiveSafetyTargetObserved" or "LiveSafetyAttackObserved"
            or "LiveSafetyAttackFindingsObserved" or "LiveSafetyFindingsObserved"
            or "LiveSafetyFindingsCompleted"
            && item.TargetId.Length > 0)
        {
            return item.TargetId;
        }

        if (item.Disposition != VitrineEventDisposition.Active
            && !string.IsNullOrWhiteSpace(item.OperationId)
            && _activeOperationNodes.TryGetValue(item.OperationId, out var activeNodeId))
        {
            return activeNodeId;
        }

        return item.Disposition == VitrineEventDisposition.Active && item.TargetId.Length > 0
            ? item.TargetId
            : item.SourceId;
    }

    private static bool IsTraversalObservation(VitrineEvent item) => item.Kind is
        "RunStarted" or "ModelRequestStarted" or "ToolExecutionStarted" or "Route"
        or "GateStarted" or "BenchmarkCheckCompleted" or "BenchmarkPersisted"
        or "OfflineBenchmarkPersisted" or "SuiteCompleted" or "LiveTrialStarting"
        or "LiveCheckStarting" or "LiveSessionCompleted" or "LiveSafetyTargetRunning"
        or "LiveSafetyTargetObserved" or "LiveSafetyAttackObserved"
        or "LiveSafetyAttackFindingsObserved" or "LiveSafetyFindingsObserved"
        or "LiveSafetyFindingsCompleted";

    private static bool IsRunTerminal(VitrineEvent item) => item.Kind is
        "RunCompleted" or "RunCancelled" or "RunFailed" or "RunComplete" or "SuiteCompleted"
        or "LiveSessionCompleted";

    private static bool ShouldRecordExecution(VitrineEvent item, GraphNodeViewModel node)
    {
        if (node.Kind == "executor") return item.Kind == "NodeStarted";
        if (node.Kind == "evaluation-subject") return item.Kind == "LiveSubjectRunning";
        if (node.Kind == "evaluation-check" && item.Kind.StartsWith("Live", StringComparison.Ordinal))
            return item.Kind == "LiveCheckStarting";
        return !string.Equals(node.Id, "controls", StringComparison.OrdinalIgnoreCase);
    }

    private static GraphNodeState AggregateLiveState(GraphNodeState left, GraphNodeState right)
    {
        if (left == GraphNodeState.Failed || right == GraphNodeState.Failed) return GraphNodeState.Failed;
        if (left == GraphNodeState.NotMeasured || right == GraphNodeState.NotMeasured)
            return GraphNodeState.NotMeasured;
        if (left == GraphNodeState.Blocked || right == GraphNodeState.Blocked) return GraphNodeState.Blocked;
        if (left == GraphNodeState.Warning || right == GraphNodeState.Warning) return GraphNodeState.Warning;
        if (left == GraphNodeState.Succeeded || right == GraphNodeState.Succeeded)
            return GraphNodeState.Succeeded;
        return right;
    }

    private void CompleteOutstandingNodes(VitrineEventDisposition disposition)
    {
        var terminalState = disposition switch
        {
            VitrineEventDisposition.Failed => GraphNodeState.Failed,
            VitrineEventDisposition.Warning => GraphNodeState.Warning,
            VitrineEventDisposition.Blocked => GraphNodeState.Blocked,
            VitrineEventDisposition.NotMeasured => GraphNodeState.NotMeasured,
            VitrineEventDisposition.NotApplicable => GraphNodeState.NotApplicable,
            VitrineEventDisposition.ExpectedDefectDetected => GraphNodeState.ExpectedDefectDetected,
            VitrineEventDisposition.SelfTestSucceeded => GraphNodeState.SelfTestSucceeded,
            _ => GraphNodeState.Succeeded,
        };
        foreach (var active in Nodes.Where(static candidate => candidate.State == GraphNodeState.Active))
            active.State = terminalState;
        _activeOperationNodes.Clear();
    }

    private void ObserveWorkflowAction(VitrineEvent item)
    {
        if (!ShowActionShelf) return;
        switch (item.Kind)
        {
            case "NodeStarted":
                ObserveAction($"executor:{item.SourceId}", "EXECUTOR", item.SourceId, 1,
                    $"Started operation {item.OperationId ?? "not supplied"}.");
                break;
            case "Search":
                ObserveAction("search", "RETRIEVAL", "Catalogue Search", 1, item.Title);
                break;
            case "ModelRequestStarted":
                ObserveAction($"model:{item.SourceId}", "MODEL", $"{item.SourceId} model boundary",
                    1, "Request sent; expand the correlated timeline event to inspect its sanitized input.");
                break;
            case "ModelResponseReceived":
                ObserveAction($"model:{item.TargetId}", "MODEL", $"{item.TargetId} model boundary",
                    0, "Response received; expand the correlated timeline event to inspect its sanitized output.");
                break;
            case "NodeCompleted" when TryReadModelCalls(item.SanitizedPayload, out var calls) && calls > 0:
                if (Actions.All(action => !string.Equals(action.Id, $"model:{item.SourceId}", StringComparison.OrdinalIgnoreCase)))
                    ObserveAction($"model:{item.SourceId}", "MODEL", $"{item.SourceId} model boundary",
                        calls, $"The typed executor completion reported {calls} model call(s).");
                break;
        }
    }

    private void ObserveAction(string id, string kind, string label, int count, string detail)
    {
        var action = Actions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (action is null)
        {
            action = new(id, kind, label);
            Actions.Add(action);
        }
        action.Observe(count, detail);
        RaisePropertyChanged(nameof(ActionsSummary));
    }

    private static bool TryReadModelCalls(string? payload, out int count)
    {
        const string prefix = "model-calls=";
        count = 0;
        return payload is not null
            && payload.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(payload.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out count);
    }

    private void AdvanceRevision()
    {
        _graphRevision = unchecked(_graphRevision + 1);
        RaisePropertyChanged(nameof(GraphRevision));
    }
}
