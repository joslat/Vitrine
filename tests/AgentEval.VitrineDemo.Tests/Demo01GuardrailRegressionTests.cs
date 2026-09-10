// SPDX-License-Identifier: MIT

using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Signals;
using Galaxus.RecommendationAgent.Tools;
using Microsoft.Extensions.AI;

namespace AgentEval.VitrineDemo.Tests;

/// <summary>
/// Credential-free regression coverage migrated from Demo01's legacy post-run console harness.
/// These twelve tests keep the original C-1..C-12 behavioral boundaries independently named,
/// while the 43-row registered mutation panel remains the canonical evaluation diagnostic surface.
/// </summary>
public sealed class Demo01GuardrailRegressionTests : IDisposable
{
    private readonly IProductRetriever? _boundRetriever = GalaxusTools.Retriever;
    private readonly string _boundMarket = GalaxusTools.Market;

    [Fact]
    [Trait("LegacyControl", "C-1")]
    public async Task GateRunsBeforeTheRetrieverIsBound()
    {
        GalaxusTools.Unbind();

        await QuietAsync(() => Demo01_RecommendationAgent.RunAsync(
            Personas.LucaUserId, personalizationDisabled: false, offline: true));

        Assert.False(
            GalaxusTools.IsBound,
            "The Luca abstention turn bound a retriever, so it crossed the pre-spend gate into retrieval.");
    }

    [Fact]
    [Trait("LegacyControl", "C-2")]
    public void GateDoesNotFireOnPersonasWithSignal()
    {
        string[] eligible = [Personas.NadiaUserId, Personas.MarcoUserId, Personas.SofiaUserId];
        var unexpectedlyAbstaining = eligible
            .Where(id => GuardrailPipeline.ShouldAbstain(ContextFor(id), out _))
            .ToArray();

        Assert.Empty(unexpectedlyAbstaining);
        Assert.True(
            GuardrailPipeline.ShouldAbstain(ContextFor(Personas.LucaUserId), out _),
            "The discrimination twin must also prove that the thin-signal Luca case still abstains.");
    }

