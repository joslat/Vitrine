// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Rendering;

/// <summary>The two catalogue fact forms that may appear in a portable recommendation artifact.</summary>
public enum CatalogueEvidenceKind
{
    Specification,
    Tag,
}

/// <summary>A statement parsed from the exact customer-facing catalogue-evidence grammar.</summary>
public sealed record ParsedCatalogueEvidence(
    string ProductId,
    CatalogueEvidenceKind Kind,
    string? AttributeKey,
    string AttributeValue,
    EvidenceRef Citation);

/// <summary>Validation result that keeps absence or malformed text distinct from a valid fact.</summary>
public sealed record CatalogueEvidenceValidation(
    bool IsValid,
    ParsedCatalogueEvidence? Evidence,
    string Detail);

/// <summary>
/// Parses and validates rendered catalogue facts independently of <see cref="CatalogueFactRenderer"/>.
/// </summary>
/// <remarks>
/// The renderer does not call this validator and the validator does not call the renderer. The
/// former projects a typed catalogue fact; the latter parses emitted text and re-reads the
/// catalogue of record. That makes a stale value, a prefix-only value, or a formatter regression
/// observable instead of comparing a string with the function that produced it.
/// </remarks>
public static class CatalogueEvidenceStatement
{
    public const string Prefix = "Catalogue evidence: ";
    public const string Separator = " · ";
    public const string TagMarker = "carries tag ";

    /// <summary>Parses only the canonical grammar; whitespace repairs are deliberately refused.</summary>
    public static bool TryParseExact(string? text, out ParsedCatalogueEvidence? evidence)
    {
        evidence = null;
        if (string.IsNullOrEmpty(text) ||
            !text.StartsWith(Prefix, StringComparison.Ordinal) ||
            !text.EndsWith(']'))
        {
            return false;
        }

        var citationStart = text.LastIndexOf(" [", StringComparison.Ordinal);
        if (citationStart <= Prefix.Length) return false;

        var body = text[Prefix.Length..citationStart];
        var separator = body.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0 || body.IndexOf(Separator, separator + Separator.Length, StringComparison.Ordinal) >= 0)
            return false;

        var productId = body[..separator];
        var payload = body[(separator + Separator.Length)..];
        var citationText = text[(citationStart + 2)..^1];
        if (productId.Length == 0 || payload.Length == 0 ||
            !EvidenceRef.TryParse(citationText, out var citation) ||
            !string.Equals(citationText, citation.ToString(), StringComparison.Ordinal))
        {
            return false;
        }

        if (payload.StartsWith(TagMarker, StringComparison.Ordinal))
        {
            var tag = payload[TagMarker.Length..];
            if (tag.Length == 0) return false;
            evidence = new(productId, CatalogueEvidenceKind.Tag, null, tag, citation);
            return true;
        }

        var equals = payload.IndexOf('=');
        if (equals <= 0 || equals == payload.Length - 1) return false;
        evidence = new(
            productId,
            CatalogueEvidenceKind.Specification,
            payload[..equals],
            payload[(equals + 1)..],
            citation);
        return true;
    }

    /// <summary>
    /// Parses a statement and verifies its exact SKU, tag/spec key, value and related citation
    /// against an independently supplied catalogue.
    /// </summary>
    public static CatalogueEvidenceValidation ValidateExact(string? text, Catalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        if (!TryParseExact(text, out var evidence) || evidence is null)
            return new(false, null, "The text does not match the exact catalogue-evidence grammar.");

        if (!catalogue.TryGet(evidence.ProductId, out var product) || product is null ||
            !string.Equals(product.Id, evidence.ProductId, StringComparison.Ordinal))
        {
            return new(false, evidence, $"SKU '{evidence.ProductId}' is not an exact catalogue id.");
        }

        bool factExists = evidence.Kind switch
        {
            CatalogueEvidenceKind.Tag => product.Tags.Contains(evidence.AttributeValue, StringComparer.Ordinal),
            CatalogueEvidenceKind.Specification =>
                evidence.AttributeKey is { } key &&
                product.Specs.TryGetValue(key, out var value) &&
                string.Equals(value, evidence.AttributeValue, StringComparison.Ordinal),
            _ => false,
        };
        if (!factExists)
            return new(false, evidence, "The exact tag or specification value is absent from the catalogue record.");

        if (!evidence.Citation.Resolves(product) || !CitationNamesFact(evidence, product))
            return new(false, evidence, "The citation does not resolve to the emitted catalogue fact.");

        return new(true, evidence, "The exact catalogue fact and its citation resolve independently.");
    }

    private static bool CitationNamesFact(ParsedCatalogueEvidence evidence, Product product)
    {
        if (evidence.Citation.Kind == EvidenceRefKind.Review)
            return product.ReviewIds.Contains(evidence.Citation.Token);

        var token = evidence.Citation.Token;
        if (evidence.Kind == CatalogueEvidenceKind.Tag)
        {
            var tag = evidence.AttributeValue;
            if (string.Equals(token, Product.NormalizeAttributeToken(tag), StringComparison.Ordinal)) return true;
            var colon = tag.IndexOf(':');
            return colon >= 0 && colon < tag.Length - 1 &&
                   string.Equals(token, Product.NormalizeAttributeToken(tag[(colon + 1)..]), StringComparison.Ordinal);
        }

        var key = evidence.AttributeKey ?? string.Empty;
        var value = evidence.AttributeValue;
        return string.Equals(token, Product.NormalizeAttributeToken(key), StringComparison.Ordinal) ||
               string.Equals(token, Product.NormalizeAttributeToken(value), StringComparison.Ordinal) ||
               string.Equals(token, Product.NormalizeAttributeToken($"{key}={value}"), StringComparison.Ordinal);
    }
}
