// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using System.Text.RegularExpressions;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Agents;

/// <summary>The model-backed discovery stages understood by the deterministic provider.</summary>
public enum DeterministicDiscoveryStage
{
    InterestMapper,
    CoverageReviewer,
    Ranker,
    Presenter,
}

/// <summary>
/// Deterministic <see cref="IChatClient"/> for the real model-backed Demo02 composition.
/// </summary>
/// <remarks>
/// This is not a replacement workflow and does not call deterministic node implementations.
/// The shipped <see cref="ChatClientAgent"/> instances, all five MAF executors, retrieval,
/// routing, post-checks, and presentation still run. Only the remote model boundary is replaced.
/// The first reviewer response asks for a materially different query and the second approves,
/// making the single loop-back edge observable without credentials.
/// </remarks>
public sealed class DeterministicDiscoveryChatClient : IChatClient
{
    private static readonly Regex CustomerIdPattern = new(
        @"\bUSR-[A-Z]{2}-\d{2}\b", RegexOptions.CultureInvariant);
    private static readonly Regex PurchaseIdPattern = new(
        @"\bPUR-[A-Z]{2}-\d{2}\b", RegexOptions.CultureInvariant);
    private static readonly Regex CandidatePattern = new(
        @"(?m)^\s{2}(GLX-\d+)\s+.*?\(for\s+(I-\d+),", RegexOptions.CultureInvariant);
    private static readonly Regex AttributePattern = new(
        @"(?m)^\s+attribute keys:\s*([^\r\n,]+)", RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Dictionary<DeterministicDiscoveryStage, int> _stageCalls = [];
    private int _callCount;
    private bool _disposed;

    /// <summary>Total model-boundary calls made by the four model-backed stages.</summary>
    public int CallCount
    {
        get { lock (_gate) return _callCount; }
    }

    /// <summary>A stable snapshot of call counts by stage.</summary>
    public IReadOnlyDictionary<DeterministicDiscoveryStage, int> StageCalls
    {
        get { lock (_gate) return new Dictionary<DeterministicDiscoveryStage, int>(_stageCalls); }
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var messageText = string.Join("\n", messages.Select(MessageText));
        var stage = IdentifyStage(options?.Instructions);
        int stageCall;
        lock (_gate)
        {
            _callCount++;
            _stageCalls.TryGetValue(stage, out stageCall);
            stageCall++;
            _stageCalls[stage] = stageCall;
        }

        var text = stage switch
        {
            DeterministicDiscoveryStage.InterestMapper => MapperResponse(messageText),
            DeterministicDiscoveryStage.CoverageReviewer => ReviewerResponse(stageCall),
            DeterministicDiscoveryStage.Ranker => RankerResponse(messageText),
            DeterministicDiscoveryStage.Presenter =>
                "A concise draft was produced from the screened selection; code remains the authority for the delivered product cards.",
            _ => throw new InvalidOperationException("The deterministic discovery stage was not recognized."),
        };

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "vitrine-deterministic-discovery",
        });
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() => _disposed = true;

    private static DeterministicDiscoveryStage IdentifyStage(string? instructions)
    {
        if (instructions?.StartsWith(InterestMapperPrompt.Instructions, StringComparison.Ordinal) == true)
            return DeterministicDiscoveryStage.InterestMapper;
        if (instructions?.StartsWith(CoverageReviewerPrompt.Instructions, StringComparison.Ordinal) == true)
            return DeterministicDiscoveryStage.CoverageReviewer;
        if (instructions?.StartsWith(DiscoveryRankerPrompt.Instructions, StringComparison.Ordinal) == true)
            return DeterministicDiscoveryStage.Ranker;
        if (instructions?.StartsWith(DiscoveryPresenterPrompt.Instructions, StringComparison.Ordinal) == true)
            return DeterministicDiscoveryStage.Presenter;

        throw new InvalidOperationException(
            "The deterministic discovery provider received an unknown instruction contract.");
    }

    private static string MapperResponse(string input)
    {
        var customerId = CustomerIdPattern.Match(input).Value;
        var purchaseId = PurchaseIdPattern.Match(input).Value;
        var (label, query, rationale) = customerId switch
        {
            "USR-MI-02" => ("home espresso preparation", "espresso accessories",
                "The customer's non-gift purchases show an espresso preparation interest."),
            "USR-SK-03" => ("whole-bean coffee preparation", "coffee grinder",
                "The customer buys whole beans and needs preparation equipment."),
            _ => ("long-exposure outdoor photography", "neutral density filters",
                "The customer's own purchases support portable outdoor photography."),
        };

        var evidence = string.IsNullOrEmpty(purchaseId) ? Array.Empty<string>() : new[] { purchaseId };
        var envelope = new InterestMapEnvelope(
            [new MappedInterest(label, "DIRECT", 0.91, evidence, rationale, [query], [], null)],
            [],
            [],
            "One independently grounded interest for deterministic orchestration testing.");
        return JsonSerializer.Serialize(envelope, DiscoveryModelCall.Json);
    }

    private static string ReviewerResponse(int call)
    {
        CoverageVerdict verdict = call == 1
            ? new CoverageVerdict(
                [],
                [new CoverageGap("I-1", "A second catalogue-vocabulary query is required.",
                    "travel accessories", null, null)],
                null,
                CoverageVerdict.GapsRemain,
                "Round one deliberately leaves one runnable gap so the conditional loop-back is exercised.")
            : new CoverageVerdict(
                ["I-1"],
                [],
                null,
                CoverageVerdict.CoverageSufficient,
                "The second round provides sufficient catalogue coverage.");
        return JsonSerializer.Serialize(verdict, DiscoveryModelCall.Json);
    }

    private static string RankerResponse(string input)
    {
        var candidate = CandidatePattern.Match(input);
        if (!candidate.Success)
            return JsonSerializer.Serialize(new RankerEnvelope([]), DiscoveryModelCall.Json);

        var attribute = AttributePattern.Match(input);
        var attributeKey = attribute.Success ? attribute.Groups[1].Value.Trim() : null;
        var envelope = new RankerEnvelope(
        [
            new RankedSelection(
                candidate.Groups[1].Value,
                candidate.Groups[2].Value,
                "This catalogue candidate directly serves the mapped interest and remains subject to deterministic screening.",
                attributeKey,
                null),
        ]);
        return JsonSerializer.Serialize(envelope, DiscoveryModelCall.Json);
    }

    private static string MessageText(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(static content => content.Text));
}
