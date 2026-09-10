// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class ModelInterestMapperFloorTests
{
    [Fact]
    public void ModelEnvelopeCannotEraseSofiasCodeDerivedGrinderGap()
    {
        var (state, classified) = SofiaState(personalizationConsent: true);
        Assert.Contains(state.Interests, IsGrinderGap);

        var envelope = Envelope(
            ModelInterest("model authored kitchen discovery", 0.91, "PUR-SK-13"));

        ModelInterestMapper.ApplyEnvelope(state, envelope, classified);

        Assert.Contains(state.Interests, IsGrinderGap);
        Assert.Contains(state.Interests, static interest =>
            string.Equals(interest.Label, "model authored kitchen discovery", StringComparison.Ordinal));
        Assert.All(state.Interests.Where(static interest => interest.Kind == InterestKind.Latent),
            static interest => Assert.True(interest.EvidenceSignalIds.Count >= 2));
    }

    [Fact]
    public void BoundedLatentFloorHonoursGlobalCapAndLeavesFourModelSlots()
    {
        var (state, classified) = SofiaState(personalizationConsent: true);
        var modelRows = Enumerable.Range(1, DiscoveryState.MaxInterests)
            .Select(index => ModelInterest($"model authored interest {index}", 0.99 - index / 100.0, "PUR-SK-13"))
            .ToArray();

        ModelInterestMapper.ApplyEnvelope(state, Envelope(modelRows), classified);

        Assert.Equal(DiscoveryState.MaxInterests, state.Interests.Count);
        Assert.Equal(
            DiscoveryState.MaxInterests - DiscoveryState.MaxCodeDerivedLatentFloorInterests,
            state.Interests.Count(static interest =>
                interest.Label.StartsWith("model authored interest ", StringComparison.Ordinal)));
        Assert.Equal(
            DiscoveryState.MaxCodeDerivedLatentFloorInterests,
            state.Interests.Count(static interest => interest.Kind == InterestKind.Latent));
        Assert.Contains(state.Interests, IsGrinderGap);
        Assert.Equal(
            Enumerable.Range(1, DiscoveryState.MaxInterests).Select(static index => $"I-{index}"),
            state.Interests.Select(static interest => interest.Id));
        Assert.Equal(
            state.Interests.OrderByDescending(static interest => interest.Confidence)
                .ThenBy(static interest => interest.Label, StringComparer.Ordinal),
            state.Interests);
    }

    [Fact]
    public void HistoryDerivedFloorDoesNotCrossPersonalizationOptOut()
    {
        var (state, classified) = SofiaState(personalizationConsent: false);

        // Simulate a stale history-backed row arriving on a reused message. ApplyEnvelope must
        // derive floor authority from current consent, not merely from the row's presence.
        state.Interests.Add(new Interest
        {
            Id = "stale-history-row",
            Label = "must not cross consent boundary",
            Kind = InterestKind.Latent,
            Origin = InterestOrigin.Mapper,
            Confidence = 1.0,
            EvidenceSignalIds = ["PUR-SK-07", "PUR-SK-13"],
            Rationale = "history-backed conjunction",
            QueryTerms = ["private history term"]
        });

        ModelInterestMapper.ApplyEnvelope(
            state,
            Envelope(ModelInterest("session only discovery", 0.75)),
            classified);

        var only = Assert.Single(state.Interests);
        Assert.Equal("session only discovery", only.Label);
        Assert.Empty(only.EvidenceSignalIds);
        Assert.DoesNotContain(state.Interests, static interest =>
            string.Equals(interest.Label, "must not cross consent boundary", StringComparison.Ordinal));
    }

    private static (DiscoveryState State, IReadOnlyList<ClassifiedPurchase> Classified) SofiaState(
        bool personalizationConsent)
    {
        var profile = UserProfiles.Require(Personas.SofiaUserId);
        var state = new DiscoveryState
        {
            CustomerId = profile.Id,
            Market = profile.Market,
            Language = profile.Language,
            PersonalizationConsent = personalizationConsent,
            SessionRequest = Personas.CanonicalPromptFor(profile.Id)
        };

        var classified = DiscoveryInterestMapping.PopulateFromCode(state, Catalogue.Default);
        return (state, classified);
    }

    private static InterestMapEnvelope Envelope(params MappedInterest[] interests) =>
        new(interests, [], [], "test envelope");

    private static MappedInterest ModelInterest(
        string label,
        double confidence,
        params string[] evidence) =>
        new(
            label,
            "DIRECT",
            confidence,
            evidence,
            "model-authored rationale",
            [label],
            [],
            null);

    private static bool IsGrinderGap(Interest interest) =>
        interest.Kind == InterestKind.Latent
        && interest.QueryTerms.Contains("grinder", StringComparer.OrdinalIgnoreCase);
}
