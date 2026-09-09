// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Rendering;

/// <summary>A catalogue-owned fact that is safe to emit beside a recommendation.</summary>
public sealed record EmittedCatalogueFact(
    string ProductId,
    CatalogueEvidenceKind Kind,
    string AttributeKey,
    string AttributeValue,
    EvidenceRef Citation);

/// <summary>
/// Resolves and renders customer-facing catalogue evidence from the catalogue of record.
/// </summary>
/// <remarks>
/// The recommendation supplies a citation, not its own truth. Resolution fails closed when a
/// key, value, review, or SKU is stale, and the emitted value always comes from the catalogue.
/// This is the single text seam used by the portable Demo01 artifact and by evaluation probes.
/// </remarks>
public static class CatalogueFactRenderer
{
    public static EmittedCatalogueFact Resolve(RecommendationDto recommendation, Catalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        ArgumentNullException.ThrowIfNull(catalogue);
        if (recommendation.Evidence is null)
            throw new InvalidOperationException($"Recommendation '{recommendation.ProductId}' has no evidence.");
        if (!catalogue.TryGet(recommendation.ProductId, out var product) || product is null)
            throw new InvalidOperationException($"Recommendation SKU '{recommendation.ProductId}' is absent from the catalogue.");

        var evidence = recommendation.Evidence;
        var (kind, canonicalKey, catalogueValue) = ResolveAttribute(product, evidence);

        if (evidence.ReviewId is { Length: > 0 } reviewId && !product.ReviewIds.Contains(reviewId))
            throw new InvalidOperationException($"Catalogue review '{reviewId}' does not belong to '{product.Id}'.");

        var citation = evidence.Citation;
        if (!citation.Resolves(product))
            throw new InvalidOperationException($"Catalogue citation '{citation}' does not resolve for '{product.Id}'.");

        return new(product.Id, kind, canonicalKey, catalogueValue, citation);
    }

    public static string Render(RecommendationDto recommendation, Catalogue catalogue) =>
        Render(Resolve(recommendation, catalogue));

    public static string Render(EmittedCatalogueFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var payload = fact.Kind == CatalogueEvidenceKind.Tag
            ? $"{CatalogueEvidenceStatement.TagMarker}{fact.AttributeValue}"
            : $"{fact.AttributeKey}={fact.AttributeValue}";
        return $"{CatalogueEvidenceStatement.Prefix}{fact.ProductId}{CatalogueEvidenceStatement.Separator}{payload} [{fact.Citation}]";
    }

    private static (CatalogueEvidenceKind Kind, string Key, string Value) ResolveAttribute(
        Product product,
        EvidenceDto evidence)
    {
        var wantedKey = Product.NormalizeAttributeToken(evidence.ProductAttributeKey);
        if (wantedKey.Length == 0)
            throw new InvalidOperationException($"Catalogue evidence key does not resolve for '{product.Id}'.");

        foreach (var (key, value) in product.Specs)
        {
            if (!string.Equals(Product.NormalizeAttributeToken(key), wantedKey, StringComparison.Ordinal)) continue;
            RequireExactValue(product.Id, key, evidence.ProductAttributeValue, value);
            return (CatalogueEvidenceKind.Specification, key, value);
        }

        var keyMatches = new List<(string Tag, string ExpectedValue)>();
        foreach (var tag in product.Tags)
        {
            if (string.Equals(Product.NormalizeAttributeToken(tag), wantedKey, StringComparison.Ordinal))
            {
                keyMatches.Add((tag, tag));
                continue;
            }

            var colon = tag.IndexOf(':');
            if (colon > 0 &&
                string.Equals(Product.NormalizeAttributeToken(tag[..colon]), wantedKey, StringComparison.Ordinal))
            {
                keyMatches.Add((tag, tag[(colon + 1)..]));
            }
        }

        if (keyMatches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Catalogue evidence key '{evidence.ProductAttributeKey}' does not resolve for '{product.Id}'.");
        }

        var valueMatches = keyMatches.Where(candidate => string.Equals(
            Product.NormalizeAttributeToken(candidate.ExpectedValue),
            Product.NormalizeAttributeToken(evidence.ProductAttributeValue),
            StringComparison.Ordinal)).ToArray();
        if (valueMatches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Catalogue evidence value for '{product.Id}'/'{evidence.ProductAttributeKey}' is stale or ambiguous.");
        }

        // The customer-facing fact is the complete catalogue tag. A prefix-keyed input such as
        // `compat`/`EOS R7` must not render as the meaningless `compat=EOS R7`, and a full-tag
        // input must never render as `compat:EOS R7=compat:EOS R7`.
        return (CatalogueEvidenceKind.Tag, valueMatches[0].Tag, valueMatches[0].Tag);
    }

    private static void RequireExactValue(string productId, string key, string observed, string expected)
    {
        if (!string.Equals(
                Product.NormalizeAttributeToken(observed),
                Product.NormalizeAttributeToken(expected),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Catalogue evidence value for '{productId}'/'{key}' is stale.");
        }
    }
}