    [Fact]
    [Trait("LegacyControl", "C-3")]
    public void ConsoleCannotClaimAPreSpendGateItDidNotRun()
    {
        const string Claim = "ran BEFORE any model spend";
        var set = RecommendationSet.Abstain("not enough signal", ["q1?", "q2?"]);

        var afterSpend = Capture(() => RecommendationPrinter.PrintAbstention(set, gateRanBeforeSpend: false));
        var beforeSpend = Capture(() => RecommendationPrinter.PrintAbstention(set, gateRanBeforeSpend: true));

        Assert.DoesNotContain(Claim, afterSpend, StringComparison.Ordinal);
        Assert.Contains(Claim, beforeSpend, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("LegacyControl", "C-4")]
    public async Task OwnPurchaseCitedForTheWrongInterestIsDropped()
    {
        var context = ContextFor(Personas.NadiaUserId);
        var pair = context.InterestMap.Signals
            .Select(signal => (Signal: signal, WrongId: context.UserPurchaseIds
                .Where(id => !signal.EvidencePurchaseIds.Contains(id, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault()))
            .FirstOrDefault(candidate => candidate.WrongId is not null);

        Assert.NotNull(pair.Signal);
        Assert.NotNull(pair.WrongId);

        var product = PresentableFor(context);
        var rightIds = string.Join(",", pair.Signal.EvidencePurchaseIds);
        var wrong = await ScreenAsync(context, product, $"{pair.Signal.Label} | {pair.WrongId}");
        var right = await ScreenAsync(context, product, $"{pair.Signal.Label} | {rightIds}");

        Assert.True(DroppedFor(wrong, product.Id, GuardrailReasons.PurchaseDoesNotEvidenceSignal));
        Assert.False(DroppedFor(right, product.Id, GuardrailReasons.PurchaseDoesNotEvidenceSignal));
    }

    [Fact]
    [Trait("LegacyControl", "C-5")]
    public async Task CandidateSetIsWidenedToEveryRetrievalRoute()
    {
        var catalogue = Catalogue.Default;
        var browsed = catalogue.All.First(product => product.CategoryPath.Count > 0);
        var detailed = catalogue.All.Last(product =>
            !string.Equals(product.RootCategory, browsed.RootCategory, StringComparison.Ordinal));

        IReadOnlySet<string>? candidates;
        using (GalaxusTools.BeginRunCapture())
        {
            await QuietAsync(async () =>
            {
                await GalaxusTools.BrowseCategory(browsed.RootCategory);
                await GalaxusTools.GetProductDetails(detailed.Id);
            });

            candidates = GalaxusTools.CandidateSetInCurrentRun;
        }

        Assert.NotNull(candidates);
        Assert.Contains(browsed.Id, candidates);
        Assert.Contains(detailed.Id, candidates);
    }

    [Fact]
    [Trait("LegacyControl", "C-6")]
    public async Task RealSkuNeverRetrievedIsDropped()
    {
        var baseline = ContextFor(Personas.NadiaUserId);
        var retrieved = PresentableFor(baseline);
        var neverRetrieved = PresentableFor(baseline, exclude: retrieved.Id);
        var context = baseline with
        {
            CandidateProductIds = new HashSet<string>(StringComparer.Ordinal) { retrieved.Id }
        };

        var outside = await ScreenAsync(context, neverRetrieved, userEvidence: null);
        var inside = await ScreenAsync(context, retrieved, userEvidence: null);

        Assert.True(DroppedFor(outside, neverRetrieved.Id, GuardrailReasons.OutsideCandidateSet));
        Assert.False(DroppedFor(inside, retrieved.Id, GuardrailReasons.OutsideCandidateSet));
    }

    [Fact]
    [Trait("LegacyControl", "C-7")]
    public async Task IncompatiblePortafilterIsDropped()
    {
        const string Incompatible = "GLX-3006";
        const string Compatible = "GLX-3004";
        var catalogue = Catalogue.Default;
        var context = ContextFor(Personas.MarcoUserId) with { SuppressDurableUpgrades = false };

        Assert.True(catalogue.TryGet(Incompatible, out var bad));
        Assert.NotNull(bad);
        Assert.True(catalogue.TryGet(Compatible, out var good));
        Assert.NotNull(good);

        var dropped = await ScreenAsync(context, bad, userEvidence: null);
        var kept = await ScreenAsync(context, good, userEvidence: null);

        Assert.True(DroppedFor(dropped, Incompatible, GuardrailReasons.IncompatibleWithOwned));
        Assert.False(DroppedFor(kept, Compatible, GuardrailReasons.IncompatibleWithOwned));
    }

    [Fact]
    [Trait("LegacyControl", "C-8")]
    public async Task ReplenishmentIsNamedBeforeOwnership()
    {
        var context = ContextFor(Personas.SofiaUserId);
        Assert.NotEmpty(context.ReplenishmentProductIds);

        foreach (var sku in context.ReplenishmentProductIds.Order(StringComparer.Ordinal))
        {
            Assert.True(Catalogue.Default.TryGet(sku, out var product));
            Assert.NotNull(product);

            var outcome = await ScreenAsync(context, product, userEvidence: null);

            Assert.True(DroppedFor(outcome, sku, GuardrailReasons.ReplenishmentNotDiscovery));
            Assert.False(DroppedFor(outcome, sku, GuardrailReasons.AlreadyOwned));
        }
    }

    [Fact]
    [Trait("LegacyControl", "C-9")]
    public async Task ToolWarningsDeriveFromScreen()
    {
        var context = ContextFor(Personas.MarcoUserId);
        var catalogue = Catalogue.Default;
        var owned = context.OwnedProductIds
            .Where(id => !context.ReplenishmentProductIds.Contains(id))
            .Order(StringComparer.Ordinal)
            .First();

        Assert.True(catalogue.TryGet(owned, out var product));
        Assert.NotNull(product);
        var citation = EvidenceRef.AttributePrefix
            + catalogue.AttributesOf(product).Order(StringComparer.Ordinal).First();

        string json = string.Empty;
        using (GalaxusTools.BeginRunCapture(context with { CandidateProductIds = null }))
        {
            await QuietAsync(async () =>
            {
                json = await GalaxusTools.PresentRecommendation(
                    owned, "A perfectly reasonable sentence.", citation, false, null);
            });
        }

        Assert.Contains(GuardrailReasons.AlreadyOwned, json, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("LegacyControl", "C-10")]
    public async Task SemanticSearchCarriesTheBoundMarket()
    {
        const string Market = "DE";
        var recorder = new QueryRecordingRetriever();
        GalaxusTools.Bind(recorder, Market);

        await QuietAsync(() => GalaxusTools.SearchProductsByMeaning("a fully specified need sentence"));

        Assert.NotNull(recorder.LastQuery);
        Assert.Equal(Market, recorder.LastQuery.Market);
    }

    [Fact]
    [Trait("LegacyControl", "C-11")]
    public void LedgerRendersTheThreeCounters()
    {
        var ledger = new GuardrailLedger { GiftExcluded = 2 };
        ledger.RecordPriceStock(requested: 6, verified: 5);

        var panel = string.Join("\n", ledger.ToPanelLines());

        Assert.Contains("gift-excluded 2", panel, StringComparison.Ordinal);
        Assert.Contains("price/stock re-verified 5 of 6", panel, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("LegacyControl", "C-12")]
    public void ToolSchemaExposesTheFifthArgument()
    {
        var tool = RecommendationAgentFactory.BuildReadOnlyTools()
            .OfType<AIFunction>()
            .Single(candidate => string.Equals(
                candidate.Name,
                nameof(GalaxusTools.PresentRecommendation),
                StringComparison.Ordinal));
        var schema = tool.JsonSchema.ToString();

        string[] required =
        [
            PresentRecommendationArguments.Sku,
            PresentRecommendationArguments.Reason,
            PresentRecommendationArguments.Evidence,
            PresentRecommendationArguments.OutOfStock,
            PresentRecommendationArguments.UserEvidence
        ];

        Assert.All(required, name => Assert.Contains($"\"{name}\"", schema, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (_boundRetriever is null)
            GalaxusTools.Unbind();
        else
            GalaxusTools.Bind(_boundRetriever, _boundMarket);
    }

    private static GuardrailContext ContextFor(string userId)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Require(userId);
        var classified = PurchaseIntentClassifier.ClassifyAll(
            profile.Purchases,
            catalogue.BySku,
            Personas.DemoToday);
        var map = InterestMapBuilder.Build(
            profile.User,
            profile.Purchases,
            catalogue.BySku,
            statedNeeds: null,
            asOf: Personas.DemoToday,
            sensitiveCategoryNames: catalogue.SensitiveCategories);

        return GuardrailContext.Create(
            catalogue.BySku,
            profile.User,
            map,
            classified,
            categories: catalogue.Categories,
            customerUtterance: Personas.CanonicalPromptFor(profile.Id),
            asOf: Personas.DemoToday);
    }

    private static async Task<GuardrailOutcome> ScreenAsync(
        GuardrailContext context,
        Product product,
        string? userEvidence)
    {
        var catalogue = Catalogue.Default;
        var citation = EvidenceRef.AttributePrefix
            + catalogue.AttributesOf(product).Order(StringComparer.Ordinal).First();

        IReadOnlyList<PresentedRecommendation> presented;
        IReadOnlyList<string?> evidence;
        using (GalaxusTools.BeginRunCapture())
        {
            await QuietAsync(() => GalaxusTools.PresentRecommendation(
                product.Id,
                "A sentence the price scanner is happy with.",
                citation,
                false,
                userEvidence));

            presented = GalaxusTools.PresentedInCurrentRun;
            evidence = GalaxusTools.UserEvidenceInCurrentRun;
        }

        var (raw, _, _) = await Demo01_RecommendationAgent.AssembleAsync(
            presented,
            evidence,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            context.InterestMap,
            catalogue,
            []);

        return GuardrailPipeline.Apply(raw, context);
    }

    private static Product PresentableFor(GuardrailContext context, string? exclude = null) =>
        Catalogue.Default.All.First(product =>
            product.Id != exclude
            && product.Attributes.Count > 0
            && product.StockUnits > 0
            && product.IsAvailableIn(context.User.Market)
            && !context.OwnedProductIds.Contains(product.Id)
            && !context.ReplenishmentProductIds.Contains(product.Id)
            && !(product.IsConsumable
                && GuardrailContext.ImmediateParentCategoryOf(product) is { } replenishmentParent
                && context.ReplenishmentParentCategories.Contains(replenishmentParent))
            && !context.OwnedDurableLeafCategories.Contains(product.LeafCategory)
            && !(GuardrailContext.ImmediateParentCategoryOf(product) is { } durableParent
                && context.OwnedDurableParentCategories.Contains(durableParent))
            && !product.CategoryPath.Any(category =>
                context.SensitiveCategoryNames.Contains(category)
                || SensitiveInferenceBlocklist.IsBlockedCategoryName(category)));

    private static bool DroppedFor(GuardrailOutcome outcome, string sku, string reason) =>
        outcome.Ledger.Entries.Any(entry =>
            entry.Action == GuardrailAction.Dropped
            && string.Equals(entry.Subject, sku, StringComparison.Ordinal)
            && string.Equals(entry.Reason, reason, StringComparison.Ordinal));

    private static async Task QuietAsync(Func<Task> action)
    {
        var saved = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private static string Capture(Action action)
    {
        var saved = Console.Out;
        using var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(saved);
        }

        return buffer.ToString();
    }

    private sealed class QueryRecordingRetriever : IProductRetriever
    {
        public RetrievalQuery? LastQuery { get; private set; }

        public string Name => "test:query-recorder";

        public int ProductCount => 0;

        public bool DenseAvailable => false;

        public ValueTask<RetrievalResult> SearchAsync(
            RetrievalQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return ValueTask.FromResult(RetrievalResult.Empty(new RetrievalDiagnostics
            {
                Dense = false,
                Lexical = false,
                Degraded = true,
                DegradedReason = "test recorder returns no products"
            }));
        }
    }
}
