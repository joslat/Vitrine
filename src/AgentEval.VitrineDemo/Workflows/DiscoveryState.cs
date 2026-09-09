// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// THE single message on every edge of Demo02's cross-category discovery loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>A mutable class, mutated in place and returned by every handler.</b> One message type on
/// every edge is what makes the loop-back a one-line <c>AddEdge</c> instead of a join, a
/// message-identity scheme and an aggregation contract; and because the whole run's state
/// travels in the message rather than in an executor field, every node is a valid resume root.
/// </para>
/// <para>
/// <b>The counter lives on the message, not on an executor.</b> <see cref="DiscoveryRound"/> is
/// incremented by the PRODUCER — <c>DiscoveryExecutor</c>, at the end of a round that actually
/// completed — so a throwing round does not consume budget and a resumed run carries the count
/// in restored state.
/// </para>
/// <para>
/// <b>Termination is a property of two predicates, not of a comment.</b>
/// <see cref="NeedsMoreDiscovery"/> and <see cref="DiscoveryLimitReached"/> PARTITION the space
/// of unapproved states, because the second is defined as the complement of the first. For any
/// state: approved ⇒ Ranker; unapproved with budget, progress and a runnable query ⇒ loop;
/// otherwise ⇒ Ranker. There is no third outcome and no state in which the reviewer has no
/// outgoing edge, so the loop can neither hang nor fall off the graph.
/// </para>
/// </remarks>
public sealed class DiscoveryState
{
    // ── Hard bounds. These guarantee termination. ────────────────────────────────────

    /// <summary>The default round cap. Three rounds, worst case, on any input.</summary>
    public const int DefaultMaxDiscoveryRounds = 3;

    /// <summary>Largest interest map the mapper may produce.</summary>
    public const int MaxInterests = 6;

    /// <summary>How many interests the reviewer may add across the whole run.</summary>
    public const int MaxReviewerInferredInterests = 2;

    /// <summary>Largest query plan one round may execute.</summary>
    public const int MaxQueriesPerRound = 10;

    /// <summary>
    /// The ceiling a reviewer-proposed confidence is clamped to IN CODE, whatever the model
    /// wrote. Topic drift is a compliance smell as well as a UX one, so the cap is mechanical.
    /// </summary>
    public const double ReviewerInferredConfidenceCeiling = 0.60;

    /// <summary>
    /// A candidate must fuse at least this well before it counts toward covering an interest.
    /// <b>UNMEASURED</b> — chosen against <c>HybridRetriever</c>'s RRF output, which is not a
    /// probability. It is a floor on a ranking statistic, and it is printed wherever it is used.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This cut is inert on the current corpus and is not transport-calibratable.</b> Across the
    /// fourteen authored customers, 54 coverage rows have candidates and a nameable interest; this
    /// cut refuses 0, for an admit rate of 1.000. Equal-tail transport needs a non-degenerate right
    /// tail, so it cannot derive a value from that anchor. Headroom is still thin: the minimum score
    /// is 0.0164, only 1.4× the cut. At 0.030 the cut decides 27 of 52 rows and admits 0.481, proving
    /// it can move even though it decides nothing at the shipped value. Eval 03 reports this as
    /// <c>MinCandidateScoreDecidesNothing</c>.
    /// </para>
    /// <para>
    /// This coverage cut is independent of
    /// <see cref="RetrievalConfidenceHalfSaturation"/>, the shape parameter in
    /// <c>DeterministicRanker.Confidence</c>'s <c>s / (s + k)</c> transform. They currently share a
    /// numeric value but have different semantics: a cut has an admit rate, while a shape parameter
    /// does not. Eval 03's <c>CoverageCutIsNotTheConfidenceShapeParameter</c> gate keeps the two
    /// roles separate.
    /// </para>
    /// </remarks>
    public const double MinCandidateScore = 0.012;

