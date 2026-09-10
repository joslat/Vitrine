// SPDX-License-Identifier: MIT

using AgentEval.Core;
using AgentEval.Evals;
using System.Text;
using System.Text.Json;
using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Extensions.AI;

namespace AgentEval.VitrineDemo.Evals.Live;

/// <summary>Injectable live services. <see cref="Default"/> creates no client until a run starts.</summary>
public sealed class LiveEvalServices
{
    private readonly Func<LiveEvalReadiness> _readiness;
    private static readonly IReadOnlyDictionary<string, string> AllowedToolArguments =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["userId"] = "userId",
            ["productId"] = "productId",
            ["sku"] = "sku",
            ["need"] = "need",
            ["categoryPathPrefix"] = "categoryPathPrefix",
            ["categoryPath"] = "categoryPath",
            ["topK"] = "topK",
            ["months"] = "months",
            ["maxPriceChf"] = "maxPriceChf",
            ["minRating"] = "minRating",
            ["inStockOnly"] = "inStockOnly",
            ["limit"] = "limit",
            ["market"] = "market",
            ["reason"] = "reason",
            ["evidence"] = "evidence",
            ["outOfStock"] = "outOfStock",
            ["userEvidence"] = "userEvidence",
        };

    public LiveEvalServices(
        ILiveEvalSubject agent,
        ILiveEvalSubject workflow,
        ILiveEvalJudge judge,
        Func<LiveEvalReadiness>? readiness = null,
        ILiveSafetyEvaluator? safety = null)
    {
        Agent = agent ?? throw new ArgumentNullException(nameof(agent));
        Workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        Judge = judge ?? throw new ArgumentNullException(nameof(judge));
        Safety = safety;
        _readiness = readiness ?? (() => new(true, "Injected live-evaluation services are ready."));
    }

    public ILiveEvalSubject Agent { get; }
    public ILiveEvalSubject Workflow { get; }
    public ILiveEvalJudge Judge { get; }
    public ILiveSafetyEvaluator? Safety { get; }

    public LiveEvalReadiness CheckReadiness()
    {
        try
        {
            return _readiness() ?? new(false, "Live-evaluation readiness was not reported.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(false, "Live-evaluation configuration could not be inspected.");
        }
    }

    /// <summary>
    /// Real paid composition: Demo01 LiveAzure, a fresh live discovery loop, and an Azure-backed
    /// ChatClientEvaluator. Every created chat pipeline enforces the request's output-token limit.
    /// </summary>
    public static LiveEvalServices Default { get; } = new(
        new DefaultAgentSubject(),
        new DefaultWorkflowSubject(),
        new DefaultJudge(),
        () =>
        {
            var readiness = Config.Readiness;
            return new(readiness.IsReady, readiness.SafeSummary);
        },
        new AgentEvalRedTeamSafetyEvaluator());

    internal static string SafeModelId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || RecommendationRuntimeEvents.ContainsSecretBearingContent(value))
            return "configured-model";
        var trimmed = value.Trim();
        if (trimmed.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("eyJ", StringComparison.Ordinal)
                && trimmed.Count(static character => character == '.') >= 2)
            return "configured-model";
        var safe = new string(trimmed.Where(static character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.').ToArray());
        return safe.Length is > 0 and <= 80 ? safe : "configured-model";
    }

    internal static bool HasWorkflowProviderTerminalFailure(IReadOnlyList<DiscoveryEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return AssessWorkflowProvider(events).HasTerminalFailure;
    }

    /// <summary>
    /// Classifies model-backed workflow stages from typed events only. A failed attempt is
    /// recoverable when the same executor later yields a response and never selects its final
    /// fallback. Caller cancellation, an explicit final fallback, an incomplete request, or a
    /// failure with no later response remains terminal for live-evaluation measurement.
    /// </summary>
    internal static WorkflowProviderAssessment AssessWorkflowProvider(
        IReadOnlyList<DiscoveryEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var indexed = events.Select(static (item, index) => (Item: item, Index: index))
            .Where(static pair => pair.Item.Kind is DiscoveryEventKind.ModelRequestStarted
                or DiscoveryEventKind.ModelResponseReceived
                or DiscoveryEventKind.ModelRequestFailed
                or DiscoveryEventKind.ModelRequestCancelled
                || pair.Item.ModelStageDisposition is DiscoveryModelStageDisposition.AttemptUnusable
                    or DiscoveryModelStageDisposition.FinalFallback)
            .ToArray();
        var conflictingAttemptNumbers = indexed
            .Where(static pair => pair.Item.ModelAttemptNumber > 0
                && (pair.Item.Kind is DiscoveryEventKind.ModelRequestStarted
                    or DiscoveryEventKind.ModelResponseReceived
                    or DiscoveryEventKind.ModelRequestFailed
                    or DiscoveryEventKind.ModelRequestCancelled
                    || pair.Item.ModelStageDisposition == DiscoveryModelStageDisposition.AttemptUnusable))
            .GroupBy(static pair => pair.Item.ModelAttemptNumber)
            .Where(static group => group.Select(pair => CanonicalWorkflowNode(pair.Item.NodeId))
                .Distinct(StringComparer.Ordinal).Count() != 1)
            .Select(static group => group.Key)
            .ToHashSet();

        var stages = new List<LiveWorkflowProviderStageEvidence>();
        foreach (var group in indexed.GroupBy(static pair => CanonicalWorkflowNode(pair.Item.NodeId),
                     StringComparer.Ordinal))
        {
            var stageEvents = group.OrderBy(static pair => pair.Index).ToArray();
            var starts = stageEvents.Where(static pair =>
                pair.Item.Kind == DiscoveryEventKind.ModelRequestStarted).ToArray();
            var responses = stageEvents.Where(static pair =>
                pair.Item.Kind == DiscoveryEventKind.ModelResponseReceived).ToArray();
            var failures = stageEvents.Where(static pair =>
                pair.Item.Kind == DiscoveryEventKind.ModelRequestFailed).ToArray();
            var cancellations = stageEvents.Where(static pair =>
                pair.Item.Kind == DiscoveryEventKind.ModelRequestCancelled).ToArray();
            var unusableAttempts = stageEvents.Where(static pair =>
                pair.Item.ModelStageDisposition == DiscoveryModelStageDisposition.AttemptUnusable).ToArray();
            var finalFallback = stageEvents.Any(static pair =>
                pair.Item.ModelStageDisposition == DiscoveryModelStageDisposition.FinalFallback);

            var numberedEvents = stageEvents.Where(static pair => pair.Item.ModelAttemptNumber > 0
                && (pair.Item.Kind is DiscoveryEventKind.ModelRequestStarted
                    or DiscoveryEventKind.ModelResponseReceived
                    or DiscoveryEventKind.ModelRequestFailed
                    or DiscoveryEventKind.ModelRequestCancelled
                    || pair.Item.ModelStageDisposition == DiscoveryModelStageDisposition.AttemptUnusable))
                .ToArray();
            var attemptNumbers = numberedEvents.Select(static pair => pair.Item.ModelAttemptNumber)
                .Distinct().ToArray();
            var usesTypedAttemptSequence = attemptNumbers.Length > 0;
            var hasUnnumberedAttemptEvidence = stageEvents.Any(static pair =>
                (pair.Item.Kind is DiscoveryEventKind.ModelRequestStarted
                    or DiscoveryEventKind.ModelResponseReceived
                    or DiscoveryEventKind.ModelRequestFailed
                    or DiscoveryEventKind.ModelRequestCancelled
                    || pair.Item.ModelStageDisposition == DiscoveryModelStageDisposition.AttemptUnusable)
                && pair.Item.ModelAttemptNumber <= 0);
            var invalidAttemptSequence = hasUnnumberedAttemptEvidence
                || usesTypedAttemptSequence
                    && (!attemptNumbers.SequenceEqual(attemptNumbers.Order())
                        || attemptNumbers.Any(conflictingAttemptNumbers.Contains));
            var invalidDisposition = stageEvents.Any(static pair =>
                !Enum.IsDefined(pair.Item.ModelStageDisposition));

            var terminalOperationIds = stageEvents
                .Where(static pair => pair.Item.Kind is DiscoveryEventKind.ModelResponseReceived
                    or DiscoveryEventKind.ModelRequestFailed or DiscoveryEventKind.ModelRequestCancelled)
                .Select(static pair => pair.Item.OperationId)
                .Where(IsSafeOperationId)
                .ToHashSet(StringComparer.Ordinal);
            var incompleteAttempt = starts.Any(pair => !IsSafeOperationId(pair.Item.OperationId)
                || !terminalOperationIds.Contains(pair.Item.OperationId!));

            var lastFailure = failures.Length == 0 ? -1 : failures[^1].Index;
            var lastResponse = responses.Length == 0 ? -1 : responses[^1].Index;
            var lastUnusable = unusableAttempts.Length == 0 ? -1 : unusableAttempts[^1].Index;
            var unusableNumbers = unusableAttempts
                .Where(static pair => pair.Item.ModelAttemptNumber > 0)
                .Select(static pair => pair.Item.ModelAttemptNumber)
                .ToHashSet();
            var unusableAttemptCount = usesTypedAttemptSequence
                ? unusableNumbers.Count
                : unusableAttempts.Length;
            var usableResponses = responses.Where(pair => pair.Item.ModelAttemptNumber > 0
                && !unusableNumbers.Contains(pair.Item.ModelAttemptNumber)).ToArray();
            var lastUnusableAttempt = unusableNumbers.Count == 0 ? 0 : unusableNumbers.Max();
            var lastUsableResponseAttempt = usableResponses.Length == 0
                ? 0
                : usableResponses.Max(static pair => pair.Item.ModelAttemptNumber);
            var attemptCount = usesTypedAttemptSequence
                ? attemptNumbers.Length
                : Math.Max(starts.Length, responses.Length + failures.Length + cancellations.Length);
            var invalidTypedAttemptShape = usesTypedAttemptSequence && attemptNumbers.Any(attemptNumber =>
            {
                var attemptEvents = stageEvents.Where(pair =>
                    pair.Item.ModelAttemptNumber == attemptNumber).ToArray();
                var startCount = attemptEvents.Count(static pair =>
                    pair.Item.Kind == DiscoveryEventKind.ModelRequestStarted);
                var providerTerminalCount = attemptEvents.Count(static pair => pair.Item.Kind is
                    DiscoveryEventKind.ModelResponseReceived or DiscoveryEventKind.ModelRequestFailed
                        or DiscoveryEventKind.ModelRequestCancelled);
                var unusableCount = attemptEvents.Count(static pair =>
                    pair.Item.ModelStageDisposition == DiscoveryModelStageDisposition.AttemptUnusable);
                if (startCount == 0)
                    return providerTerminalCount != 0 || unusableCount != 1;
                if (startCount != 1 || providerTerminalCount != 1 || unusableCount > 1)
                    return true;
                var start = attemptEvents.Single(static pair =>
                    pair.Item.Kind == DiscoveryEventKind.ModelRequestStarted);
                var terminal = attemptEvents.Single(static pair => pair.Item.Kind is
                    DiscoveryEventKind.ModelResponseReceived or DiscoveryEventKind.ModelRequestFailed
                        or DiscoveryEventKind.ModelRequestCancelled);
                return terminal.Index <= start.Index
                    || !string.Equals(start.Item.OperationId, terminal.Item.OperationId,
                        StringComparison.Ordinal)
                    || unusableCount == 1 && attemptEvents.Single(static pair =>
                        pair.Item.ModelStageDisposition == DiscoveryModelStageDisposition.AttemptUnusable).Index
                        <= terminal.Index;
            });
            var recoveredAfterUnusable = usesTypedAttemptSequence
                ? lastUsableResponseAttempt > Math.Max(
                    lastUnusableAttempt,
                    failures.Length == 0 ? 0 : failures.Max(static pair => pair.Item.ModelAttemptNumber))
                : lastResponse > Math.Max(lastFailure, lastUnusable);
            var status = invalidAttemptSequence || invalidDisposition
                ? LiveWorkflowProviderStageStatus.Unrecovered
                : cancellations.Length > 0
                ? LiveWorkflowProviderStageStatus.Cancelled
                : finalFallback
                    ? attemptCount > 0 && unusableAttemptCount > 0
                        ? LiveWorkflowProviderStageStatus.FinalFallback
                        : LiveWorkflowProviderStageStatus.Unrecovered
                    : incompleteAttempt || invalidTypedAttemptShape
                        ? LiveWorkflowProviderStageStatus.Unrecovered
                    : failures.Length > 0 || unusableAttempts.Length > 0
                            ? recoveredAfterUnusable
                                ? LiveWorkflowProviderStageStatus.Recovered
                                : LiveWorkflowProviderStageStatus.Unrecovered
                            : attemptCount > 0 && responses.Length == attemptCount
                                ? LiveWorkflowProviderStageStatus.Completed
                                : LiveWorkflowProviderStageStatus.Unrecovered;

            stages.Add(new(
                group.Key,
                attemptCount,
                responses.Length,
                unusableAttemptCount,
                failures.Length,
                cancellations.Length,
                status)
            {
                LastUnusableAttemptNumber = lastUnusableAttempt,
                LastUsableResponseAttemptNumber = lastUsableResponseAttempt,
            });
        }

        var ordered = stages.OrderBy(static item => WorkflowNodeOrder(item.ExecutorId))
            .ThenBy(static item => item.ExecutorId, StringComparer.Ordinal)
            .ToArray();
        var failedAttempts = ordered.Sum(static item => item.FailedAttemptCount);
        var recoveredFailedAttempts = ordered
            .Where(static item => item.Status == LiveWorkflowProviderStageStatus.Recovered)
            .Sum(static item => item.FailedAttemptCount);
        var terminalStages = ordered.Count(static item => item.Status is
            LiveWorkflowProviderStageStatus.FinalFallback
            or LiveWorkflowProviderStageStatus.Unrecovered
            or LiveWorkflowProviderStageStatus.Cancelled);
        return new(Array.AsReadOnly(ordered), failedAttempts, recoveredFailedAttempts, terminalStages);
    }

    private static string CanonicalWorkflowNode(string? nodeId) =>
        nodeId is DiscoveryExecutorIds.InterestMapper or DiscoveryExecutorIds.CoverageReviewer
            or DiscoveryExecutorIds.Ranker or DiscoveryExecutorIds.Presenter
            ? nodeId
            : "unknown";

    private static int WorkflowNodeOrder(string nodeId) => nodeId switch
    {
        DiscoveryExecutorIds.InterestMapper => 0,
        DiscoveryExecutorIds.Discovery => 1,
        DiscoveryExecutorIds.CoverageReviewer => 2,
        DiscoveryExecutorIds.Ranker => 3,
        DiscoveryExecutorIds.Presenter => 4,
        _ => int.MaxValue,
    };

    internal sealed record WorkflowProviderAssessment(
        IReadOnlyList<LiveWorkflowProviderStageEvidence> Stages,
        int FailedAttemptCount,
        int RecoveredFailedAttemptCount,
        int TerminalStageCount)
    {
        public bool HasTerminalFailure => TerminalStageCount > 0;
        public bool HasCancellation => Stages.Any(static item =>
            item.Status == LiveWorkflowProviderStageStatus.Cancelled);
        public bool HasFinalFallback => Stages.Any(static item =>
            item.Status == LiveWorkflowProviderStageStatus.FinalFallback);
    }

    internal static bool HasAgentProviderTerminalFailure(IReadOnlyList<RecommendationRuntimeEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.Any(static item => item.Kind is RecommendationRuntimeEventKind.ModelRequestFailed
            or RecommendationRuntimeEventKind.ModelRequestCancelled);
    }

    internal static string ProjectWorkflowDegradationKind(DiscoveryEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var node = DiscoveryExecutorIds.All.Contains(item.NodeId, StringComparer.Ordinal)
            ? item.NodeId
            : "unknown";
        var kind = item.ModelStageDisposition switch
        {
            DiscoveryModelStageDisposition.AttemptUnusable => "attempt-unusable",
            DiscoveryModelStageDisposition.FinalFallback => "final-fallback",
            _ when item.Kind is DiscoveryEventKind.ModelRequestFailed
                or DiscoveryEventKind.ModelRequestCancelled => "model-failure",
            _ => "degradation",
        };
        return $"{node}:{kind}";
    }

    internal static LiveToolEvidence ProjectTools(IReadOnlyList<RecommendationRuntimeEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var toolEvents = events.Select((item, index) => (item, index))
            .Where(static pair => pair.item.Kind is RecommendationRuntimeEventKind.ToolExecutionStarted
                or RecommendationRuntimeEventKind.ToolCompleted or RecommendationRuntimeEventKind.ToolFailed
                or RecommendationRuntimeEventKind.ToolCancelled)
            .ToArray();
        var starts = toolEvents.Where(static pair =>
            pair.item.Kind == RecommendationRuntimeEventKind.ToolExecutionStarted).ToArray();
        var terminals = toolEvents.Where(static pair => pair.item.Kind is
            RecommendationRuntimeEventKind.ToolCompleted or RecommendationRuntimeEventKind.ToolFailed
                or RecommendationRuntimeEventKind.ToolCancelled).ToArray();
        var observedNames = toolEvents.Select(static pair => pair.item.Kind ==
                RecommendationRuntimeEventKind.ToolExecutionStarted ? pair.item.Target : pair.item.Source)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).ToArray();
        var safeNames = observedNames.Where(ToolSurfaceInvariant.IsReadOnlyToolName)
            .Order(StringComparer.Ordinal).ToArray();
        var unknownNames = observedNames.Length - safeNames.Length;
        var unreconciled = toolEvents.Count(static pair => !IsSafeOperationId(pair.item.OperationId));
        var calls = new List<(int Order, LiveToolCallEvidence Call)>();

        foreach (var operation in toolEvents.Where(static pair => IsSafeOperationId(pair.item.OperationId))
                     .GroupBy(static pair => pair.item.OperationId!, StringComparer.Ordinal))
        {
            var operationStarts = operation.Where(static pair => pair.item.Kind ==
                RecommendationRuntimeEventKind.ToolExecutionStarted).ToArray();
            var operationTerminals = operation.Where(static pair => pair.item.Kind is
                RecommendationRuntimeEventKind.ToolCompleted or RecommendationRuntimeEventKind.ToolFailed
                    or RecommendationRuntimeEventKind.ToolCancelled).ToArray();
            var startName = operationStarts.Length == 1 ? operationStarts[0].item.Target : null;
            var terminalName = operationTerminals.Length == 1 ? operationTerminals[0].item.Source : null;
            var reconciled = operationStarts.Length == 1 && operationTerminals.Length == 1 &&
                string.Equals(startName, terminalName, StringComparison.Ordinal);
            if (!reconciled) unreconciled++;
            var toolName = startName ?? terminalName;
            if (toolName is null || !ToolSurfaceInvariant.IsReadOnlyToolName(toolName)) continue;
            var status = !reconciled
                ? operationStarts.Length == 1 && operationTerminals.Length == 0
                    ? LiveToolCallStatus.Incomplete
                    : LiveToolCallStatus.Unreconciled
                : operationTerminals[0].item.Kind switch
                {
                    RecommendationRuntimeEventKind.ToolCompleted => LiveToolCallStatus.Completed,
                    RecommendationRuntimeEventKind.ToolFailed => LiveToolCallStatus.Failed,
                    RecommendationRuntimeEventKind.ToolCancelled => LiveToolCallStatus.Cancelled,
                    _ => LiveToolCallStatus.Unreconciled,
                };
            var arguments = operationStarts.Length == 1
                ? ProjectArguments(operationStarts[0].item.PayloadPreview)
                : [];
            calls.Add((operation.Min(static pair => pair.index),
                new(operation.Key, toolName, status, arguments)));
        }

        return new(
            JournalObserved: events.Count > 0,
            ToolNames: Array.AsReadOnly(safeNames),
            Executed: starts.Length,
            Completed: terminals.Count(static pair => pair.item.Kind == RecommendationRuntimeEventKind.ToolCompleted),
            Failed: terminals.Count(static pair => pair.item.Kind == RecommendationRuntimeEventKind.ToolFailed),
            Cancelled: terminals.Count(static pair => pair.item.Kind == RecommendationRuntimeEventKind.ToolCancelled),
            UnknownNameCount: unknownNames,
            Calls: Array.AsReadOnly(calls.OrderBy(static item => item.Order).Select(static item => item.Call).ToArray()),
            UnreconciledCount: unreconciled);
    }

    internal static bool IsSafeOperationId(string? operationId) =>
        operationId is { Length: > 0 and <= 80 } && operationId.All(static character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.');

    internal static bool IsAllowedToolArgument(string? name) =>
        name is not null && AllowedToolArguments.ContainsKey(name);

    private static IReadOnlyList<LiveToolArgumentEvidence> ProjectArguments(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return [];
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            var arguments = new List<LiveToolArgumentEvidence>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!AllowedToolArguments.TryGetValue(property.Name, out var canonical)) continue;
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "null",
                    _ => null,
                };
                if (value is null) continue;
                arguments.Add(new(canonical, LiveEvidenceText.Bound(value, 240)));
            }
            return Array.AsReadOnly(arguments.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray());
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IChatClient CreateBudgetedClient(int maximumOutputTokens, string deployment)
    {
        if (maximumOutputTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        return RecommendationAgentFactory.CreateConfiguredChatClient(deployment)
            .AsBuilder()
            .ConfigureOptions(options => options.MaxOutputTokens = maximumOutputTokens)
            .Build();
    }

    private sealed class DefaultAgentSubject : ILiveEvalSubject
    {
        public string ArmId => "robin-agent-live";
        public string ModelId => Config.Deployments.SubjectLabel;
        public LiveSubjectArchitecture Architecture => LiveSubjectArchitecture.Agent;

        public async Task<LiveSubjectObservation> RunAsync(
            LiveSubjectRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            try
            {
                using var client = CreateBudgetedClient(request.MaxOutputTokens, Config.Model);
                var run = await RecommendationRunEngine.RunAsync(
                    new RecommendationRunOptions(
                        request.PersonaId,
                        Arm: RecommendationExecutionArm.LiveAzure,
                        ChatClient: client,
                        SessionRequest: request.Query),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var status = run.Status switch
                {
                    RecommendationRunStatus.Completed => LiveSubjectStatus.Completed,
                    RecommendationRunStatus.Abstained => LiveSubjectStatus.Abstained,
                    RecommendationRunStatus.Cancelled => LiveSubjectStatus.Cancelled,
                    _ => LiveSubjectStatus.Failed,
                };
                var tools = ProjectTools(run.Events);
                var usage = ProjectUsage(run.ProviderUsage, ModelId);

                if (status is LiveSubjectStatus.Failed or LiveSubjectStatus.Cancelled || run.Outcome is null)
                {
                    var providerFailure = HasAgentProviderTerminalFailure(run.Events);
                    return new(AgentEval.Evals.Meta.MeasurementState.NotMeasured, status, null, tools, null, usage,
                        status == LiveSubjectStatus.Cancelled
                            ? LiveEvalFailureCode.Cancelled
                            : providerFailure
                                ? LiveEvalFailureCode.SubjectProviderFailure
                                : LiveEvalFailureCode.SubjectExecutionFailed,
                        status == LiveSubjectStatus.Cancelled
                            ? "The agent subject was cancelled."
                            : providerFailure
                                ? "The agent provider request failed or was cancelled."
                                : $"The agent subject failed ({LiveEvidenceText.SafeIdentifier(run.FailureKind)})." );
                }

                var screen = RecommendationArtifactComposer.ComposeScreened(run);
                var response = screen.IsSafe ? screen.Answer ?? string.Empty : string.Empty;
                return new(AgentEval.Evals.Meta.MeasurementState.Measured, status, response, tools, null, usage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return LiveSubjectObservation.NotMeasured(LiveSubjectStatus.Failed, Architecture) with
                {
                    FailureCode = LiveEvalFailureCode.SubjectExecutionFailed,
                    FailureDetail = $"The agent subject threw {LiveEvidenceText.SafeIdentifier(exception.GetType().Name)}.",
                };
            }
        }
    }

    private sealed class DefaultWorkflowSubject : ILiveEvalSubject
    {
        public string ArmId => "discovery-workflow-live";
        public string ModelId => Config.Deployments.SubjectLabel;
        public LiveSubjectArchitecture Architecture => LiveSubjectArchitecture.Workflow;

        public async Task<LiveSubjectObservation> RunAsync(
            LiveSubjectRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            try
            {
                using var client = CreateBudgetedClient(request.MaxOutputTokens, Config.Model);
                var recorder = new RecordingDiscoveryProgressSink();
                var run = await GalaxusDiscoveryLoop.RunAsync(
                    request.PersonaId,
                    new DiscoveryLoopOptions(
                        Offline: false,
                        SessionRequest: request.Query,
                        ChatClient: client,
                        Progress: recorder),
                    cancellationToken).ConfigureAwait(false);

                var provider = AssessWorkflowProvider(recorder.Events);
                var workflow = ProjectWorkflow(run, recorder.Events, provider);
                var usage = ProjectUsage(run.State.ProviderUsage, ModelId);
                var providerFailed = provider.HasTerminalFailure;
                if (providerFailed || run.Failed || run.State.CustomerAnswerSafety is null)
                {
                    var status = provider.HasCancellation ? LiveSubjectStatus.Cancelled : LiveSubjectStatus.Failed;
                    var failureCode = provider.HasCancellation
                        ? LiveEvalFailureCode.Cancelled
                        : provider.HasFinalFallback
                            ? LiveEvalFailureCode.SubjectModelStageUnusable
                            : providerFailed
                                ? LiveEvalFailureCode.SubjectProviderFailure
                                : LiveEvalFailureCode.SubjectExecutionFailed;
                    var failureDetail = failureCode switch
                    {
                        LiveEvalFailureCode.Cancelled => "A required workflow model stage was cancelled by the caller.",
                        LiveEvalFailureCode.SubjectModelStageUnusable =>
                            "A required workflow model stage selected its bounded fallback after unusable model output.",
                        LiveEvalFailureCode.SubjectProviderFailure =>
                            "A required workflow provider attempt did not recover.",
                        _ => "The workflow subject did not produce a complete screened answer.",
                    };
                    return new(AgentEval.Evals.Meta.MeasurementState.NotMeasured, status,
                        null, LiveToolEvidence.NotApplicable, workflow, usage,
                        failureCode, failureDetail);
                }

                var screen = run.State.CustomerAnswerSafety;
                var response = screen.IsSafe ? screen.Answer ?? string.Empty : string.Empty;
                return new(AgentEval.Evals.Meta.MeasurementState.Measured, LiveSubjectStatus.Completed,
                    response, LiveToolEvidence.NotApplicable, workflow, usage);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return LiveSubjectObservation.NotMeasured(LiveSubjectStatus.Failed, Architecture) with
                {
                    FailureCode = LiveEvalFailureCode.SubjectExecutionFailed,
                    FailureDetail = $"The workflow subject threw {LiveEvidenceText.SafeIdentifier(exception.GetType().Name)}.",
                };
            }
        }

        private static LiveWorkflowEvidence ProjectWorkflow(
            DiscoveryRunResult run,
            IReadOnlyList<DiscoveryEvent> events,
            WorkflowProviderAssessment provider)
        {
            var executorSet = DiscoveryExecutorIds.All.ToHashSet(StringComparer.Ordinal);
            var routeSet = new HashSet<string>(
                [DiscoveryRouteIds.MapToDiscovery, DiscoveryRouteIds.DiscoveryToReview,
                 DiscoveryRouteIds.ReviewToMoreDiscovery, DiscoveryRouteIds.ReviewToRanker,
                 DiscoveryRouteIds.RankerToPresenter], StringComparer.Ordinal);
            var observedExecutors = events.Where(item => item.Kind == DiscoveryEventKind.NodeStarted)
                .Select(static item => item.NodeId).ToArray();
            var executors = DiscoveryExecutorIds.All.Select(id => new LiveExecutorEvidence(
                id, observedExecutors.Count(observed => string.Equals(observed, id, StringComparison.Ordinal)))).ToArray();
            var safeRoutes = run.RoutesTaken.Where(routeSet.Contains).ToArray();
            var degradationEvents = events.Where(item => item.Kind is DiscoveryEventKind.Degraded
                or DiscoveryEventKind.ModelRequestFailed or DiscoveryEventKind.ModelRequestCancelled).ToArray();
            var recoveryKinds = provider.Stages
                .Where(static item => item.Status != LiveWorkflowProviderStageStatus.Completed)
                .Select(static item => $"{item.ExecutorId}:{item.Status switch
                {
                    LiveWorkflowProviderStageStatus.Recovered => "provider-recovered",
                    LiveWorkflowProviderStageStatus.FinalFallback => "final-fallback",
                    LiveWorkflowProviderStageStatus.Unrecovered => "provider-unrecovered",
                    LiveWorkflowProviderStageStatus.Cancelled => "provider-cancelled",
                    _ => "provider-observed",
                }}");
            var degradationKinds = degradationEvents.Select(ProjectWorkflowDegradationKind)
                .Concat(recoveryKinds)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new(
                Array.AsReadOnly(executors),
                Array.AsReadOnly(safeRoutes),
                run.State.DiscoveryRound,
                run.State.MaxRounds,
                run.SuperSteps,
                Enum.IsDefined(run.State.StopReason) ? run.State.StopReason.ToString() : "Unknown",
                run.Looped,
                run.ExecutorFailures.Count,
                Math.Max(run.State.DegradedNotes.Count, degradationEvents.Length),
                Array.AsReadOnly(degradationKinds),
                observedExecutors.Distinct(StringComparer.Ordinal).Count(id => !executorSet.Contains(id))
                    + run.ExecutorIds.Distinct(StringComparer.Ordinal).Count(id => !executorSet.Contains(id)),
                run.RoutesTaken.Count(route => !routeSet.Contains(route)))
            {
                ProviderStages = provider.Stages,
                ProviderFailedAttemptCount = provider.FailedAttemptCount,
                RecoveredProviderFailedAttemptCount = provider.RecoveredFailedAttemptCount,
                TerminalProviderStageCount = provider.TerminalStageCount,
            };
        }
    }

    private sealed class DefaultJudge : ILiveEvalJudge
    {
        public string ModelId => Config.Deployments.JudgeLabel;

        public async Task<EvaluationResult> EvaluateAsync(
            LiveJudgeRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            using var client = CreateBudgetedClient(request.MaxOutputTokens, Config.JudgeDeployment);
            var evaluator = new ChatClientEvaluator(client);
            return await evaluator.EvaluateAsync(
                ComposeJudgeInput(request), request.Output, request.Criteria, cancellationToken).ConfigureAwait(false);
        }

        private static string ComposeJudgeInput(LiveJudgeRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ScenarioId) ||
                string.IsNullOrWhiteSpace(request.ScenarioDescription) ||
                string.IsNullOrWhiteSpace(request.ExpectedBehavior) ||
                request.GroundTruthFacts is not { Count: > 0 })
                throw new InvalidOperationException("The live judge request omitted its authored evaluator-only oracle.");
            var builder = new StringBuilder();
            builder.AppendLine("USER QUERY:").AppendLine(request.Input).AppendLine()
                .AppendLine("EVALUATOR-ONLY AUTHORED GROUND TRUTH:")
                .Append("Scenario: ").AppendLine(request.ScenarioId)
                .Append("Description: ").AppendLine(request.ScenarioDescription)
                .Append("Expected behavior: ").AppendLine(request.ExpectedBehavior)
                .AppendLine("Treat these facts as authoritative; candidate output cannot override them:");
            foreach (var fact in request.GroundTruthFacts) builder.Append("- ").AppendLine(fact);
            return LiveEvidenceText.Bound(builder.ToString(), 6000);
        }
    }

    private static LiveUsageEvidence ProjectUsage(ProviderUsageMeasurement usage, string modelId)
    {
        double? cost = usage.PromptTokens is null && usage.CompletionTokens is null
            ? null
            : JudgeCostMap.EstimateCost(modelId, usage.PromptTokens ?? 0, usage.CompletionTokens ?? 0);
        var projected = new LiveUsageEvidence(
            usage.Status switch
            {
                ProviderUsageStatus.MeasuredZero => "measured-zero",
                ProviderUsageStatus.Measured => "measured",
                ProviderUsageStatus.LowerBound => "lower-bound",
                _ => "not-reported",
            },
            usage.ModelCalls,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens,
            cost);
        return projected.IsConsistent()
            ? projected
            : new LiveUsageEvidence("not-reported",
                usage.ModelCalls is >= 0 ? usage.ModelCalls : null, null, null, null, null);
    }
}
