// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;
using Galaxus.RecommendationAgent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Demos;

/// <summary>The three deliberately distinct Demo01 execution arms.</summary>
public enum RecommendationExecutionArm
{
    /// <summary>Real ChatClientAgent and tools, deterministic scripted model boundary, no key.</summary>
    ScriptedAgent,

    /// <summary>Deterministic retrieval/tag baseline, explicitly zero model calls.</summary>
    ZeroModelBaseline,

    /// <summary>Real ChatClientAgent and tools over the configured Azure deployment.</summary>
    LiveAzure,
}

/// <summary>Terminal state of a recommendation run.</summary>
public enum RecommendationRunStatus
{
    Completed,
    Abstained,
    Cancelled,
    Failed,
}

/// <summary>Inputs to the console-independent Demo01 engine.</summary>
public sealed record RecommendationRunOptions(
    string UserId,
    bool PersonalizationDisabled = false,
    RecommendationExecutionArm Arm = RecommendationExecutionArm.ScriptedAgent,
    IChatClient? ChatClient = null,
    IProductRetriever? Retriever = null,
    RecommendationToolSet? RegisteredTools = null,
    TimeSpan? RunTimeout = null,
    TimeSpan? ModelCallTimeout = null);

/// <summary>
/// Safe, immutable description of a completed run. Runtime service objects deliberately do not
/// cross the result boundary.
/// </summary>
public sealed record RecommendationRunDescriptor(
    string UserId,
    bool PersonalizationDisabled,
    RecommendationExecutionArm Arm,
    Guid RegisteredToolSetId,
    TimeSpan RunTimeout,
    TimeSpan ModelCallTimeout);

/// <summary>
/// Typed result consumed by the CLI and UI. Nullable measurements remain absent; no renderer is
/// permitted to turn them into a numeric zero.
/// </summary>
public sealed record RecommendationRunResult(
    Guid RunId,
    RecommendationRunDescriptor Options,
    RecommendationRunStatus Status,
    CustomerProfile? Profile,
    string? Prompt,
    InterestMap? InterestMap,
    IReadOnlyList<ClassifiedPurchase> ClassifiedPurchases,
    IReadOnlyList<PresentedRecommendation> Presented,
    GuardrailOutcome? Outcome,
    string? RetrieverName,
    IReadOnlyList<string> RegisteredToolNames,
    int? ModelCalls,
    int? ToolCallsUsed,
    string? BudgetSummary,
    UsageDetails? ModelUsage,
    string? AgentText,
    string? FailureKind,
    DateTimeOffset StartedAtUtc,
    TimeSpan Elapsed,
    IReadOnlyList<RecommendationRuntimeEvent> Events)
{
    /// <summary>The shared, absence-preserving projection consumed by reports and evaluations.</summary>
    public ProviderUsageMeasurement ProviderUsage =>
        ProviderUsageMeasurement.FromDemo01(ModelCalls, ModelUsage);
}

