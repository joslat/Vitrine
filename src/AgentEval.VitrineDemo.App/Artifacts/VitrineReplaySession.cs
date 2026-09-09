// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Models;

namespace AgentEval.VitrineDemo.App.Artifacts;

/// <summary>Pure cursor over immutable artifact events. It has no execution-service dependency.</summary>
public sealed class VitrineReplaySession
{
    public VitrineReplaySession(VitrineRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Artifact = VitrineArtifactSerializer.Freeze(artifact);
        if (!VitrineArtifactSerializer.Verify(Artifact)) throw new InvalidDataException("Artifact integrity verification failed.");
    }

    public VitrineRunArtifact Artifact { get; }
    public int Position { get; private set; } = -1;
    public VitrineEvent? Current => Position >= 0 && Position < Artifact.Events.Count ? Artifact.Events[Position] : null;
    public bool CanMoveNext => Position + 1 < Artifact.Events.Count;
    public bool CanMovePrevious => Position >= 0;

    public VitrineEvent? MoveNext()
    {
        if (CanMoveNext) Position++;
        return Current;
    }

    public VitrineEvent? MovePrevious()
    {
        if (Position >= 0) Position--;
        return Current;
    }

    public void Reset() => Position = -1;
}
