// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using System.Text.Json;
using Galaxus.RecommendationAgent.Agents;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Observability;

/// <summary>
/// Transparent decorator that emits a tool start before invoking the real function and emits its
/// completion afterward. Metadata and invocation remain MEAI's; observation changes neither.
/// </summary>
public sealed class ObservedAIFunction : DelegatingAIFunction
{
    private readonly IRecommendationRuntimeEventSink _events;

    public ObservedAIFunction(AIFunction inner, IRecommendationRuntimeEventSink events)
        : base(inner)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        _events.EmitSafely(new(
            RecommendationRuntimeEventKind.ToolExecutionStarted,
            RecommendationAgentFactory.AgentName,
            Name,
            $"{Name} started",
            "The real registered MEAI function is executing.",
            operationId,
            RenderArguments(arguments)));

        try
        {
            var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
            _events.EmitSafely(new(
                RecommendationRuntimeEventKind.ToolCompleted,
                Name,
                RecommendationAgentFactory.AgentName,
                $"{Name} completed",
                $"The function returned after {stopwatch.Elapsed.TotalMilliseconds:0} ms.",
                operationId,
                RenderValue(result)));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _events.EmitSafely(new(
                RecommendationRuntimeEventKind.ToolCancelled,
                Name,
                RecommendationAgentFactory.AgentName,
                $"{Name} cancelled",
                "The function observed cancellation.",
                operationId));
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _events.EmitSafely(new(
                RecommendationRuntimeEventKind.ToolFailed,
                Name,
                RecommendationAgentFactory.AgentName,
                $"{Name} failed",
                $"The function failed with {exception.GetType().Name}; its message is intentionally not captured.",
                operationId));
            throw;
        }
    }

    private static string RenderArguments(AIFunctionArguments arguments)
    {
        try
        {
            return RecommendationRuntimeEvents.SafePreview(JsonSerializer.Serialize(arguments));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return "[arguments unavailable]";
        }
    }

    private static string RenderValue(object? value)
    {
        try
        {
            var text = value as string ?? JsonSerializer.Serialize(value);
            return RecommendationRuntimeEvents.SafePreview(text);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return "[result unavailable]";
        }
    }
}