    /// <summary>
    /// The half-saturation constant of <see cref="DeterministicRanker.Confidence"/>'s squashing
    /// transform <c>s / (s + k)</c> — the search score at which the retrieval term equals exactly
    /// 0.5. <b>Not a threshold. Not a cut. It admits nothing and refuses nothing.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// It currently carries the same value as <see cref="MinCandidateScore"/>, but that equality
    /// expresses no relationship. The two values must be reasoned about and changed independently.
    /// </para>
    /// <para>
    /// <b>Equal-tail transport does not apply to this number, in either direction.</b> The rule in
    /// <c>ThresholdCalibration</c> derives a cut by matching the fraction of a fit population an
    /// anchor admits; a shape parameter has no admit rate to match, so asking that machinery for a
    /// value here would not be unfavourable, it would be a category error. If this number is ever
    /// calibrated it has to be against an OUTCOME the confidence is supposed to predict, and this
    /// sample has no such outcome — <c>DeterministicRanker.Confidence</c>'s own remarks say the
    /// number is uncalibrated and routes between two trays, and that statement stands.
    /// </para>
    /// </remarks>
    public const double RetrievalConfidenceHalfSaturation = 0.012;

    /// <summary>How many candidates an interest needs before the deterministic reviewer calls it covered.</summary>
    public const int MinCandidatesForCoverage = 2;

    /// <summary>How many review snippets travel with a candidate into the reviewer's context.</summary>
    public const int MaxReviewSnippetsPerCandidate = 3;

    /// <summary>Longest review snippet carried. Truncation is announced in-band, never silent.</summary>
    public const int ReviewSnippetCharacterBudget = 400;

    /// <summary>How many candidates the reviewer is shown, newest first.</summary>
    public const int MaxCandidatesShownToReviewer = 20;

    /// <summary>How many products the Ranker may select.</summary>
    public const int MaxRankedRecommendations = 12;

    // ── Identity + policy ────────────────────────────────────────────────────────────

    /// <summary>Unique per run. Printed in the summary so two runs are never confused.</summary>
    public Guid RunId { get; init; } = Guid.NewGuid();

    /// <summary>The customer this run serves, e.g. <c>"USR-NB-01"</c>.</summary>
    public required string CustomerId { get; init; }

    /// <summary>Two-letter market code. Gates <see cref="Product.AvailableMarkets"/> in retrieval.</summary>
    public required string Market { get; init; }

    /// <summary>Interface language. Recommendation quality must not depend on it.</summary>
    public required string Language { get; init; }

    /// <summary>
    /// The FDPIC opt-out, in the positive polarity. When false the purchase history is never
    /// read, so it does not reach the prompt BECAUSE it does not reach the state — data
    /// minimisation as a control-flow property, not a promise.
    /// </summary>
    public bool PersonalizationConsent { get; init; }

    /// <summary>The in-session stated need. History explains; the request decides.</summary>
    public string SessionRequest { get; set; } = "";

    /// <summary>
    /// The round cap for THIS run. Defaults to <see cref="DefaultMaxDiscoveryRounds"/>.
    /// </summary>
    /// <remarks>
    /// Init-only and per-run rather than a bare const, because the round-cap termination
    /// condition has to be REACHABLE in a test on a small catalogue. A guard nobody can trigger
    /// is a guard nobody has checked.
    /// </remarks>
    public int MaxRounds { get; init; } = DefaultMaxDiscoveryRounds;

    // ── Accumulated across rounds ────────────────────────────────────────────────────

    /// <summary>The RUNNING interest map. Appended, never replaced.</summary>
    public List<Interest> Interests { get; } = [];

    /// <summary>Things the customer has told us not to recommend.</summary>
    public List<AntiInterest> AntiInterests { get; } = [];

    /// <summary>Hard facts every recommendation must respect.</summary>
    public List<CompatibilityConstraint> Constraints { get; } = [];

    /// <summary>The per-interest coverage ledger, keyed by <see cref="Interest.Id"/>.</summary>
    public Dictionary<string, InterestCoverage> Coverage { get; } = new(StringComparer.Ordinal);

    /// <summary>Every candidate retrieved this run, in discovery order.</summary>
    public List<ProductCandidate> Candidates { get; } = [];

