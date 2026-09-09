// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Agents;

/// <summary>
/// Deterministic model boundary for the no-key showcase. The real ChatClientAgent still performs
/// its normal tool-call loop and invokes the real registered tools; only model choice is scripted.
/// </summary>
public sealed class DeterministicChatClient : IChatClient
{
    private readonly Queue<DeterministicChatTurn> _turns = new();

    public IReadOnlyList<IReadOnlyList<ChatMessage>> ReceivedMessages => _receivedMessages;
    private readonly List<IReadOnlyList<ChatMessage>> _receivedMessages = [];

    /// <summary>The effective options the real <c>ChatClientAgent</c> sent on each turn.</summary>
    public IReadOnlyList<ChatOptions?> ReceivedOptions => _receivedOptions;
    private readonly List<ChatOptions?> _receivedOptions = [];

    public int CallCount => _receivedMessages.Count;

    public DeterministicChatClient AddToolCall(
        string callId,
        string name,
        IDictionary<string, object?> arguments)
    {
        _turns.Enqueue(new DeterministicChatTurn(callId, name, arguments, null));
        return this;
    }

    public DeterministicChatClient AddText(string text)
    {
        _turns.Enqueue(new DeterministicChatTurn(null, null, null, text));
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _receivedMessages.Add(messages.ToArray());
        _receivedOptions.Add(options);
        if (_turns.Count == 0)
            throw new InvalidOperationException(
                "The deterministic model script was exhausted. Add the missing turn instead of treating an incomplete fixture as an empty model response.");
        var turn = _turns.Dequeue();

        var contents = new List<AIContent>();
        if (turn.ToolName is not null)
        {
            contents.Add(new FunctionCallContent(
                turn.CallId ?? $"call_{CallCount}",
                turn.ToolName,
                turn.Arguments ?? new Dictionary<string, object?>()));
        }

        if (!string.IsNullOrEmpty(turn.Text)) contents.Add(new TextContent(turn.Text));

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            FinishReason = turn.ToolName is null ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls,
            ModelId = "vitrine-offline-scripted",
        };
        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var message in response.Messages)
        {
            yield return new ChatResponseUpdate(message.Role, message.Contents)
            {
                FinishReason = response.FinishReason,
                ModelId = response.ModelId,
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>One deterministic assistant action.</summary>
public sealed record DeterministicChatTurn(
    string? CallId,
    string? ToolName,
    IDictionary<string, object?>? Arguments,
    string? Text);

/// <summary>The committed, zero-credential Demo01 trajectory used by the UI.</summary>
public static class OfflineRecommendationScript
{
    /// <summary>The committed Nadia search need. Kept under the original name for fixture compatibility.</summary>
    public const string SearchNeed = "A neutral density filter for long-exposure outdoor photography at first light.";

    /// <summary>The committed Sofia search need, grounded in her whole-bean capability gap.</summary>
    public const string SofiaSearchNeed =
        "A precise grinder for whole coffee beans and espresso at home, with a serviceable or packable design.";

    public static bool Supports(string userId) =>
        string.Equals(userId, GalaxusDemoPrompts.NadiaUserId, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(userId, GalaxusDemoPrompts.SofiaUserId, StringComparison.OrdinalIgnoreCase);

    public static DeterministicChatClient Create(string userId)
    {
        if (string.Equals(userId, GalaxusDemoPrompts.NadiaUserId, StringComparison.OrdinalIgnoreCase))
            return CreateNadia(userId);

        if (string.Equals(userId, GalaxusDemoPrompts.SofiaUserId, StringComparison.OrdinalIgnoreCase))
            return CreateSofia(userId);

        throw new NotSupportedException(
            $"No committed scripted-agent trajectory exists for customer '{userId}'. Use the zero-model baseline or a live/injected model for this persona.");
    }

    private static DeterministicChatClient CreateNadia(string userId) =>
        new DeterministicChatClient()
        .AddToolCall("profile", "GetUserProfile", new Dictionary<string, object?> { ["userId"] = userId })
        .AddToolCall("map", "GetInterestMap", new Dictionary<string, object?> { ["userId"] = userId })
        .AddToolCall("search", "SearchProductsByMeaning", new Dictionary<string, object?>
        {
            ["need"] = SearchNeed,
            ["topK"] = 6,
        })
        .AddToolCall("details", "GetProductDetails", new Dictionary<string, object?> { ["productId"] = "GLX-1003" })
        .AddToolCall("present", "PresentRecommendation", new Dictionary<string, object?>
        {
            ["sku"] = "GLX-1003",
            ["reason"] = "This packable filter set supports long-exposure landscape work on multi-day trips. The ten-stop option is the useful trade-off, while a slight warm cast may need correction.",
            ["evidence"] = "review:REV-1003-01",
            ["outOfStock"] = false,
            ["userEvidence"] = "multi-day trips, starts before sunrise, carried | PUR-NB-01,PUR-NB-02,PUR-NB-03,PUR-NB-04,PUR-NB-05",
        })
        .AddText("I presented one recommendation through the sanctioned tool channel; code will now screen it.");

    private static DeterministicChatClient CreateSofia(string userId) =>
        new DeterministicChatClient()
        .AddToolCall("profile", "GetUserProfile", new Dictionary<string, object?> { ["userId"] = userId })
        .AddToolCall("map", "GetInterestMap", new Dictionary<string, object?> { ["userId"] = userId })
        .AddToolCall("search", "SearchProductsByMeaning", new Dictionary<string, object?>
        {
            ["need"] = SofiaSearchNeed,
            ["topK"] = 6,
        })
        .AddToolCall("details", "GetProductDetails", new Dictionary<string, object?> { ["productId"] = "GLX-3007" })
        .AddToolCall("present", "PresentRecommendation", new Dictionary<string, object?>
        {
            ["sku"] = "GLX-3007",
            ["reason"] = "Because you repeatedly buy whole beans and keep them in a vacuum canister, a grinder closes the missing preparation gap. The fine external adjustment supports espresso, while hand grinding takes about forty seconds per dose.",
            ["evidence"] = "attr:provides:grinder",
            ["outOfStock"] = false,
            ["userEvidence"] = "owns whole beans and vacuum canisters but no grinder | PUR-SK-07,PUR-SK-08,PUR-SK-09,PUR-SK-10,PUR-SK-11,PUR-SK-12,PUR-SK-13",
        })
        .AddText("I presented one recommendation through the sanctioned tool channel; code will now screen it.");
}
