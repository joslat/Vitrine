// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// Which embedding SPACE a run retrieves in. Requested on the command line, resolved once by
/// <see cref="EmbeddingSpace"/>, and printed in every banner that shows a retrieved number.
/// </summary>
public enum EmbeddingSpaceChoice
{
    /// <summary>Let <see cref="EmbeddingSpace"/> choose. See <see cref="EmbeddingSpace.AutoPrefers"/>.</summary>
    Auto = 0,

    /// <summary>
    /// Force the real <c>text-embedding-3-small</c> space: the committed PRODUCT vectors
    /// (<see cref="PrecomputedEmbeddingSource"/>) searched with queries embedded LIVE at search
    /// time. Needs credentials; without them <see cref="EmbeddingSpace.Resolve"/> falls back to
    /// the concept space and says so.
    /// </summary>
    RealVectors = 1,

    /// <summary>Force the authored 24-dimension concept space (<see cref="ConceptEmbeddingSource"/>).</summary>
    ConceptVectors = 2,
}

/// <summary>
/// The ONE place that decides which <see cref="IEmbeddingSource"/> a run retrieves with.
/// </summary>
/// <remarks>
/// <para>
/// Demo 01 retrieval, confidence and attribution, <c>DiscoveryWorkflow</c>, and the evaluation
/// composition root all resolve through this selector. One run therefore cannot compute related
/// values in different embedding spaces.
/// </para>
/// <para>
/// <b>Embedding spaces never mix.</b> The concept space has 24 authored dimensions and the
/// committed <c>text-embedding-3-small</c> index has 1536. A cosine across spaces is a category
/// error, not a weak signal. Resolution is process-wide and memoised, and <see cref="Requested"/>
/// becomes immutable after resolution.
/// </para>
/// <para>
/// <b>The real-vector path is a precomputed product index with live queries.</b> Product documents
/// are committed once; open-ended query text is embedded at search time through
/// <see cref="AzureEmbeddingSource"/> and memoised per exact text for the run. This path needs
/// credentials and incurs provider usage.
/// </para>
/// <para>
/// <b>The asset stamp selects the query deployment.</b> Model name matters even when dimensions
/// agree: <c>text-embedding-ada-002</c> and <c>text-embedding-3-small</c> are both 1536-dimensional
/// but are different spaces. A configuration mismatch is reported, and the committed index's
/// model stamp remains authoritative.
/// </para>
/// <para>
/// <b>The live and committed spaces are verified.</b> <see cref="Resolve"/> re-embeds one product's
/// exact document and compares it with the committed vector for that text. The expected cosine is
/// 1.0; <see cref="SpaceIdentityProbeFloor"/> allows float32 round-trip and provider variation.
/// The measured cosine is reported. The probe costs one additional embedding call.
/// </para>
/// <para>
/// <b>Fallback is explicit.</b> Missing assets, invalid stamps, absent credentials, or a failed
/// identity probe select the deterministic concept source with a printed reason. Every reported
/// number is therefore attributable to the named space; a real-vector request never degrades
/// silently or pretends to remain offline.
/// </para>
/// </remarks>
public static class EmbeddingSpace
{
    /// <summary>
    /// What <see cref="EmbeddingSpaceChoice.Auto"/> resolves to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Auto selects the concept space for reproducibility.</b> It is deterministic, key-free,
    /// and cost-free, so the same scored suite does not change spaces merely because one machine
    /// has credentials in its environment.
    /// </para>
    /// <para>
    /// Retrieval fidelity is a separate choice. On the current 50-phrase diagnostic, the live
    /// real-vector path has <b>0 of 50</b> unanswerable phrases and the concept path has
    /// <b>6 of 50</b>. Callers that accept credentials, network access, and spend can request
    /// <c>--real-vectors</c> explicitly.
    /// </para>
    /// </remarks>
    public const EmbeddingSpaceChoice AutoPrefers = EmbeddingSpaceChoice.ConceptVectors;

