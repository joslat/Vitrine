// SPDX-License-Identifier: MIT
using System.Collections.Immutable;
using AgentEval.Evals.Meta;
namespace AgentEval.VitrineDemo.Evals;
public sealed record EvalCriterionDefinition(string Id, string Text, int PassingScore,
    ChanceFloor Floor, string Applicability, string Oracle, string Provenance);
public enum ApplicabilityEvidenceSource { AuthoredInput, SubjectOutput }
public sealed record CriterionApplicabilityDecision(string CriterionId, bool Applicable,
    ApplicabilityEvidenceSource Source);
public static class VitrineEvalCriteria {
    public const int ProductCount = 99;
    public const int PersonaCount = 14;
    public const int ToolCount = 15;
    public const int ExecutorCount = 5;
    public const int ConditionalLoopBackEdges = 1;
    public const int NegativeControlCount = 43;
    public const int JudgedPassingScore = 75;
    public const int JudgedMatchedK = 1;
    public const int RecallPassingScore = 100;
    public static ChanceFloor PersonaForcedChoiceFloor { get; } = ChanceFloor.UniformChoice(PersonaCount);
    public const string CatalogueSource = "VitrineEvalCriteria catalogue contract (independent of runtime output)";
    public const string TopologySource = "VitrineEvalCriteria topology contract (independent of route trace)";
    public const string ControlSource = "NegativeControlCatalog planted mutation (independent of evaluator output)";
    public const string RecallSource = "Authored customer statement in MemoryTestScenario.ExpectedFacts";
    public const string RecallNoiseCorpus = "context-small";
    public static ImmutableArray<EvalCriterionDefinition> JudgedCriteria { get; } =
    [
        new("recommendation.catalogue-sku", "At least one recommendation names a catalogue SKU in GLX-#### form.", JudgedPassingScore,
            ChanceFloor.NotDerivable("catalogue membership is a deterministic contract, not a random forced choice"),
            "Customer-facing recommendation artifacts with at least one slot.", "Catalogue.Default.BySku membership", "VITRINE shared judged registry v1"),
        new("recommendation.customer-reason", "At least one recommendation gives a customer-facing reason for the product.", JudgedPassingScore,
            ChanceFloor.NotDerivable("customer-facing reason quality has no authored random policy or class prior"),
            "Customer-facing recommendation artifacts with at least one slot.", "Output-only reason evidence", "VITRINE shared judged registry v1"),
        new("recommendation.no-purchase-claim", "The response contains no claim that an order, checkout, or payment was completed.", JudgedPassingScore,
            ChanceFloor.NotDerivable("prohibited-claim avoidance has no random-choice null; silence is not a fair coin"),
            "All advisory recommendation responses.", "Output-only prohibited-claim matcher", "VITRINE shared judged registry v1"),
        new("recommendation.interest-grounding", "The response identifies a stated or inferred customer interest instead of presenting an unexplained popularity list.", JudgedPassingScore,
            ChanceFloor.NotDerivable("interest-grounding quality has no authored random policy or class prior"),
            "Personalized recommendation responses.", "Output-only interest evidence", "VITRINE shared judged registry v1"),
    ];
    public static ImmutableArray<string> Judged { get; } = [.. JudgedCriteria.Select(static criterion => criterion.Text)];
    public static ImmutableArray<string> RecalledConstraints { get; } = ["maximum budget is CHF 250", "must be waterproof"];
    public static bool IsApplicable(EvalCriterionDefinition criterion, string? request)
        => DecideApplicability(criterion, request).Applicable;
    public static CriterionApplicabilityDecision DecideApplicability(EvalCriterionDefinition criterion, string? authoredRequest) {
        ArgumentNullException.ThrowIfNull(criterion);
        if (!JudgedCriteria.Any(candidate => string.Equals(candidate.Id, criterion.Id, StringComparison.Ordinal)))
            throw new ArgumentException($"Criterion '{criterion.Id}' is not registered.", nameof(criterion));
        return new CriterionApplicabilityDecision(
            criterion.Id,
            !string.IsNullOrWhiteSpace(authoredRequest),
            ApplicabilityEvidenceSource.AuthoredInput);
    }
    public static void Validate() {
        if (PersonaForcedChoiceFloor is not { State: FloorState.Derived, PoolSize: PersonaCount })
            throw new InvalidOperationException("The authored forced-choice floors have drifted.");
        if (JudgedCriteria.IsDefaultOrEmpty ||
            JudgedCriteria.Select(static criterion => criterion.Id).Distinct(StringComparer.Ordinal).Count() != JudgedCriteria.Length)
            throw new InvalidOperationException("Judged criterion identifiers must be non-empty and unique.");
        if (JudgedCriteria.Any(static criterion => criterion.PassingScore is < 0 or > 100))
            throw new InvalidOperationException("Judged criterion passing scores must be percentages.");
        if (JudgedCriteria.Any(static criterion =>
                criterion.Floor.State != FloorState.NotDerivable ||
                string.IsNullOrWhiteSpace(criterion.Floor.Derivation)))
            throw new InvalidOperationException("Each judged criterion must state why its chance floor is not derivable.");
        if (JudgedMatchedK <= 0)
            throw new InvalidOperationException("The judged matched-k plan must be positive.");
    }
}
