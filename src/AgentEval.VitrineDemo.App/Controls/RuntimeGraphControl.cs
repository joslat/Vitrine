// SPDX-License-Identifier: MIT

using System.Globalization;
using AgentEval.VitrineDemo.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AgentEval.VitrineDemo.App.Controls;

/// <summary>Compact runtime graph renderer; the adjacent lists provide its semantic fallback.</summary>
public sealed class RuntimeGraphControl : Control
{
    public static readonly StyledProperty<GraphViewModel?> ModelProperty =
        AvaloniaProperty.Register<RuntimeGraphControl, GraphViewModel?>(nameof(Model));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<RuntimeGraphControl, int>(nameof(Revision));

    static RuntimeGraphControl()
    {
        AffectsRender<RuntimeGraphControl>(ModelProperty, RevisionProperty);
        AffectsMeasure<RuntimeGraphControl>(ModelProperty, RevisionProperty);
    }

    public GraphViewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = base.MeasureOverride(availableSize);
        if (Model is not { Nodes.Count: > 0 } model) return desired;
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0
            ? availableSize.Width
            : Bounds.Width > 0
                ? Bounds.Width
                : 600;
        return new Size(desired.Width, Math.Max(desired.Height, RequiredHeightForLayout(model, width)));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#091321")), Bounds);
        if (Model is not { Nodes.Count: > 0 } model)
        {
            DrawText(context, "No topology is available for this mode.", new Point(24, 28), "#7890AF", 13);
            return;
        }

        var positions = Layout(model);
        foreach (var edge in model.Edges)
        {
            if (!positions.TryGetValue(edge.SourceId, out var source) || !positions.TryGetValue(edge.TargetId, out var target)) continue;
            var active = edge.IsActive;
            var colour = active ? "#5AE4D2" : edge.IsLoopBack ? "#B77CFF" : "#526987";
            var brush = new SolidColorBrush(Color.Parse(colour));
            var pen = new Pen(brush, active ? 3 : 1.5);
            if (edge.IsLoopBack)
            {
                var sourceTop = new Point(source.X, source.Y - 29);
                var targetTop = new Point(target.X, target.Y - 29);
                var loopY = Math.Max(34, Math.Min(source.Y, target.Y) - 66);
                var geometry = new StreamGeometry();
                using (var stream = geometry.Open())
                {
                    stream.BeginFigure(sourceTop, false);
                    stream.LineTo(new Point(source.X, loopY));
                    stream.LineTo(new Point(target.X, loopY));
                    stream.LineTo(targetTop);
                }
                context.DrawGeometry(null, pen, geometry);
                DrawArrowHead(context, targetTop, new Vector(0, 1), brush);
                var backLabel = $"BACK · {edge.Label} · ×{edge.TraversalCount} observed";
                DrawText(context, backLabel,
                    new Point(Math.Min(source.X, target.X) + Math.Abs(source.X - target.X) * 0.18, loopY - 16),
                    colour, 8);
            }
            else
            {
                var sourceNode = model.Nodes.First(node => string.Equals(node.Id, edge.SourceId, StringComparison.OrdinalIgnoreCase));
                var targetNode = model.Nodes.First(node => string.Equals(node.Id, edge.TargetId, StringComparison.OrdinalIgnoreCase));
                if (IsDemo01Surface(model) && sourceNode.Kind == "agent" && targetNode.Kind == "tool")
                {
                    var route = Demo01ToolRoute(source, target);
                    foreach (var leg in route) context.DrawLine(pen, leg.Start, leg.End);
                    DrawArrowHead(context, route[^1].End, new Vector(0, 1), brush);
                    continue;
                }
                var segment = TrimmedSegment(source, target,
                    NodeWidth(sourceNode), 50, NodeWidth(targetNode), 50);
                context.DrawLine(pen, segment.Start, segment.End);
                DrawArrowHead(context, segment.End, segment.Direction, brush);
                if (model.Edges.Count <= 7)
                {
                    var label = edge.TraversalCount > 0
                        ? $"{edge.Label} · ×{edge.TraversalCount}"
                        : edge.Label;
                    DrawText(context, ClipText(label, 31),
                        new Point((segment.Start.X + segment.End.X) / 2 - 34,
                            (segment.Start.Y + segment.End.Y) / 2 - 15), colour, 8);
                }
            }
        }

