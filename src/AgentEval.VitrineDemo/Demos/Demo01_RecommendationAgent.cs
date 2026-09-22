// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;
using Galaxus.RecommendationAgent.Tools;

namespace Galaxus.RecommendationAgent.Demos;

/// <summary>
/// Demo 01 — Robin, the single all-in-one recommendation agent (the customer-output contract).
/// </summary>
/// <remarks>
/// <para>
/// The turn runs in four clearly separated phases, and the separation is the demo:
/// </para>
/// <list type="number">
///   <item><b>CODE derives the interest map.</b> <see cref="PurchaseIntentClassifier"/> and
///         <see cref="InterestMapBuilder"/> run before the model is constructed. Gift
///         suppression, the replenishment lane and the abstention gate are decided here,
///         deterministically, at zero token cost.</item>
///   <item><b>The MODEL searches and presents.</b> Its only sanctioned recommendation
///         channel is the <c>PresentRecommendation</c> tool call — a
///         product named only in prose is not shown and does not count.</item>
///   <item><b>CODE screens what it presented.</b> <see cref="GuardrailPipeline"/> verifies
///         every tool call against the catalogue and writes each drop to the ledger.</item>
///   <item><b>The ledger is printed.</b> Every guardrail mechanism, counted and named on screen.</item>
/// </list>
/// <para>
/// ⚠ <b>Two things this file deliberately does NOT do.</b> It does not parse the model's
/// final text for recommendations — the retired "return only this JSON object" prompt contract is
/// deleted, and re-introducing a prose parser here would resurrect the former split-contract defect. And it does
/// not repair a bad tool call before screening it: a repaired argument is a defect that can
/// never fire, which is a failure in the flattering direction.
/// </para>
/// <para>
/// ⏱️ Runtime: ~20–60 seconds against a live deployment (several model + tool round-trips).
/// <c>--offline</c> runs the deterministic half in well under a second and costs nothing.
/// </para>
/// </remarks>
public static class Demo01_RecommendationAgent
{
    /// <summary>The persona the demo opens on when <c>--user</c> is not given.</summary>
    public const string DefaultUserId = GalaxusDemoPrompts.NadiaUserId;

    /// <summary>The per-run tool-call cap opened around <c>RunAsync</c>.</summary>
    public const int ToolCallCap = ToolCallBudget.DefaultMaxCalls;

    /// <summary>
    /// Minimum attribution score before a presented product is credited to a derived interest
    /// signal. Below it the recommendation carries NO user side and the pipeline drops it with
    /// <c>unknown_signal_label</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a routing threshold, not a calibrated probability. It is deliberately permissive:
    /// it must reject products that no derived interest explains without rejecting a valid result
    /// merely because its search wording differs from the interest label.
    /// </para>
    /// <para>
    /// In the offline concept-space sample, the gaming headset scores below 0.20 against every
    /// espresso/hiking signal, while Marco's scale scores 0.69 against his missing-scale signal and
    /// Nadia's ND filter scores 0.53 against her 0.86 conjunction. The deliberately loose edge case
    /// is an espresso-cleaning product at 0.26 against "Trekking packs"; the independent confidence
    /// band then drops it at 0.39. These are two stated routing filters, not probabilities.
    /// </para>
    /// <para>
    /// <b>Space-dependent.</b> Half of <see cref="AttributeSignalAsync"/>'s match is a cosine, so
    /// the applicable floor must come from <see cref="CalibratedThresholds"/> for the embedding
    /// space resolved by <see cref="EmbeddingSpace"/>. The fit slice excludes every persona whose
    /// tray is printed.
    /// </para>
    /// <para>
    /// The concept-space separation does not transfer unchanged to the real-vector path. The gaming headset
    /// <c>GLX-4004</c>, embedded against the fourteen derived signals of the three espresso/hiking
    /// personas: on the concept path every one of the fourteen scores <b>exactly 0.000</b> — the
    /// authored lexicon gives it no shared dimension at all. On <c>--real-vectors</c> the fourteen
    /// run 0.059–0.224, and <b>one of them CLEARS this floor</b>: Nadia's <c>"Headlamps"</c> signal
    /// at <b>0.224</b>. The product the gift trap must never surface is attributable there.
    /// It is still stopped, but by the SECOND filter only — confidence
    /// <c>(0.52 + 0.224) / 2 = 0.372</c> is under <see cref="ConfidenceBands.SecondaryThreshold"/> —
    /// so a series of two loose filters became a series of one on that path.
    /// </para>
    /// <para>
    /// <b>Why the floor loses its grip there.</b> A 24-dimension authored cosine between unrelated
    /// texts is very often EXACTLY zero, so 0.20 sits far above the mass of the distribution. A
    /// <c>text-embedding-3-small</c> cosine between two arbitrary catalogue texts is not: measured
    /// over all 99 products for five real signal labels, the per-label medians are 0.144–0.209 on
    /// the real path against 0.000–0.244 on the concept path, and the fraction of the catalogue
    /// clearing 0.20 for the two most specific labels goes 24/99 → 42/99 and 24/99 → 62/99.
    /// The floor sits near the MEDIAN of the real-space distribution.
    /// </para>
    /// <para>
    /// A floor chosen to reproduce the concept-path output would be outcome-fitting. The value
    /// therefore comes from a held-out, per-space row in <see cref="CalibratedThresholds"/>, listed
    /// beside the confidence bands in <c>docs/Vitrine-Retrieval-Deep-Dive.html</c>.
    /// </para>
    /// </remarks>
    public static double AttributionFloor => CalibratedThresholds.Current.AttributionFloor;

