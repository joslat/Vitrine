// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Signals;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class SofiaWorkflowPolicyTests
{
    [Fact]
    public void DomainProjectionPreservesClassifierOwnedGiftAndReplenishmentRouting()
    {
        var sofia = ProjectRouting(Personas.SofiaUserId);
        Assert.Empty(sofia.ExcludedBecauseGift);
        Assert.Equal(
            ["PUR-SK-02", "PUR-SK-03", "PUR-SK-04", "PUR-SK-05", "PUR-SK-06",
             "PUR-SK-07", "PUR-SK-08", "PUR-SK-09", "PUR-SK-10", "PUR-SK-11", "PUR-SK-12"],
            sofia.RoutedToReplenishment);

        var marco = ProjectRouting(Personas.MarcoUserId);
        Assert.Equal(["PUR-MI-04", "PUR-MI-05"], marco.ExcludedBecauseGift);
        Assert.Empty(marco.RoutedToReplenishment);
    }

    [Fact]
    public async Task OfflineWorkflowShipsSofiasCadenceBackedRepeatBuyTrayInTheCustomerAnswer()
    {
        var run = await GalaxusDiscoveryLoop.RunAsync(
            Personas.SofiaUserId,
            new DiscoveryLoopOptions(Offline: true, MaxRounds: 3));

        Assert.False(run.Failed, string.Join("; ", run.ExecutorFailures));
        var screened = Assert.IsType<ScreenedAnswer>(run.State.Screened);
        Assert.Equal(11, screened.Map.RoutedToReplenishment.Count);
        Assert.Equal(
            ["GLX-5002", "GLX-3008"],
            screened.Outcome.Cleaned.Replenishment.Select(static item => item.ProductId));
        Assert.Contains("Due for a repeat buy — separate from discovery", run.State.FinalAnswer, StringComparison.Ordinal);
        Assert.Contains("Brita Maxtra Pro", run.State.FinalAnswer, StringComparison.Ordinal);
        Assert.Contains("Blasercafe Ethiopia Yirgacheffe", run.State.FinalAnswer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GLX-3009", GuardrailReasons.ReplenishmentNotDiscovery, "Coffee")]
    [InlineData("GLX-5014", GuardrailReasons.ReplenishmentNotDiscovery, "Water filtration")]
    [InlineData("GLX-5005", GuardrailReasons.DurableStillInHorizon, "Blenders")]
    public void SofiaCategoryScopesRejectAlternateConsumablesAndSiblingDurablesWithExactReasons(
        string sku,
        string expectedReason,
        string expectedCategory)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Require(Personas.SofiaUserId);
        var classified = PurchaseIntentClassifier.ClassifyAll(
            profile.Purchases,
            catalogue.BySku,
            Personas.DemoToday);
        var map = InterestMapBuilder.Build(
            profile.User,
            profile.Purchases,
            catalogue.BySku,
            asOf: Personas.DemoToday,
            sensitiveCategoryNames: catalogue.SensitiveCategories);
        var context = GuardrailContext.Create(
            catalogue.BySku,
            profile.User,
            map,
            classified,
            categories: catalogue.Categories,
            customerUtterance: Personas.CanonicalPromptFor(profile.Id),
            asOf: Personas.DemoToday);

        Assert.Contains("Coffee", context.ReplenishmentParentCategories);
        Assert.Contains("Water filtration", context.ReplenishmentParentCategories);
        Assert.Contains("Blenders", context.OwnedDurableParentCategories);

        var raw = RecommendationSet.Empty with
        {
            Recommendations =
            [
                new RecommendationDto(
                    sku,
                    "candidate selected by the model",
                    new EvidenceDto("unused", [], "unused", "unused", null),
                    0.90)
            ]
        };
        var ledger = new GuardrailLedger();
        var cleaned = CatalogueGroundingFilter.Apply(raw, context, ledger);

        Assert.Empty(cleaned.Recommendations);
        var drop = Assert.Single(ledger.Entries);
        Assert.Equal(GuardrailAction.Dropped, drop.Action);
        Assert.Equal(expectedReason, drop.Reason);
        Assert.Equal(sku, drop.Subject);
        Assert.Contains(expectedCategory, drop.Detail, StringComparison.Ordinal);

        var screenContext = context with
        {
            CandidateProductIds = new HashSet<string>([sku], StringComparer.Ordinal)
        };
        var verdict = GuardrailPipeline.Screen(
            new PresentedRecommendation(sku, "candidate selected by the model", "attr:unused"),
            screenContext,
            new GuardrailLedger());

        Assert.Equal(PresentationDecision.Reject, verdict.Decision);
        Assert.Equal(expectedReason, verdict.Reason);
        Assert.Contains(expectedCategory, verdict.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GLX-3009", "I want decaf beans for espresso.", "Whole beans")]
    [InlineData("GLX-5006", "I need a personal blender for smoothies.", "Personal blenders")]
    public void SofiaCategoryScopesPermitAlternativesTheCustomerExplicitlyRequests(
        string sku,
        string customerUtterance,
        string expectedRequestedCategory)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Require(Personas.SofiaUserId);
        var classified = PurchaseIntentClassifier.ClassifyAll(
            profile.Purchases,
            catalogue.BySku,
            Personas.DemoToday);
        var map = InterestMapBuilder.Build(
            profile.User,
            profile.Purchases,
            catalogue.BySku,
            asOf: Personas.DemoToday,
            sensitiveCategoryNames: catalogue.SensitiveCategories);
        var context = GuardrailContext.Create(
            catalogue.BySku,
            profile.User,
            map,
            classified,
            categories: catalogue.Categories,
            customerUtterance: customerUtterance,
            asOf: Personas.DemoToday);
        var product = catalogue.BySku[sku];

        Assert.Contains(expectedRequestedCategory, context.ExplicitlyRequestedCategories);

        var raw = RecommendationSet.Empty with
        {
            Recommendations =
            [
                new RecommendationDto(
                    sku,
                    "the customer explicitly requested this alternative",
                    new EvidenceDto("unused", [], "unused", "unused", null),
                    0.90)
            ]
        };
        var batchLedger = new GuardrailLedger();
        var cleaned = CatalogueGroundingFilter.Apply(raw, context, batchLedger);

        Assert.Equal(sku, Assert.Single(cleaned.Recommendations).ProductId);
        Assert.DoesNotContain(batchLedger.Entries, static entry =>
            entry.Reason is GuardrailReasons.ReplenishmentNotDiscovery or GuardrailReasons.DurableStillInHorizon);

        var screenContext = context with
        {
            CandidateProductIds = new HashSet<string>([sku], StringComparer.Ordinal)
        };
        var evidenceToken = product.Attributes.Order(StringComparer.Ordinal).First();
        var toolLedger = new GuardrailLedger();
        var verdict = GuardrailPipeline.Screen(
            new PresentedRecommendation(
                sku,
                "the customer explicitly requested this alternative",
                EvidenceRef.AttributePrefix + evidenceToken),
            screenContext,
            toolLedger);

        Assert.Equal(PresentationDecision.Accept, verdict.Decision);
        Assert.DoesNotContain(toolLedger.Entries, static entry =>
            entry.Reason is GuardrailReasons.ReplenishmentNotDiscovery or GuardrailReasons.DurableStillInHorizon);
    }

    [Theory]
    [InlineData("GLX-3008", "I need my regular whole beans.", GuardrailReasons.ReplenishmentNotDiscovery)]
    [InlineData("GLX-5001", "I want another Vitamix blender.", GuardrailReasons.AlreadyOwned)]
    public void ExplicitCategoryRequestDoesNotExemptTheExactCadenceOrOwnedSku(
        string sku,
        string customerUtterance,
        string expectedReason)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Require(Personas.SofiaUserId);
        var classified = PurchaseIntentClassifier.ClassifyAll(
            profile.Purchases,
            catalogue.BySku,
            Personas.DemoToday);
        var map = InterestMapBuilder.Build(
            profile.User,
            profile.Purchases,
            catalogue.BySku,
            asOf: Personas.DemoToday,
            sensitiveCategoryNames: catalogue.SensitiveCategories);
        var context = GuardrailContext.Create(
            catalogue.BySku,
            profile.User,
            map,
            classified,
            categories: catalogue.Categories,
            customerUtterance: customerUtterance,
            asOf: Personas.DemoToday);
        var raw = RecommendationSet.Empty with
        {
            Recommendations =
            [
                new RecommendationDto(
                    sku,
                    "the customer explicitly requested this exact item",
                    new EvidenceDto("unused", [], "unused", "unused", null),
                    0.90)
            ]
        };

        var batchLedger = new GuardrailLedger();
        var cleaned = CatalogueGroundingFilter.Apply(raw, context, batchLedger);
        Assert.Empty(cleaned.Recommendations);
        Assert.Equal(expectedReason, Assert.Single(batchLedger.Entries).Reason);

        var screenContext = context with
        {
            CandidateProductIds = new HashSet<string>([sku], StringComparer.Ordinal)
        };
        var verdict = GuardrailPipeline.Screen(
            new PresentedRecommendation(sku, "the customer explicitly requested this exact item", "attr:unused"),
            screenContext,
            new GuardrailLedger());

        Assert.Equal(PresentationDecision.Reject, verdict.Decision);
        Assert.Equal(expectedReason, verdict.Reason);
    }

    private static InterestMap ProjectRouting(string userId)
    {
        var catalogue = Catalogue.Default;
        var profile = UserProfiles.Require(userId);
        var classified = PurchaseIntentClassifier.ClassifyAll(
            profile.Purchases,
            catalogue.BySku,
            Personas.DemoToday);
        var state = new DiscoveryState
        {
            CustomerId = profile.Id,
            Market = profile.Market,
            Language = profile.Language,
            PersonalizationConsent = true
        };

        return DiscoveryProjection.ToDomainInterestMap(state, classified);
    }
}
