// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Galaxus.RecommendationAgent.Agents;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Observability;

/// <summary>
/// Observes real model boundaries. It records roles, tool names, and visible assistant content;
/// it never receives or reports endpoint/key configuration and it does not expose hidden reasoning.
/// </summary>
public sealed class ObservedChatClient : DelegatingChatClient
{
    private readonly IRecommendationRuntimeEventSink _events;

    public ObservedChatClient(IChatClient inner, IRecommendationRuntimeEventSink events)
        : base(inner)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages.ToArray();
        var operationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        EmitRequest(materialized, options, operationId);

        try
        {
            var response = await base.GetResponseAsync(materialized, options, cancellationToken).ConfigureAwait(false);
            EmitResponse(response.Messages, operationId, stopwatch.Elapsed);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _events.EmitSafely(new(
                RecommendationRuntimeEventKind.ModelRequestCancelled,
                "model",
                RecommendationAgentFactory.AgentName,
                "Model request cancelled",
                "The request observed cancellation.",
                operationId));
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _events.EmitSafely(new(
                RecommendationRuntimeEventKind.ModelRequestFailed,
                "model",
                RecommendationAgentFactory.AgentName,
                "Model request failed",
                $"The provider boundary failed with {exception.GetType().Name}; its message is intentionally not captured.",
                operationId));
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var materialized = messages.ToArray();
        var operationId = Guid.NewGuid().ToString("N");
        var stopwatch = Stopwatch.StartNew();
        var updates = new List<ChatResponseUpdate>();
        EmitRequest(materialized, options, operationId);

        await using var enumerator = base.GetStreamingResponseAsync(materialized, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _events.EmitSafely(new(
                    RecommendationRuntimeEventKind.ModelRequestCancelled,
                    "model",
                    RecommendationAgentFactory.AgentName,
                    "Streaming model request cancelled",
                    "The request observed cancellation.",
                    operationId));
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _events.EmitSafely(new(
                    RecommendationRuntimeEventKind.ModelRequestFailed,
                    "model",
                    RecommendationAgentFactory.AgentName,
                    "Streaming model request failed",
                    $"The provider boundary failed with {exception.GetType().Name}; its message is intentionally not captured.",
                    operationId));
                throw;
            }

            if (!hasNext) break;
            var update = enumerator.Current;
            updates.Add(update);
            yield return update;
        }

        EmitResponse(updates.ToChatResponse().Messages, operationId, stopwatch.Elapsed);
    }

    private void EmitRequest(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        string operationId)
    {
        var tools = options?.Tools is { Count: > 0 } registered
            ? string.Join(", ", registered.Select(static tool => tool.Name))
            : "none";
        var roles = string.Join(", ", messages.Select(static message => message.Role.ToString()));
        _events.EmitSafely(new(
            RecommendationRuntimeEventKind.ModelRequestStarted,
            RecommendationAgentFactory.AgentName,
            "model",
            "Model request started",
            $"Conversation messages: {messages.Count}; roles: {roles}; registered tools: {tools}.",
            operationId,
            RecommendationRuntimeEvents.SafePreview(RenderMessages(messages))));
    }

    private void EmitResponse(
        IEnumerable<ChatMessage> messages,
        string operationId,
        TimeSpan elapsed)
    {
        var materialized = messages.ToArray();
        var calls = materialized
            .SelectMany(static message => message.Contents.OfType<FunctionCallContent>())
            .Select(static call => call.Name)
            .ToArray();
        var action = calls.Length > 0
            ? $"Requested tool call(s): {string.Join(", ", calls)}."
            : "Returned a visible assistant answer.";

        _events.EmitSafely(new(
            RecommendationRuntimeEventKind.ModelResponseReceived,
            "model",
            RecommendationAgentFactory.AgentName,
            "Model response received",
            $"{action} Elapsed {elapsed.TotalMilliseconds:0} ms.",
            operationId,
            RecommendationRuntimeEvents.SafePreview(RenderMessages(materialized))));
    }

    /// <summary>
    /// Renders only observable MEAI message content: visible text, function names/arguments, and
    /// function results. Provider metadata and hidden reasoning content are intentionally absent.
    /// The shared preview policy applies the final redaction and size bound before publication.
    /// </summary>
    private static string RenderMessages(IEnumerable<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            builder.Append('[').Append(message.Role).AppendLine("]");
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        builder.AppendLine(text.Text);
                        break;
                    case FunctionCallContent call:
                        builder.Append("tool call ").Append(call.Name)
                            .Append(" · call ").Append(call.CallId)
                            .Append(" · arguments ").AppendLine(RenderValue(call.Arguments));
                        break;
                    case FunctionResultContent result:
                        builder.Append("tool response · call ").Append(result.CallId)
                            .Append(" · result ").AppendLine(RenderValue(result.Result));
                        break;
                }
            }
        }

        return builder.Length == 0 ? "[no observable text or function content]" : builder.ToString();
    }

    private static string RenderValue(object? value)
    {
        try
        {
            return value as string ?? JsonSerializer.Serialize(value);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return "[content unavailable]";
        }
    }
}
