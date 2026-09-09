// SPDX-License-Identifier: MIT

using System.Diagnostics;
using AgentEval.VitrineDemo.App.Models;

namespace AgentEval.VitrineDemo.App.Runtime;

/// <summary>Thread-safe append-only authoritative event store for one run.</summary>
public sealed class VitrineEventStore
{
    private readonly Lock _gate = new();
    private readonly List<VitrineEvent> _events = [];
    private readonly Queue<VitrineEvent> _pendingNotifications = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _sequence;
    private bool _notificationDrainActive;

    public VitrineEventStore(Guid? runId = null) => RunId = runId ?? Guid.NewGuid();

    public Guid RunId { get; }

    public event Action<VitrineEvent>? EventAppended;

    public IReadOnlyList<VitrineEvent> Snapshot()
    {
        lock (_gate) return _events.ToArray();
    }

    public VitrineEvent Append(VitrineEventDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        VitrineEvent item;
        var shouldDrainNotifications = false;
        lock (_gate)
        {
            item = new VitrineEvent(
                RunId,
                ++_sequence,
                draft.TimestampUtc ?? DateTimeOffset.UtcNow,
                _clock.Elapsed,
                draft.Category,
                PayloadPreviewPolicy.Sanitize(draft.Kind),
                draft.Disposition,
                PayloadPreviewPolicy.Sanitize(draft.SourceId),
                PayloadPreviewPolicy.Sanitize(draft.TargetId),
                string.IsNullOrWhiteSpace(draft.OperationId) ? null : PayloadPreviewPolicy.Sanitize(draft.OperationId),
                PayloadPreviewPolicy.Sanitize(draft.Title),
                PayloadPreviewPolicy.Sanitize(draft.Detail),
                string.IsNullOrWhiteSpace(draft.Payload) ? null : PayloadPreviewPolicy.Sanitize(draft.Payload));
            _events.Add(item);
            _pendingNotifications.Enqueue(item);
            if (!_notificationDrainActive)
            {
                _notificationDrainActive = true;
                shouldDrainNotifications = true;
            }
        }

        if (shouldDrainNotifications) DrainNotifications();
        return item;
    }

    private void DrainNotifications()
    {
        while (true)
        {
            VitrineEvent item;
            lock (_gate)
            {
                if (_pendingNotifications.Count == 0)
                {
                    _notificationDrainActive = false;
                    return;
                }

                item = _pendingNotifications.Dequeue();
            }

            NotifySafely(item);
        }
    }

    private void NotifySafely(VitrineEvent item)
    {
        var handlers = EventAppended;
        if (handlers is null) return;
        foreach (var callback in handlers.GetInvocationList().Cast<Action<VitrineEvent>>())
        {
            try { callback(item); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
    }
}
