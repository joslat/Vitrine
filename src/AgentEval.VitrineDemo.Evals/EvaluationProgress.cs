// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals;
public enum EvaluationProgressKind { SuiteStarted, GateStarted, GateCompleted, BenchmarkCheckCompleted, BenchmarkPersisted, ControlStarted,
    ControlHealthyCompleted, ControlBrokenCompleted, ControlRestoredCompleted, ControlCompleted, SuiteCompleted }
public enum EvaluationProgressExpectation { None, CatalogueDefectDetection, CatalogueSelfTestSucceeded }
public sealed record EvaluationProgressEvent(EvaluationProgressKind Kind, string Id, string Name, string Detail,
    bool? Passed = null, GateResult? Gate = null, ControlResult? Control = null, int? Completed = null,
    int? Total = null, DateTimeOffset? OccurredAtUtc = null, bool IncludesDiagnosticControls = false,
    EvaluationProgressExpectation Expectation = EvaluationProgressExpectation.None,
    GateAuthority? Authority = null) {
    public DateTimeOffset TimestampUtc { get; } = OccurredAtUtc ?? DateTimeOffset.UtcNow;
}
public interface IEvaluationProgressSink
{ void Publish(EvaluationProgressEvent progressEvent); }
public sealed class NullEvaluationProgressSink : IEvaluationProgressSink {
    private NullEvaluationProgressSink() { }
    public static NullEvaluationProgressSink Instance { get; } = new();
    public void Publish(EvaluationProgressEvent progressEvent) { }
}
public sealed class RecordingEvaluationProgressSink : IEvaluationProgressSink {
    private readonly Lock _gate = new();
    private readonly List<EvaluationProgressEvent> _events = [];
    public IReadOnlyList<EvaluationProgressEvent> Events {
        get { lock (_gate) return _events.ToArray(); }
    }
    public void Publish(EvaluationProgressEvent progressEvent) {
        ArgumentNullException.ThrowIfNull(progressEvent);
        lock (_gate) _events.Add(progressEvent);
    }
}
public sealed class CallbackEvaluationProgressSink(Action<EvaluationProgressEvent> callback)
    : IEvaluationProgressSink {
    private readonly Action<EvaluationProgressEvent> _callback =
        callback ?? throw new ArgumentNullException(nameof(callback));
    public void Publish(EvaluationProgressEvent progressEvent) => _callback(progressEvent);
}
internal static class EvaluationProgressSinkExtensions {
    public static void PublishSafely(this IEvaluationProgressSink? sink, EvaluationProgressEvent progressEvent) {
        try {
            EvaluationReportBoundary.EnsureSafe(progressEvent);
            (sink ?? NullEvaluationProgressSink.Instance).Publish(progressEvent);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) {
        }
    }
}
