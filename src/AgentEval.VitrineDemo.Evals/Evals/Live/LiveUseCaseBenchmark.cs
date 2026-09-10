// SPDX-License-Identifier: MIT

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using System.Security.Cryptography;
using System.Text;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Tools;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Evals.Live;

/// <summary>Stable definition and admitted-check identities for the paid use-case benchmark.</summary>
public static class LiveUseCaseBenchmark
{
    public const string DefinitionKey = "vitrine-live-use-cases";
    public const string DefinitionVersion = "1.1.0";
    public const string JudgePromptId = "vitrine-live-use-cases-v2";
    public const string UseCaseQualityCheckKey = "vitrine.live.use-case-quality";
    public const string ResponseObservedCheckKey = "vitrine.live.response-observed";
    public const string AgentToolJournalCheckKey = "vitrine.live.agent-tool-journal";
    public const string WorkflowTraceCheckKey = "vitrine.live.workflow-trace";

    internal const string ObservationMetadataKey = "vitrine.live.typed-observation";

    internal static BenchmarkDefinition CreateDefinition(
        ILiveEvalJudge judge,
        int judgeMaximumOutputTokens,
        double passThreshold,
        IReadOnlyList<LiveUseCaseScenario> scenarios,
        LiveObservationRegistry registry,
        LiveProgressReporter progress)
    {
        var rubric = JudgeRubricFor(scenarios);
        var definitionVersion = DefinitionVersionFor(
            scenarios, passThreshold, judge.ModelId, judgeMaximumOutputTokens);
        var routedJudge = new ScenarioRoutingEvaluator(
            judge, judgeMaximumOutputTokens, passThreshold, registry, progress);
        var llm = new AtomicLlmEval(
            routedJudge,
            UseCaseQualityCheckKey,
            "Canonical use-case criteria",
            "recommendation-use-case",
            definitionVersion,
            rubric,
            passThreshold,
            LiveEvalServices.SafeModelId(judge.ModelId),
            promptId: JudgePromptId);

        return new BenchmarkDefinition(
            DefinitionKey,
            definitionVersion,
            [.. scenarios.Select(scenario => new TestCase
            {
                Id = scenario.Id,
                Name = scenario.Title,
                Input = scenario.Query,
                PassingScore = (int)Math.Round(passThreshold * 100, MidpointRounding.AwayFromZero),
            })],
            [
                new AdmittedCheck(llm, ChanceFloor.NotDerivable(
                    "the judge grades unbounded recommendation prose against four use-case criteria; no random answer population is defined.")),
                new AdmittedCheck(new ResponseObservedEval(progress), ChanceFloor.NotDerivable(
                    "non-empty response observation is a deterministic instrumentation predicate, not a random-choice task.")),
                new AdmittedCheck(new AgentToolJournalEval(progress), ChanceFloor.NotDerivable(
                    "tool-journal reconciliation is a deterministic trace invariant with no random-choice baseline.")),
                new AdmittedCheck(new WorkflowTraceEval(progress), ChanceFloor.NotDerivable(
                    "workflow topology reconciliation is a deterministic trace invariant with no random-choice baseline.")),
            ]);
    }

    internal static string DefinitionVersionFor(
        IReadOnlyList<LiveUseCaseScenario> scenarios,
        double passThreshold,
        string judgeModelId,
        int judgeMaximumOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        if (scenarios.Count == 0) throw new ArgumentException("At least one scenario is required.", nameof(scenarios));
        if (!double.IsFinite(passThreshold) || passThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(passThreshold));
        if (judgeMaximumOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(judgeMaximumOutputTokens));
        var cases = string.Join('.', scenarios.Select(static scenario => scenario.Id));
        var thresholdBits = BitConverter.DoubleToInt64Bits(passThreshold)
            .ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        return $"{DefinitionVersion}+cases.{cases}.rubric.{RubricHashFor(scenarios)[..16]}.threshold.{thresholdBits}.judge.{LiveEvalServices.SafeModelId(judgeModelId)}.tokens.{judgeMaximumOutputTokens}";
    }