    /// <summary>
    /// IDENTITY-LEVEL dedup, checked at ingest. A product id already seen is not re-added and
    /// does not count toward <see cref="LastRoundNewProductCount"/>; the set is also passed
    /// INTO retrieval as an exclusion, so round 2 does not spend its budget re-finding round 1.
    /// </summary>
    public HashSet<string> SeenProductIds { get; } = new(StringComparer.Ordinal);

    /// <summary>SKUs the customer already owns. Excluded from retrieval, not merely filtered after it.</summary>
    public HashSet<string> OwnedProductIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Review snippets seen this run — the channel a mid-run interest can come out of.</summary>
    public List<ObservedSignal> ObservedSignals { get; } = [];

    /// <summary>Every query term the structural query-vocabulary control refused. Printed.</summary>
    public List<DroppedQueryTerm> DroppedQueryTerms { get; } = [];

    /// <summary>
    /// Every mid-run interest the reviewer PROPOSED this run, accepted or refused, with the reason.
    /// </summary>
    /// <remarks>
    /// The DENOMINATOR of the structural query-vocabulary control ledger. <see cref="ReviewerInferredCount"/> counts only what was
    /// accepted, and an arm that proposed nothing is indistinguishable from one whose every
    /// proposal was refused if that is the only number on the page — but only the second one
    /// exercised the control.
    /// </remarks>
    public List<ProposalOutcome> Proposals { get; } = [];

    /// <summary>The query plan the CURRENT round executed. Cleared and rebuilt each round.</summary>
    public List<QueryPlanEntry> LastQueryPlan { get; } = [];

    /// <summary>
    /// Every query the run has executed, in execution order, with the round it belonged to,
    /// whether it was written from the MAP or from a GAP, and what it returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the record the loop's central claim is read off.</b> Round 1's queries are
    /// written from the customer's own vocabulary, before a single catalogue record has been
    /// seen; round 2+'s are written from the reviewer's gaps, i.e. after the category paths and
    /// attribute keys that came back were in front of it. <see cref="QueryPlanEntry.Origin"/>
    /// is the difference, and the console panel that argues the loop pays for itself groups on it.
    /// </para>
    /// <para>
    /// Appended, never cleared — unlike <see cref="LastQueryPlan"/>, which is this round only.
    /// The alternative was to re-derive the history by parsing the console trace, which would
    /// make a demo panel depend on the format of a log line.
    /// </para>
    /// </remarks>
    public List<ExecutedQuery> QueryLog { get; } = [];

    // ── Loop control ─────────────────────────────────────────────────────────────────

    /// <summary>Completed discovery rounds. Incremented by the producer at the end of a round.</summary>
    public int DiscoveryRound { get; set; }

    /// <summary>
    /// New product ids the last completed round added. <c>-1</c> means no round has run yet,
    /// which is NOT the same as a round that added nothing — and the difference is load-bearing:
    /// the no-progress stop must not fire before the first round has had a chance.
    /// </summary>
    public int LastRoundNewProductCount { get; set; } = -1;

    /// <summary>The reviewer approved. Only the reviewer may set this.</summary>
    public bool CoverageApproved { get; set; }

    /// <summary>The gaps the reviewer left open. Replaced wholesale each round.</summary>
    public List<CoverageGap> OpenGaps { get; } = [];

    /// <summary>How many interests the reviewer has added so far. Capped at <see cref="MaxReviewerInferredInterests"/>.</summary>
    public int ReviewerInferredCount { get; set; }

    /// <summary>The reviewer's assessment, carried SEPARATELY from the query so a retry cannot lose it.</summary>
    public string ReviewNotes { get; set; } = "";

    /// <summary>Why the loop stopped. Printed.</summary>
    public DiscoveryStopReason StopReason { get; set; } = DiscoveryStopReason.None;

    /// <summary>True when the deterministic pre-gate rejected before any token was spent this round.</summary>
    public bool PreGateFiredThisRound { get; set; }

    // ── Output ───────────────────────────────────────────────────────────────────────

    /// <summary>The Ranker's selection, after the deterministic post-checks.</summary>
    public List<RankedRecommendation> Ranked { get; } = [];

