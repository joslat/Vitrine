// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using System.Globalization;
using System.Text;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The lexical leg of the hybrid retriever — an in-process, IDF-weighted token
/// overlap scorer with an exact boost for model numbers and GTINs.
/// </summary>
/// <remarks>
/// <para>
/// Dense retrieval is weak on exact identities such as model numbers and GTINs. This leg treats
/// <c>α7 IV</c>, <c>A7IV</c>, and <c>ILCE-7M4</c> as strong evidence rather than semantic noise.
/// </para>
/// <para>
/// It stands in for an existing production search service. Reciprocal-rank fusion combines its
/// ordering with dense retrieval without pretending that cosine and token-overlap scores share a
/// calibrated scale.
/// </para>
/// <para>
/// <b>Indexed fields:</b> <see cref="Product.Name"/>, <see cref="Product.Brand"/>,
/// <see cref="Product.Specs"/> keys and values, and <see cref="Product.Description"/> at
/// <see cref="DescriptionFieldWeight"/>. Use-context tags are intentionally excluded: the
/// cross-category bridge remains evidence supplied by the semantic leg, not leaked into the
/// lexical baseline.
/// </para>
/// <para>
/// Description text keeps lexical-only degraded mode useful for need-shaped queries. It receives
/// the lowest field weight because prose is much longer than names and specs; equal per-token
/// weighting would let volume outrank an exact identity. The weights are chosen, not measured.
/// On the shipped 99-SKU corpus, the index has 1,957 terms and the query
/// <c>"multi-day trips, starts before sunrise, carried"</c> yields eight lexical hits led by the
/// Icebreaker layer, Petzl headlamp, and Osprey pack. <c>"Mirrorless full-frame"</c> keeps the
/// intended Sony body at rank one.
/// </para>
/// <para>
/// <b>A fragment may add score but may not create a hit.</b> <see cref="ExpandToBag"/> exposes
/// parts of hyphenated tokens so <c>16-35</c> can match <c>16</c> and <c>35</c>. Admission still
/// requires a whole-token anchor, a model-number match, or an exact GTIN. This prevents unrelated
/// compounds such as <c>multi-day</c> and <c>multi-tool</c> from becoming candidates solely through
/// a shared fragment while retaining useful fragment evidence for an already anchored product.
/// </para>
/// <para>
/// Known lexical limits remain explicit: the index does no general stemming, and semantic
/// cross-category queries may still return no lexical hit. The dense leg exists for those cases.
/// </para>
/// </remarks>
public sealed class LexicalIndex
{
    /// <summary>Field weight for tokens found in <see cref="Product.Name"/>.</summary>
    public const float NameFieldWeight = 3.0f;

    /// <summary>Field weight for tokens found in <see cref="Product.Brand"/>.</summary>
    public const float BrandFieldWeight = 3.0f;

    /// <summary>Field weight for tokens found in a spec VALUE ("82 mm", "10 stops").</summary>
    public const float SpecValueFieldWeight = 1.5f;

    /// <summary>Field weight for tokens found in a spec KEY ("Filter thread").</summary>
    public const float SpecKeyFieldWeight = 1.0f;

    /// <summary>
    /// Field weight for tokens found in <see cref="Product.Description"/>. The lowest weight in
    /// this class, and deliberately below <see cref="SpecKeyFieldWeight"/>.
    /// </summary>
    /// <remarks>
    /// <b>Chosen, not measured</b> — like every weight here. The ORDERING is the part that is
    /// argued rather than picked: the description is the longest field by an order of magnitude,
    /// so at an equal per-token weight prose volume would out-score an exact name match by count
    /// alone, and a product merely MENTIONED in another product's copy would rank above the
    /// product itself. A description token may make a product findable; it may not make it the
    /// best answer.
    /// </remarks>
    public const float DescriptionFieldWeight = 0.75f;

    /// <summary>Flat bonus when a model-number-shaped query token appears inside the squashed name or specs.</summary>
    public const float ModelNumberBoost = 6.0f;

