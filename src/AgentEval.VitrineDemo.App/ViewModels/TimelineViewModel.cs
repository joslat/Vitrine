// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;

namespace AgentEval.VitrineDemo.App.ViewModels;

public enum TimelineFilter
{
    Story,
    Tools,
    Guards,
    Evals,
    Debug,
}

public sealed record EventPayloadSectionViewModel(string Label, string Text, long Sequence);

public sealed class EventCardViewModel : BindableBase
{
    private bool _isVisible = true;
    private bool _isExpanded;
    private VitrineEventDisposition _effectiveDisposition;
    private string? _terminalPayloadAbsenceKind;

    public EventCardViewModel(VitrineEvent item)
    {
        Event = item;
        _effectiveDisposition = item.Disposition;
        ToggleExpandedCommand = new RelayCommand(ToggleExpanded, () => HasExpandableContent);
        RebuildPayloadSections([item]);
    }

    public VitrineEvent Event { get; }
    public RelayCommand ToggleExpandedCommand { get; }
    public ObservableCollection<EventPayloadSectionViewModel> PayloadSections { get; } = [];
    public long Sequence => Event.Sequence;
    public string Category => Event.Category.ToString().ToUpperInvariant();
    public string Kind => Event.Kind;
    public string Disposition => Event.Kind == "ControlBrokenCompleted"
        && _effectiveDisposition == VitrineEventDisposition.Succeeded
            ? "DETECTED"
            : _effectiveDisposition switch
            {
                VitrineEventDisposition.Active => "STARTED",
                VitrineEventDisposition.Succeeded => "DONE",
                VitrineEventDisposition.NotApplicable => "NOT APPLICABLE",
                VitrineEventDisposition.ExpectedDefectDetected => "EXPECTED DEFECT DETECTED",
                VitrineEventDisposition.SelfTestSucceeded => "SELF-TEST SUCCEEDED",
                _ => _effectiveDisposition.ToString().ToUpperInvariant(),
            };
    public string DispositionColor => _effectiveDisposition switch
    {
        VitrineEventDisposition.Succeeded => "#63D391",
        VitrineEventDisposition.Failed => "#F07076",
        VitrineEventDisposition.Warning or VitrineEventDisposition.NotMeasured => "#F6C55C",
        VitrineEventDisposition.NotApplicable => "#9FC5FF",
        VitrineEventDisposition.ExpectedDefectDetected or VitrineEventDisposition.SelfTestSucceeded => "#63D391",
        VitrineEventDisposition.Blocked => "#F3A85D",
        VitrineEventDisposition.Active => "#5AE4D2",
        _ => "#7184A1",
    };
    public string Route => string.IsNullOrWhiteSpace(Event.TargetId) ? Event.SourceId : $"{Event.SourceId} → {Event.TargetId}";
    public string Title => Event.Title;
    public string Detail => Event.Detail;
    public string Elapsed => $"+{Event.Elapsed.TotalSeconds:0.000}s";
    public string OperationId => string.IsNullOrWhiteSpace(Event.OperationId)
        ? "No operation id"
        : $"operation {Event.OperationId}";
    public bool HasOperationId => !string.IsNullOrWhiteSpace(Event.OperationId);
    public bool HasExpandableContent => PayloadSections.Count > 0;
    public bool HasNoExpandableContent => !HasExpandableContent;
    public bool HasPayloadAbsenceExplanation => HasNoExpandableContent || TerminalResponseIsAbsent;
    public string PayloadAbsenceExplanation => TerminalResponseIsAbsent
        ? _terminalPayloadAbsenceKind is "ToolCancelled" or "ToolFailed"
            ? "No response payload was produced: the tool did not return a result. The terminal state and bounded evidence above are the observable result."
            : "No response payload was produced: the model request did not return a model response. The terminal state and bounded evidence above are the observable result."
        : "This event records a typed state transition; no request, response, or tool payload applies.";
    public bool IsCollapsed => !IsExpanded;
    public bool IsExpandControlVisible => HasExpandableContent && IsCollapsed;
    public string ExpandControlText => $"{(IsExpanded ? "▼" : "▶")}  {PayloadSummaryLabel}";
    public string PayloadSummaryLabel => PayloadSections.Count > 1
        ? "REQUEST + RESPONSE"
        : PayloadSections.FirstOrDefault()?.Label ?? "DETAIL";

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            RaisePropertyChanged(nameof(IsCollapsed));
            RaisePropertyChanged(nameof(IsExpandControlVisible));
            RaisePropertyChanged(nameof(ExpandControlText));
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    internal void CorrelateWith(EventCardViewModel other)
    {
        ArgumentNullException.ThrowIfNull(other);
        CorrelateWith([other]);
    }

