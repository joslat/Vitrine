// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Signals;

/// <summary>Projects cadence-classified purchases into the repeat-buy tray shared by both demos.</summary>
internal static class ReplenishmentLaneBuilder
{
    /// <summary>
    /// A consumable enters the tray once this fraction of its typical cadence has elapsed.
    /// </summary>
    internal const double DueFraction = 0.80;

    /// <summary>
    /// Builds the repeat-buy tray from the purchases the classifier routed to replenishment.
    /// The tray remains separate from discovery recommendations.
    /// </summary>
    internal static IReadOnlyList<ReplenishmentDto> Build(
        InterestMap map,
        IReadOnlyList<ClassifiedPurchase> classified,
        Catalogue catalogue)
    {
        if (map.RoutedToReplenishment.Count == 0) return [];

        var routed = new HashSet<string>(map.RoutedToReplenishment, StringComparer.Ordinal);
        var lane = new List<ReplenishmentDto>();

        var byProduct = classified
            .Where(c => routed.Contains(c.PurchaseId))
            .GroupBy(c => c.Product.Id, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var group in byProduct)
        {
            var latest = group.OrderByDescending(c => c.Purchase.PurchasedOn)
                              .ThenBy(c => c.PurchaseId, StringComparer.Ordinal)
                              .First();

            if (!catalogue.TryGet(group.Key, out var product) || product is null) continue;

            var cadence = product.TypicalReplenishDays ?? 0;
            if (cadence <= 0) continue;

            var elapsed = latest.Purchase.DaysSince(Personas.DemoToday);
            if (elapsed < cadence * DueFraction) continue;

            lane.Add(new ReplenishmentDto(product.Id, elapsed, cadence, latest.Because));
        }

        return lane
            .OrderBy(r => r.DaysUntilDue)
            .ThenBy(r => r.ProductId, StringComparer.Ordinal)
            .ToList();
    }
}
