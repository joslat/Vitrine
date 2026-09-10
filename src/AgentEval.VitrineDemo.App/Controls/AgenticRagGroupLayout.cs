// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.ViewModels;
using Avalonia;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.App.Controls;

/// <summary>
/// Computes the render-only enclosure for Demo02's bounded retrieve/critique loop.
/// It deliberately does not add a graph node or route to the persisted topology.
/// </summary>
internal static class AgenticRagGroupLayout
{
    internal const string Label = "BOUNDED AGENTIC RAG · RETRIEVE ↔ CRITIQUE";
    internal const string AccessibleDescription =
        "Bounded Agentic RAG: Discovery retrieves candidates and CoverageReviewer critiques coverage; actionable gaps return to Discovery within the configured round limit.";

    private const double CardHalfWidth = 83;
    private const double CardHalfHeight = 25;
    private const double HorizontalPadding = 18;
    private const double VerticalPadding = 18;
    private const double MinimumWidth = 310;
    private const double CanvasInset = 12;
    private const double RouteClearance = 4;

    internal static AgenticRagGroupGeometry? TryCreate(
        GraphViewModel model,
        IReadOnlyDictionary<string, Point> positions,
        double availableWidth)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(positions);

        if (!HasRoute(model, DiscoveryExecutorIds.Discovery, DiscoveryExecutorIds.CoverageReviewer, loopBack: false)
            || !HasRoute(model, DiscoveryExecutorIds.CoverageReviewer, DiscoveryExecutorIds.Discovery, loopBack: true)
            || !positions.TryGetValue(DiscoveryExecutorIds.Discovery, out var discovery)
            || !positions.TryGetValue(DiscoveryExecutorIds.CoverageReviewer, out var reviewer))
        {
            return null;
        }

        var canvasWidth = Math.Max(600, double.IsFinite(availableWidth) ? availableWidth : 600);
        var naturalLeft = Math.Min(discovery.X, reviewer.X) - CardHalfWidth - HorizontalPadding;
        var naturalRight = Math.Max(discovery.X, reviewer.X) + CardHalfWidth + HorizontalPadding;
        var width = Math.Max(MinimumWidth, naturalRight - naturalLeft);
        var centre = (naturalLeft + naturalRight) / 2;
        var left = centre - width / 2;
        var right = centre + width / 2;

        if (left < CanvasInset)
        {
            right += CanvasInset - left;
            left = CanvasInset;
        }
        if (right > canvasWidth - CanvasInset)
        {
            left -= right - (canvasWidth - CanvasInset);
            right = canvasWidth - CanvasInset;
        }

        var loopY = Math.Max(34, Math.Min(discovery.Y, reviewer.Y) - 66);
        var top = Math.Max(8,
            Math.Min(loopY - 38,
                Math.Min(discovery.Y, reviewer.Y) - CardHalfHeight - VerticalPadding));
        var bottom = Math.Max(discovery.Y, reviewer.Y) + CardHalfHeight + VerticalPadding;
        var bounds = new Rect(left, top, right - left, bottom - top);
        var feedbackRoute = BuildFeedbackRoute(discovery, reviewer, bounds, loopY);
        var loopLabelOrigin = Math.Abs(discovery.X - reviewer.X) >= 80
            ? new Point(
                Math.Min(discovery.X, reviewer.X) + Math.Abs(discovery.X - reviewer.X) * 0.62,
                loopY - 15)
            : new Point(bounds.X + 17, (discovery.Y + reviewer.Y) / 2 - 5);
        return new(bounds, new Point(bounds.X + 17, bounds.Y + 2), loopY, loopLabelOrigin,
            feedbackRoute);
    }

    private static IReadOnlyList<Point> BuildFeedbackRoute(
        Point discovery,
        Point reviewer,
        Rect bounds,
        double loopY)
    {
        if (Math.Abs(discovery.X - reviewer.X) >= CardHalfWidth * 2 + HorizontalPadding)
        {
            return
            [
                new Point(reviewer.X, reviewer.Y - CardHalfHeight - RouteClearance),
                new Point(reviewer.X, loopY),
                new Point(discovery.X, loopY),
                new Point(discovery.X, discovery.Y - CardHalfHeight - RouteClearance),
            ];
        }

        // At compact widths the retrieve and critique cards occupy one column. A top loop would
        // collapse onto the forward edge, so use whichever side of the enclosure has more room.
        var leftCardEdge = Math.Min(discovery.X, reviewer.X) - CardHalfWidth;
        var rightCardEdge = Math.Max(discovery.X, reviewer.X) + CardHalfWidth;
        var useLeft = leftCardEdge - bounds.Left >= bounds.Right - rightCardEdge;
        var direction = useLeft ? -1d : 1d;
        var busX = useLeft
            ? Math.Max(bounds.Left + RouteClearance, leftCardEdge - HorizontalPadding)
            : Math.Min(bounds.Right - RouteClearance, rightCardEdge + HorizontalPadding);
        var sideOffset = direction * (CardHalfWidth + RouteClearance);

        return
        [
            new Point(reviewer.X + sideOffset, reviewer.Y),
            new Point(busX, reviewer.Y),
            new Point(busX, discovery.Y),
            new Point(discovery.X + sideOffset, discovery.Y),
        ];
    }

    private static bool HasRoute(GraphViewModel model, string sourceId, string targetId, bool loopBack) =>
        model.Edges.Any(edge =>
            string.Equals(edge.SourceId, sourceId, StringComparison.Ordinal)
            && string.Equals(edge.TargetId, targetId, StringComparison.Ordinal)
            && edge.IsLoopBack == loopBack);
}

internal readonly record struct AgenticRagGroupGeometry(
    Rect Bounds,
    Point LabelOrigin,
    double LoopY,
    Point LoopLabelOrigin,
    IReadOnlyList<Point> FeedbackRoute);