/// <summary>
/// Runs Demo01 without parsing console output. It owns composition and screening; the model's
/// artifact supplies presentations only and never supplies its own acceptance criteria.
/// </summary>
public static class RecommendationRunEngine
{
    public static TimeSpan DefaultRunTimeout { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan DefaultModelCallTimeout { get; } = TimeSpan.FromSeconds(45);

    public static async Task<RecommendationRunResult> RunAsync(
        RecommendationRunOptions options,
        IRecommendationRuntimeEventSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var runId = Guid.NewGuid();
        var startedAt = DateTimeOffset.UtcNow;
        var recording = new RecordingRecommendationRuntimeEventSink();
        var events = progress is null
            ? (IRecommendationRuntimeEventSink)recording
            : new CompositeRecommendationRuntimeEventSink(recording, progress);
        var toolSet = options.RegisteredTools ?? RecommendationAgentFactory.PrepareReadOnlyTools(events);
        var toolNames = toolSet.Tools
            .Select(static tool => tool.Name)
            .ToArray();
        var runTimeout = ValidateTimeout(options.RunTimeout, DefaultRunTimeout, nameof(options.RunTimeout));
        var modelCallTimeout = ValidateTimeout(options.ModelCallTimeout, DefaultModelCallTimeout, nameof(options.ModelCallTimeout));
        options = options with
        {
            RegisteredTools = toolSet,
            RunTimeout = runTimeout,
            ModelCallTimeout = modelCallTimeout,
        };
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCancellation.CancelAfter(runTimeout);
        var runToken = runCancellation.Token;

        events.EmitSafely(new(
            RecommendationRuntimeEventKind.RunStarted,
            "customer",
            RecommendationAgentFactory.AgentName,
            "Recommendation run started",
            $"Arm {options.Arm}; customer {options.UserId}; concept vectors."));

        if (cancellationToken.IsCancellationRequested)
        {
            return Finish(RecommendationRunStatus.Cancelled, null, null, null, null, [], [], null,
                null, toolNames, null, null, null, null, null, runId, options, startedAt, recording, events);
        }

        if (!Enum.IsDefined(options.Arm))
        {
            return Finish(RecommendationRunStatus.Failed, "InvalidExecutionArm", null, null, null, [], [], null,
                null, toolNames, null, null, null, null, null, runId, options, startedAt, recording, events);
        }

        var profile = ResolveProfile(options);
        if (profile is null)
        {
            return Finish(RecommendationRunStatus.Failed, "UnknownCustomer", null, null, null, [], [], null,
                null, toolNames, null, null, null, null, null, runId, options, startedAt, recording, events);
        }

        var catalogue = Catalogue.Default;
        var prompt = Personas.CanonicalPromptFor(profile.Id);
        var classified = profile.User.PersonalizationEnabled
            ? PurchaseIntentClassifier.ClassifyAll(profile.Purchases, catalogue.BySku, Personas.DemoToday)
            : [];
        var map = InterestMapBuilder.Build(
            profile.User,
            profile.Purchases,
            catalogue.BySku,
            statedNeeds: profile.User.PersonalizationEnabled ? null : [prompt],
            asOf: Personas.DemoToday,
            sensitiveCategoryNames: catalogue.SensitiveCategories);
        var context = GuardrailContext.Create(
            catalogue.BySku,
            profile.User,
            map,
            classified,
            categories: catalogue.Categories,
            customerUtterance: prompt,
            asOf: Personas.DemoToday);
        var replenishment = Demo01_RecommendationAgent.BuildReplenishmentLane(map, classified, catalogue);

        if (GuardrailPipeline.ShouldAbstain(context, out var abstainReason))
        {
            var raw = RecommendationSet.Empty with
            {
                InterestMap = [.. map.Signals.Select(InterestSignalDto.From)],
                Replenishment = replenishment,
            };
            var outcome = GuardrailPipeline.ApplyWithAbstentionGate(raw, context);
            outcome.Ledger.GiftExcluded = map.ExcludedBecauseGift.Count;
            events.EmitSafely(new(
                RecommendationRuntimeEventKind.GuardDecision,
                "guardrails",
                "customer",
                "Pre-spend abstention",
                abstainReason));
            return Finish(RecommendationRunStatus.Abstained, null, profile, prompt, map, classified, [], outcome,
                null, toolNames, 0, 0, "no model or tool execution; pre-spend abstention", null, null,
                runId, options, startedAt, recording, events);
        }

        try
        {
            runToken.ThrowIfCancellationRequested();
            var retriever = options.Retriever ?? await HybridRetriever.BuildAsync(
                catalogue.All,
                EmbeddingSpace.Resolve(catalogue.All).Source,
                cancellationToken: runToken).ConfigureAwait(false);
            using var profileScope = GalaxusTools.BeginProfileScope(
                options.PersonalizationDisabled ? profile : null);
            using var bindingScope = GalaxusTools.BeginBinding(retriever, profile.Market);
            GalaxusTools.AssertBound();

            var arm = options.Arm switch
            {
                RecommendationExecutionArm.ZeroModelBaseline =>
                    await RunBaselineAsync(map, context, retriever, catalogue, runToken).ConfigureAwait(false),
                RecommendationExecutionArm.ScriptedAgent or RecommendationExecutionArm.LiveAzure =>
                    await RunAgentAsync(options, profile, prompt, context, events, recording, toolSet, modelCallTimeout, runToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The execution arm was not recognized."),
            };

            var screeningContext = context with { CandidateProductIds = arm.CandidateSet };
            var (raw, preDrops, modelStatedUserSides) = await Demo01_RecommendationAgent.AssembleAsync(
                arm.Presented,
                arm.UserEvidence,
                arm.Provenance,
                map,
                catalogue,
                replenishment,
                runToken).ConfigureAwait(false);
            var outcome = GuardrailPipeline.ApplyWithAbstentionGate(raw, screeningContext);
            foreach (var drop in preDrops)
                outcome.Ledger.Drop(drop.Stage, drop.Reason, drop.Subject, drop.Detail);
            outcome.Ledger.RecordInput(arm.Presented.Count);
            outcome.Ledger.GiftExcluded = map.ExcludedBecauseGift.Count;
            outcome.Ledger.ToolCallsUsed = arm.ToolCallsUsed ?? 0;
            outcome.Ledger.ToolCallCap = arm.ToolCallsUsed.HasValue ? Demo01_RecommendationAgent.ToolCallCap : 0;
            Demo01_RecommendationAgent.NoteEvidenceArms(
                outcome.Ledger,
                options.Arm == RecommendationExecutionArm.ZeroModelBaseline,
                arm.Presented.Count,
                modelStatedUserSides);

            events.EmitSafely(new(
                RecommendationRuntimeEventKind.GuardDecision,
                "guardrails",
                "customer",
                "Mechanical screening completed",
                $"{outcome.Ledger.InputCount} presented; {outcome.Ledger.OutputCount} survived; {outcome.Ledger.DroppedCount} dropped."));

            return Finish(RecommendationRunStatus.Completed, null, profile, prompt, map, classified,
                arm.Presented, outcome, retriever.Name, toolNames, arm.ModelCalls, arm.ToolCallsUsed,
                arm.BudgetSummary, arm.Usage, arm.Text, runId, options, startedAt, recording, events);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(RecommendationRunStatus.Cancelled, null, profile, prompt, map, classified, [], null,
                null, toolNames, null, null, null, null, null, runId, options, startedAt, recording, events);
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            return Finish(RecommendationRunStatus.Failed, "RunTimeout", profile, prompt, map, classified, [], null,
                null, toolNames, null, null, null, null, null, runId, options, startedAt, recording, events,
                toolSet.Id, runTimeout, modelCallTimeout);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Finish(RecommendationRunStatus.Failed, exception.GetType().Name, profile, prompt, map,
                classified, [], null, null, toolNames, null, null, null, null, null,
                runId, options, startedAt, recording, events);
        }
    }

    private static CustomerProfile? ResolveProfile(RecommendationRunOptions options)
    {
        var seeded = UserProfiles.Find(options.UserId);
        if (seeded is null) return null;
        return seeded.WithPersonalization(!options.PersonalizationDisabled);
    }

    private static async Task<ArmResult> RunAgentAsync(
        RecommendationRunOptions options,
        CustomerProfile profile,
        string prompt,
        GuardrailContext context,
        IRecommendationRuntimeEventSink events,
        RecordingRecommendationRuntimeEventSink recording,
        RecommendationToolSet toolSet,
        TimeSpan modelCallTimeout,
        CancellationToken cancellationToken)
    {
        IChatClient? ownedClient = null;
        var chatClient = options.ChatClient;
        if (chatClient is null && options.Arm == RecommendationExecutionArm.ScriptedAgent)
            chatClient = ownedClient = OfflineRecommendationScript.Create(profile.Id);

        if (chatClient is null)
            chatClient = ownedClient = RecommendationAgentFactory.CreateConfiguredChatClient();

        try
        {
            using var deadlineClient = new DeadlineChatClient(chatClient!, modelCallTimeout, ownsInner: false);
            var agent = RecommendationAgentFactory.Create(deadlineClient, events, toolSet);
            var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            var messages = new[]
            {
                new ChatMessage(ChatRole.User, Demo01_RecommendationAgent.SessionHeader(profile)),
                new ChatMessage(ChatRole.User, prompt),
            };
            using var budget = ToolCallBudget.BeginScope(Demo01_RecommendationAgent.ToolCallCap);
            using var capture = GalaxusTools.BeginRunCapture(context);
            var response = await agent.RunAsync(messages, session, cancellationToken: cancellationToken).ConfigureAwait(false);

            // The internal recorder is always present, even when a UI/progress observer is composed
            // around it. Counting the observer argument itself made injected/live runs report an
            // absent measurement exactly when somebody was watching.
            var modelCalls = recording.Events.Count(
                static item => item.Kind == RecommendationRuntimeEventKind.ModelResponseReceived);
            return new ArmResult(
                GalaxusTools.PresentedInCurrentRun,
                GalaxusTools.UserEvidenceInCurrentRun,
                GalaxusTools.RetrievalProvenanceInCurrentRun,
                GalaxusTools.CandidateSetInCurrentRun,
                modelCalls,
                ToolCallBudget.Used,
                ToolCallBudget.Summary,
                response.Usage,
                response.Text);
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    private static async Task<ArmResult> RunBaselineAsync(
        InterestMap map,
        GuardrailContext context,
        IProductRetriever retriever,
        Catalogue catalogue,
        CancellationToken cancellationToken)
    {
        var presented = new List<PresentedRecommendation>();
        var provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var exclude = new HashSet<string>(context.OwnedProductIds, StringComparer.Ordinal);

        foreach (var signal in map.Signals.OrderByDescending(static signal => signal.Strength).Take(3))
        {
            var query = RetrievalQuery.For(signal.Label) with
            {
                TopK = 6,
                Market = context.User.Market,
                ExcludeProductIds = exclude,
            };
            var result = await retriever.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            foreach (var hit in result.Hits) candidates.Add(hit.ProductId);

            var kept = 0;
            foreach (var hit in result.Hits)
            {
                if (kept >= 2) break;
                if (!taken.Add(hit.ProductId)) continue;
                if (!catalogue.TryGet(hit.ProductId, out var product) || product is null) continue;
                var citation = catalogue.AttributesOf(product).OrderBy(static value => value, StringComparer.Ordinal).FirstOrDefault();
                if (citation is null) continue;
                presented.Add(new(
                    product.Id,
                    $"Retrieved for the derived interest \"{signal.Label}\". Selected by the baseline arm, with no model call.",
                    EvidenceRef.AttributePrefix + citation,
                    product.StockUnits == 0));
                provenance[product.Id] = [signal.Label];
                kept++;
            }
        }

        return new ArmResult(
            presented,
            [.. presented.Select(static _ => (string?)null)],
            provenance,
            candidates,
            ModelCalls: 0,
            ToolCallsUsed: null,
            BudgetSummary: "zero-model baseline; registered tools were not invoked",
            Usage: null,
            Text: null);
    }

    private static RecommendationRunResult Finish(
        RecommendationRunStatus status,
        string? failureKind,
        CustomerProfile? profile,
        string? prompt,
        InterestMap? map,
        IReadOnlyList<ClassifiedPurchase> classified,
        IReadOnlyList<PresentedRecommendation> presented,
        GuardrailOutcome? outcome,
        string? retrieverName,
        IReadOnlyList<string> toolNames,
        int? modelCalls,
        int? toolCalls,
        string? budgetSummary,
        UsageDetails? usage,
        string? text,
        Guid runId,
        RecommendationRunOptions options,
        DateTimeOffset startedAt,
        RecordingRecommendationRuntimeEventSink recording,
        IRecommendationRuntimeEventSink events)
        => Finish(status, failureKind, profile, prompt, map, classified, presented, outcome,
            retrieverName, toolNames, modelCalls, toolCalls, budgetSummary, usage, text, runId,
            options, startedAt, recording, events,
            options.RegisteredTools?.Id ?? Guid.Empty,
            ValidateTimeout(options.RunTimeout, DefaultRunTimeout, nameof(options.RunTimeout)),
            ValidateTimeout(options.ModelCallTimeout, DefaultModelCallTimeout, nameof(options.ModelCallTimeout)));

    private static RecommendationRunResult Finish(
        RecommendationRunStatus status,
        string? failureKind,
        CustomerProfile? profile,
        string? prompt,
        InterestMap? map,
        IReadOnlyList<ClassifiedPurchase> classified,
        IReadOnlyList<PresentedRecommendation> presented,
        GuardrailOutcome? outcome,
        string? retrieverName,
        IReadOnlyList<string> toolNames,
        int? modelCalls,
        int? toolCalls,
        string? budgetSummary,
        UsageDetails? usage,
        string? text,
        Guid runId,
        RecommendationRunOptions options,
        DateTimeOffset startedAt,
        RecordingRecommendationRuntimeEventSink recording,
        IRecommendationRuntimeEventSink events,
        Guid toolSetId,
        TimeSpan runTimeout,
        TimeSpan modelCallTimeout)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var kind = status switch
        {
            RecommendationRunStatus.Completed => RecommendationRuntimeEventKind.RunCompleted,
            RecommendationRunStatus.Cancelled => RecommendationRuntimeEventKind.RunCancelled,
            RecommendationRunStatus.Failed => RecommendationRuntimeEventKind.RunFailed,
            _ => RecommendationRuntimeEventKind.RunCompleted,
        };
        events.EmitSafely(new(kind, RecommendationAgentFactory.AgentName, "customer",
            $"Recommendation run {status.ToString().ToLowerInvariant()}",
            failureKind is null ? $"Elapsed {elapsed.TotalMilliseconds:0} ms." : $"Failure type {failureKind}; message intentionally not captured."));
        var descriptor = new RecommendationRunDescriptor(
            options.UserId,
            options.PersonalizationDisabled,
            options.Arm,
            toolSetId,
            runTimeout,
            modelCallTimeout);
        return new(runId, descriptor, status, profile, prompt, map, classified, presented, outcome,
            retrieverName, toolNames, modelCalls, toolCalls, budgetSummary, usage, text, failureKind,
            startedAt, elapsed, recording.Events);
    }

    private static TimeSpan ValidateTimeout(TimeSpan? configured, TimeSpan fallback, string parameterName)
    {
        var value = configured ?? fallback;
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName, "Timeouts must be finite and positive.");
        return value;
    }

    private sealed record ArmResult(
        IReadOnlyList<PresentedRecommendation> Presented,
        IReadOnlyList<string?> UserEvidence,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Provenance,
        IReadOnlySet<string>? CandidateSet,
        int? ModelCalls,
        int? ToolCallsUsed,
        string? BudgetSummary,
        UsageDetails? Usage,
        string? Text);
}