        foreach (var node in model.Nodes)
        {
            if (!positions.TryGetValue(node.Id, out var center)) continue;
            var state = model.StateOf(node.Id);
            var fill = StateColor(state);
            var width = NodeWidth(node);
            var rect = new Rect(center.X - width / 2, center.Y - 25, width, 50);
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#101D30")), new Pen(new SolidColorBrush(Color.Parse(fill)), state == GraphNodeState.Active ? 3 : 1.5), rect, 8, 8);
            DrawText(context, ClipText(node.Label, LabelLimit(node)), new Point(rect.X + 10, rect.Y + 9), "#F0F5FF", 11);
            DrawText(context, node.CanvasStateText, new Point(rect.X + 10, rect.Y + 29), fill, 8);
            if (node.ExecutionCount > 0)
                DrawText(context, node.ExecutionBadgeText,
                    new Point(rect.Right - (node.Kind == "evaluation-controls" ? 43 : 27), rect.Y + 29),
                    "#9FB2CD", 8);
        }
    }

    internal static double RequiredHeightForLayout(GraphViewModel model, double availableWidth)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Nodes.Count == 0) return 0;
        var width = Math.Max(600, double.IsFinite(availableWidth) ? availableWidth : 600);
        var tools = model.Nodes.Count(static node => node.Kind == "tool");
        if (tools > 0)
        {
            var toolColumns = IsDemo01Surface(model) ? Demo01ToolColumnCount(width) : ToolColumnCount(width);
            var rows = (int)Math.Ceiling(tools / (double)toolColumns);
            var firstToolY = IsDemo01Surface(model) ? 230 : 145;
            // Demo01 reserves an unobstructed centre lane below the complete tool fan for the
            // model boundary. This keeps Robin→tool paths from crossing the model at 620 px.
            var trailingSpace = IsDemo01Surface(model) ? 135 : 45;
            return Math.Max(390, firstToolY + trailingSpace + Math.Max(0, rows - 1) * 72);
        }

        if (IsLiveEvaluationSurface(model))
            return IsSafetyEvaluationSurface(model) ? 570 : 480;

        var generalColumns = GeneralColumnCount(model, width);
        var generalRows = (int)Math.Ceiling(model.Nodes.Count / (double)generalColumns);
        // Fifty-pixel cards retain at least 28 px of vertical air for arrows and labels.
        return Math.Max(390, 100 + Math.Max(0, generalRows - 1) * 78);
    }

    private Dictionary<string, Point> Layout(GraphViewModel model) =>
        Layout(model, Bounds.Width, Bounds.Height);

    internal static Dictionary<string, Point> Layout(
        GraphViewModel model,
        double availableWidth,
        double availableHeight)
    {
        ArgumentNullException.ThrowIfNull(model);
        var width = Math.Max(600, availableWidth);
        var height = Math.Max(RequiredHeightForLayout(model, width), availableHeight);
        var result = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        var tools = model.Nodes.Where(node => node.Kind == "tool").ToArray();
        if (tools.Length > 0)
        {
            var principals = model.Nodes.Where(node => node.Kind != "tool").ToArray();
            if (IsDemo01Surface(model))
            {
                var customer = principals.First(static node => node.Id == "customer");
                var agent = principals.First(static node => node.Kind == "agent");
                var modelNode = principals.First(static node => node.Id == "model");
                var guardrails = principals.First(static node => node.Id == "guardrails");
                var agentX = width * 0.48;
                result[customer.Id] = new Point(100, 55);
                result[agent.Id] = new Point(agentX, 55);
                result[guardrails.Id] = new Point(width - 105, 55);
                var demoColumns = Demo01ToolColumnCount(width);
                var demoRows = (int)Math.Ceiling(tools.Length / (double)demoColumns);
                for (var i = 0; i < tools.Length; i++)
                {
                    var row = i / demoColumns;
                    var column = i % demoColumns;
                    result[tools[i].Id] = new Point(
                        105 + column * ((width - 210) / Math.Max(1, demoColumns - 1)),
                        230 + row * 72);
                }
                // Even tool columns leave the centre clear; putting the boundary after the last
                // tool row makes every Robin→tool segment terminate before the model rectangle.
                result[modelNode.Id] = new Point(width / 2, 230 + Math.Max(0, demoRows - 1) * 72 + 100);
                return result;
            }
            for (var i = 0; i < principals.Length; i++)
                result[principals[i].Id] = new Point(110 + i * ((width - 220) / Math.Max(1, principals.Length - 1)), 55);
            var columns = ToolColumnCount(width);
            for (var i = 0; i < tools.Length; i++)
            {
                var row = i / columns;
                var column = i % columns;
                result[tools[i].Id] = new Point(
                    105 + column * ((width - 210) / Math.Max(1, columns - 1)),
                    145 + row * 72);
            }
            return result;
        }


        if (IsLiveEvaluationSurface(model))
        {
            if (IsSafetyEvaluationSurface(model)) LayoutSafetyEvaluation(width, result);
            else LayoutLiveEvaluation(model, width, result);
            return result;
        }

        var rowColumns = GeneralColumnCount(model, width);
        var rows = (int)Math.Ceiling(model.Nodes.Count / (double)rowColumns);
        for (var i = 0; i < model.Nodes.Count; i++)
        {
            var row = i / rowColumns;
            var positionInRow = i % rowColumns;
            var column = row % 2 == 0 ? positionInRow : rowColumns - 1 - positionInRow;
            result[model.Nodes[i].Id] = new Point(
                100 + column * ((width - 200) / Math.Max(1, rowColumns - 1)),
                rows == 1 ? height * 0.56 : 55 + row * ((height - 110) / Math.Max(1, rows - 1)));
        }
        return result;
    }

    private static int ToolColumnCount(double width) =>
        Math.Min(4, Math.Max(1, (int)(width / 190)));

    // An even column count leaves the centre lane clear for Robin's model boundary.
    private static int Demo01ToolColumnCount(double width) => width >= 760 ? 4 : 2;

    private static bool IsDemo01Surface(GraphViewModel model) =>
        model.Nodes.Any(static node => node.Id == "customer")
        && model.Nodes.Any(static node => node.Id == "model")
        && model.Nodes.Any(static node => node.Id == "guardrails")
        && model.Nodes.Any(static node => node.Kind == "agent");

    private static bool IsLiveEvaluationSurface(GraphViewModel model) =>
        model.Nodes.Any(static node => node.Id == "live-session")
        && model.Nodes.Any(static node => node.Id == "live-persistence")
        && model.Nodes.Any(static node => node.Id == "live-complete");

    private static bool IsSafetyEvaluationSurface(GraphViewModel model) =>
        model.Nodes.Any(static node => node.Id == "live-safety-target");

    private static void LayoutSafetyEvaluation(double width, IDictionary<string, Point> result)
    {
        result["live-session"] = new Point(width / 2, 45);
        result["live-safety-target"] = new Point(width / 2, 130);
        result["live-safety-jailbreak"] = new Point(150, 235);
        result["live-safety-extraction"] = new Point(width - 150, 235);
        result["live-safety-findings"] = new Point(width / 2, 340);
        result["live-persistence"] = new Point(width / 2, 435);
        result["live-complete"] = new Point(width / 2, 525);
    }

    private static void LayoutLiveEvaluation(
        GraphViewModel model,
        double width,
        IDictionary<string, Point> result)
    {
        result["live-session"] = new Point(width / 2, 50);
        var comparison = model.Nodes.Any(static node =>
            node.Id.StartsWith("live-agent:", StringComparison.Ordinal));
        if (!comparison)
        {
            var subject = model.Nodes.First(static node => node.Kind == "evaluation-subject");
            result[subject.Id] = new Point(width / 2, 140);
            var checks = model.Nodes.Where(static node => node.Kind == "evaluation-check").ToArray();
            for (var index = 0; index < checks.Length; index++)
                result[checks[index].Id] = new Point(
                    100 + index * ((width - 200) / Math.Max(1, checks.Length - 1)), 250);
            result["live-persistence"] = new Point(width / 2, 365);
            result["live-complete"] = new Point(width / 2, 445);
            return;
        }

        var agentX = width * 0.25;
        var workflowX = width * 0.75;
        result["live-agent"] = new Point(agentX, 140);
        result["live-workflow"] = new Point(workflowX, 140);
        LayoutComparisonChecks(model, result, "live-agent:", 65, width / 2 - 65);
        LayoutComparisonChecks(model, result, "live-workflow:", width / 2 + 65, width - 65);
        result["live-persistence"] = new Point(width / 2, 365);
        result["live-complete"] = new Point(width / 2, 445);
    }

    private static void LayoutComparisonChecks(
        GraphViewModel model,
        IDictionary<string, Point> result,
        string prefix,
        double startX,
        double endX)
    {
        var checks = model.Nodes.Where(node => node.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        for (var index = 0; index < checks.Length; index++)
            result[checks[index].Id] = new Point(
                startX + index * ((endX - startX) / Math.Max(1, checks.Length - 1)), 250);
    }

    private static int GeneralColumnCount(GraphViewModel model, double width)
    {
        var desired = Math.Max(1, (int)((width - 40) / (NodeWidth(model.Nodes[0]) + 28)));
        return Math.Clamp(desired, 1, model.Nodes.Count);
    }

    private static string StateColor(GraphNodeState state) => state switch
    {
        GraphNodeState.Active => "#5AE4D2",
        GraphNodeState.Succeeded => "#63D391",
        GraphNodeState.Warning => "#F6C55C",
        GraphNodeState.Blocked => "#F3A85D",
        GraphNodeState.Failed => "#F07076",
        GraphNodeState.NotMeasured => "#F6C55C",
        GraphNodeState.NotApplicable => "#9FC5FF",
        GraphNodeState.ExpectedDefectDetected or GraphNodeState.SelfTestSucceeded => "#63D391",
        _ => "#50647F",
    };

    private static double NodeWidth(GraphNodeViewModel node) => node.Id switch
    {
        "live-session" => 220d,
        _ when node.Id.StartsWith("live-agent:", StringComparison.Ordinal)
            || node.Id.StartsWith("live-workflow:", StringComparison.Ordinal) => 124d,
        _ when node.Kind == "tool" => 150d,
        _ => 166d,
    };

    private static int LabelLimit(GraphNodeViewModel node) => node.Id switch
    {
        _ when node.Id.StartsWith("live-agent:", StringComparison.Ordinal)
            || node.Id.StartsWith("live-workflow:", StringComparison.Ordinal) => 16,
        _ when node.Kind == "tool" => 20,
        "live-session" => 31,
        _ => 24,
    };

    internal static (Point Start, Point End, Vector Direction) TrimmedSegment(
        Point source,
        Point target,
        double sourceWidth,
        double sourceHeight,
        double targetWidth,
        double targetHeight)
    {
        var delta = target - source;
        var length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (length < 0.001) return (source, target, new Vector(1, 0));
        var direction = new Vector(delta.X / length, delta.Y / length);
        var startInset = RectangleBoundaryDistance(direction, sourceWidth, sourceHeight) + 4;
        var endInset = RectangleBoundaryDistance(direction, targetWidth, targetHeight) + 4;
        if (startInset + endInset >= length)
        {
            startInset = length * 0.42;
            endInset = length * 0.42;
        }
        return (
            source + direction * startInset,
            target - direction * endInset,
            direction);
    }

    /// <summary>
    /// Routes the dense Demo01 function fan through two clear side buses. Horizontal legs sit in
    /// the 22-pixel gap between tool rows, so later calls never disappear beneath an earlier card.
    /// </summary>
    internal static IReadOnlyList<(Point Start, Point End)> Demo01ToolRoute(Point source, Point target)
    {
        var side = target.X < source.X ? -1d : 1d;
        var start = new Point(source.X + side * 20, source.Y + 29);
        var bus = new Point(source.X + side * 44, source.Y + 50);
        var rowApproach = new Point(bus.X, target.Y - 36);
        var aboveTarget = new Point(target.X, target.Y - 36);
        var tip = new Point(target.X, target.Y - 29);
        return
        [
            (start, bus),
            (bus, rowApproach),
            (rowApproach, aboveTarget),
            (aboveTarget, tip),
        ];
    }

    private static double RectangleBoundaryDistance(Vector direction, double width, double height)
    {
        var horizontal = Math.Abs(direction.X) < 0.0001
            ? double.PositiveInfinity
            : width / 2 / Math.Abs(direction.X);
        var vertical = Math.Abs(direction.Y) < 0.0001
            ? double.PositiveInfinity
            : height / 2 / Math.Abs(direction.Y);
        return Math.Min(horizontal, vertical);
    }

    private static void DrawArrowHead(DrawingContext context, Point tip, Vector direction, IBrush brush)
    {
        var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (length < 0.001) return;
        var unit = new Vector(direction.X / length, direction.Y / length);
        var normal = new Vector(-unit.Y, unit.X);
        var baseCenter = tip - unit * 9;
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(tip, true);
            stream.LineTo(baseCenter + normal * 4.5);
            stream.LineTo(baseCenter - normal * 4.5);
            stream.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawText(DrawingContext context, string value, Point origin, string colour, double size)
    {
        var text = new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter"),
            size,
            new SolidColorBrush(Color.Parse(colour)));
        context.DrawText(text, origin);
    }

    private static string ClipText(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