    internal static IReadOnlyList<string> JudgeRubricFor(IReadOnlyList<LiveUseCaseScenario> scenarios)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        return Array.AsReadOnly(scenarios.SelectMany(static scenario =>
                new[]
                {
                    $"scenario:{scenario.Id}",
                    $"description:{scenario.Description}",
                    $"expected:{scenario.ExpectedBehavior}",
                }
                .Concat(scenario.GroundTruthFacts.Select(static fact => $"fact:{fact}"))
                .Concat(scenario.Criteria.Select(static criterion =>
                    $"criterion:{criterion.Id}:{criterion.Text}")))
            .ToArray());
    }

    internal static string RubricHashFor(IReadOnlyList<LiveUseCaseScenario> scenarios)
    {
        var canonical = string.Join('\n', JudgeRubricFor(scenarios));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

internal sealed record LiveBenchmarkObservation(
    VitrineEvaluationPlan Plan,
    string ScenarioId,
    string ArmId,
    int Repetition,
    LiveSubjectArchitecture Architecture,
    LiveSubjectObservation Subject);

internal sealed class LiveObservationRegistry
{
    private readonly Dictionary<string, LiveBenchmarkObservation> _byQuery = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal void Record(string query, LiveBenchmarkObservation observation)
    {
        lock (_gate) _byQuery[query] = observation;
    }

    internal bool TryGet(string query, out LiveBenchmarkObservation observation)
    {
        lock (_gate) return _byQuery.TryGetValue(query, out observation!);
    }
}

internal sealed class LiveProgressReporter(
    VitrineEvaluationPlan plan,
    IProgress<LiveEvalProgress>? progress)
{
    internal void Report(
        LiveEvalProgressPhase phase,
        string detail,
        LiveBenchmarkObservation? observation = null,
        string? checkKey = null,
        MeasurementState? measurement = null,
        bool? passed = null)
    {
        try
        {
            var safeDetail = LiveEvidenceText.Bound(detail, 320).Replace('\n', ' ');
            progress?.Report(new(
                plan,
                phase,
                observation?.ScenarioId,
                observation?.ArmId,
                observation?.Repetition,
                checkKey,
                measurement,
                passed,
                safeDetail));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Observation must never influence the subject or its verdict.
        }
    }
}

internal sealed class ScenarioRoutingEvaluator(
    ILiveEvalJudge judge,
    int maximumOutputTokens,
    double passThreshold,
    LiveObservationRegistry registry,
    LiveProgressReporter progress) : IEvaluator
{
    public async Task<EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken cancellationToken = default)
    {
        if (!registry.TryGet(input, out var observation))
            return Failed("The typed subject observation was unavailable.");

        progress.Report(LiveEvalProgressPhase.CheckStarting, "Use-case judge started.", observation,
            LiveUseCaseBenchmark.UseCaseQualityCheckKey);

        if (observation.Subject.Measurement != MeasurementState.Measured)
        {
            progress.Report(LiveEvalProgressPhase.CheckCompleted, "Use-case judge was not run because the subject was not measured.",
                observation, LiveUseCaseBenchmark.UseCaseQualityCheckKey, MeasurementState.NotMeasured, null);
            return Failed("The subject did not produce a measurable response; the judge was not invoked.");
        }

        var scenario = LiveUseCaseScenarios.Require(observation.ScenarioId);
        EvaluationResult result;
        try
        {
            result = await judge.EvaluateAsync(
                new LiveJudgeRequest(
                    scenario.Id,
                    input,
                    output,
                    scenario.Description,
                    scenario.ExpectedBehavior,
                    scenario.GroundTruthFacts,
                    scenario.Criteria.Select(static item => item.Text).ToArray(),
                    maximumOutputTokens),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = Failed("The judge request failed; provider details were withheld.");
        }

        result = Sanitize(result, scenario);
        var state = result.EvaluationFailed ? MeasurementState.NotMeasured : MeasurementState.Measured;
        progress.Report(LiveEvalProgressPhase.CheckCompleted,
            result.EvaluationFailed ? "Use-case judge did not produce a verdict." : "Use-case judge completed.",
            observation, LiveUseCaseBenchmark.UseCaseQualityCheckKey, state,
            result.EvaluationFailed ? null : result.OverallScore / 100d >= passThreshold);
        return result;
    }

    private static EvaluationResult Sanitize(EvaluationResult? source, LiveUseCaseScenario scenario)
    {
        if (source is null) return Failed("The judge returned no result.");
        var sourceCriteria = (source.CriteriaResults ?? [])
            .Where(static item => item is not null && !string.IsNullOrWhiteSpace(item.Criterion))
            .ToArray();
        var expectedTexts = scenario.Criteria.Select(static item => item.Text)
            .ToHashSet(StringComparer.Ordinal);
        var byText = sourceCriteria.GroupBy(static item => item.Criterion, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var exactIdentity = sourceCriteria.Length == scenario.Criteria.Count
            && byText.Count == scenario.Criteria.Count
            && byText.All(item => expectedTexts.Contains(item.Key) && item.Value.Length == 1);
        if (!exactIdentity)
            return Failed("The judge did not return the exact authored criterion identities.");
        var projected = new List<CriterionResult>(scenario.Criteria.Count);
        foreach (var expected in scenario.Criteria)
        {
            var result = byText[expected.Text][0];
            projected.Add(new CriterionResult
            {
                Criterion = expected.Text,
                Met = result.Met,
                Explanation = Bound(result.Explanation, 320),
            });
        }

        var failed = source.EvaluationFailed;
        var derivedScore = projected.Count == 0
            ? 0
            : (int)Math.Round(
                projected.Count(static item => item.Met) * 100d / projected.Count,
                MidpointRounding.AwayFromZero);
        return new EvaluationResult
        {
            // The exact authored criterion census is authoritative. Trusting an unrelated free-form
            // overall number lets a judge return four false predicates and a flattering 100.
            OverallScore = failed ? 0 : derivedScore,
            Summary = failed
                ? "The judge did not return a complete parseable verdict."
                : Bound(source.Summary, 480),
            Improvements = (source.Improvements ?? []).Take(4).Select(item => Bound(item, 240)).ToArray(),
            CriteriaResults = failed ? [] : projected,
            EvaluationFailed = failed,
            InputTokenCount = source.InputTokenCount is >= 0 ? source.InputTokenCount : null,
            OutputTokenCount = source.OutputTokenCount is >= 0 ? source.OutputTokenCount : null,
        };
    }

    private static EvaluationResult Failed(string summary) => new()
    {
        EvaluationFailed = true,
        Summary = summary,
    };

    private static string Bound(string? text, int maximum)
    {
        if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
        var safe = RecommendationRuntimeEvents.SafePreview(text);
        return safe.Length <= maximum ? safe : maximum == 1 ? "…" : safe[..(maximum - 1)] + "…";
    }
}

internal abstract class LiveObservationEval(
    string key,
    string name,
    LiveProgressReporter progress) : AtomicCodeEval(key, name, "recommendation-observation", "1.0.0")
{
    protected sealed override EvalResult Evaluate(EvalInput input)
    {
        if (input.Metadata is null ||
            !input.Metadata.TryGetValue(LiveUseCaseBenchmark.ObservationMetadataKey, out var raw) ||
            raw is not LiveBenchmarkObservation observation)
            return NotApplicable("this check requires the typed live-observation boundary.");

        progress.Report(LiveEvalProgressPhase.CheckStarting, $"{Name} started.", observation, Key);
        EvalResult result;
        if (!AppliesTo(observation))
        {
            result = NotApplicable(NotApplicableReason);
        }
        else if (observation.Subject.Measurement != MeasurementState.Measured)
        {
            result = EvalResult.Skipped(this, "NOT MEASURED: the subject observation did not complete.");
        }
        else
        {
            result = EvaluateMeasured(input, observation);
        }
        var state = result.Score.CensusBucket();
        progress.Report(LiveEvalProgressPhase.CheckCompleted,
            state == MeasurementState.Measured ? $"{Name} completed." : $"{Name} was not measured.",
            observation, Key, state, state == MeasurementState.Measured ? result.Score.Passed : null);
        return result;
    }

    protected abstract EvalResult EvaluateMeasured(EvalInput input, LiveBenchmarkObservation observation);
    protected virtual bool AppliesTo(LiveBenchmarkObservation observation) => true;
    protected virtual string NotApplicableReason => "this architecture-specific check does not apply to this arm.";

    protected EvalResult Verdict(bool passed, string summary, string reference)
    {
        var result = Build(passed ? 1 : 0, passed, passed ? "none" : "high", evidence:
            [new EvalEvidence("typed-observation", reference, summary)]);
        return result with { Details = result.Details with { Summary = summary } };
    }
}

internal sealed class ResponseObservedEval(LiveProgressReporter progress)
    : LiveObservationEval(
        LiveUseCaseBenchmark.ResponseObservedCheckKey,
        "A non-empty customer response was instrumented",
        progress)
{
    protected override EvalResult EvaluateMeasured(EvalInput input, LiveBenchmarkObservation observation) =>
        Verdict(!string.IsNullOrWhiteSpace(input.Response),
            string.IsNullOrWhiteSpace(input.Response)
                ? "the measured subject produced no customer-facing response."
                : "the measured subject produced a non-empty customer-facing response.",
            observation.ScenarioId);
}

internal sealed class AgentToolJournalEval(LiveProgressReporter progress)
    : LiveObservationEval(
        LiveUseCaseBenchmark.AgentToolJournalCheckKey,
        "Agent tool journal is complete and read-only",
        progress)
{
    protected override bool AppliesTo(LiveBenchmarkObservation observation) =>
        observation.Architecture == LiveSubjectArchitecture.Agent;

    protected override EvalResult EvaluateMeasured(EvalInput input, LiveBenchmarkObservation observation)
    {
        var tools = observation.Subject.Tools;
        var calls = tools.Calls ?? [];
        var terminal = tools.Completed + tools.Failed + tools.Cancelled;
        var scenario = LiveUseCaseScenarios.Require(observation.ScenarioId);
        var expectation = scenario.AgentToolExpectation;
        var executionCountFitsStatus = expectation.RequiresAbstention
            ? observation.Subject.Status == LiveSubjectStatus.Abstained && tools.Executed == 0 && calls.Count == 0
            : observation.Subject.Status == LiveSubjectStatus.Completed && tools.Executed > 0;
        var completedCalls = calls.Where(static call => call.Status == LiveToolCallStatus.Completed).ToArray();
        var requiredToolsObserved = expectation.RequiredTools.All(required => completedCalls.Any(call =>
            string.Equals(call.ToolName, required, StringComparison.Ordinal)));
        var forbiddenToolsAbsent = expectation.ForbiddenTools.All(forbidden => calls.All(call =>
            !string.Equals(call.ToolName, forbidden, StringComparison.Ordinal)));
        var forbiddenProductsAbsent = calls.Where(static call => call.ToolName == nameof(GalaxusTools.PresentRecommendation))
            .Select(call => Argument(call, "sku"))
            .All(sku => sku is not null && !expectation.ForbiddenPresentedSkus.Contains(sku, StringComparer.Ordinal));
        var correctPersona = calls.Where(static call => call.ToolName is
                nameof(GalaxusTools.GetUserProfile) or nameof(GalaxusTools.GetPurchaseHistory) or
                nameof(GalaxusTools.GetInterestMap))
            .All(call => string.Equals(Argument(call, "userId"), scenario.PersonaId, StringComparison.Ordinal));
        var protocolOrder = expectation.RequiresAbstention || RecommendationProtocolIsComplete(completedCalls);
        var passed = tools.JournalObserved && tools.UnknownNameCount == 0 && tools.Executed == terminal
            && tools.Failed == 0 && tools.Cancelled == 0
            && tools.UnreconciledCount == 0 && calls.Count == tools.Executed
            && executionCountFitsStatus
            && requiredToolsObserved && forbiddenToolsAbsent && forbiddenProductsAbsent && correctPersona && protocolOrder
            && tools.ToolNames.All(ToolSurfaceInvariant.IsReadOnlyToolName)
            && calls.All(call => ToolSurfaceInvariant.IsReadOnlyToolName(call.ToolName));
        return Verdict(passed,
            passed
                ? expectation.RequiresAbstention
                    ? "the authored thin-signal scenario stopped at the deterministic abstention gate with zero tool calls."
                    : $"the tool journal observed {tools.Executed} operation-correlated execution(s), the required persona/search/detail/presentation protocol, and only successful read-only terminal events."
                : "the tool journal was absent, uncorrelated, used a wrong persona/unknown tool, recorded a failure, or did not satisfy this scenario's authored call protocol.",
            observation.ScenarioId);
    }

    private static bool RecommendationProtocolIsComplete(IReadOnlyList<LiveToolCallEvidence> calls)
    {
        var profile = IndexOf(calls, nameof(GalaxusTools.GetUserProfile));
        var interest = IndexOf(calls, nameof(GalaxusTools.GetInterestMap));
        var search = IndexOf(calls, nameof(GalaxusTools.SearchProductsByMeaning));
        if (profile != 0 || interest <= profile || search <= interest) return false;

        var presentations = calls.Select((call, index) => (call, index))
            .Where(item => string.Equals(item.call.ToolName, nameof(GalaxusTools.PresentRecommendation),
                StringComparison.Ordinal)).ToArray();
        if (presentations.Length == 0) return false;
        foreach (var (presentation, index) in presentations)
        {
            var sku = Argument(presentation, "sku");
            if (string.IsNullOrWhiteSpace(sku) || !calls.Take(index).Any(call =>
                    string.Equals(call.ToolName, nameof(GalaxusTools.GetProductDetails), StringComparison.Ordinal) &&
                    string.Equals(Argument(call, "productId"), sku, StringComparison.Ordinal)))
                return false;
        }
        return true;
    }

    private static int IndexOf(IReadOnlyList<LiveToolCallEvidence> calls, string toolName)
    {
        for (var index = 0; index < calls.Count; index++)
            if (string.Equals(calls[index].ToolName, toolName, StringComparison.Ordinal)) return index;
        return -1;
    }

    private static string? Argument(LiveToolCallEvidence call, string name) =>
        (call.Arguments ?? []).FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal))?.Value;
}