    /// <summary>
    /// Minimum cosine the space-identity probe must reach before the real-vector path is accepted.
    /// </summary>
    /// <remarks>
    /// <b>Not a tuned threshold.</b> The probe embeds one product's exact
    /// <see cref="EmbeddingDocument.ForProduct"/> text through the live source and compares it with
    /// the committed vector for that same text, so the expected value in the right space is 1.0 by
    /// construction. This number is the tolerance for a float32 round-trip through base64 and for
    /// provider nondeterminism — nothing else. In a WRONG space (an <c>ada-002</c> deployment
    /// against a <c>3-small</c> index, which no dimension check can catch because both are 1536)
    /// the cosine is near zero, so the test separates the two cases by roughly the whole range.
    /// The measured value is carried on <see cref="EmbeddingSourceResolution.SpaceIdentityCosine"/>
    /// and printed, so it is never taken on trust.
    /// </remarks>
    public const float SpaceIdentityProbeFloor = 0.98f;

    private static readonly Lock Gate = new();
    private static EmbeddingSpaceChoice _requested = EmbeddingSpaceChoice.Auto;
    private static EmbeddingSourceResolution? _resolution;
    private static IReadOnlyList<Product>? _resolvedFor;

    /// <summary>Latch for <see cref="PrintLiveSpend"/>: the figure is printed at most once per process.</summary>
    private static bool _liveSpendPrinted;

    /// <summary>
    /// The space this process was ASKED for. Set once from the command line, before anything
    /// retrieves.
    /// </summary>
    /// <remarks>
    /// Setting it after <see cref="Resolve"/> has run THROWS. A process that changed space
    /// half-way would produce one report whose numbers came from two incomparable spaces, and
    /// nothing downstream could tell which line came from which.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Something has already resolved a source.</exception>
    public static EmbeddingSpaceChoice Requested
    {
        get { lock (Gate) return _requested; }
        set
        {
            lock (Gate)
            {
                if (_requested == value) return;

                if (_resolution is not null)
                {
                    throw new InvalidOperationException(
                        $"The embedding space is already resolved to '{_resolution.Chosen}'. Changing it now would " +
                        "produce one run whose numbers came from two incomparable vector spaces. Set " +
                        $"{nameof(EmbeddingSpace)}.{nameof(Requested)} from the argument parser, before any retriever is built.");
                }

                _requested = value;
            }
        }
    }

    /// <summary>The resolution in force, or null when nothing has resolved yet.</summary>
    public static EmbeddingSourceResolution? Current
    {
        get { lock (Gate) return _resolution; }
    }

    /// <summary>
    /// Resolves the embedding source for this process, loading and validating the committed assets
    /// when the real-vector path is in play. Memoised: the assets are parsed at most once.
    /// </summary>
    /// <param name="products">
    /// The catalogue. Needed because the catalogue asset is keyed by product id and each document
    /// is re-rendered at load, which is what makes a template change a cache MISS rather than a
    /// wrong vector.
    /// </param>
    /// <remarks>
    /// On the real-vector path this issues ONE live embedding call — the space-identity probe. It
    /// happens under the resolution lock, once per process, before anything retrieves; a startup
    /// probe that proves the index and the query embedder are the same space is worth strictly more
    /// than a mid-run crash or, worse, a run of confident nonsense.
    /// </remarks>
    public static EmbeddingSourceResolution Resolve(IReadOnlyList<Product> products)
    {
        ArgumentNullException.ThrowIfNull(products);

        lock (Gate)
        {
            if (_resolution is not null)
            {
                if (ReferenceEquals(_resolvedFor, products)) return _resolution;

                // Re-resolving over a different catalogue could issue another live probe and swap
                // the process-wide source after earlier retrievals. One report must use one space;
                // reaching this guard is a wiring error.
                throw new InvalidOperationException(
                    $"The embedding space is already resolved to '{_resolution.Chosen}' over a catalogue of "
                  + $"{_resolvedFor?.Count ?? 0} products, and a different catalogue of {products.Count} was passed. "
                  + "Resolution is per PROCESS: re-resolving would put one run's numbers in two vector spaces.");
            }

            var resolution = ResolveCore(products, _requested);

            _resolution  = resolution;
            _resolvedFor = products;
            return resolution;
        }
    }

