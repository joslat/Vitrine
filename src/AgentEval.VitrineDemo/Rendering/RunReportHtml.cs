// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using System.Globalization;
using System.Text;

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Workflows;

namespace Galaxus.RecommendationAgent.Rendering;

/// <summary>
/// Writes ONE self-contained HTML file for the turn that just ran: the customer, the tray the
/// agent actually produced, the discovery loop's coverage ledger when there was one, and — beside
/// it — what the guardrails measured on this turn.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the console panel, in a browser.</b> It takes exactly the arguments
/// <see cref="RecommendationPrinter.PrintAnswer"/> takes, plus the loop result when there is one.
/// That is deliberate and it is the whole design: a second renderer with a second set of inputs
/// would be a second thing to keep in agreement with the run, and the first time the two disagreed
/// the prettier one would be believed. Nothing here re-derives, re-screens, re-ranks or re-scores.
/// Every number on the page is read off the objects the run already produced.
/// </para>
/// <para>
/// <b>Four properties this file must keep, each of them load-bearing for the reason beside it.</b>
/// </para>
/// <list type="number">
///   <item><description><b>Self-contained.</b> No external stylesheet, script, font or image. It
///   is opened from a file, possibly with no network, possibly on someone else's laptop.</description></item>
///   <item><description><b>No credential surface.</b> No endpoint, no key, no fingerprint, no
///   absolute path. The DEPLOYMENT NAME is the only configuration value that reaches the page, and
///   <see cref="RefuseIfSecretsLeaked"/> re-reads the finished string and refuses to write it if a
///   secret got in anyway — the check reads the bar out of the ENVIRONMENT, never out of the page.</description></item>
///   <item><description><b>Deterministic.</b> No wall clock, no run id, no elapsed time, no
///   iteration over an unordered map. Two offline runs of the same persona produce byte-identical
///   files, so the page can be regenerated in front of someone.</description></item>
///   <item><description><b>An absence is never a zero.</b> A check that could not run on this turn
///   renders as <c>NOT MEASURED</c>, in the loudest style on the page; a count no caller supplied
///   renders as <c>n/a</c>. Neither is allowed to look like a clean result. This is the failure
///   this repository has fixed four times, and a report is exactly where it comes back.</description></item>
/// </list>
/// <para>
/// ⚠ <b>Why there is no timestamp.</b> Every price on this page came from a
/// <see cref="PriceStockSnapshot"/> that carries the UTC instant it was read. The instant is not
/// printed, because printing it would make two identical runs differ and cost the property above.
/// The claim that matters — that the figures were re-read from the catalogue at render time and
/// never taken from model context — is printed instead, and it is a claim about the code path, not
/// about the clock.
/// </para>
/// </remarks>
public static class RunReportHtml
{
    /// <summary>Environment variables whose values may never appear in a written report.</summary>
    /// <remarks>
    /// The bar comes from the environment, not from the page: the artifact being checked does not
    /// get to supply the thing it is checked against. <c>AZURE_OPENAI_ENDPOINT</c> is included as
    /// well as the key because the endpoint URL names the Azure resource.
    /// </remarks>
    private static readonly string[] ForbiddenEnvironmentValues =
    [
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_ENDPOINT"
    ];

    /// <summary>
    /// Writes the report and returns the path it was written to.
    /// </summary>
    /// <param name="path">Destination file. Parent directories are created.</param>
    /// <param name="demoTitle">Which demo produced this, e.g. <c>"Demo 01 — one agent, 15 tools"</c>.</param>
    /// <param name="arm">
    /// Which arm ran, in words the page prints verbatim, e.g. <c>"offline baseline — no model call"</c>.
    /// ⚠ May name the DEPLOYMENT and nothing else about the account.
    /// </param>
    /// <param name="utterance">What the customer actually typed.</param>
    /// <param name="user">The customer this turn served.</param>
    /// <param name="map">The code-derived interest map.</param>
    /// <param name="classified">
    /// The purchases the run was ALLOWED to read, already classified. Empty under the
    /// personalization opt-out — and the page then says the history was not read, rather than
    /// showing a customer with no history.
    /// </param>
    /// <param name="set">The recommendation set AFTER the guardrail pipeline.</param>
    /// <param name="verified">Price and stock snapshots. A missing entry prints as unverified, never as a price.</param>
    /// <param name="ledger">The guardrail ledger this turn accumulated.</param>
    /// <param name="catalogue">The catalogue, for product names and category paths.</param>
    /// <param name="toolCallsUsed">Tool calls spent, or <see cref="RecommendationPrinter.OmitToolCalls"/>.</param>
    /// <param name="toolCallCap">The per-turn cap, or <see cref="RecommendationPrinter.OmitToolCalls"/>.</param>
    /// <param name="loop">The discovery loop's result, when Demo 02 produced this turn. Null for Demo 01.</param>
    /// <returns>The full path of the file written.</returns>
    /// <exception cref="InvalidOperationException">The rendered page contained a credential. Nothing is written.</exception>
    public static string Write(
        string path,
        string demoTitle,
        string arm,
        string utterance,
        User user,
        InterestMap map,
        IReadOnlyList<ClassifiedPurchase> classified,
        RecommendationSet set,
        IReadOnlyDictionary<string, PriceStockSnapshot> verified,
        GuardrailLedger ledger,
        Catalogue catalogue,
        int toolCallsUsed = RecommendationPrinter.OmitToolCalls,
        int toolCallCap = RecommendationPrinter.OmitToolCalls,
        DiscoveryRunResult? loop = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(classified);
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(catalogue);

        var html = Render(demoTitle, arm, utterance, user, map, classified, set, verified,
                          ledger, catalogue, toolCallsUsed, toolCallCap, loop);

        RefuseIfSecretsLeaked(html);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // UTF-8 without a BOM: a BOM makes two otherwise identical files differ from a hand-written
        // one, and browsers do not need it once the page declares its charset.
        File.WriteAllText(full, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return full;
    }

    /// <summary>
    /// Refuses to write a page that contains the value of a credential environment variable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing above is supposed to be able to put a secret here</b> — the only configuration
    /// value the renderer touches is the deployment name, and the caller passes even that as text.
    /// This exists because "supposed to" is not a control. A report is a file people mail, screen
    /// share and commit, and a leak found after that is not recoverable. The check runs on the
    /// finished string, before a single byte reaches the disk.
    /// </para>
    /// <para>
    /// ⚠ The exception says WHICH variable leaked and never its value: an error message is a log
    /// line, and printing the secret to explain that the secret escaped would be the same defect
    /// one layer out. It is the same reason <c>Config.PrintAzureTarget</c> stopped printing an
    /// eight-character fingerprint of the key.
    /// </para>
    /// <para>
    /// Empty and whitespace values are skipped — an unset variable is not a secret, and matching
    /// on the empty string would refuse every page.
    /// </para>
    /// </remarks>
    /// <param name="html">The finished page.</param>
    private static void RefuseIfSecretsLeaked(string html)
    {
        foreach (var name in ForbiddenEnvironmentValues)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value) || value.Length < 8) continue;