internal sealed class WorkflowTraceEval(LiveProgressReporter progress)
    : LiveObservationEval(
        LiveUseCaseBenchmark.WorkflowTraceCheckKey,
        "Workflow executors and routes completed without failure",
        progress)
{
    protected override bool AppliesTo(LiveBenchmarkObservation observation) =>
        observation.Architecture == LiveSubjectArchitecture.Workflow;

    protected override EvalResult EvaluateMeasured(EvalInput input, LiveBenchmarkObservation observation)
    {
        if (observation.Subject.Workflow is not { } workflow)
            return EvalResult.Skipped(this, "NOT MEASURED: the workflow trace was not observed.");

        var expectedRoutes = new List<string> { DiscoveryRouteIds.MapToDiscovery };
        for (var round = 1; round <= workflow.DiscoveryRounds; round++)
        {
            expectedRoutes.Add(DiscoveryRouteIds.DiscoveryToReview);
            if (round < workflow.DiscoveryRounds) expectedRoutes.Add(DiscoveryRouteIds.ReviewToMoreDiscovery);
        }
        expectedRoutes.Add(DiscoveryRouteIds.ReviewToRanker);
        expectedRoutes.Add(DiscoveryRouteIds.RankerToPresenter);
        var duplicateExecutors = workflow.Executors.GroupBy(static item => item.ExecutorId, StringComparer.Ordinal)
            .Any(static group => group.Count() != 1);
        var counts = workflow.Executors.GroupBy(static item => item.ExecutorId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Sum(item => item.ExecutionCount),
                StringComparer.Ordinal);
        var terminalStop = Enum.TryParse<DiscoveryStopReason>(workflow.StopReason, out var stopReason)
            && stopReason is DiscoveryStopReason.CoverageSufficient or DiscoveryStopReason.RoundLimitReached
                or DiscoveryStopReason.NoProgress or DiscoveryStopReason.GapsUnresolvable;
        var stopMatchesRounds = stopReason != DiscoveryStopReason.RoundLimitReached ||
            workflow.DiscoveryRounds == workflow.MaximumRounds;
        var passed = !duplicateExecutors && workflow.Executors.Count == DiscoveryExecutorIds.All.Count
            && workflow.FailureCount == 0 && workflow.UnknownExecutorCount == 0
            && workflow.UnknownRouteCount == 0 && workflow.SuperSteps > 0
            && workflow.SuperSteps == counts.Values.Sum()
            && workflow.DiscoveryRounds is > 0 && workflow.DiscoveryRounds <= workflow.MaximumRounds
            && workflow.Routes.SequenceEqual(expectedRoutes, StringComparer.Ordinal)
            && counts.GetValueOrDefault(DiscoveryExecutorIds.InterestMapper) == 1
            && counts.GetValueOrDefault(DiscoveryExecutorIds.Discovery) == workflow.DiscoveryRounds
            && counts.GetValueOrDefault(DiscoveryExecutorIds.CoverageReviewer) == workflow.DiscoveryRounds
            && counts.GetValueOrDefault(DiscoveryExecutorIds.Ranker) == 1
            && counts.GetValueOrDefault(DiscoveryExecutorIds.Presenter) == 1
            && workflow.Looped == (workflow.DiscoveryRounds > 1)
            && terminalStop && stopMatchesRounds;
        return Verdict(passed,
            passed
                ? $"all five executors ran; {workflow.Routes.Count} route fact(s), {workflow.DiscoveryRounds} round(s), no execution failures, and {workflow.DegradationCount} disclosed degradation event(s) [{string.Join(", ", workflow.DegradationKinds)}]."
                : $"the workflow trace was incomplete, unknown, failed, or did not traverse the required forward route sequence; {workflow.DegradationCount} degradation event(s) were disclosed.",
            observation.ScenarioId);
    }
}