    /// <summary>
    /// A consumable enters the replenishment tray once this fraction of its typical cadence has
    /// elapsed. 0.80 means "inside the last fifth of the cycle, or already overdue".
    /// </summary>
    public const double ReplenishmentDueFraction = ReplenishmentLaneBuilder.DueFraction;

    /// <summary>Runs the deterministic no-provider arm. Live execution is never the default API path.</summary>
    public static Task RunAsync() => RunAsync(DefaultUserId, personalizationDisabled: false, offline: true);

    /// <summary>
    /// Runs one customer turn end to end.
    /// </summary>
    /// <param name="userId">One of <see cref="Personas.AllPersonaIds"/>. Null selects <see cref="DefaultUserId"/>.</param>
    /// <param name="personalizationDisabled">
    /// The <c>--no-personalization</c> toggle. Flips <see cref="User.PersonalizationEnabled"/>
    /// to false for this run: <c>GetPurchaseHistory</c> and <c>GetInterestMap</c> then return a typed
    /// refusal, and the turn runs on the customer's stated need alone.
    /// </param>
    /// <param name="offline">
    /// The <c>--offline</c> toggle. Skips the model entirely and lets the deterministic retrieval +
    /// guardrail path select the products. This is the baseline arm, printed as such — it is what the
    /// system produces with ZERO model calls, and it exists because a claim about what the agent adds
    /// is worthless without it.
    /// </param>
    /// <param name="reportPath">
    /// The <c>--report</c> destination. When set, the turn that just ran is also written as ONE
    /// self-contained HTML file — the same objects the console panel is printed from, no fixture and
    /// no second derivation. Null skips it. See <see cref="RunReportHtml"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task RunAsync(
        string? userId,
        bool personalizationDisabled,
        bool offline,
        string? reportPath = null,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
            userId,
            personalizationDisabled,
            offline ? RecommendationExecutionArm.ZeroModelBaseline : RecommendationExecutionArm.LiveAzure,
            reportPath,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Runs one customer turn through an explicitly selected execution arm.</summary>
    public static async Task RunAsync(
        string? userId,
        bool personalizationDisabled,
        RecommendationExecutionArm arm,
        string? reportPath = null,
        CancellationToken cancellationToken = default)
    {
        PrintHeader();
        var id = string.IsNullOrWhiteSpace(userId) ? DefaultUserId : userId.Trim();

        if (!Enum.IsDefined(arm))
        {
            Console.Error.WriteLine("  Invalid Demo01 execution arm. No provider call was made.");
            Environment.ExitCode = 2;
            return;
        }

        if (arm == RecommendationExecutionArm.LiveAzure && !Config.IsConfigured)
        {
            PrintMissingCredentials();
            Environment.ExitCode = 2;
            return;
        }

        if (arm == RecommendationExecutionArm.LiveAzure)
        {
            Config.PrintProviderTarget();
            Console.WriteLine();
        }

        var result = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(
                id,
                personalizationDisabled,
                arm),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Profile is null)
        {
            PrintUnknownPersona(id);
            Environment.ExitCode = 2;
            return;
        }

        PrintRequest(result.Profile, result.Prompt ?? string.Empty, personalizationDisabled);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Retrieval: {result.RetrieverName ?? "NOT MEASURED"}");
        Console.ResetColor();
        if (arm == RecommendationExecutionArm.ZeroModelBaseline) PrintOfflineBanner();
        if (arm == RecommendationExecutionArm.ScriptedAgent) PrintScriptedBanner();

        if (result.Outcome is not { } outcome || result.InterestMap is not { } map)
        {
            Console.ForegroundColor = result.Status == RecommendationRunStatus.Cancelled ? ConsoleColor.Yellow : ConsoleColor.Red;
            Console.WriteLine($"  {result.Status}: {result.FailureKind ?? "no typed result"}");
            Console.ResetColor();
            Environment.ExitCode = result.Status == RecommendationRunStatus.Cancelled ? 130 : 1;
            return;
        }