    /// <summary>Flat bonus when a query token is exactly a product's GTIN. An exact GTIN is an identity, not a hint.</summary>
    public const float GtinExactBoost = 12.0f;

    /// <summary>Shortest squashed model token accepted for the substring boost — below this, noise wins.</summary>
    public const int MinimumModelTokenLength = 3;

    /// <summary>
    /// Closed-class function words that are never indexed. This is a correctness boundary, not a
    /// relevance tuning knob.
    /// </summary>
    /// <remarks>
    /// Smoothed IDF rewards rare terms, including rare function words. A measured counterexample
    /// gave <c>"it"</c> the corpus's strongest single-token score because it appeared in one product
    /// name. Excluding closed-class words at index time prevents grammar from outranking content;
    /// the query side needs no parallel list because unindexed tokens cannot score.
    /// </remarks>
    public static readonly IReadOnlySet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
    {
        // articles, conjunctions, and the copula
        "a", "an", "and", "as", "at", "be", "been", "being", "but", "by", "is", "am", "are",
        "or", "so", "than", "that", "the", "then", "there", "these", "this", "those", "was", "were",
        // prepositions and particles
        "for", "from", "in", "into", "of", "off", "on", "onto", "to", "up", "with", "without",
        // pronouns and determiners
        "he", "her", "hers", "him", "his", "i", "it", "its", "me", "mine", "my", "our", "ours",
        "she", "their", "theirs", "them", "they", "us", "we", "what", "when", "which", "who",
        "whom", "whose", "you", "your", "yours",
        // auxiliaries
        "can", "could", "did", "do", "does", "had", "has", "have", "if", "may", "might", "must",
        "shall", "should", "will", "would",
    };

    private readonly IReadOnlyList<Product> _products;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Dictionary<string, float> _inverseDocumentFrequency;

    private LexicalIndex(
        IReadOnlyList<Product> products,
        Dictionary<string, Entry> entries,
        Dictionary<string, float> inverseDocumentFrequency)
    {
        _products = products;
        _entries = entries;
        _inverseDocumentFrequency = inverseDocumentFrequency;
    }

    /// <summary>How many products are indexed.</summary>
    public int Count => _entries.Count;

    /// <summary>How many distinct tokens the index holds.</summary>
    public int VocabularySize => _inverseDocumentFrequency.Count;

    /// <summary>The products this index was built over, in catalogue order.</summary>
    public IReadOnlyList<Product> Products => _products;

    /// <summary>
    /// Builds the index. Pure and synchronous — no model, no key, no network. This is why the
    /// lexical leg is always available and degraded mode still returns something useful.
    /// </summary>
    /// <param name="products">The catalogue.</param>
    public static LexicalIndex Build(IReadOnlyList<Product> products)
    {
        ArgumentNullException.ThrowIfNull(products);

        var entries         = new Dictionary<string, Entry>(products.Count, StringComparer.Ordinal);
        var documentFreq    = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var product in products)
        {
            if (product is null) continue;

            var weights = new Dictionary<string, float>(StringComparer.Ordinal);
            var whole   = new HashSet<string>(StringComparer.Ordinal);

            AddField(weights, product.Name, NameFieldWeight);
            AddField(weights, product.Brand, BrandFieldWeight);
            AddWhole(whole, product.Name);
            AddWhole(whole, product.Brand);

            // The description is indexed for SCORE and for ANCHORING alike. Anchoring is the
            // half that matters: a token this product carries only in its prose must be able to
            // ADMIT it to the result set, or degraded mode is still lexical-only over four
            // fields that answer no need query. It is deliberately NOT added to the squashed
            // haystack below — that surface backs the flat 6.0 model-number boost, which is an
            // IDENTITY claim, and a model number named in another product's copy is a mention.
            AddField(weights, product.Description, DescriptionFieldWeight);
            AddWhole(whole, product.Description);

            foreach (var (key, value) in product.Specs)
            {
                AddField(weights, key, SpecKeyFieldWeight);
                AddField(weights, value, SpecValueFieldWeight);
                AddWhole(whole, key);
                AddWhole(whole, value);
            }

            var haystack = new StringBuilder();
            haystack.Append(Squash(product.Name));
            haystack.Append('|');
            haystack.Append(Squash(product.Brand));
            foreach (var (key, value) in product.Specs)
            {
                haystack.Append('|');
                haystack.Append(Squash(key));
                haystack.Append('|');
                haystack.Append(Squash(value));
            }

            entries[product.Id] = new Entry(product, weights, whole, haystack.ToString(), Squash(product.Gtin));

            foreach (var token in weights.Keys)
            {
                documentFreq[token] = documentFreq.TryGetValue(token, out var n) ? n + 1 : 1;
            }
        }

