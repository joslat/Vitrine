// SPDX-License-Identifier: MIT

namespace AgentEval.VitrineDemo.App.Models;

public enum VitrineGraphSource
{
    RegisteredFunctionsPreview,
    RegisteredFunctions,
    DiscoveryTopologyPreview,
    MafWorkflow,
    EvaluationServicePreview,
    EvaluationService,
}

public sealed record VitrineGraphNode(
    string Id,
    string Label,
    string Kind,
    string? Description = null,
    bool IsEntry = false,
    bool IsExit = false);

public sealed record VitrineGraphEdge(
    string Id,
    string SourceId,
    string TargetId,
    string Label,
    bool IsConditional = false,
    bool IsLoopBack = false);

/// <summary>Immutable topology snapshot whose source states how it was obtained.</summary>
public sealed record VitrineGraphSnapshot(
    string Name,
    VitrineGraphSource Source,
    IReadOnlyList<VitrineGraphNode> Nodes,
    IReadOnlyList<VitrineGraphEdge> Edges,
    Guid? RuntimeSourceId = null)
{
    public static VitrineGraphSnapshot Empty(string name) =>
        new(name, VitrineGraphSource.EvaluationService, [], []);
}
