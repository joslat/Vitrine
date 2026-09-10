// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.RegularExpressions;

namespace Galaxus.RecommendationAgent.Observability;

/// <summary>The typed execution facts emitted while Demo01 is actually running.</summary>
public enum RecommendationRuntimeEventKind
{
    RunStarted,
    ModelRequestStarted,
    ModelResponseReceived,
    ModelRequestCancelled,
    ModelRequestFailed,
    ToolExecutionStarted,
    ToolCompleted,
    ToolCancelled,
    ToolFailed,
    GuardDecision,
    RunCompleted,
    RunCancelled,
    RunFailed,
}

/// <summary>
/// One safe presentation event. It contains observable boundaries and bounded previews, never
/// provider configuration, hidden reasoning, an endpoint, or a credential.
/// </summary>
public sealed record RecommendationRuntimeEvent(
    RecommendationRuntimeEventKind Kind,
    string Source,
    string Target,
    string Title,
    string Detail,
    string? OperationId = null,
    string? PayloadPreview = null,
    DateTimeOffset? OccurredAtUtc = null)
{
    /// <summary>The event time supplied by the producer, or the current UTC time.</summary>
    public DateTimeOffset TimestampUtc { get; } = OccurredAtUtc ?? DateTimeOffset.UtcNow;
}

/// <summary>Receives Demo01 execution events. Implementations must not influence the run.</summary>
public interface IRecommendationRuntimeEventSink
{
    /// <summary>Records one event.</summary>
    void Emit(RecommendationRuntimeEvent runtimeEvent);
}

/// <summary>A sink used when nobody is observing.</summary>
public sealed class NullRecommendationRuntimeEventSink : IRecommendationRuntimeEventSink
{
    private NullRecommendationRuntimeEventSink() { }

    public static NullRecommendationRuntimeEventSink Instance { get; } = new();

    public void Emit(RecommendationRuntimeEvent runtimeEvent) { }
}

/// <summary>A thread-safe sink useful to the application, tests, and replay capture.</summary>
public sealed class RecordingRecommendationRuntimeEventSink : IRecommendationRuntimeEventSink
{
    private readonly Lock _gate = new();
    private readonly List<RecommendationRuntimeEvent> _events = [];

    public IReadOnlyList<RecommendationRuntimeEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public void Emit(RecommendationRuntimeEvent runtimeEvent)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        lock (_gate) _events.Add(runtimeEvent);
    }
}

/// <summary>Adapts the event contract to a callback without coupling the subject to a UI.</summary>
public sealed class CallbackRecommendationRuntimeEventSink(Action<RecommendationRuntimeEvent> callback)
    : IRecommendationRuntimeEventSink
{
    private readonly Action<RecommendationRuntimeEvent> _callback =
        callback ?? throw new ArgumentNullException(nameof(callback));

    public void Emit(RecommendationRuntimeEvent runtimeEvent) => _callback(runtimeEvent);
}

/// <summary>Fans events out while preserving observer isolation.</summary>
public sealed class CompositeRecommendationRuntimeEventSink(params IRecommendationRuntimeEventSink[] sinks)
    : IRecommendationRuntimeEventSink
{
    private readonly IReadOnlyList<IRecommendationRuntimeEventSink> _sinks =
        sinks?.Where(static sink => sink is not null).ToArray()
        ?? throw new ArgumentNullException(nameof(sinks));

    public void Emit(RecommendationRuntimeEvent runtimeEvent)
    {
        foreach (var sink in _sinks) sink.EmitSafely(runtimeEvent);
    }
}

/// <summary>Safety helpers shared by every subject-side event producer.</summary>
public static partial class RecommendationRuntimeEvents
{
    private const int MaximumPreviewCharacters = 6000;

    /// <summary>
    /// Emits without allowing an observer failure to change the artifact under test. The sink is
    /// presentation infrastructure and therefore may never become an input to pass/fail behavior.
    /// </summary>
    public static void EmitSafely(
        this IRecommendationRuntimeEventSink? sink,
        RecommendationRuntimeEvent runtimeEvent)
    {
        try
        {
            (sink ?? NullRecommendationRuntimeEventSink.Instance).Emit(runtimeEvent);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Observer failures are deliberately isolated from subject execution.
        }
    }

    /// <summary>
    /// Bounds display text and removes URLs plus conventional credential assignments before an
    /// event can enter storage. Configuration values are never passed here in the first place;
    /// this is a second, fail-closed boundary.
    /// </summary>
    public static string SafePreview(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var safe = value;
        var configuredKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var configuredEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var configuredManagedIdentityClientId =
            Environment.GetEnvironmentVariable("AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(configuredKey))
            safe = safe.Replace(configuredKey, "[REDACTED_KEY]", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(configuredEndpoint))
            safe = safe.Replace(configuredEndpoint, "[REDACTED_ENDPOINT]", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(configuredManagedIdentityClientId))
            safe = safe.Replace(
                configuredManagedIdentityClientId,
                "[REDACTED_MANAGED_IDENTITY]",
                StringComparison.OrdinalIgnoreCase);
        safe = UrlPattern().Replace(safe, "[REDACTED_URL]");
        safe = SecretAssignmentPattern().Replace(safe, match => $"{match.Groups[1].Value}=[REDACTED]");
        // Normalize platform line endings without flattening the exact customer-facing artifact.
        // A redaction boundary may remove secrets; it must not silently rewrite a multi-line answer.
        safe = safe.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return safe.Length <= MaximumPreviewCharacters
            ? safe
            : safe[..MaximumPreviewCharacters] + "\n… [bounded preview truncated]";
    }

    /// <summary>Checks raw text before it may enter any digest or persistence boundary.</summary>
    public static bool ContainsSecretBearingContent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var managedIdentityClientId =
            Environment.GetEnvironmentVariable("AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID");
        return !string.IsNullOrWhiteSpace(apiKey) && value.Contains(apiKey, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(endpoint) && value.Contains(endpoint, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(managedIdentityClientId)
                && value.Contains(managedIdentityClientId, StringComparison.OrdinalIgnoreCase)
            || UrlPattern().IsMatch(value)
            || SecretAssignmentPattern().IsMatch(value);
    }

    [GeneratedRegex(@"(?i)https?://[^\s\""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(
        @"(?i)\b(api[-_ ]?key|client[-_ ]?secret|account[-_ ]?key|shared[-_ ]?access[-_ ]?signature|token|secret|authorization|credential|endpoint|connection[-_ ]?string)\b\s*[\""']?\s*[:=]\s*[\""']?(?:bearer\s+)?[^\""'\s,;}]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignmentPattern();
}