    /// <summary>Everything the post-checks removed, with reasons. Shown on screen.</summary>
    public List<DroppedSku> DroppedSkus { get; } = [];

    /// <summary>
    /// What the customer was actually SHOWN, after the shared <c>GuardrailPipeline</c> ran.
    /// </summary>
    /// <remarks>
    /// Strictly a subset of <see cref="Ranked"/>: the pipeline removes items the Ranker selected.
    /// Written once, by the Presenter, at render time — never re-derived downstream, because a
    /// second derivation of a run that already happened is a second thing to keep in agreement.
    /// </remarks>
    public List<PresentedItem> Presented { get; } = [];

    /// <summary>The customer-facing answer the Presenter composed.</summary>
    public string FinalAnswer { get; set; } = "";

    /// <summary>
    /// Typed final-text safety observation written beside <see cref="FinalAnswer"/>. Null means the
    /// Presenter has not reached the customer boundary; missing, unscreened, clean, and unsafe are
    /// deliberately separate once it has.
    /// </summary>
    public CustomerAnswerScreenResult? CustomerAnswerSafety { get; set; }

    /// <summary>
    /// Optional model-authored presenter draft. It is retained for diagnostics but is not the
    /// delivered artifact until a dedicated prose-grounding gate exists.
    /// </summary>
    public string? PresenterDraft { get; set; }

    /// <summary>
    /// What the Presenter screened and showed — the four objects the panel was rendered from.
    /// Null until the Presenter has run.
    /// </summary>
    /// <remarks>
    /// Written once, by <c>DiscoveryPresentation.Render</c>, for the same reason
    /// <see cref="Presented"/> is written there: it is a fact about the run rather than about
    /// whether anything was printed. <c>--report</c> reads it, and reads nothing else — a report
    /// assembled from a second screening pass would be a second answer.
    /// </remarks>
    public ScreenedAnswer? Screened { get; set; }

    // ── Accounting ───────────────────────────────────────────────────────────────────

    /// <summary>Model calls actually made. Discovery contributes ZERO, by design.</summary>
    public int ModelCalls { get; set; }

    /// <summary>
    /// What those calls COST, in tokens the provider reported — never in tokens we counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ModelCalls"/> answers "how many"; this meter answers "how much" from provider
    /// usage and preserves UNKNOWN rather than substituting zero when the provider did not say.
    /// </para>
    /// <para>
    /// Every counted call is paired with one <see cref="ChatSpend"/> record on success, timeout,
    /// and non-cancellation failure. Caller cancellation is the deliberate exception: it rethrows
    /// without recording usage and may occur after <see cref="ModelCalls"/> increments, so
    /// <c>Spend.Calls == ModelCalls</c> is not an invariant on that path. The throw bypasses the
    /// printers, so no incomplete meter line is emitted.
    /// </para>
    /// </remarks>
    public ChatSpend Spend { get; } = new();

    /// <summary>The shared, absence-preserving projection consumed by reports and evaluations.</summary>
    public ProviderUsageMeasurement ProviderUsage =>
        ProviderUsageMeasurement.FromDemo02(Spend.Snapshot());

    /// <summary>Retrieval calls actually made.</summary>
    public int SearchesRun { get; set; }

    /// <summary>Nodes that fell back to their deterministic path, and why. Never silent.</summary>
    public List<string> DegradedNotes { get; } = [];

    /// <summary>
    /// True when the SELECTION came from code rather than from a model — offline, or because the
    /// Ranker fell back.
    /// </summary>
    /// <remarks>
    /// The guardrail ledger reads this to decide whether to mark the evidence arms INAPPLICABLE.
    /// It has to be a fact the Ranker records rather than a guess made downstream: "no prose came
    /// back" is not the same statement as "no model chose these products", and conflating them
    /// would have the ledger claim on a live run that no model ran.
    /// </remarks>
    public bool SelectionWasDeterministic { get; set; }

    // ── Routing predicates. These two PARTITION !CoverageApproved. ───────────────────