        var total = Math.Max(1, entries.Count);
        var idf   = new Dictionary<string, float>(documentFreq.Count, StringComparer.Ordinal);
        foreach (var (token, df) in documentFreq)
        {
            // Smoothed IDF damps common terms and rewards rare ones. StopWords prevents rare
            // function words from receiving the same authority as useful identities.
            idf[token] = (float)Math.Log(1.0 + (double)total / df);
        }

        return new LexicalIndex(products, entries, idf);
    }

    /// <summary>
    /// Scores every product that passes <paramref name="filter"/> and returns the best
    /// <paramref name="topK"/>, best first.
    /// </summary>
    /// <remarks>
    /// The filter is a PRE-filter — applied before the cut, never after — so a constrained query
    /// still gets a full <paramref name="topK"/> of eligible candidates instead of the eligible
    /// remainder of an unconstrained top-k.
    /// </remarks>
    /// <param name="query">Raw query text.</param>
    /// <param name="topK">Maximum results.</param>
    /// <param name="filter">Hard pre-filter over products.</param>
    /// <returns>Product ids with their raw scores. Scores are NOT comparable across queries — RRF only reads the rank.</returns>
    public IReadOnlyList<(string ProductId, float Score)> Search(string query, int topK, Func<Product, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (topK <= 0 || _entries.Count == 0) return [];

        var tokens = Tokenize(query);
        if (tokens.Count == 0) return [];

        var bag         = ExpandToBag(tokens);
        var modelTokens = ModelTokens(tokens);
        var wholeQuery  = new HashSet<string>(tokens, StringComparer.Ordinal);
        var results     = new List<(string ProductId, float Score)>();

        foreach (var product in _products)
        {
            if (product is null) continue;
            if (!_entries.TryGetValue(product.Id, out var entry)) continue;
            if (!filter(product)) continue;

            float score = 0f;

            // A hyphen fragment may ADD score; it may not CREATE a hit. See the ANCHOR remarks
            // on the class: an anchor is a token neither side had to be broken apart to produce.
            bool anchored = entry.WholeTokens.Overlaps(wholeQuery);

            foreach (var token in bag)
            {
                if (!entry.FieldWeightByToken.TryGetValue(token, out var fieldWeight)) continue;
                var weight = _inverseDocumentFrequency.TryGetValue(token, out var idf) ? idf : 1f;
                score += weight * fieldWeight;
            }

            foreach (var modelToken in modelTokens)
            {
                if (modelToken.Length >= MinimumModelTokenLength &&
                    entry.SquashedHaystack.Contains(modelToken, StringComparison.Ordinal))
                {
                    score += ModelNumberBoost;
                    anchored = true;
                }
            }

            foreach (var token in bag)
            {
                if (LooksLikeGtin(token) &&
                    entry.SquashedGtin.Length > 0 &&
                    string.Equals(Squash(token), entry.SquashedGtin, StringComparison.Ordinal))
                {
                    score += GtinExactBoost;
                    anchored = true;
                }
            }

            if (score > 0f && anchored) results.Add((product.Id, score));
        }

        // Deterministic order: score descending, then product id ascending. Without the tie-break
        // two runs of the same query can return different lists, and a demo that is not
        // reproducible cannot be evaluated.
        results.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.ProductId, right.ProductId);
        });

        if (results.Count > topK) results.RemoveRange(topK, results.Count - topK);
        return results;
    }

    /// <summary>
    /// The query tokens that actually matched a product — for explaining a hit in the console
    /// rather than asserting one.
    /// </summary>
    /// <param name="productId">Product to explain.</param>
    /// <param name="query">The query that retrieved it.</param>
    public IReadOnlyList<string> MatchedTokens(string productId, string query)
    {
        if (string.IsNullOrWhiteSpace(productId) || !_entries.TryGetValue(productId, out var entry)) return [];

        var matched = new List<string>();
        foreach (var token in ExpandToBag(Tokenize(query)))
        {
            if (entry.FieldWeightByToken.ContainsKey(token)) matched.Add(token);
        }

        matched.Sort(StringComparer.Ordinal);
        return matched;
    }

    // ── Text processing. Shared with ConceptEmbeddingSource so both legs agree on what a token is. ──

    /// <summary>
    /// THE tokeniser for this project. Folds the text (see <see cref="Fold"/>), then emits
    /// positional tokens of <c>[a-z0-9]</c> plus the inner characters <c>: - . +</c>.
    /// </summary>
    /// <remarks>
    /// Inner <c>-</c> and <c>.</c> are kept so <c>16-35</c>, <c>10-stop</c>, <c>wh-1000xm5</c> and
    /// <c>2.5</c> survive as single tokens — splitting them is precisely how a lexical index loses
    /// the model numbers it exists to catch. Inner <c>:</c> is kept so <c>trip:multi-day</c> stays
    /// one token; <see cref="ExpandToBag"/> adds the suffix separately.
    /// </remarks>
    /// <param name="text">Any text.</param>
    /// <returns>Tokens in order of appearance. Never null.</returns>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var folded = Fold(text);
        var tokens = new List<string>();
        var buffer = new StringBuilder(16);

        void Flush()
        {
            while (buffer.Length > 0 && IsInnerOnly(buffer[^1])) buffer.Length--;
            if (buffer.Length > 0) tokens.Add(buffer.ToString());
            buffer.Clear();
        }

        foreach (var ch in folded)
        {
            if (char.IsAsciiLetterOrDigit(ch)) { buffer.Append(ch); continue; }

            if (IsInnerOnly(ch))
            {
                if (buffer.Length > 0) buffer.Append(ch);
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;
    }

    /// <summary>
    /// Expands a positional token stream into the SET used for overlap scoring: every token, plus
    /// the part after a <c>:</c>, plus the parts of a hyphenated token. Set semantics on purpose —
    /// a query that repeats a word must not out-score one that does not.
    /// </summary>
    /// <param name="tokens">Positional tokens from <see cref="Tokenize"/>.</param>
    public static IReadOnlySet<string> ExpandToBag(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var bag = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            if (token.Length == 0) continue;
            bag.Add(token);

            var colon = token.IndexOf(':');
            if (colon >= 0 && colon < token.Length - 1) bag.Add(token[(colon + 1)..]);

            if (token.Contains('-', StringComparison.Ordinal))
            {
                foreach (var part in token.Split('-', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.Length > 1) bag.Add(part);
                }
            }
        }

        return bag;
    }

    /// <summary>
    /// The model-number-shaped tokens of a query, squashed for substring matching — including
    /// concatenations of adjacent short tokens, so <c>"α7 IV"</c> also produces <c>"a7iv"</c>
    /// and matches a catalogue name written the other way round.
    /// </summary>
    /// <param name="tokens">Positional tokens from <see cref="Tokenize"/>.</param>
    public static IReadOnlyList<string> ModelTokens(IReadOnlyList<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        var found = new List<string>();
        var seen  = new HashSet<string>(StringComparer.Ordinal);

        void Consider(string candidate)
        {
            var squashed = Squash(candidate);
            if (squashed.Length >= MinimumModelTokenLength && seen.Add(squashed)) found.Add(squashed);
        }

        for (int i = 0; i < tokens.Count; i++)
        {
            if (LooksLikeModelNumber(tokens[i])) Consider(tokens[i]);

            if (i + 1 < tokens.Count &&
                tokens[i].Length <= 4 && tokens[i + 1].Length <= 4 &&
                (LooksLikeModelNumber(tokens[i]) || LooksLikeModelNumber(tokens[i + 1])))
            {
                Consider(tokens[i] + tokens[i + 1]);
            }
        }

        return found;
    }

    /// <summary>
    /// True when a token looks like a model number: it mixes letters and digits, or it is a
    /// hyphenated run containing a digit. <c>"a7iv"</c>, <c>"wh-1000xm5"</c>, <c>"16-35"</c>,
    /// <c>"ilce-7m4"</c> qualify; <c>"tripod"</c> and <c>"2025"</c> do not.
    /// </summary>
    /// <param name="token">A folded token.</param>
    public static bool LooksLikeModelNumber(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length < 2) return false;

        bool hasLetter = false, hasDigit = false, hasHyphen = false;
        foreach (var ch in token)
        {
            if (char.IsAsciiDigit(ch)) hasDigit = true;
            else if (char.IsAsciiLetter(ch)) hasLetter = true;
            else if (ch == '-') hasHyphen = true;
        }

        if (!hasDigit) return false;
        return hasLetter || hasHyphen;
    }

    /// <summary>True when a token is an 8-, 12-, 13- or 14-digit all-numeric code — a GTIN shape.</summary>
    /// <param name="token">A folded token.</param>
    public static bool LooksLikeGtin(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token.Length is not (8 or 12 or 13 or 14)) return false;

        foreach (var ch in token)
        {
            if (!char.IsAsciiDigit(ch)) return false;
        }
        return true;
    }

    /// <summary>
    /// Lower-cases, decomposes accents away, and maps the handful of non-ASCII characters that
    /// actually appear in this catalogue's brand and model names: <c>α</c> → <c>a</c> (Sony α7),
    /// <c>ø</c> → <c>o</c>, <c>ß</c> → <c>ss</c>, <c>×</c> → <c>x</c>, <c>—</c>/<c>–</c> → space.
    /// </summary>
    /// <param name="text">Any text.</param>
    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length + 4);

        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;

            var ch = char.ToLowerInvariant(raw);
            switch (ch)
            {
                case 'α': builder.Append('a'); break;
                case 'β': builder.Append('b'); break;
                case 'ø': builder.Append('o'); break;
                case 'ß': builder.Append("ss"); break;
                case '×': builder.Append('x'); break;
                case '—':
                case '–':
                case '‑': builder.Append(' '); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Folds then removes every non-alphanumeric character, so <c>"Sony α7 IV"</c> and
    /// <c>"sony-a7iv"</c> both become <c>"sonya7iv"</c>. This is the form the model-number
    /// substring boost matches against, and the reason three surface forms of one product agree.
    /// </summary>
    /// <param name="text">Any text.</param>
    public static string Squash(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var folded  = Fold(text);
        var builder = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (char.IsAsciiLetterOrDigit(ch)) builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool IsInnerOnly(char ch) => ch is '-' or '.' or ':' or '+';

    private static void AddField(Dictionary<string, float> weights, string? text, float fieldWeight)
    {
        foreach (var token in ExpandToBag(Tokenize(text)))
        {
            // See StopWords: an unindexed function word cannot be scored, whatever its df.
            if (StopWords.Contains(token)) continue;
            weights[token] = weights.TryGetValue(token, out var existing) ? existing + fieldWeight : fieldWeight;
        }
    }

    private static void AddWhole(HashSet<string> whole, string? text)
    {
        foreach (var token in Tokenize(text))
        {
            if (StopWords.Contains(token)) continue;
            whole.Add(token);
        }
    }

    private sealed record Entry(
        Product Product,
        Dictionary<string, float> FieldWeightByToken,
        HashSet<string> WholeTokens,
        string SquashedHaystack,
        string SquashedGtin);
}