    /// <summary>
    /// Embeds text in the resolved space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Async because the resolved real-vector space embeds queries live. Retrieval, confidence,
    /// and attribution all use this same path.
    /// </para>
    /// <para>
    /// Returns the UNAVAILABLE sentinel (an empty memory) when the resolved source cannot answer;
    /// callers that turn that into a cosine get 0 from
    /// <see cref="EmbeddingVectors.DotOfUnitVectors"/>, which is the correct reading — no evidence,
    /// not evidence of nothing.
    /// </para>
    /// </remarks>
    /// <param name="products">The catalogue, for <see cref="Resolve"/>.</param>
    /// <param name="text">Text to embed.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static ValueTask<ReadOnlyMemory<float>> EmbedAsync(
        IReadOnlyList<Product> products,
        string text,
        CancellationToken cancellationToken = default)
        => Resolve(products).Source.EmbedAsync(text, cancellationToken);

    /// <summary>
    /// Prints what the live query path actually cost this run, and prints NOTHING on the concept
    /// path.
    /// </summary>
    /// <remarks>
    /// The banner warns before the run that <c>--real-vectors</c> spends; this closes the loop with
    /// the count afterwards. Distinct texts embedded, requests the memo absorbed, and prompt tokens
    /// read from the responses' own usage blocks — never estimated, because an estimate presented
    /// as a cost is a fabricated measurement.
    /// </remarks>
    /// <param name="indent">Leading spaces, so it lines up with the caller's own panel.</param>
    /// <remarks>
    /// ⚠ <b>PRINT-ONCE per process.</b> Both entry points call it in a <c>finally</c> so that no
    /// command can declare a cost and report none, and Demo 01 calls it inside its own panel where
    /// the figure reads best. Without this latch that demo would print the line twice, and a reader
    /// who added the two totals would double the bill. The second call is a no-op, not a second
    /// measurement.
    /// </remarks>
    public static void PrintLiveSpend(string indent = "  ")
    {
        if (Current is not { Source: PrecomputedEmbeddingSource index } || !index.HasLiveFallback) return;
        if (_liveSpendPrinted) return;
        _liveSpendPrinted = true;

        var azure = index.LiveSource as AzureEmbeddingSource;

        // azure.CallCount is every call the deployment was billed for and index.FallbackCalls is
        // the QUERY half of it; the difference is the startup space-identity probe, which is spend
        // too and is therefore named rather than folded into the query number.
        var probeCalls = azure is null ? 0 : Math.Max(0, azure.CallCount - index.FallbackCalls);

        var tokens = azure is not null
            ? $"{azure.PromptTokens} prompt token(s)"
              + (azure.CallsWithoutUsage == 0 ? string.Empty : $" — LOWER BOUND: {azure.CallsWithoutUsage} response(s) carried no usage block")
            : "token usage not reported by this source, which is NOT the same as free";

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            $"{indent}💸 Live embedding: {index.FallbackCalls} query call(s) for {index.FallbackCalls} distinct text(s)"
          + (probeCalls > 0 ? $" + {probeCalls} space-identity probe" : string.Empty)
          + $" · {index.LiveMemoHits} request(s) served from the per-run memo and {index.CacheHits} from the "
          + $"committed index, at no cost · {tokens} in total.");
        Console.ResetColor();
    }

    private static EmbeddingSourceResolution ResolveCore(IReadOnlyList<Product> products, EmbeddingSpaceChoice requested)
    {
        var effective = requested == EmbeddingSpaceChoice.Auto ? AutoPrefers : requested;

        if (effective == EmbeddingSpaceChoice.ConceptVectors)
        {
            var reason = requested == EmbeddingSpaceChoice.Auto
                ? "default: deterministic, no key, identical on every machine — so two runs of the same suite "
                + "cannot silently score in two spaces. Pass --real-vectors for the committed "
                + "text-embedding-3-small index with LIVE query embedding (needs credentials)."
                : "requested on the command line (--concept-vectors).";

            return new EmbeddingSourceResolution(
                ConceptEmbeddingSource.Instance,
                requested,
                EmbeddingSpaceChoice.ConceptVectors,
                reason,
                FellBack: false,
                Warnings: [],
                CachedVectorCount: 0);
        }

        // ── Step 1: read the committed index, and its STAMP. TryLoad, never Load: an absent or
        //    stale asset is a condition to REPORT and degrade from, not a crash. No live source
        //    yet — the stamp is what decides which live source is even admissible.
        PrecomputedEmbeddingSource index;
        try
        {
            index = PrecomputedEmbeddingSource.TryLoad(products, liveFallback: null);
        }
        catch (Exception ex)
        {
            return FallBack(requested, $"the committed index could not be read at all ({ex.GetType().Name}; message withheld)", []);
        }

        if (index.IsEmpty)
        {
            return FallBack(requested, "the committed index loaded NO vectors", index.LoadWarnings);
        }

        // ── Step 2: credentials. A real embedding space needs a real embedder; there is no offline
        //    way to embed a query into text-embedding-3-small. Absent credentials are an ORDINARY
        //    state on this demo, so this degrades with a printed reason rather than failing.
        if (!Config.IsConfigured)
        {
            return FallBack(
                requested,
                $"the {index.CachedVectorCount} committed '{index.ModelId}' product vectors validated, but a QUERY "
              + "must be embedded at search time and no inference provider is locally ready. An "
              + "index with no way to embed a query returns nothing at all, which is worse than a different space",
                index.LoadWarnings);
        }

        // ── Step 3: the deployment NAME comes from the asset's stamp. See the class remarks: the
        //    configured deployment is 1536 dims too, so nothing downstream could catch the swap.
        var configured = Config.EmbeddingDeployment;
        var overridden = !string.Equals(configured, index.ModelId, StringComparison.Ordinal);

        if (!AzureEmbeddingSource.TryCreate(out var live, out var createReason, deployment: index.ModelId))
        {
            return FallBack(
                requested,
                $"the committed index needs a live '{index.ModelId}' query embedder and one could not be created "
              + $"({createReason})",
                index.LoadWarnings);
        }

        // ── Step 4: rebuild WITH the live source, so the loader's own model-mismatch guard runs
        //    against it rather than being trusted to be redundant.
        PrecomputedEmbeddingSource searchable;
        try
        {
            searchable = PrecomputedEmbeddingSource.TryLoad(products, liveFallback: live);
        }
        catch (Exception ex)
        {
            live!.Dispose();
            return FallBack(requested, $"the committed index could not be re-read with the live query embedder attached ({ex.GetType().Name}; message withheld)", []);
        }

        if (searchable.IsEmpty)
        {
            live!.Dispose();
            return FallBack(
                requested,
                "the committed index was REFUSED once the live query embedder was attached",
                searchable.LoadWarnings);
        }

        // ── Step 5: prove the space. See SpaceIdentityProbeFloor — the expected value is 1.0.
        var (probed, cosine, probeNote) = ProbeSpaceIdentity(products, searchable);
        if (!probed)
        {
            live!.Dispose();
            return FallBack(requested, probeNote, searchable.LoadWarnings);
        }

        var reasonText =
            (requested == EmbeddingSpaceChoice.Auto ? "default" : "requested on the command line (--real-vectors)")
          + $": {searchable.CachedVectorCount} committed '{searchable.ModelId}' product vectors validated, and "
          + $"QUERIES are embedded LIVE against model '{live!.ModelId}' at search time. "
          + $"Space identity probe: cosine {cosine:F4} against the committed vector for the same text "
          + $"(expected 1.0000, floor {SpaceIdentityProbeFloor:F2}). {probeNote}"
          + (overridden
                ? $" ⚠️  The configured embedding model resolves to '{configured}', which was NOT used: the "
                + "committed index names the only embedder that can answer questions about it, and two "
                + "embedding models are two spaces. Rebuild the index if you want a different one."
                : string.Empty);

        return new EmbeddingSourceResolution(
            searchable,
            requested,
            EmbeddingSpaceChoice.RealVectors,
            reasonText,
            FellBack: false,
            searchable.LoadWarnings,
            searchable.CachedVectorCount)
        {
            SpaceIdentityCosine = cosine,
            LiveQueryDeployment = live.ModelId,
        };
    }

    /// <summary>
    /// Embeds one product's exact embedding document through the live path and compares it with the
    /// committed vector for that same text. In the right space this is 1.0 by construction.
    /// </summary>
    /// <remarks>
    /// This is the check a dimension assertion cannot make. <c>text-embedding-ada-002</c> and
    /// <c>text-embedding-3-small</c> both return 1536 floats, so a deployment pointed at the wrong
    /// one produces vectors of exactly the right SHAPE in exactly the wrong SPACE — and every
    /// cosine downstream is then noise wearing a plausible number. Cost: one call.
    /// </remarks>
    private static (bool Passed, double Cosine, string Note) ProbeSpaceIdentity(
        IReadOnlyList<Product> products,
        PrecomputedEmbeddingSource searchable)
    {
        var probeProduct = products.FirstOrDefault(p => p is not null);
        if (probeProduct is null) return (false, 0.0, "the catalogue is empty, so the space could not be probed");

        var document = EmbeddingDocument.ForProduct(probeProduct);

        if (!searchable.TryGetCommitted(document, out var committed))
        {
            return (false, 0.0,
                $"the committed index holds no vector for '{probeProduct.Id}', so the space could not be probed. "
              + "That means the asset and this build's document template disagree in a way the stamp did not catch");
        }

        ReadOnlyMemory<float> fresh;
        try
        {
            // Straight at the live source, never through `searchable` — that would hit the
            // committed vector and the probe would compare the asset with itself, which is the
            // artifact-supplies-its-own-input failure this project keeps a rule about.
            var pending = searchable.LiveSource!.EmbedAsync(document);
            fresh = pending.IsCompleted ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return (false, 0.0, $"the live query embedder could not be reached ({ex.GetType().Name}; message withheld)");
        }

        if (fresh.IsUnavailable())
        {
            return (false, 0.0, "the live query embedder returned NO vector for a product document");
        }

        var cosine = EmbeddingVectors.DotOfUnitVectors(
            EmbeddingVectors.Normalized(fresh.Span), committed.Span);

        if (cosine < SpaceIdentityProbeFloor)
        {
            return (false, cosine,
                $"the live embedder and the committed index are NOT the same space: re-embedding "
              + $"'{probeProduct.Id}'s own document scored {cosine:F4} against its committed vector, and the "
              + $"expected value in one space is 1.0000. Two spaces produce confident nonsense, not a weak signal");
        }

        return (true, cosine, $"Probed on '{probeProduct.Id}'.");
    }

    private static EmbeddingSourceResolution FallBack(
        EmbeddingSpaceChoice requested,
        string why,
        IReadOnlyList<string> warnings)
        => new(
            ConceptEmbeddingSource.Instance,
            requested,
            EmbeddingSpaceChoice.ConceptVectors,
            $"the real-vector path was asked for but {why}. Falling back to the concept space — every number "
          + "below was produced by 24 authored dimensions, NOT by text-embedding-3-small.",
            FellBack: true,
            warnings,
            CachedVectorCount: 0);
}

