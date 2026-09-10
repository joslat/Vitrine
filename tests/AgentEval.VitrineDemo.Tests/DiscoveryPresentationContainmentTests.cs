// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class DiscoveryPresentationContainmentTests
{
    [Fact]
    public async Task SharedPresentationPipelineRejectsARealSkuMissingFromWorkflowCandidates()
    {
        var run = await GalaxusDiscoveryLoop.RunAsync(
            Personas.MarcoUserId,
            new DiscoveryLoopOptions(Offline: true, MaxRounds: 3));

        Assert.False(run.Failed, string.Join("; ", run.ExecutorFailures));

        var selected = Assert.IsType<RankedRecommendation>(run.State.Ranked.FirstOrDefault());
        Assert.Equal(1, run.State.Candidates.RemoveAll(candidate =>
            string.Equals(candidate.ProductId, selected.ProductId, StringComparison.Ordinal)));

        var outcome = DiscoveryPresentation.Render(
            run.State,
            Catalogue.Default,
            NullDiscoveryProgressSink.Instance,
            modelProse: null,
            print: false);

        Assert.DoesNotContain(
            outcome.Cleaned.AllPresented,
            item => string.Equals(item.ProductId, selected.ProductId, StringComparison.Ordinal));

        var drop = Assert.Single(outcome.Ledger.Entries, entry =>
            entry.Stage == GuardrailStage.CandidateContainment &&
            entry.Action == GuardrailAction.Dropped &&
            entry.Reason == GuardrailReasons.OutsideCandidateSet &&
            string.Equals(entry.Subject, selected.ProductId, StringComparison.Ordinal));

        Assert.Contains("model may only select", drop.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(outcome.Ledger.Entries, entry =>
            entry.Stage == GuardrailStage.CandidateContainment &&
            entry.Reason == GuardrailReasons.ArmInapplicable);
    }
}
