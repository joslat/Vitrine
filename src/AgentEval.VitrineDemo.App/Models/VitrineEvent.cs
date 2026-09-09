// SPDX-License-Identifier: MIT

namespace AgentEval.VitrineDemo.App.Models;

public enum VitrineEventCategory
{
    Agent,
    Model,
    Tool,
    Workflow,
    Retrieval,
    Guardrail,
    Evaluation,
    System,
}

public enum VitrineEventDisposition
{
    Neutral,
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

/// <summary>Immutable normalized event stored by the control room.</summary>
public sealed record VitrineEvent(
    Guid RunId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    TimeSpan Elapsed,
    VitrineEventCategory Category,
    string Kind,
    VitrineEventDisposition Disposition,
    string SourceId,
    string TargetId,
    string? OperationId,
    string Title,
    string Detail,
    string? SanitizedPayload);

/// <summary>Unsequenced event accepted at the app boundary.</summary>
public sealed record VitrineEventDraft(
    VitrineEventCategory Category,
    string Kind,
    VitrineEventDisposition Disposition,
    string SourceId,
    string TargetId,
    string Title,
    string Detail,
    string? OperationId = null,
    string? Payload = null,
    DateTimeOffset? TimestampUtc = null);
