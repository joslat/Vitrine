// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>One executor in the shipped discovery workflow.</summary>
public sealed record DiscoveryTopologyNode(
    string Id,
    int Order,
    bool IsEntry = false,
    bool IsExit = false);

/// <summary>One directed route in the shipped discovery workflow.</summary>
public sealed record DiscoveryTopologyRoute(
    string Id,
    string SourceExecutorId,
    string TargetExecutorId,
    string Label,
    bool IsConditional = false,
    bool IsLoopBack = false);

/// <summary>A runtime observation projected into the topology contract.</summary>
public sealed record DiscoveryTopologyObservation(
    IReadOnlyList<string> ExecutorIds,
    IReadOnlyList<(string SourceExecutorId, string TargetExecutorId)> Edges);

/// <summary>The result of comparing a runtime observation with the shipped topology.</summary>
public sealed record DiscoveryTopologyValidation(bool IsExact, string Detail);

/// <summary>
/// Subject-owned topology contract shared by workflow observers, evals, and the control-room graph.
/// </summary>
/// <remarks>
/// The executable MAF workflow remains authoritative for execution. This descriptor is the
/// independently inspectable contract observers use to reject a stale or flattering projection:
/// exactly five executors, five routes, and exactly one conditional loop-back edge.
/// </remarks>
public sealed class DiscoveryTopologyDescriptor
{
    private readonly IReadOnlyDictionary<string, DiscoveryTopologyRoute> _routesByEndpoints;

    public DiscoveryTopologyDescriptor(
        IReadOnlyList<DiscoveryTopologyNode> nodes,
        IReadOnlyList<DiscoveryTopologyRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(routes);

        Nodes = nodes.ToArray();
        Routes = routes.ToArray();
        if (Nodes.Count == 0) throw new ArgumentException("A topology needs at least one executor.", nameof(nodes));
        if (Nodes.Select(static node => node.Id).Distinct(StringComparer.Ordinal).Count() != Nodes.Count)
            throw new ArgumentException("Executor ids must be unique.", nameof(nodes));
        if (Routes.Select(static route => route.Id).Distinct(StringComparer.Ordinal).Count() != Routes.Count)
            throw new ArgumentException("Route ids must be unique.", nameof(routes));

        var knownNodes = Nodes.Select(static node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (Routes.Any(route => !knownNodes.Contains(route.SourceExecutorId) || !knownNodes.Contains(route.TargetExecutorId)))
            throw new ArgumentException("Every route endpoint must name an executor in the descriptor.", nameof(routes));

        _routesByEndpoints = Routes.ToDictionary(
            static route => EndpointKey(route.SourceExecutorId, route.TargetExecutorId),
            StringComparer.Ordinal);
    }

    public IReadOnlyList<DiscoveryTopologyNode> Nodes { get; }

    public IReadOnlyList<DiscoveryTopologyRoute> Routes { get; }

    /// <summary>Returns the canonical route for a runtime edge, failing closed on an unknown edge.</summary>
    public DiscoveryTopologyRoute RequireRoute(string sourceExecutorId, string targetExecutorId)
    {
        if (_routesByEndpoints.TryGetValue(EndpointKey(sourceExecutorId, targetExecutorId), out var route))
            return route;

        throw new InvalidOperationException(
            $"Prepared workflow edge '{sourceExecutorId}' -> '{targetExecutorId}' is absent from the discovery topology contract.");
    }

    /// <summary>Compares an observed runtime graph with this descriptor without trusting display metadata.</summary>
    public DiscoveryTopologyValidation Validate(DiscoveryTopologyObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.ExecutorIds);
        ArgumentNullException.ThrowIfNull(observation.Edges);

        var expectedExecutors = Nodes.OrderBy(static node => node.Order).Select(static node => node.Id).ToArray();
        if (!expectedExecutors.SequenceEqual(observation.ExecutorIds, StringComparer.Ordinal))
        {
            return new(false,
                $"Executor order differs: expected [{string.Join(", ", expectedExecutors)}], observed [{string.Join(", ", observation.ExecutorIds)}].");
        }

        var expectedEdges = Routes
            .Select(static route => EndpointKey(route.SourceExecutorId, route.TargetExecutorId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var observedEdges = observation.Edges
            .Select(static edge => EndpointKey(edge.SourceExecutorId, edge.TargetExecutorId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!expectedEdges.SequenceEqual(observedEdges, StringComparer.Ordinal))
        {
            return new(false,
                $"Edge set differs: expected [{string.Join(", ", expectedEdges)}], observed [{string.Join(", ", observedEdges)}].");
        }

        return new(true,
            $"Observed the exact {Nodes.Count}-executor/{Routes.Count}-route workflow with {Routes.Count(static route => route.IsLoopBack)} loop-back edge.");
    }

    /// <summary>Rejects a stale observation before it can be rendered as the live workflow.</summary>
    public void RequireExact(DiscoveryTopologyObservation observation)
    {
        var validation = Validate(observation);
        if (!validation.IsExact) throw new InvalidOperationException(validation.Detail);
    }

    private static string EndpointKey(string sourceExecutorId, string targetExecutorId) =>
        string.Concat(sourceExecutorId, "\u001f", targetExecutorId);
}

/// <summary>The exact topology shipped by <see cref="DiscoveryWorkflowFactory"/>.</summary>
public static class DiscoveryTopology
{
    public static DiscoveryTopologyDescriptor Shipped { get; } = new(
        [
            new(DiscoveryExecutorIds.InterestMapper, 1, IsEntry: true),
            new(DiscoveryExecutorIds.Discovery, 2),
            new(DiscoveryExecutorIds.CoverageReviewer, 3),
            new(DiscoveryExecutorIds.Ranker, 4),
            new(DiscoveryExecutorIds.Presenter, 5, IsExit: true),
        ],
        [
            new(DiscoveryRouteIds.MapToDiscovery, DiscoveryExecutorIds.InterestMapper,
                DiscoveryExecutorIds.Discovery, "interests mapped"),
            new(DiscoveryRouteIds.DiscoveryToReview, DiscoveryExecutorIds.Discovery,
                DiscoveryExecutorIds.CoverageReviewer, "candidates ready"),
            new(DiscoveryRouteIds.ReviewToMoreDiscovery, DiscoveryExecutorIds.CoverageReviewer,
                DiscoveryExecutorIds.Discovery, "gaps remain", IsConditional: true, IsLoopBack: true),
            new(DiscoveryRouteIds.ReviewToRanker, DiscoveryExecutorIds.CoverageReviewer,
                DiscoveryExecutorIds.Ranker, "coverage sufficient", IsConditional: true),
            new(DiscoveryRouteIds.RankerToPresenter, DiscoveryExecutorIds.Ranker,
                DiscoveryExecutorIds.Presenter, "ranked"),
        ]);
}