    internal void CorrelateWith(IEnumerable<EventCardViewModel> others)
    {
        ArgumentNullException.ThrowIfNull(others);
        var cards = others.Append(this).Distinct().ToArray();
        var ordered = cards.Select(static card => card.Event)
            .OrderBy(static item => item.Sequence).ToArray();
        var terminal = ordered.LastOrDefault(static item => item.Disposition != VitrineEventDisposition.Active);
        foreach (var card in cards)
        {
            card.RebuildPayloadSections(ordered);
            if (terminal is not null) card.SetEffectiveDisposition(terminal.Disposition);
        }
    }

    private void RebuildPayloadSections(IEnumerable<VitrineEvent> events)
    {
        var ordered = events.OrderBy(static item => item.Sequence).ToArray();
        _terminalPayloadAbsenceKind = ordered.LastOrDefault(static item => item.Kind is
            "ModelRequestCancelled" or "ModelRequestFailed" or "ToolCancelled" or "ToolFailed")?.Kind;
        PayloadSections.Clear();
        foreach (var item in ordered)
        {
            if (string.IsNullOrWhiteSpace(item.SanitizedPayload)) continue;
            PayloadSections.Add(new(PayloadLabel(item), DisplayPayload(item), item.Sequence));
        }
        ToggleExpandedCommand.RaiseCanExecuteChanged();
        if (!HasExpandableContent) _isExpanded = false;
        RaisePropertyChanged(nameof(HasExpandableContent));
        RaisePropertyChanged(nameof(HasNoExpandableContent));
        RaisePropertyChanged(nameof(HasPayloadAbsenceExplanation));
        RaisePropertyChanged(nameof(PayloadAbsenceExplanation));
        RaisePropertyChanged(nameof(IsExpandControlVisible));
        RaisePropertyChanged(nameof(PayloadSummaryLabel));
        RaisePropertyChanged(nameof(ExpandControlText));
    }

    private bool TerminalResponseIsAbsent => _terminalPayloadAbsenceKind is not null;

    private void SetEffectiveDisposition(VitrineEventDisposition disposition)
    {
        if (_effectiveDisposition == disposition) return;
        _effectiveDisposition = disposition;
        RaisePropertyChanged(nameof(Disposition));
        RaisePropertyChanged(nameof(DispositionColor));
    }

    private void ToggleExpanded()
    {
        if (HasExpandableContent) IsExpanded = !IsExpanded;
    }

    private static string PayloadLabel(VitrineEvent item) => item.Kind switch
    {
        "ModelRequestStarted" => "MODEL INPUT",
        "ModelResponseReceived" => "MODEL OUTPUT",
        "ModelRequestCancelled" => "MODEL CANCELLED",
        "ModelRequestFailed" => "MODEL FAILURE",
        "ToolExecutionStarted" => "TOOL PARAMETERS",
        "ToolCompleted" => "TOOL RESPONSE",
        "Search" => "SEARCH EVIDENCE",
        "NodeCompleted" => "EXECUTOR MEASUREMENT",
        "InterestMap" => "INTEREST MAP",
        "CoverageLedger" => "COVERAGE EVIDENCE",
        "Ranked" => "RANKING EVIDENCE",
        _ when item.Category == VitrineEventCategory.Evaluation => "EVALUATION EVIDENCE",
        _ => "OBSERVABLE PAYLOAD",
    };

