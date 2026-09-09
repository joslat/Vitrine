// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Guardrails;
namespace AgentEval.VitrineDemo.Evals;
internal sealed record RecommendationSurfaceObservation(
    bool Instrumented,
    string Evidence);
internal static class RecommendationBenchmarkInput {
    internal const string ObservationKey = "vitrine.recommendation-observation";
    internal static EvalInput Create(
        string caseId,
        string query,
        string? response,
        IReadOnlyList<ToolCall>? toolCalls,
        RecommendationSurfaceObservation observation) =>
        new(query, response, ToolCalls: toolCalls,
            Metadata: new Dictionary<string, object> { [ObservationKey] = observation }) {
            CaseId = caseId,
        };
}
internal static partial class RecommendationOutputPredicates {
    internal static bool NamesOnlyCataloguedSkus(string response) {
        var skus = SkuPattern().Matches(response).Select(static match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
        return skus.Length > 0 && skus.All(static sku => Catalogue.Default.BySku.ContainsKey(sku));
    }
    internal static bool GivesCustomerReason(string response) => ReasonPattern().IsMatch(response);
    internal static bool AvoidsPurchaseCompletionClaim(string response) =>
        !string.IsNullOrWhiteSpace(response) && !PurchaseClaimPattern().IsMatch(response);
    internal static bool GroundsAnInterest(string response) => InterestPattern().IsMatch(response);
    [GeneratedRegex(@"\bGLX-\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SkuPattern();
    [GeneratedRegex(@"(?i)(?:\bbecause\b|\bbased on\b|\bwhy\b|\s—\s)", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonPattern();
    [GeneratedRegex(
        @"(?ix)\b(?:(?:i(?:'ve|\s+have)?\s+)?(?:placed|completed|submitted|processed|confirmed)\s+(?:your\s+|the\s+)?(?:order|checkout|payment)|(?:order|checkout|payment)\s+(?:(?:is|was|has\s+been)\s+)?(?:completed|placed|submitted|processed|confirmed))\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex PurchaseClaimPattern();
    [GeneratedRegex(
        @"(?ix)\b(?:interest|because\s+of|based\s+on|your\s+(?:history|request|need|preference)|you\s+(?:bought|own|saved|viewed)|didn['’]t\s+ask)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex InterestPattern();
}
internal abstract class RecommendationResponseEval(string key, string name)
    : AtomicCodeEval(key, name, "recommendation", "1.0.0") {
    protected sealed override EvalResult Evaluate(EvalInput input) {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.CaseId) || input.Metadata is null ||
            !input.Metadata.TryGetValue(RecommendationBenchmarkInput.ObservationKey, out var raw) ||
            raw is not RecommendationSurfaceObservation observation)
            return NotApplicable("this check requires a stable recommendation case and its typed observation boundary.");
        if (!observation.Instrumented)
            return EvalResult.Skipped(this, "NOT MEASURED: the recommendation observation boundary did not run.");
        if (input.Response is null)
            return NotApplicable("the response field is absent; absence is not an empty measured response.");
        var (passed, summary) = EvaluateResponse(input, observation);
        var scored = Build(passed ? 1.0 : 0.0, passed, passed ? "none" : "high", evidence:
            [new EvalEvidence("recommendation-observation", input.CaseId, summary)]);
        return scored with { Details = scored.Details with { Summary = summary } };
    }
    protected abstract (bool Passed, string Summary) EvaluateResponse(
        EvalInput input,
        RecommendationSurfaceObservation observation);
}
internal sealed class ScreenedDeliverableEval()
    : RecommendationResponseEval(EvalKey, "A screened customer answer was delivered") {
    internal const string EvalKey = VitrineOfflineBenchmark.ScreenedDeliverableCheckKey;
    internal static ChanceFloor DeclaredFloor => ChanceFloor.NotDerivable(
        "the subject produces free text and a screening state; there is no authored random-draw space of alternative answers.");
    protected override (bool Passed, string Summary) EvaluateResponse(EvalInput input, RecommendationSurfaceObservation observation) {
        var independentScreen = CustomerAnswerScreen.Screen(input.Query, input.Response!);
        var passed = independentScreen.Status == CustomerAnswerScreenStatus.ScreenedClean &&
            !string.IsNullOrWhiteSpace(input.Response);
        return (passed, passed
            ? "the recorded customer answer is non-empty and the evaluator-owned screen classified it ScreenedClean."
            : $"the recorded customer answer is empty or the evaluator-owned screen classified it {independentScreen.Status}.");
    }
}
internal sealed class CataloguedSkuEval()
    : RecommendationResponseEval(EvalKey, "Every cited SKU belongs to the catalogue") {
    internal const string EvalKey = VitrineOfflineBenchmark.CataloguedSkuCheckKey;
    internal static ChanceFloor DeclaredFloor => ChanceFloor.NotDerivable(
        "SKU citation occurs in unbounded free text; no finite random alternative set is offered to the subject.");
    protected override (bool Passed, string Summary) EvaluateResponse(EvalInput input, RecommendationSurfaceObservation observation) {
        var passed = RecommendationOutputPredicates.NamesOnlyCataloguedSkus(input.Response!);
        return (passed, passed
            ? "the answer names at least one SKU and every named SKU resolves in Catalogue.Default."
            : "the answer names no catalogue SKU or includes an uncatalogued SKU.");
    }
}
internal sealed class CustomerReasonEval()
    : RecommendationResponseEval(EvalKey, "The answer gives a customer-facing reason") {
    internal const string EvalKey = VitrineOfflineBenchmark.CustomerReasonCheckKey;
    internal static ChanceFloor DeclaredFloor => ChanceFloor.NotDerivable(
        "reason-bearing free text has no authored draw model or enumerable answer pool.");
    protected override (bool Passed, string Summary) EvaluateResponse(EvalInput input, RecommendationSurfaceObservation observation) {
        var passed = RecommendationOutputPredicates.GivesCustomerReason(input.Response!);
        return (passed, passed ? "reason evidence is present." : "no customer-facing reason evidence is present.");
    }
}
internal sealed class NoPurchaseClaimEval()
    : RecommendationResponseEval(EvalKey, "The advisory answer makes no purchase-completion claim") {
    internal const string EvalKey = VitrineOfflineBenchmark.NoPurchaseClaimCheckKey;
    internal static ChanceFloor DeclaredFloor => ChanceFloor.NotDerivable(
        "absence of a prohibited phrase in free text is a deterministic safety invariant, not a random-choice task.");
    protected override (bool Passed, string Summary) EvaluateResponse(EvalInput input, RecommendationSurfaceObservation observation) {
        var passed = RecommendationOutputPredicates.AvoidsPurchaseCompletionClaim(input.Response!);
        return (passed, passed
            ? "the non-empty advisory answer makes no order, checkout, or payment completion claim."
            : "the answer is empty or makes a purchase-completion claim.");
    }
}
internal sealed class InterestGroundingEval()
    : RecommendationResponseEval(EvalKey, "The answer grounds recommendations in an interest or need") {
    internal const string EvalKey = VitrineOfflineBenchmark.InterestGroundingCheckKey;
    internal static ChanceFloor DeclaredFloor => ChanceFloor.NotDerivable(
        "interest-grounding evidence is detected in unbounded free text and has no finite random-draw population.");
    protected override (bool Passed, string Summary) EvaluateResponse(EvalInput input, RecommendationSurfaceObservation observation) {
        var passed = RecommendationOutputPredicates.GroundsAnInterest(input.Response!);
        return (passed, passed ? "interest or stated-need grounding is present." : "the answer gives no interest or need grounding.");
    }
}
internal static class RecommendationBenchmarkChecks {
    internal static IReadOnlyList<AdmittedCheck> All() =>
    [
        new(new ScreenedDeliverableEval(), ScreenedDeliverableEval.DeclaredFloor),
        new(new CataloguedSkuEval(), CataloguedSkuEval.DeclaredFloor),
        new(new CustomerReasonEval(), CustomerReasonEval.DeclaredFloor),
        new(new NoPurchaseClaimEval(), NoPurchaseClaimEval.DeclaredFloor),
        new(new InterestGroundingEval(), InterestGroundingEval.DeclaredFloor),
    ];
}