        var toolCalls = result.ToolCallsUsed ?? RecommendationPrinter.OmitToolCalls;
        RecommendationPrinter.PrintAnswer(
            result.Profile.User,
            map,
            result.ClassifiedPurchases,
            outcome,
            toolCalls,
            result.ToolCallsUsed.HasValue ? ToolCallCap : RecommendationPrinter.OmitToolCalls,
            gateRanBeforeSpend: result.Status == RecommendationRunStatus.Abstained);
        PrintPresentationAudit(result.Presented, outcome.Cleaned);
        WriteReport(
            reportPath,
            arm,
            result.Prompt ?? string.Empty,
            result.Profile.User,
            map,
            result.ClassifiedPurchases,
            outcome,
            Catalogue.Default,
            toolCalls,
            result.ToolCallsUsed.HasValue ? ToolCallCap : RecommendationPrinter.OmitToolCalls,
            result.ProviderUsage);
        if (result.BudgetSummary is not null) PrintBudgetNote(result.BudgetSummary);
        if (result.AgentText is not null) PrintRobinsProse(result.AgentText);
    }

    /// <summary>
    /// Writes the <c>--report</c> page, or does nothing when no path was asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every argument here is an object the turn already produced. Nothing is recomputed, and in
    /// particular the ledger is the one the panel printed — a report built from a second screening
    /// pass would be a second answer to keep in agreement with the first.
    /// </para>
    /// <para>
    /// A failure to write is reported and swallowed: a bad <c>--report</c> path is an operator typo,
    /// and a demo that has already produced a correct answer must not die on the way to writing a
    /// file about it. The exit code is left alone for the same reason.
    /// </para>
    /// </remarks>
    private static void WriteReport(
        string? reportPath,
        RecommendationExecutionArm executionArm,
        string prompt,
        User user,
        InterestMap map,
        IReadOnlyList<ClassifiedPurchase> classified,
        GuardrailOutcome outcome,
        Catalogue catalogue,
        int toolCallsUsed,
        int toolCallCap,
        ProviderUsageMeasurement providerUsage)
    {
        if (string.IsNullOrWhiteSpace(reportPath)) return;

        try
        {
            // The DEPLOYMENT NAME is the only configuration value that reaches the page. Never the
            // endpoint, which names the Azure resource, and never the key in any form.
            var arm = executionArm switch
            {
                RecommendationExecutionArm.ZeroModelBaseline => "offline baseline — no model call",
                RecommendationExecutionArm.ScriptedAgent => "scripted ChatClient — deterministic local chat boundary",
                RecommendationExecutionArm.LiveAzure =>
                    $"live agent · {Config.Readiness.ProviderDisplayName} · subject model {Config.Deployments.SubjectLabel} · {Config.Readiness.AuthenticationLabel}",
                _ => throw new InvalidOperationException("The Demo01 report arm was not recognized."),
            };

            var written = RunReportHtml.Write(
                reportPath, "Demo 01 — one agent", arm, prompt,
                user, map, classified, outcome.Cleaned, outcome.VerifiedPrices, outcome.Ledger,
                catalogue, toolCallsUsed, toolCallCap,
                liveProviderUsage: executionArm == RecommendationExecutionArm.LiveAzure ? providerUsage : null);

            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"\n  📄 Report written: {written}");
            Console.ResetColor();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException
                                      or InvalidOperationException)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  ⚠️  The report was NOT written ({ex.GetType().Name}). Details are intentionally not printed.");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// The session header sent ahead of the customer's own words.
    /// </summary>
    /// <remarks>
    /// The header supplies the exact customer id so the model never has to guess an identity or
    /// rely on a dangerous default. The customer's utterance remains byte-identical to
    /// <see cref="GalaxusDemoPrompts"/> in a separate message because the eval lane grades those
    /// exact strings.
    /// </remarks>
    internal static string SessionHeader(CustomerProfile profile) =>
        $"[session] You are serving customer id {profile.Id} — market {profile.Market}, language {profile.Language}. "
      + "Pass that id to GetUserProfile, GetPurchaseHistory and GetInterestMap. Never substitute another customer, "
      + "and never invent an id. The next message is the customer speaking.";

    // ── Assembly: tool calls → RecommendationSet ──────────────────────────────

    /// <summary>A drop decided before the pipeline ran, replayed into its ledger afterwards.</summary>
    internal sealed record PreDrop(GuardrailStage Stage, string Reason, string Subject, string Detail);

    /// <summary>
    /// Builds the answer from the <c>PresentRecommendation</c> calls — the ONLY sanctioned channel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two paths for the user side, and the ledger always says which one ran.</b>
    /// When the model supplies <c>userEvidence</c>, its label and its purchase ids are carried
    /// through UNCHANGED and verified against the code-derived map: an invented label fails,
    /// somebody else's id fails, a gift fails, and — the arm that only exists on this path — one
    /// of the customer's OWN ids cited for an interest it does not evidence fails.
    /// </para>
    /// <para>
    /// ⚠ When the model omits it, the user side is DERIVED from retrieval provenance, and on that
    /// path the user-side arm can only fail one way: when no derived interest explains the product
    /// at all. It cannot catch a wrongly cited purchase, because nothing was cited. That is not a
    /// defect in the check, it is the absence of an input — and it is why the derivation is now
    /// the fallback rather than the only option, and why <see cref="NoteEvidenceArms"/> counts how
    /// many items took which path.
    /// </para>
    /// <para>
    /// Nothing here repairs a bad argument. A user-side label the model got wrong stays wrong all
    /// the way into the filter, because a repaired argument is a defect that can never fire.
    /// </para>
    /// </remarks>
    /// <param name="presented">The <c>PresentRecommendation</c> calls, verbatim.</param>
    /// <param name="userEvidence">The fifth argument of each call, index-aligned with <paramref name="presented"/>.</param>
    /// <param name="provenance">Product id → the search needs that surfaced it. The fallback user side.</param>
    /// <param name="map">The code-derived interest map.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    /// <param name="replenishment">The repeat-buy tray, built before the model ran.</param>
    /// <param name="cancellationToken">
    /// Cancellation for confidence and attribution embeddings. On the <c>--real-vectors</c> path,
    /// <see cref="EmbeddingSpace"/> can reach a live embedding deployment.
    /// </param>
    /// <returns>The assembled answer, the pre-pipeline drops, and how many items carried a MODEL-stated user side.</returns>
    internal static async Task<(RecommendationSet Raw, IReadOnlyList<PreDrop> Drops, int ModelStatedUserSides)> AssembleAsync(
        IReadOnlyList<PresentedRecommendation> presented,
        IReadOnlyList<string?> userEvidence,
        IReadOnlyDictionary<string, IReadOnlyList<string>> provenance,
        InterestMap map,
        Catalogue catalogue,
        IReadOnlyList<ReplenishmentDto> replenishment,
        CancellationToken cancellationToken = default)
    {
        var recommendations = new List<RecommendationDto>();
        var drops           = new List<PreDrop>();
        var seen            = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelStated     = 0;

        for (var i = 0; i < presented.Count; i++)
        {
            var item = presented[i];
            var sku  = item.Sku.Trim();

            if (!seen.Add(sku))
            {
                drops.Add(new PreDrop(GuardrailStage.CatalogueGrounding, GuardrailReasons.DuplicatePresentation, sku,
                    "presented more than once in this turn; only the first call is shown"));
                continue;
            }

            catalogue.TryGet(sku, out var product);

            var (key, value) = ResolveProductSide(product, item);
            var reviewId = item.Citation is { Kind: EvidenceRefKind.Review } review ? review.Token : null;

            // ── the MODEL's own user side, when it wrote one ────────────────────────────
            var raw = i < userEvidence.Count ? userEvidence[i] : null;
            if (UserEvidenceRef.TryParse(raw, out var stated))
            {
                modelStated++;

                // Verbatim. The label is not matched fuzzily against the map and the ids are not
                // filtered down to the ones that happen to fit — either would repair the claim
                // before the filter got to test it.
                var cited = map.FindSignal(stated.SignalLabel);

                recommendations.Add(new RecommendationDto(
                    sku,
                    item.Reason,
                    new EvidenceDto(stated.SignalLabel, stated.PurchaseIds, key, value, reviewId),
                    await ConfidenceAsync(cited, product, cancellationToken).ConfigureAwait(false)));
                continue;
            }

            // ── the fallback: derive it from which search surfaced the SKU ──────────────
            var needs  = provenance.TryGetValue(sku, out var recorded) ? recorded : [];
            var signal = await AttributeSignalAsync(needs, product, map, cancellationToken).ConfigureAwait(false);

            recommendations.Add(new RecommendationDto(
                sku,
                item.Reason,
                new EvidenceDto(
                    signal?.Label ?? string.Empty,
                    // A stated-in-session interest is evidenced by the sentence, never by history
                    // (the two-sided evidence check, and under the personalization opt-out there IS no history to cite).
                    signal is not null && !IsStatedInSession(signal) ? signal.EvidencePurchaseIds : [],
                    key,
                    value,
                    reviewId),
                await ConfidenceAsync(signal, product, cancellationToken).ConfigureAwait(false)));
        }

        var assembled = RecommendationSet.Empty with
        {
            InterestMap     = [.. map.Signals.Select(InterestSignalDto.From)],
            Recommendations = recommendations,
            Replenishment   = replenishment
        };

        return (assembled, drops, modelStated);
    }

    /// <summary>
    /// Credits a presented SKU to the derived interest signal whose label best matches the search
    /// needs that surfaced it. Returns null when nothing clears <see cref="AttributionFloor"/> —
    /// which drops the item, because a product no derived interest explains is exactly the case
    /// the two-sided evidence check is meant to remove.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Match</b> is the better of two legs. The cosine is the one that matters: it connects
    /// "owns whole beans and vacuum canisters but no grinder" to a search for "a burr grinder for
    /// espresso at home" with not one shared word. Token overlap is the fallback for a need whose
    /// vocabulary the space does not cover, where the cosine is legitimately 0 for everything.
    /// </para>
    /// <para>
    /// The cosine is taken in the same resolved <see cref="EmbeddingSpace"/> that performed
    /// retrieval, so it is a consistency check rather than independent evidence. Both stored
    /// product documents and runtime signal labels are embedded in that one space, whose name is
    /// printed because a 24-dimension authored cosine and a <c>text-embedding-3-small</c> cosine are
    /// not comparable.
    /// </para>
    /// <para>
    /// <b>Ranking</b> among the signals that clear the floor is <c>match × strength</c> — two
    /// independent pieces of evidence multiplied, not one of them ignored. This prevents a weak
    /// leaf-category signal that happens to share query nouns from outranking a stronger conjunction;
    /// for example, Nadia's 0.86 conjunction must outrank the 0.52 "Trekking packs" signal for the
    /// trekking-pole case.
    /// </para>
    /// </remarks>
    /// <param name="needs">The search needs that surfaced this product. Empty falls back to the product's own document.</param>
    /// <param name="product">The presented product.</param>
    /// <param name="map">The customer's derived interest map.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The credited signal, or null when nothing clears <see cref="AttributionFloor"/>.</returns>
    public static async Task<InterestSignal?> AttributeSignalAsync(
        IReadOnlyList<string> needs,
        Product? product,
        InterestMap map,
        CancellationToken cancellationToken = default)
    {
        if (map.Signals.Count == 0) return null;

        // Only semantic tools record search-need provenance. BrowseCategory is also permitted, so
        // its products fall back to comparing the product's own embedding document with the labels
        // in the same resolved space. A product no derived interest explains still stays below the
        // floor and is dropped.
        var probes = needs.Count > 0
            ? needs
            : product is not null ? [EmbeddingDocument.ForProduct(product)] : (IReadOnlyList<string>)[];

        if (probes.Count == 0) return null;

        InterestSignal? best = null;
        double bestRank = 0.0;

        var products = Catalogue.Default.All;

        foreach (var signal in map.Signals)
        {
            var label  = await EmbeddingSpace.EmbedAsync(products, signal.Label, cancellationToken).ConfigureAwait(false);

            double match = 0.0;
            foreach (var probe in probes)
            {
                var probeVector = await EmbeddingSpace.EmbedAsync(products, probe, cancellationToken).ConfigureAwait(false);
                double cosine   = EmbeddingVectors.DotOfUnitVectors(label.Span, probeVector.Span);
                match = Math.Max(match, AttributionMatch(signal.Label, probe, cosine));
            }

            // The floor is on the MATCH, never on the rank: a very strong interest must not be
            // able to buy its way past a query it does not explain.
            if (match < AttributionFloor) continue;

            double rank = match * signal.Strength;

            // Ties break on label, ordinal, so identical runs produce identical ledgers. A demo
            // whose numbers move between two runs of the same input teaches the audience to
            // distrust every other number on the screen.
            if (rank > bestRank ||
                (rank == bestRank && best is not null && string.CompareOrdinal(signal.Label, best.Label) < 0))
            {
                bestRank = rank;
                best = signal;
            }
        }

        return best;
    }

    /// <summary>
    /// Maps the model's evidence citation back to the <c>(key, value)</c> pair it names, so the
    /// product side of <see cref="EvidenceDto"/> restates a catalogue fact rather than inventing one.
    /// </summary>
    /// <remarks>
    /// When the citation names nothing, the raw token is passed through unchanged and the pipeline
    /// drops the item with <c>attribute_not_found</c>. Substituting a real attribute for a bad one
    /// would be repairing the artifact under test — the citation is the model's claim, and a claim
    /// that resolves to nothing has to stay unresolved.
    /// </remarks>
    private static (string Key, string Value) ResolveProductSide(Product? product, PresentedRecommendation item)
    {
        var raw = item.Evidence?.Trim() ?? string.Empty;
        if (product is null) return (raw, raw);

        if (item.Citation is not { } citation) return (raw, raw);

        if (citation.Kind == EvidenceRefKind.Review)
        {
            // A review citation carries no attribute, and the tool has no second argument for
            // one. The product side is read from the catalogue's authored spec order; the
            // discriminating fact for this citation is still the model's own review id, checked
            // verbatim against Product.ReviewIds.
            var first = product.Specs.FirstOrDefault();
            return first.Key is { Length: > 0 } ? (first.Key, first.Value) : (raw, raw);
        }

        var token = citation.Token;

        foreach (var (key, value) in product.Specs)
        {
            var k = Product.NormalizeAttributeToken(key);
            var v = Product.NormalizeAttributeToken(value);
            if (string.Equals(k, token, StringComparison.Ordinal) ||
                string.Equals(v, token, StringComparison.Ordinal) ||
                string.Equals($"{k}={v}", token, StringComparison.Ordinal))
            {
                return (key, value);
            }
        }

        foreach (var tag in product.Tags)
        {
            if (string.Equals(Product.NormalizeAttributeToken(tag), token, StringComparison.Ordinal))
                return (tag, tag);

            var colon = tag.IndexOf(':');
            if (colon > 0 && colon < tag.Length - 1 &&
                string.Equals(Product.NormalizeAttributeToken(tag[(colon + 1)..]), token, StringComparison.Ordinal))
            {
                // The WHOLE tag, not the suffix: TryGetAttributeValue resolves a whole tag and
                // Product.Attributes contains it, so the assembled evidence stays verifiable on
                // both sides. The suffix alone satisfies only one of the two.
                return (tag, tag);
            }
        }

        return (raw, raw);
    }

    /// <summary>
    /// The routing number printed on each card and banded by <see cref="ConfidenceBands"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is NOT the model's self-reported confidence. The <c>PresentRecommendation</c> tool has
    /// no confidence argument, deliberately: self-reported LLM confidence is uncalibrated until
    /// somebody measures it, and the confidence-band policy would rather route on something the code can point at.
    /// </para>
    /// <para>
    /// So this is the mean of two code-derived quantities — the strength of the interest signal
    /// the product was credited to, and the embedding-space fit between the product's own document
    /// and that signal's label. Both are unmeasured against outcomes. It is a routing
    /// heuristic for the two trays and nothing more; the reliability curve of this number against
    /// a gold set belongs to the eval lane, and until that runs no claim is made about it.
    /// </para>
    /// <para>
    /// <b>Co-moving operands.</b> The fit is computed in the same space that retrieved the product,
    /// so a product retrieved because it is near the label also scores well on being near the label.
    /// Breaking that coupling requires a second independent signal and belongs in the eval lane.
    /// </para>
    /// <para>
    /// Both operands must exist in the same space. Runtime labels are therefore embedded on demand
    /// rather than treated as zero when absent from a committed product index; otherwise confidence
    /// would collapse to <c>strength / 2</c> and shift every card by an implementation artifact.
    /// </para>
    /// <para>
    /// The call is asynchronous because the real-vector space can reach the network. Mixing concept
    /// retrieval with real-vector confidence (or the reverse) would put incomparable cosines in one
    /// report; <see cref="EmbeddingSpace.Requested"/> prevents that. Per-run memoisation keeps stored
    /// product documents as cache hits and embeds each distinct runtime label once.
    /// </para>
    /// </remarks>
    private static async Task<double> ConfidenceAsync(
        InterestSignal? signal,
        Product? product,
        CancellationToken cancellationToken = default)
    {
        if (signal is null || product is null) return 0.0;

        var products = Catalogue.Default.All;

        var labelVector   = await EmbeddingSpace.EmbedAsync(products, signal.Label, cancellationToken).ConfigureAwait(false);
        var productVector = await EmbeddingSpace
            .EmbedAsync(products, EmbeddingDocument.ForProduct(product), cancellationToken)
            .ConfigureAwait(false);

        var fit = EmbeddingVectors.DotOfUnitVectors(labelVector.Span, productVector.Span);

        return ConfidenceFrom(signal.Strength, fit);
    }

    /// <summary>
    /// The shipped ATTRIBUTION MATCH arithmetic, as one public expression: the better of the
    /// space's cosine and the token-overlap fallback.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="AttributeSignalAsync"/> — which now calls it — so the calibration
    /// lane can collect the distribution this floor cuts by evaluating the SHIPPED formula rather
    /// than a second copy of it. A calibration harness that re-implements the arithmetic it is
    /// calibrating derives a threshold for a function the product does not run.
    /// </remarks>
    /// <param name="signalLabel">The derived interest's label — the left-hand side of the overlap.</param>
    /// <param name="probe">The search need that surfaced the product, or the product's own embedding document.</param>
    /// <param name="cosine">The cosine between the two in the RESOLVED embedding space.</param>
    public static double AttributionMatch(string signalLabel, string probe, double cosine) =>
        Math.Max(cosine, Overlap(ContentTokens(signalLabel), ContentTokens(probe)));

    /// <summary>
    /// The shipped CONFIDENCE arithmetic, as one public expression: the mean of the signal's
    /// strength and the product's fit, clamped to 0..1.
    /// </summary>
    /// <remarks>
    /// Extracted from <see cref="ConfidenceAsync"/> for the reason
    /// <see cref="AttributionMatch"/> gives. Note the shape: <paramref name="signalStrength"/> is
    /// space-INDEPENDENT (it is derived from purchase history) and <paramref name="fit"/> is a
    /// cosine, so exactly half of every confidence moves when the embedding space changes and the
    /// other half does not. That asymmetry is why the confidence bands are space-dependent at all.
    /// </remarks>
    /// <param name="signalStrength">The attributed interest signal's strength, 0..1.</param>
    /// <param name="fit">Cosine between the signal label and the product's embedding document. Negative reads as 0.</param>
    public static double ConfidenceFrom(double signalStrength, double fit) =>
        Math.Clamp((signalStrength + Math.Max(0.0, fit)) / 2.0, 0.0, 1.0);

    // ── Ledger annotations ────────────────────────────────────────────────────

    /// <summary>
    /// Records, in the ledger itself, which arms could not have failed on this turn.
    /// </summary>
    /// <remarks>
    /// A clean ledger is only evidence when every arm on it was able to fire. Writing the
    /// inapplicable arms down beside the counts is what stops the panel from being read as a
    /// score — see <see cref="GuardrailLedger.HasInapplicableArm"/>, which makes the renderer
    /// print the warning in red.
    /// </remarks>
    /// <param name="ledger">The turn's ledger.</param>
    /// <param name="offline">True when the baseline arm stood in for the model.</param>
    /// <param name="presentedCount">How many presentations were made in total.</param>
    /// <param name="modelStatedUserSides">How many of them carried a MODEL-written user side.</param>
    internal static void NoteEvidenceArms(GuardrailLedger ledger, bool offline, int presentedCount, int modelStatedUserSides)
    {
        // Record the fallback only for presentations whose user side was actually derived. If this
        // note is absent, every presentation carried model-written evidence the filter could test.
        var derived = Math.Max(0, presentedCount - modelStatedUserSides);
        if (derived > 0)
        {
            ledger.Note(GuardrailStage.EvidenceRequired, GuardrailReasons.ArmInapplicable, "user-side evidence",
                $"{derived} of {presentedCount} presentation(s) carried NO userEvidence argument, so their user side was "
              + "DERIVED from retrieval provenance. On those the arm can fail only when no derived interest explains the "
              + "product at all — it cannot catch a wrongly cited purchase, because nothing was cited. Do not read its "
              + "silence as a pass");
        }

        // ⚠ The PRODUCT side has two silent arms on this path as well, and leaving them unnamed
        // let a two-sided check read as a clean sheet on a turn where only one side could fire.
        //
        //   attribute_value_mismatch: ResolveProductSide fills BOTH ProductAttributeKey and
        //   ProductAttributeValue out of the catalogue's own record, so the comparison the filter
        //   makes is x == x. It cannot fail while the model has no argument for a value.
        //
        //   unresolvable_evidence: EvidenceDto.Citation is REBUILT from ProductAttributeKey, which
        //   TryGetAttributeValue resolved one branch above. It re-asserts a fact already checked.
        //
        // The discriminating product-side arm that DOES fire is attribute_not_found: the citation
        // is the model's own verbatim argument, and a token the catalogue does not carry fails there.
        ledger.Note(GuardrailStage.EvidenceRequired, GuardrailReasons.ArmInapplicable, "product-side value + citation",
            "attribute_value_mismatch and unresolvable_evidence cannot fire on this path. The tool carries ONE "
          + "evidence string, so the product-side key AND value are both resolved from the catalogue before the "
          + "check runs, and the compact citation is rebuilt from the key that resolution already verified. The "
          + "product-side arm that IS discriminating is attribute_not_found, on the model's verbatim citation. "
          + "Do not read these two arms' silence as a pass");

        if (offline)
        {
            ledger.Note(GuardrailStage.EvidenceRequired, GuardrailReasons.ArmInapplicable, "offline baseline arm",
                "no model ran: the citations below were read straight from the catalogue and the reason strings were "
              + "composed by this file, so the evidence, grounding, sensitive_prose and stated_price arms cannot fail "
              + "on this path either. The already_owned arm is likewise pre-empted — the offline selector passes the "
              + "customer's owned SKUs as a retrieval exclusion, so nothing owned ever reaches the filter. This panel "
              + "measures the BASELINE, not the agent");
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reconciles what the model presented against what the customer will see, item by item.
    /// </summary>
    /// <remarks>
    /// Printed because the two counts differing is the interesting event, and a panel that shows
    /// only the survivors hides it. Zero presentations gets its own loud line: an agent that says
    /// nothing must never read the same as an agent that answered well.
    /// </remarks>
    private static void PrintPresentationAudit(IReadOnlyList<PresentedRecommendation> presented, RecommendationSet cleaned)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─── Channel audit (PresentRecommendation calls → cards) ──────────────────");
        Console.ResetColor();

        if (presented.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ⚠  The agent made ZERO PresentRecommendation calls.");
            Console.WriteLine(cleaned.Abstained
                ? "     The abstention gate had already fired, so this is the expected shape for this persona."
                : "     Nothing was recommended and the gate did NOT fire — this is a MISS, not a cautious answer.\n"
                + "     A product named only in the prose below is not a recommendation; the channel is the tool call.");
            Console.ResetColor();
            Console.WriteLine();
            return;
        }

        var survived = cleaned.AllPresented.Select(r => r.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in presented)
        {
            var kept = survived.Contains(item.Sku.Trim());
            Console.ForegroundColor = kept ? ConsoleColor.DarkGray : ConsoleColor.Yellow;
            Console.WriteLine($"     {(kept ? "✓" : "✗")} {item.Sku,-10} evidence={Clip(item.Evidence, 34),-34} "
                            + $"outOfStock={item.OutOfStock.ToString().ToLowerInvariant()}");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"     {presented.Count} presented → {cleaned.PresentedCount} shown");
        Console.ResetColor();
        Console.WriteLine();
    }

    /// <summary>
    /// Explains the accounting behind the ledger's "tool calls N of 24" line, every run.
    /// </summary>
    /// <remarks>
    /// The ledger's figure is REFUSABLE calls only: the three semantic and nine structured tools.
    /// <c>PresentRecommendation</c> is the answer channel — counted separately, never refused and
    /// never charged to a cap, because a spent budget must bound the spend, not silence the
    /// answer. Identical repeats within the turn are replayed from memory and charged to nothing.
    /// Printing the counters separately prevents a saturated aggregate from making presentations
    /// look like search spend or a cap-triggered stop.
    /// </remarks>
    private static void PrintBudgetNote(string budgetSummary)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ℹ  budget accounting: {budgetSummary}");
        Console.WriteLine("     'refusable' is the ledger's tool-call figure: the twelve retrieval and lookup tools, capped.");
        Console.WriteLine("     Presentations are the answer channel — counted, never refused, never charged to a cap.");
        Console.WriteLine("     'replays' are identical repeats answered from this turn's memory at no cost.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintRobinsProse(string? text)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─── What Robin wrote ─────────────────────────────────────────────────────");
        Console.WriteLine("  (prose only. Nothing here is parsed: the cards above were built from the");
        Console.WriteLine("   PresentRecommendation tool calls, which is the one sanctioned channel.)");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine();
        Console.WriteLine(string.IsNullOrWhiteSpace(text) ? "  (empty)" : Indent(text));
        Console.ResetColor();
        Console.WriteLine();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════════════════════╗
║   Demo 01 — Robin, the VITRINE recommendation agent (single agent)           ║
║   Code derives the interests · the model searches · code screens the answer  ║
╚══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintRequest(CustomerProfile profile, string prompt, bool personalizationDisabled)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Customer : {profile.DisplayName} ({profile.Id}) · {profile.Market} · {profile.Language} · "
                        + $"{profile.PurchaseCount} purchase line(s)");
        Console.ResetColor();

        if (personalizationDisabled)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  🔒 personalization OFF — GetPurchaseHistory and GetInterestMap will REFUSE. The");
            Console.WriteLine("     history is not filtered or summarised; it is not read. The turn runs on what");
            Console.WriteLine("     the customer says in this conversation.");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"\n  Request  : \"{prompt}\"\n");
        Console.ResetColor();
    }

    private static void PrintOfflineBanner()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"  ┌──────────────────────────────────────────────────────────────────────────┐
  │  OFFLINE — no model call was made.                                       │
  │  Everything below was produced by the deterministic path alone: the       │
  │  interest map, one search per signal, and the guardrail pipeline. This is │
  │  the BASELINE arm, not the agent. Compare it with a live run before       │
  │  believing any claim about what the model adds.                          │
  └──────────────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintScriptedBanner()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(@"  ┌──────────────────────────────────────────────────────────────────────────┐
  │  SCRIPTED AGENT — deterministic local chat boundary.                    │
  │  The real ChatClientAgent and registered read-only tools execute, while │
  │  no remote chat model is called. This is a reproducible mechanism run,  │
  │  not evidence of provider-model quality.                                │
  └──────────────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintUnknownPersona(string userId)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ❌ Unknown customer '{userId}'.");
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("     Known personas:");
        foreach (var id in Personas.AllPersonaIds)
        {
            var profile = UserProfiles.Require(id);
            Console.WriteLine($"       {id}  {profile.DisplayName}");
        }
        Console.WriteLine("\n     No fallback is applied on purpose: running the wrong persona's prompt against the");
        Console.WriteLine("     right persona's history produces a plausible, wrong demo.");
        Console.ResetColor();
    }

    private static void PrintMissingCredentials()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n  ⚠️  Skipping the live run — no inference provider is locally ready.");
        Console.WriteLine($"     {Config.Readiness.BlockingReason}");
        Console.WriteLine("     Select a host with AI_INFERENCE_PROVIDER (azure, bitdeer, openai, foundry,");
        Console.WriteLine("     openai-compatible) and give it the variables named above, as documented in");
        Console.WriteLine("     docs/Vitrine-Live-Run-Setup.html, or run the deterministic path:");
        Console.WriteLine("       dotnet run --project src/AgentEval.VitrineDemo -- 1 --offline\n");
        Console.ResetColor();
    }

    // ── Small pure helpers ────────────────────────────────────────────────────

    private static bool IsStatedInSession(InterestSignal signal) =>
        string.Equals(signal.EvidenceKind, InterestEvidenceKinds.StatedInSession, StringComparison.Ordinal);

    /// <summary>
    /// Content tokens for the fallback attribution leg: lower-cased words of three characters or
    /// more, with a crude trailing-s / -es / -ing / -ed strip. Crude on purpose and documented as
    /// such — it is a fallback for vocabulary the concept lexicon does not cover, not a stemmer.
    /// </summary>
    private static HashSet<string> ContentTokens(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        foreach (var raw in text.ToLowerInvariant().Split(
                     [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '"', '\'', '/', '-', '—'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 3) continue;
            var token = raw;
            if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 5) token = token[..^3];
            else if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 4) token = token[..^2];
            else if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 4) token = token[..^2];
            else if (token.EndsWith('s') && token.Length > 3) token = token[..^1];
            tokens.Add(token);
        }

        return tokens;
    }

    private static double Overlap(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0) return 0.0;
        var shared = left.Count(right.Contains);
        return (double)shared / left.Count;
    }

    private static string Clip(string? text, int max)
    {
        var value = text ?? string.Empty;
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine,
            text.Replace("\r\n", "\n").Split('\n').Select(line => "  " + line));
}