/// <summary>
/// What <see cref="EmbeddingSpace.Resolve"/> decided, and why — everything a banner needs to say
/// which space produced the numbers on the screen.
/// </summary>
/// <param name="Source">The source every retriever and every cosine in this run must use.</param>
/// <param name="Requested">What the command line asked for.</param>
/// <param name="Chosen">What it actually got. Different from <paramref name="Requested"/> only via a fallback.</param>
/// <param name="Reason">Why, in words, never empty. Printed.</param>
/// <param name="FellBack">True when the real-vector path was wanted and could not be validated.</param>
/// <param name="Warnings">Loader warnings. Non-empty means the caller MUST print them.</param>
/// <param name="CachedVectorCount">Committed vectors loaded, or 0 on the concept path.</param>
public sealed record EmbeddingSourceResolution(
    IEmbeddingSource Source,
    EmbeddingSpaceChoice Requested,
    EmbeddingSpaceChoice Chosen,
    string Reason,
    bool FellBack,
    IReadOnlyList<string> Warnings,
    int CachedVectorCount)
{
    /// <summary>The flag that forces this space, for a banner that tells a reader how to change it.</summary>
    public string Flag => Chosen == EmbeddingSpaceChoice.RealVectors ? "--real-vectors" : "--concept-vectors";

    /// <summary>
    /// The deployment queries are embedded against, or null on the concept path. Taken from the
    /// committed index's model stamp, never from configuration — see <see cref="EmbeddingSpace"/>.
    /// </summary>
    public string? LiveQueryDeployment { get; init; }

    /// <summary>
    /// The space-identity probe's measured cosine, or null when no probe ran. Printed rather than
    /// merely compared, so nobody has to trust <see cref="EmbeddingSpace.SpaceIdentityProbeFloor"/>
    /// to know the index and the query embedder agree.
    /// </summary>
    public double? SpaceIdentityCosine { get; init; }

    /// <summary>True when this run's queries reach a network. The concept path never does.</summary>
    public bool QueriesAreLive => LiveQueryDeployment is { Length: > 0 };

    /// <summary>One line: which space, which model, how many vectors, and whether queries go live.</summary>
    public string SummaryLine =>
        $"Embedding space: {Source.Name} ({Source.ModelId}, {Source.Dimensions} dims)"
      + (CachedVectorCount > 0 ? $" · {CachedVectorCount} committed product vectors" : string.Empty)
      + (QueriesAreLive
            ? $" · queries embedded LIVE against '{LiveQueryDeployment}'"
              + (SpaceIdentityCosine is { } cosine ? $" · space probe {cosine:F4}" : string.Empty)
            : " · queries embedded offline")
      + $" · {Flag}";

    /// <summary>
    /// Prints the space, its reason, and any loader warning. Yellow when a fallback happened,
    /// because a reader who asked for real vectors and got authored ones must not have to notice
    /// a grey line to find that out.
    /// </summary>
    /// <param name="indent">Leading spaces, so it lines up with the caller's own banner.</param>
    public void PrintBanner(string indent = "  ")
    {
        Console.ForegroundColor = FellBack ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
        Console.WriteLine($"{indent}{SummaryLine}");
        Console.WriteLine($"{indent}  {(FellBack ? "⚠️  " : string.Empty)}{Reason}");

        if (QueriesAreLive)
        {
            // Not buried in grey with the rest: this run spends money, and a reader who believed
            // the sample's "no key needed" promise must be told the promise does not hold here.
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"{indent}  💸 This run EMBEDS QUERIES LIVE. It needs credentials and it spends — a "
                            + "fraction of a cent, but not zero. --concept-vectors is the key-free path.");
        }

        foreach (var warning in Warnings)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"{indent}  ⚠️  {warning}");
        }

        Console.ResetColor();
    }
}
