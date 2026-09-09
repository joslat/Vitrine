// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Guardrails;

namespace Galaxus.RecommendationAgent.Rendering;

/// <summary>
/// Composes the portable customer-facing Demo01 artifact exclusively from mechanically screened
/// recommendations. UI, export, and judged evaluation call this same function.
/// </summary>
public static class RecommendationArtifactComposer
{
    /// <summary>Composes, screens, and returns the only text permitted across the customer boundary.</summary>
    public static string Compose(RecommendationRunResult run, int? maximumItems = null) =>
        ComposeScreened(run, maximumItems).RequireDeliverableText();

    /// <summary>
    /// Returns the typed final-text screening result so reports and evaluations can distinguish an
    /// absent answer from a screened clean answer and can observe a planted answer-only leak.
    /// </summary>
    public static CustomerAnswerScreenResult ComposeScreened(
        RecommendationRunResult run,
        int? maximumItems = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (maximumItems is < 1) throw new ArgumentOutOfRangeException(nameof(maximumItems));
        if (run.Outcome is null) return CustomerAnswerScreen.Screen(null, run.Prompt);

        return CustomerAnswerScreen.Screen(ComposeUnchecked(run, maximumItems), run.Prompt);
    }

    private static string ComposeUnchecked(RecommendationRunResult run, int? maximumItems)
    {
        var outcome = run.Outcome!;

        var cleaned = outcome.Cleaned;
        if (cleaned.Abstained)
        {
            var abstention = new StringBuilder(cleaned.AbstainReason ?? "I need more information before recommending a product.");
            foreach (var question in cleaned.ClarifyingQuestions) abstention.AppendLine().Append("  · ").Append(question);
            return abstention.ToString();
        }

        var items = cleaned.AllPresented.Take(maximumItems ?? int.MaxValue).ToArray();
        if (items.Length == 0) return string.Empty;

        var interest = run.InterestMap?.Signals
            .OrderByDescending(static signal => signal.Strength)
            .Select(static signal => signal.Label)
            .FirstOrDefault() ?? "your stated need";
        var builder = new StringBuilder($"Because of your {interest} interest:{Environment.NewLine}");
        foreach (var item in items)
        {
            var name = Catalogue.Default.TryGet(item.ProductId, out var product) && product is not null
                ? product.Name
                : item.ProductId;
            builder.Append("  · ").Append(name).Append(" (").Append(item.ProductId).Append(") — ")
                .AppendLine(item.WhyThis)
                .Append("    ").AppendLine(CatalogueFactRenderer.Render(item, Catalogue.Default));
        }

        builder.Append("The choice remains yours.");
        return builder.ToString();
    }
}