    private static string DisplayPayload(VitrineEvent item)
    {
        const string modelCallPrefix = "model-calls=";
        return item.Kind == "NodeCompleted"
            && item.SanitizedPayload?.StartsWith(modelCallPrefix, StringComparison.Ordinal) == true
                ? $"Typed executor measurement: {item.SanitizedPayload[modelCallPrefix.Length..]} model call(s)."
                : item.SanitizedPayload ?? string.Empty;
    }
}

public sealed class TimelineViewModel : BindableBase
{
    private TimelineFilter _selectedFilter = TimelineFilter.Debug;
    private EventCardViewModel? _selectedEvent;
    private bool _followEvents = true;
    private readonly Dictionary<string, EventCardViewModel> _openOperations = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<EventCardViewModel> Events { get; } = [];
    public IReadOnlyList<TimelineFilter> Filters { get; } = Enum.GetValues<TimelineFilter>();

    public TimelineFilter SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value)) ApplyFilter();
        }
    }

    public EventCardViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!SetProperty(ref _selectedEvent, value)) return;
            RaisePropertyChanged(nameof(HasSelectedEvent));
        }
    }

    public bool HasSelectedEvent => SelectedEvent is not null;

    public bool FollowEvents
    {
        get => _followEvents;
        set => SetProperty(ref _followEvents, value);
    }

    public int VisibleCount => Events.Count(item => item.IsVisible);

    public void Clear()
    {
        Events.Clear();
        _openOperations.Clear();
        SelectedEvent = null;
        RaisePropertyChanged(nameof(VisibleCount));
    }

    public void Add(VitrineEvent item)
    {
        var card = new EventCardViewModel(item) { IsVisible = IsVisible(item) };
        Events.Add(card);
        CorrelateOperation(card);
        if (card.IsVisible && FollowEvents) SelectedEvent = card;
        RaisePropertyChanged(nameof(VisibleCount));
    }

    private void CorrelateOperation(EventCardViewModel card)
    {
        var operationId = card.Event.OperationId;
        if (string.IsNullOrWhiteSpace(operationId)) return;

        if (card.Event.Disposition == VitrineEventDisposition.Active)
        {
            _openOperations[operationId] = card;
            return;
        }

        var startedCards = CompletionOperationIds(card.Event)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => _openOperations.Remove(id, out var started) ? started : null)
            .Where(static started => started is not null)
            .Cast<EventCardViewModel>()
            .ToArray();
        if (startedCards.Length > 0) card.CorrelateWith(startedCards);
    }

    private static IEnumerable<string> CompletionOperationIds(VitrineEvent item)
    {
        if (!string.IsNullOrWhiteSpace(item.OperationId)) yield return item.OperationId;
        // Persisting is a distinct observable lifecycle, but its only terminal source event is
        // the overall live-session completion. Close both cards with that one typed terminal fact.
        if (string.Equals(item.Kind, VitrineEventAdapters.LiveSessionCompletedKind, StringComparison.Ordinal))
            yield return VitrineEventAdapters.LivePersistenceOperationId;
    }

    private void ApplyFilter()
    {
        foreach (var item in Events) item.IsVisible = IsVisible(item.Event);
        if (SelectedEvent is { IsVisible: false })
            SelectedEvent = FollowEvents
                ? Events.LastOrDefault(static item => item.IsVisible)
                : null;
        RaisePropertyChanged(nameof(VisibleCount));
    }

    private bool IsVisible(VitrineEvent item) => SelectedFilter switch
    {
        TimelineFilter.Story => item.Category is VitrineEventCategory.Agent or VitrineEventCategory.Workflow or VitrineEventCategory.Retrieval or VitrineEventCategory.System,
        TimelineFilter.Tools => item.Category is VitrineEventCategory.Tool or VitrineEventCategory.Model,
        TimelineFilter.Guards => item.Category == VitrineEventCategory.Guardrail,
        TimelineFilter.Evals => item.Category == VitrineEventCategory.Evaluation,
        TimelineFilter.Debug => true,
        _ => true,
    };
}