            if (html.Contains(value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Refusing to write the report: it contains the value of {name}. " +
                    "Nothing was written. A report is a file people share; a credential in one is not recoverable.");
        }
    }

    // ── The page ──────────────────────────────────────────────────────────────

    private static string Render(
        string demoTitle,
        string arm,
        string utterance,
        User user,
        InterestMap map,
        IReadOnlyList<ClassifiedPurchase> classified,
        RecommendationSet set,
        IReadOnlyDictionary<string, PriceStockSnapshot> verified,
        GuardrailLedger ledger,
        Catalogue catalogue,
        int toolCallsUsed,
        int toolCallCap,
        DiscoveryRunResult? loop)
    {
        var sb = new StringBuilder(64 * 1024);

        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<title>").Append(E(user.DisplayName)).Append(" — ").Append(E(demoTitle)).Append("</title>\n")
          .Append("<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");

        AppendHeader(sb, demoTitle, arm, map);
        AppendCustomer(sb, user, utterance, classified, catalogue);

        sb.Append("<div class=\"split\">\n<div class=\"col-main\">\n");
        AppendTray(sb, set, verified, catalogue, ledger);
        if (loop is not null) AppendLoop(sb, loop);
        sb.Append("</div>\n<div class=\"col-side\">\n");
        AppendMeasurement(sb, ledger, set, toolCallsUsed, toolCallCap, loop);
        sb.Append("</div>\n</div>\n");

        AppendFooter(sb);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, string demoTitle, string arm, InterestMap map)
    {
        sb.Append("<header class=\"top\">\n")
          .Append("  <div class=\"brand\">Galaxus · <b>Robin</b>, the recommendation agent</div>\n")
          .Append("  <div class=\"top-meta\"><span class=\"pill\">").Append(E(demoTitle)).Append("</span>")
          .Append("<span class=\"pill\">").Append(E(arm)).Append("</span>");

        if (!map.PersonalizationEnabled)
            sb.Append("<span class=\"pill pill-warn\">personalization OFF — history was never read</span>");

        sb.Append("</div>\n</header>\n");
    }

    // ── 1 · The customer ──────────────────────────────────────────────────────

    private static void AppendCustomer(
        StringBuilder sb, User user, string utterance,
        IReadOnlyList<ClassifiedPurchase> classified, Catalogue catalogue)
    {
        sb.Append("<section class=\"card who\">\n")
          .Append("  <h2>1 · The customer</h2>\n")
          .Append("  <p class=\"oneline\"><b>").Append(E(user.DisplayName)).Append("</b> <span class=\"mono dim\">")
          .Append(E(user.Id)).Append("</span> · ").Append(E(user.Market)).Append(" · ")
          .Append(E(user.Language)).Append(" · customer since ")
          .Append(E(user.CustomerSince.ToString("yyyy-MM", CultureInfo.InvariantCulture))).Append("</p>\n")
          .Append("  <blockquote class=\"said\">").Append(E(utterance)).Append("</blockquote>\n");

        if (classified.Count == 0)
        {
            sb.Append("  <p class=\"note note-warn\"><b>Bought before: not read on this turn.</b> ")
              .Append(user.PersonalizationEnabled
                  ? "The classifier returned nothing for this customer, so no history reached the turn."
                  : "The customer has opted out of personalization, so the tools <em>refused</em> the purchase "
                  + "history — it never reached the state. This is not an empty history; it is a history that "
                  + "was not looked at.")
              .Append("</p>\n");
        }
        else
        {
            sb.Append("  <p class=\"lbl\">Bought before — ").Append(classified.Count)
              .Append(" order line(s), and how the code read each one</p>\n  <ul class=\"chips\">\n");

            foreach (var c in classified)
            {
                var name = catalogue.TryGet(c.Purchase.ProductId, out var p) && p is not null
                    ? p.Name : c.Purchase.ProductId;

                sb.Append("    <li class=\"chip chip-").Append(IntentClass(c.Intent)).Append("\">")
                  .Append("<span class=\"chip-tag\">").Append(E(c.Intent.ToString().ToUpperInvariant())).Append("</span> ")
                  .Append(E(name))
                  .Append(" <span class=\"dim mono\">").Append(E(c.Purchase.Id)).Append(" · ")
                  .Append(E(c.Purchase.PurchasedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                  .Append("</span>");

                // The gift verdict is the guardrail the audience gets to WATCH fire, so the reason
                // it fired is on the page rather than only the verdict.
                if (c.Intent != PurchaseIntent.ForSelf)
                    sb.Append("<span class=\"chip-why\">").Append(E(c.Because)).Append("</span>");

                sb.Append("</li>\n");
            }

            sb.Append("  </ul>\n");
        }

        sb.Append("</section>\n");
    }

    private static string IntentClass(PurchaseIntent intent) => intent switch
    {
        PurchaseIntent.Gift           => "gift",
        PurchaseIntent.Replenishment  => "repl",
        PurchaseIntent.Replacement    => "repl",
        _                             => "self"
    };

    // ── 2 · The tray ──────────────────────────────────────────────────────────

    private static void AppendTray(
        StringBuilder sb, RecommendationSet set,
        IReadOnlyDictionary<string, PriceStockSnapshot> verified,
        Catalogue catalogue, GuardrailLedger ledger)
    {
        sb.Append("<section class=\"card\">\n  <h2>2 · What Robin recommended</h2>\n");

        if (set.Abstained)
        {
            AppendAbstention(sb, set);
        }
        else if (set.PresentedCount == 0)
        {
            // The ablation this section exists for. An empty tray that renders as an empty tray
            // reads as a clean result; it is the opposite. Say what happened to the items.
            sb.Append("  <div class=\"empty\">\n    <p class=\"empty-head\">No recommendation.</p>\n")
              .Append("    <p>The turn produced <b>")
              .Append(ledger.Proposed.ToString(CultureInfo.InvariantCulture))
              .Append("</b> candidate(s) and the guardrails removed every one of them, so there is nothing to show. ")
              .Append("This is <em>not</em> a clean result — an empty tray and a screened tray are different outcomes, ")
              .Append("and the reasons are listed beside this panel.</p>\n  </div>\n");
        }
        else
        {
            AppendCards(sb, set.Recommendations, verified, catalogue, "Recommended", primary: true);
            AppendCards(sb, set.AlsoConsider, verified, catalogue, "You might also consider", primary: false);
        }

        AppendReplenishment(sb, set, catalogue);
        sb.Append("</section>\n");
    }

    private static void AppendAbstention(StringBuilder sb, RecommendationSet set)
    {
        sb.Append("  <div class=\"empty empty-abstain\">\n")
          .Append("    <p class=\"empty-head\">No recommendation — the agent declined to guess.</p>\n")
          .Append("    <p>").Append(E(set.AbstainReason ?? "no reason recorded")).Append("</p>\n");

        if (set.ClarifyingQuestions.Count > 0)
        {
            sb.Append("    <p class=\"lbl\">It asked instead</p>\n    <ul class=\"asked\">\n");
            foreach (var q in set.ClarifyingQuestions)
                sb.Append("      <li>").Append(E(q)).Append("</li>\n");
            sb.Append("    </ul>\n");
        }

        sb.Append("    <p class=\"note\">An abstention is <b>not automatically a pass</b>. On a case that had a right "
                + "answer it must be scored as a miss, or saying nothing becomes a way to score well.</p>\n  </div>\n");
    }

    private static void AppendCards(
        StringBuilder sb, IReadOnlyList<RecommendationDto> items,
        IReadOnlyDictionary<string, PriceStockSnapshot> verified,
        Catalogue catalogue, string heading, bool primary)
    {
        if (items.Count == 0) return;

        sb.Append("  <p class=\"lbl\">").Append(E(heading)).Append(" · ")
          .Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append("</p>\n  <div class=\"tray\">\n");

        foreach (var item in items)
        {
            catalogue.TryGet(item.ProductId, out var product);

            sb.Append("    <article class=\"prod").Append(primary ? "" : " prod-second").Append("\">\n")
              .Append("      <div class=\"prod-head\">\n        <div>\n")
              .Append("          <div class=\"prod-name\">").Append(E(product?.Name ?? item.ProductId)).Append("</div>\n")
              .Append("          <div class=\"dim small\">")
              .Append(E(product is null ? "not in the catalogue" : product.Brand))
              .Append(product is not null && product.CategoryPath.Count > 0
                  ? " · " + E(string.Join(" › ", product.CategoryPath))
                  : string.Empty)
              .Append(" · <span class=\"mono\">").Append(E(item.ProductId)).Append("</span></div>\n        </div>\n")
              .Append("        <div class=\"conf\" title=\"self-reported by the selector; uncalibrated, it routes between trays\">")
              .Append("conf ").Append(item.Confidence.ToString("0.00", CultureInfo.InvariantCulture)).Append("</div>\n")
              .Append("      </div>\n");

            // The price authority is the renderer, never the model. A missing snapshot prints as
            // an absence — never as a number from anywhere else.
            if (verified.TryGetValue(item.ProductId, out var price))
            {
                sb.Append("      <div class=\"price\">CHF ")
                  .Append(price.PriceChf.ToString("0.00", CultureInfo.InvariantCulture));

                if (price.IsDiscounted)
                    sb.Append(" <s class=\"dim\">CHF ")
                      .Append(price.WasPriceChf!.Value.ToString("0.00", CultureInfo.InvariantCulture)).Append("</s>");

                sb.Append(" <span class=\"dim small\">· ")
                  .Append(price.InStock
                      ? price.StockUnits.ToString(CultureInfo.InvariantCulture) + " in stock · "
                        + price.DeliveryEstimateDays.ToString(CultureInfo.InvariantCulture) + " working days"
                      : "out of stock")
                  .Append("</span></div>\n");
            }
            else
            {
                sb.Append("      <div class=\"price price-none\">price not verified on this turn — no figure is shown</div>\n");
            }

            sb.Append("      <p class=\"why\">").Append(E(item.WhyThis)).Append("</p>\n")
              .Append("      <div class=\"ev\">\n")
              .Append("        <div class=\"ev-side\"><span class=\"ev-lbl\">Your signal</span> ")
              .Append(E(item.Evidence.UserSignalLabel));

            if (item.Evidence.UserPurchaseIds.Count > 0)
                sb.Append(" <span class=\"mono dim\">← ")
                  .Append(E(string.Join(", ", item.Evidence.UserPurchaseIds))).Append("</span>");

            sb.Append("</div>\n")
              .Append("        <div class=\"ev-side\"><span class=\"ev-lbl\">Catalogue</span> ")
              .Append(item.Evidence.ReviewId is { Length: > 0 } reviewId
                  ? "customer review " + E(reviewId)
                  : E(item.Evidence.ProductAttributeKey) + ": " + E(item.Evidence.ProductAttributeValue))
              .Append(" <span class=\"mono dim\">[").Append(E(item.Evidence.Citation.ToString())).Append("]</span>")
              .Append("</div>\n      </div>\n    </article>\n");
        }

        sb.Append("  </div>\n");
    }

    private static void AppendReplenishment(StringBuilder sb, RecommendationSet set, Catalogue catalogue)
    {
        if (set.Replenishment.Count == 0) return;

        sb.Append("  <p class=\"lbl\">Buy again — the replenishment lane, never counted as a discovery</p>\n")
          .Append("  <ul class=\"repl\">\n");

        foreach (var r in set.Replenishment)
        {
            var name = catalogue.TryGet(r.ProductId, out var p) && p is not null ? p.Name : r.ProductId;
            sb.Append("    <li").Append(r.IsOverdue ? " class=\"overdue\"" : string.Empty).Append('>')
              .Append(E(name)).Append(" <span class=\"dim small\">")
              .Append(E(r.Because)).Append("</span></li>\n");
        }

        sb.Append("  </ul>\n");
    }

    // ── 3 · The loop (Demo 02 only) ───────────────────────────────────────────

    private static void AppendLoop(StringBuilder sb, DiscoveryRunResult loop)
    {
        var state = loop.State;
        var covered = state.Interests.Count(i => state.CoverageFor(i.Id).Status == CoverageStatus.Covered);

        sb.Append("<section class=\"card\">\n  <h2>3 · The interest map it inferred, and whether the loop covered it</h2>\n")
          .Append("  <p class=\"oneline\"><b>").Append(covered.ToString(CultureInfo.InvariantCulture))
          .Append(" of ").Append(state.Interests.Count.ToString(CultureInfo.InvariantCulture))
          .Append("</b> interest(s) covered · <b>").Append(state.OpenGaps.Count.ToString(CultureInfo.InvariantCulture))
          .Append("</b> gap(s) still open · round <b>")
          .Append(state.DiscoveryRound.ToString(CultureInfo.InvariantCulture)).Append(" of ")
          .Append(state.MaxRounds.ToString(CultureInfo.InvariantCulture)).Append("</b></p>\n");

        // The conditional edge is the architectural claim. Say which way it went, and say the
        // negative out loud — an edge that did not fire is a result, not a missing section.
        sb.Append(loop.Looped
            ? "  <p class=\"edge edge-on\">↩ <b>The loop-back edge FIRED.</b> The coverage reviewer sent the run "
              + "back to Discovery, so round 2 wrote its queries from the gaps it had just found — in the "
              + "catalogue's vocabulary, against records round 1 had already retrieved.</p>\n"
            : "  <p class=\"edge edge-off\">↪ <b>The loop-back edge did NOT fire.</b> The reviewer approved coverage "
              + "in round 1, so the run went straight to the ranker. The edge exists and is bounded; on this "
              + "customer it correctly declined to spend a second round.</p>\n");

        var caseAssessment = loop.TopologyCaseAssessment;
        sb.Append(caseAssessment.Outcome switch
        {
            DiscoveryTopologyCaseOutcome.Match =>
                "  <p class=\"note\"><b>Authored topology claim: MATCH.</b> The exact route trace, loop count, "
                + "round counter and stop reason agree with the independently registered cell for "
                + E(caseAssessment.Observation.EmbeddingSpace?.ToString() ?? "unknown") + ".</p>\n",
            DiscoveryTopologyCaseOutcome.Mismatch =>
                "  <p class=\"note note-warn\"><b>Authored topology claim: MISMATCH.</b> "
                + E(string.Join("; ", caseAssessment.Differences)) + ".</p>\n",
            _ =>
                "  <p class=\"note note-warn\"><b>Authored topology claim: NOT MEASURED.</b> "
                + E(string.Join("; ", caseAssessment.Differences)) + "</p>\n",
        });

        sb.Append("  <table class=\"cov\">\n")
          .Append("    <thead><tr><th>Interest</th><th>Kind</th><th>Coverage</th><th class=\"num\">Candidates</th>"
                + "<th>Note</th></tr></thead>\n    <tbody>\n");

        foreach (var interest in state.Interests)
        {
            var row = state.CoverageFor(interest.Id);

            sb.Append("      <tr>\n        <td><b>").Append(E(interest.Label)).Append("</b><div class=\"dim small\">")
              .Append(E(interest.Rationale)).Append("</div></td>\n")
              .Append("        <td><span class=\"kind kind-").Append(interest.Kind == InterestKind.Latent ? "latent" : "direct")
              .Append("\">").Append(E(interest.Kind.ToString().ToUpperInvariant())).Append("</span>")
              .Append(interest.IsReviewerInferred ? "<div class=\"dim small\">proposed mid-run</div>" : string.Empty)
              .Append("</td>\n")
              .Append("        <td><span class=\"cov-").Append(row.Status.ToString().ToLowerInvariant()).Append("\">")
              .Append(E(row.Status.ToString().ToUpperInvariant())).Append("</span></td>\n")
              .Append("        <td class=\"num\">")
              .Append(row.CandidateProductIds.Count.ToString(CultureInfo.InvariantCulture)).Append("</td>\n")
              .Append("        <td class=\"dim small\">")
              .Append(row.LastGapReason is { Length: > 0 } gap ? E(gap) : "—")
              .Append("</td>\n      </tr>\n");
        }

        sb.Append("    </tbody>\n  </table>\n")
          .Append("  <p class=\"note\"><b>Why it stopped: ").Append(E(state.StopReason.ToString())).Append(".</b> ")
          .Append(E(StopBecause(state.StopReason)))
          .Append(" The last round added ")
          .Append(state.LastRoundNewProductCount < 0
              ? "no round had run"
              : state.LastRoundNewProductCount.ToString(CultureInfo.InvariantCulture) + " new product id(s)")
          .Append(". Routes taken: <span class=\"mono small\">")
          .Append(E(string.Join(" → ", loop.RoutesTaken))).Append("</span></p>\n");

        if (state.DroppedQueryTerms.Count > 0)
        {
            sb.Append("  <p class=\"note note-warn\"><b>")
              .Append(state.DroppedQueryTerms.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" query term(s) refused</b> before anything was searched. A mid-run interest can be "
                    + "proposed out of review text, and review text is written by marketplace sellers, so a "
                    + "proposed term must already exist in the catalogue's own vocabulary: ")
              .Append(E(string.Join(", ", state.DroppedQueryTerms.Select(t => "\"" + t.Term + "\""))))
              .Append("</p>\n");
        }

        sb.Append("</section>\n");
    }

    private static string StopBecause(DiscoveryStopReason reason) => reason switch
    {
        DiscoveryStopReason.CoverageSufficient => "The reviewer approved: every interest on the map has candidates worth opening.",
        DiscoveryStopReason.RoundLimitReached  => "The round cap was reached with gaps still open. The bound held.",
        DiscoveryStopReason.NoProgress         => "A round added zero new product ids, so another identical round could not change the answer.",
        DiscoveryStopReason.GapsUnresolvable   => "Gaps remain and no materially different query is available — the reviewer said so rather than inventing one.",
        DiscoveryStopReason.GapsRemain         => "Gaps remain and a further round was still permitted; this is not a terminal state.",
        _                                      => "The loop recorded no stop reason, which should not happen."
    };

    // ── 4 · The measurement ───────────────────────────────────────────────────

    /// <summary>
    /// The checks this turn ran, in pipeline order, each read off the ledger the run produced.
    /// </summary>
    /// <remarks>
    /// One row per <see cref="GuardrailStage"/>, because the stage is what the ledger records and
    /// inventing a different grouping here would mean the page and the console could disagree about
    /// what ran. The prose is what the stage checks, not what it found — what it found is the
    /// verdict, and the verdict comes from the entries.
    /// </remarks>
    private static readonly (GuardrailStage Stage, string Name, string Checks)[] Checks =
    [
        (GuardrailStage.ToolSurface,          "Read-only tool surface",
            "Every registered tool is on the read-only allow-list, asserted at construction. Nothing here can place an order."),
        (GuardrailStage.AbstentionGate,       "Refuse before spending",
            "A history too thin to act on must stop the turn BEFORE the retriever is built and before the model is constructed."),
        (GuardrailStage.InterestMap,          "Sensitive labels, inbound",
            "An emitted interest label may not name a special category."),
        (GuardrailStage.CatalogueGrounding,   "Catalogue grounding + ownership",
            "The SKU must resolve in the catalogue, and must not be something the customer already owns."),
        (GuardrailStage.EvidenceRequired,     "Two-sided evidence",
            "Each item must cite a code-derived interest AND a catalogue fact that resolves. Plausible prose cannot pass."),
        (GuardrailStage.SensitiveInference,   "Special-category screen, outbound",
            "A product under a sensitive leaf is not surfaced unless the customer asked for it."),
        (GuardrailStage.ConfidenceBands,      "Confidence banding",
            "Routes between the two trays. A number that is not a confidence is not a pass."),
        (GuardrailStage.PriceStock,           "Price + stock authority",
            "Prices are re-read from the catalogue at render time. An item whose reason text states a price is dropped."),
        (GuardrailStage.CandidateContainment, "Candidate containment",
            "An item may only be presented if some retrieval route in THIS turn actually returned it."),
        (GuardrailStage.Compatibility,        "Compatibility with owned hardware",
            "An accessory whose compat: value contradicts hardware the customer owns is dropped, not down-ranked.")
    ];

    private static void AppendMeasurement(
        StringBuilder sb, GuardrailLedger ledger, RecommendationSet set,
        int toolCallsUsed, int toolCallCap, DiscoveryRunResult? loop)
    {
        var rows = Checks
            .Select(c => (c.Name, c.Checks, Entries: ledger.EntriesFor(c.Stage)))
            .Select(c => (
                c.Name,
                c.Checks,
                c.Entries,
                Inapplicable: c.Entries.Any(e =>
                    string.Equals(e.Reason, GuardrailReasons.ArmInapplicable, StringComparison.Ordinal)),
                Acted: c.Entries.Count(e => e.Action != GuardrailAction.Noted)))
            .ToList();

        int notMeasured = rows.Count(r => r.Inapplicable);

        sb.Append("<section class=\"card side\">\n  <h2>4 · What was measured on this turn</h2>\n");

        // The loudest thing on the page, and it is the honest one. A check that could not fire has
        // a chance floor of 1.0; reading its silence as a pass is how a clean-looking panel lies.
        sb.Append(notMeasured > 0
            ? "  <p class=\"banner banner-warn\"><b>" + notMeasured.ToString(CultureInfo.InvariantCulture)
              + " of these " + rows.Count.ToString(CultureInfo.InvariantCulture)
              + " checks COULD NOT FAIL on this run.</b> They had nothing to fire against, so their silence is "
              + "not a pass — it is evidence they were never tested. They are marked NOT MEASURED below, and "
              + "no green tick on this page covers them.</p>\n"
            : "  <p class=\"banner banner-ok\">Every check below was exercised on this run.</p>\n");

        sb.Append("  <ul class=\"checks\">\n");

        foreach (var row in rows)
        {
            var (cls, label) = row.Inapplicable ? ("na", "NOT MEASURED")
                             : row.Acted > 0    ? ("hit", "CAUGHT " + row.Acted.ToString(CultureInfo.InvariantCulture))
                             :                    ("ok", "CLEAN");

            sb.Append("    <li class=\"chk chk-").Append(cls).Append("\">\n")
              .Append("      <div class=\"chk-head\"><span class=\"chk-name\">").Append(E(row.Name))
              .Append("</span><span class=\"chk-verdict\">").Append(label).Append("</span></div>\n")
              .Append("      <div class=\"chk-what\">").Append(E(row.Checks)).Append("</div>\n");

            foreach (var entry in row.Entries)
            {
                var action = entry.Action switch
                {
                    GuardrailAction.Dropped => "dropped",
                    GuardrailAction.Demoted => "demoted",
                    _ => string.Equals(entry.Reason, GuardrailReasons.ArmInapplicable, StringComparison.Ordinal)
                        ? "not tested" : "noted"
                };

                sb.Append("      <div class=\"chk-entry\"><span class=\"mono\">").Append(E(entry.Subject))
                  .Append("</span> <b>").Append(E(action)).Append("</b> · <span class=\"mono dim\">")
                  .Append(E(entry.Reason)).Append("</span><div class=\"dim small\">").Append(E(entry.Detail))
                  .Append("</div></div>\n");
            }

            sb.Append("    </li>\n");
        }

        sb.Append("  </ul>\n");
        AppendCounters(sb, ledger, set, toolCallsUsed, toolCallCap, loop);
        AppendNotMeasuredHere(sb);
        sb.Append("</section>\n");
    }

    private static void AppendCounters(
        StringBuilder sb, GuardrailLedger ledger, RecommendationSet set,
        int toolCallsUsed, int toolCallCap, DiscoveryRunResult? loop)
    {
        sb.Append("  <p class=\"lbl\">This turn, counted</p>\n  <table class=\"nums\">\n");

        Num(sb, "Proposed → shown",
            ledger.Proposed.ToString(CultureInfo.InvariantCulture) + " → " +
            set.PresentedCount.ToString(CultureInfo.InvariantCulture), false);
        Num(sb, "Removed by a guardrail", ledger.DroppedCount.ToString(CultureInfo.InvariantCulture), false);
        Num(sb, "Moved to the second tray", ledger.DemotedCount.ToString(CultureInfo.InvariantCulture), false);

        // ⚠ int?, and that is the point: Demo 02's presenter never sees the interest map, so it
        // cannot supply this count. Printing 0 there put "gift-excluded 0" on Marco's panel, who has
        // TWO — a false zero in the flattering direction, produced by an absent caller rather than
        // by a run in which nothing was excluded.
        Num(sb, "Purchases ruled out as gifts",
            ledger.GiftExcluded is { } gifts
                ? gifts.ToString(CultureInfo.InvariantCulture)
                : "n/a — this caller did not supply the count",
            ledger.GiftExcluded is null);

        Num(sb, "Prices re-read from the catalogue",
            ledger.PriceStockVerified.ToString(CultureInfo.InvariantCulture) + " of " +
            ledger.PriceStockRequested.ToString(CultureInfo.InvariantCulture), false);

        Num(sb, "Tool calls spent",
            toolCallsUsed >= 0 && toolCallCap > 0
                ? toolCallsUsed.ToString(CultureInfo.InvariantCulture) + " of " +
                  toolCallCap.ToString(CultureInfo.InvariantCulture) + " allowed"
                : "n/a — this arm makes no refusable tool calls",
            toolCallsUsed < 0);

        if (loop is not null)
        {
            Num(sb, "Model calls", loop.State.ModelCalls.ToString(CultureInfo.InvariantCulture), false);
            Num(sb, "Searches run", loop.State.SearchesRun.ToString(CultureInfo.InvariantCulture), false);

            if (loop.Failed)
                Num(sb, "Executors that FAILED",
                    loop.ExecutorFailures.Count.ToString(CultureInfo.InvariantCulture), true);
        }

        sb.Append("  </table>\n");
    }

    private static void Num(StringBuilder sb, string label, string value, bool absent) =>
        sb.Append("    <tr><td>").Append(E(label)).Append("</td><td class=\"num")
          .Append(absent ? " absent" : string.Empty).Append("\">").Append(E(value)).Append("</td></tr>\n");

    /// <summary>
    /// What this page is NOT evidence for. Stated on the page, because a report that only shows
    /// what went well is a marketing asset.
    /// </summary>
    private static void AppendNotMeasuredHere(StringBuilder sb) =>
        sb.Append("  <p class=\"lbl\">What this page does not measure</p>\n")
          .Append("  <p class=\"note\">Everything above is <b>one turn</b>. The claims that need more than one turn — "
                + "does the loop cover more of a customer's interests than the single agent, does a hostile product "
                + "review change what gets recommended, is the answer stable when the same turn is repeated — cannot be "
                + "read off this page in either direction. They are measured separately, over the whole persona set, "
                + "with negative controls and a chance floor per arm:</p>\n")
          .Append("  <pre class=\"cmd\">dotnet run --project src/AgentEval.VitrineDemo.Evals -- --all</pre>\n");

    private static void AppendFooter(StringBuilder sb) =>
        sb.Append("<footer class=\"foot\">\n")
          .Append("  <p><b>These are suggestions.</b> Prices and availability were re-read from the catalogue at render "
                + "time and never taken from model context; the decision is the customer's. Nothing was added to a "
                + "basket or ordered — this agent has no tool that can.</p>\n")
          .Append("  <p class=\"dim small\">Generated by the run itself, not from a fixture. Self-contained: no external "
                + "stylesheet, script, font or image, so it opens with no network. It carries no endpoint, no key and no "
                + "file path. It also carries no timestamp and no run id — deliberately, so two offline runs of the same "
                + "customer produce byte-identical files and this page can be regenerated in front of you.</p>\n")
          .Append("</footer>\n");

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// HTML-escapes untrusted text. Everything on the page goes through it — product names, review
    /// ids, model prose and, above all, the customer's own sentence.
    /// </summary>
    /// <remarks>
    /// Quotes are escaped as well as angle brackets: a value that is safe in element content is not
    /// safe in an attribute, and a page that is right only because no current caller writes into an
    /// attribute is right by accident.
    /// </remarks>
    /// <param name="text">Any string bound for the page.</param>
    private static string E(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&':  sb.Append("&amp;");  break;
                case '<':  sb.Append("&lt;");   break;
                case '>':  sb.Append("&gt;");   break;
                case '"':  sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;");  break;
                default:   sb.Append(ch);       break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The whole stylesheet, inline. No CDN, no font file, no image — see the type remarks.
    /// </summary>
    private const string Css = """
    :root{color-scheme:light}
    *{box-sizing:border-box}
    body{margin:0;background:#f4f5f7;color:#16181d;
         font:15px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;
         padding:0 0 48px}
    h2{font-size:13px;letter-spacing:.08em;text-transform:uppercase;color:#5f6672;margin:0 0 14px}
    .mono{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:.86em}
    .dim{color:#6b7280}
    .small{font-size:12px}
    .num{text-align:right;font-variant-numeric:tabular-nums}

    .top{background:#16181d;color:#fff;padding:18px 24px;display:flex;flex-wrap:wrap;
         align-items:center;justify-content:space-between;gap:12px}
    .brand{font-size:17px;letter-spacing:.01em}
    .top-meta{display:flex;flex-wrap:wrap;gap:8px}
    .pill{background:#2c3038;border-radius:999px;padding:4px 12px;font-size:12px;color:#d6dae1}
    .pill-warn{background:#7c2d12;color:#fed7aa}

    .card{background:#fff;border:1px solid #e3e6ea;border-radius:10px;padding:20px 22px;margin:20px 24px 0}
    .split{display:grid;grid-template-columns:minmax(0,1.65fr) minmax(0,1fr);gap:0;align-items:start}
    .col-main,.col-side{min-width:0}
    .col-side .card{margin-right:24px;margin-left:4px}
    .col-main .card{margin-right:4px}
    @media (max-width:1000px){.split{grid-template-columns:1fr}
      .col-side .card{margin-left:24px}.col-main .card{margin-right:24px}}

    .oneline{margin:0 0 12px;font-size:15px}
    .said{margin:0 0 16px;padding:12px 16px;background:#f7f8fa;border-left:3px solid #16181d;
          border-radius:0 6px 6px 0;font-size:15px;color:#2c3038}
    .lbl{font-size:11px;letter-spacing:.09em;text-transform:uppercase;color:#8b929c;margin:18px 0 8px}
    .note{background:#f7f8fa;border:1px solid #e8ebef;border-radius:6px;padding:10px 12px;
          font-size:13px;color:#3f4652;margin:12px 0 0}
    .note-warn{background:#fffbeb;border-color:#fcd9a4;color:#7c4a12}

    .chips{list-style:none;padding:0;margin:0;display:flex;flex-direction:column;gap:6px}
    .chip{border:1px solid #e3e6ea;border-radius:6px;padding:7px 10px;font-size:13px;background:#fbfcfd}
    .chip-gift{background:#fff7ed;border-color:#fcd9a4}
    .chip-repl{background:#f0f9ff;border-color:#bae0f5}
    .chip-tag{font-size:10px;letter-spacing:.07em;font-weight:700;color:#5f6672;margin-right:6px}
    .chip-gift .chip-tag{color:#9a3412}
    .chip-repl .chip-tag{color:#075985}
    .chip-why{display:block;color:#6b7280;font-size:12px;margin-top:3px}

    .tray{display:flex;flex-direction:column;gap:10px}
    .prod{border:1px solid #e3e6ea;border-radius:8px;padding:14px 16px;background:#fff}
    .prod-second{background:#fbfcfd}
    .prod-head{display:flex;justify-content:space-between;align-items:flex-start;gap:12px}
    .prod-name{font-weight:600;font-size:15px}
    .conf{font-size:12px;color:#5f6672;white-space:nowrap;font-variant-numeric:tabular-nums}
    .price{margin:8px 0 0;font-size:15px;font-weight:600}
    .price-none{color:#b91c1c;font-weight:400;font-size:13px}
    .why{margin:10px 0 0;font-size:14px;color:#2c3038}
    .ev{margin-top:10px;border-top:1px dashed #e3e6ea;padding-top:9px;display:flex;
        flex-direction:column;gap:4px;font-size:13px}
    .ev-lbl{display:inline-block;min-width:74px;font-size:10px;letter-spacing:.07em;
            text-transform:uppercase;color:#8b929c}

    .empty{border:1px solid #e3e6ea;border-radius:8px;padding:16px 18px;background:#fbfcfd}
    .empty-abstain{background:#fffbeb;border-color:#fcd9a4}
    .empty-head{margin:0 0 8px;font-size:16px;font-weight:600}
    .asked{margin:6px 0 0;padding-left:18px}
    .repl{list-style:none;padding:0;margin:0;display:flex;flex-direction:column;gap:5px;font-size:13px}
    .repl li{border-left:3px solid #bae0f5;padding-left:9px}
    .repl .overdue{border-left-color:#f59e0b}

    .edge{margin:0 0 14px;padding:10px 12px;border-radius:6px;font-size:13px}
    .edge-on{background:#eef4ff;border:1px solid #c3d6fb;color:#1e3a8a}
    .edge-off{background:#f7f8fa;border:1px solid #e8ebef;color:#3f4652}
    .cov{width:100%;border-collapse:collapse;font-size:13px}
    .cov th{text-align:left;font-size:10px;letter-spacing:.07em;text-transform:uppercase;
            color:#8b929c;border-bottom:1px solid #e3e6ea;padding:0 8px 6px 0;font-weight:600}
    .cov td{border-bottom:1px solid #f0f2f4;padding:8px 8px 8px 0;vertical-align:top}
    .kind{font-size:10px;letter-spacing:.06em;font-weight:700;padding:2px 6px;border-radius:3px}
    .kind-latent{background:#f3e8ff;color:#6b21a8}
    .kind-direct{background:#eef2f6;color:#475569}
    .cov-covered{color:#0f766e;font-weight:600}
    .cov-partial{color:#b45309;font-weight:600}
    .cov-uncovered,.cov-unexplored{color:#9ca3af;font-weight:600}

    .banner{border-radius:6px;padding:11px 13px;font-size:13px;margin:0 0 14px}
    .banner-warn{background:#fffbeb;border:1px solid #f0b429;color:#7c4a12}
    .banner-ok{background:#f0fdfa;border:1px solid #99e6dc;color:#115e56}
    .checks{list-style:none;padding:0;margin:0;display:flex;flex-direction:column;gap:8px}
    .chk{border:1px solid #e3e6ea;border-left-width:4px;border-radius:6px;padding:9px 11px;background:#fff}
    .chk-head{display:flex;justify-content:space-between;align-items:baseline;gap:8px}
    .chk-name{font-weight:600;font-size:13px}
    .chk-verdict{font-size:10px;letter-spacing:.07em;font-weight:700;white-space:nowrap}
    .chk-what{font-size:12px;color:#6b7280;margin-top:3px}
    .chk-entry{margin-top:7px;padding-top:7px;border-top:1px dotted #e8ebef;font-size:12px}
    .chk-ok{border-left-color:#0f766e}
    .chk-ok .chk-verdict{color:#0f766e}
    .chk-hit{border-left-color:#1d4ed8;background:#f8fbff}
    .chk-hit .chk-verdict{color:#1d4ed8}
    .chk-na{border-left-color:#f0b429;background:#fffbeb}
    .chk-na .chk-verdict{color:#92400e}

    .nums{width:100%;border-collapse:collapse;font-size:13px}
    .nums td{border-bottom:1px solid #f0f2f4;padding:6px 0}
    .nums td.num{padding-left:12px;white-space:normal}
    .nums td.absent{color:#92400e;font-size:12px}
    .cmd{background:#16181d;color:#e6e9ee;border-radius:6px;padding:10px 12px;margin:8px 0 0;
         font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:11.5px;
         overflow-x:auto;white-space:pre}

    .foot{margin:22px 24px 0;padding:16px 22px;border:1px solid #e3e6ea;border-radius:10px;
          background:#fbfcfd;font-size:13px;color:#3f4652}
    .foot p{margin:0 0 8px}
    .foot p:last-child{margin:0}
    """;
}