    /// <summary>
    /// The loop-back condition. All four clauses are termination-relevant:
    /// unapproved, inside the round cap, the last round made progress, and there is a query to run.
    /// </summary>
    public bool NeedsMoreDiscovery =>
        !CoverageApproved
        && DiscoveryRound < MaxRounds            // TERMINATION 1 — bounded
        && LastRoundNewProductCount != 0         // TERMINATION 2 — a round that added nothing
                                                 //                 cannot be fixed by another
        && OpenGaps.Count > 0;                   // TERMINATION 3 — and we must have a query

    /// <summary>
    /// Defined as the COMPLEMENT so the two predicates are provably exhaustive over
    /// <c>!CoverageApproved</c>. Restating the condition in both places is a drift hazard, so
    /// it is not restated.
    /// </summary>
    public bool DiscoveryLimitReached => !CoverageApproved && !NeedsMoreDiscovery;

    /// <summary>
    /// True when the answer is a PARTIAL one: the loop exited without approving coverage. The
    /// Presenter renders an explicit shortfall section, and progress is a WARNING, not a failure.
    /// </summary>
    public bool IsPartialAnswer => !CoverageApproved;

    // ── Derived views the executors and the console read ─────────────────────────────

    /// <summary>The interest with this id, or null.</summary>
    /// <param name="interestId">An <see cref="Interest.Id"/>.</param>
    public Interest? FindInterest(string? interestId)
    {
        if (string.IsNullOrWhiteSpace(interestId)) return null;
        foreach (var interest in Interests)
            if (string.Equals(interest.Id, interestId, StringComparison.Ordinal))
                return interest;
        return null;
    }

    /// <summary>The coverage row for an interest, creating it if this is its first mention.</summary>
    /// <param name="interestId">An <see cref="Interest.Id"/>.</param>
    public InterestCoverage CoverageFor(string interestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interestId);

        if (!Coverage.TryGetValue(interestId, out var row))
        {
            row = new InterestCoverage { InterestId = interestId };
            Coverage[interestId] = row;
        }
        return row;
    }

    /// <summary>The candidate with this id, or null.</summary>
    /// <param name="productId">A catalogue product id.</param>
    public ProductCandidate? FindCandidate(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        foreach (var candidate in Candidates)
            if (string.Equals(candidate.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                return candidate;
        return null;
    }

    /// <summary>Interests the ledger does not currently call <see cref="CoverageStatus.Covered"/>.</summary>
    public IReadOnlyList<Interest> UncoveredInterests()
    {
        var open = new List<Interest>();
        foreach (var interest in Interests)
            if (CoverageFor(interest.Id).Status != CoverageStatus.Covered)
                open.Add(interest);
        return open;
    }

    /// <summary>
    /// The FINAL stop reason, resolved from the routing predicates after the reviewer has
    /// spoken. Kept in ONE place so the printed reason and the taken edge can never disagree.
    /// </summary>
    /// <remarks>
    /// Order is deliberate. <c>GapsUnresolvable</c> is the most specific claim the reviewer can
    /// make, <c>NoProgress</c> outranks the round cap because "another identical round cannot
    /// change the answer" is the more informative statement when both hold, and the round cap is
    /// the backstop that makes the other two safe to trust.
    /// </remarks>
    public DiscoveryStopReason ResolveStopReason()
    {
        if (CoverageApproved) return DiscoveryStopReason.CoverageSufficient;
        if (NeedsMoreDiscovery) return DiscoveryStopReason.GapsRemain;
        if (OpenGaps.Count == 0) return DiscoveryStopReason.GapsUnresolvable;
        if (LastRoundNewProductCount == 0) return DiscoveryStopReason.NoProgress;
        return DiscoveryStopReason.RoundLimitReached;
    }

    /// <summary>The one-line run summary the console prints at the end.</summary>
    public string ToSummaryLine() =>
        $"rounds {DiscoveryRound} of {MaxRounds} · stop_reason {StopReason} · model calls {ModelCalls} · " +
        $"searches {SearchesRun} · {Candidates.Count} discovered · {Ranked.Count} recommended · {DroppedSkus.Count} dropped";
}
