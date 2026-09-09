// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Retrieval;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>One independently authored expectation for one persona in one embedding space.</summary>
/// <remarks>
/// These are descriptive pins, not workflow inputs. The executable workflow never reads them.
/// Runtime reports and evals may compare a completed run with them, which keeps a stale narrative
/// visible without allowing that narrative to steer the run it describes.
/// </remarks>
public sealed record DiscoveryTopologyCaseClaim(
    string PersonaId,
    EmbeddingSpaceChoice EmbeddingSpace,
    IReadOnlyList<string> RouteIds,
    int LoopBackCount,
    int Rounds,
    DiscoveryStopReason StopReason);

/// <summary>Topology facts projected only from a completed workflow result.</summary>
public sealed record DiscoveryTopologyCaseObservation(
    string PersonaId,
    EmbeddingSpaceChoice? EmbeddingSpace,
    IReadOnlyList<string> RouteIds,
    int LoopBackCount,
    int Rounds,
    DiscoveryStopReason StopReason);

/// <summary>Three-state result of comparing a run with its independently authored claim.</summary>
public enum DiscoveryTopologyCaseOutcome
{
    /// <summary>The run did not identify a registered case and space; no comparison was possible.</summary>
    NotMeasured,

    /// <summary>Every route and terminal fact matched.</summary>
    Match,

    /// <summary>At least one route or terminal fact differed.</summary>
    Mismatch,
}

/// <summary>A claim comparison suitable for eval and report projections.</summary>
public sealed record DiscoveryTopologyCaseAssessment(
    DiscoveryTopologyCaseOutcome Outcome,
    DiscoveryTopologyCaseClaim? Claim,
    DiscoveryTopologyCaseObservation Observation,
    IReadOnlyList<string> Differences)
{
    /// <summary>True only for an observed exact match; absence can never become success.</summary>
    public bool IsMatch => Outcome == DiscoveryTopologyCaseOutcome.Match;
}

/// <summary>
/// The ten authored workflow-topology cells: five personas in both supported vector spaces.
/// </summary>
/// <remarks>
/// This replaces free-text claims that once described the wrong customer's run. Each cell pins
/// the complete route trace, loop-back count, producer-owned round counter and typed stop reason.
/// The registry validates its own coverage at type initialization, so adding a vector space or
/// dropping a case makes startup fail rather than silently narrowing the evidence.
/// </remarks>
public static class DiscoveryTopologyCaseRegistry
{
    private static readonly string[] AuthoredPersonaIds =
    [
        Personas.RenzoUserId,
        Personas.MarcoUserId,
        Personas.MirjamUserId,
        Personas.NadiaUserId,
        Personas.LucaUserId,
    ];

    private static readonly IReadOnlyList<DiscoveryTopologyCaseClaim> Claims = Validate(
    [
        Claim(Personas.RenzoUserId, EmbeddingSpaceChoice.ConceptVectors, 0, 1, DiscoveryStopReason.CoverageSufficient),
        Claim(Personas.RenzoUserId, EmbeddingSpaceChoice.RealVectors,    0, 1, DiscoveryStopReason.CoverageSufficient),

        Claim(Personas.MarcoUserId, EmbeddingSpaceChoice.ConceptVectors, 1, 2, DiscoveryStopReason.CoverageSufficient),
        Claim(Personas.MarcoUserId, EmbeddingSpaceChoice.RealVectors,    2, 3, DiscoveryStopReason.GapsUnresolvable),

        Claim(Personas.MirjamUserId, EmbeddingSpaceChoice.ConceptVectors, 2, 3, DiscoveryStopReason.GapsUnresolvable),
        Claim(Personas.MirjamUserId, EmbeddingSpaceChoice.RealVectors,    1, 2, DiscoveryStopReason.CoverageSufficient),

        Claim(Personas.NadiaUserId, EmbeddingSpaceChoice.ConceptVectors, 0, 1, DiscoveryStopReason.CoverageSufficient),
        Claim(Personas.NadiaUserId, EmbeddingSpaceChoice.RealVectors,    0, 1, DiscoveryStopReason.CoverageSufficient),

        Claim(Personas.LucaUserId, EmbeddingSpaceChoice.ConceptVectors, 0, 1, DiscoveryStopReason.GapsUnresolvable),
        Claim(Personas.LucaUserId, EmbeddingSpaceChoice.RealVectors,    0, 1, DiscoveryStopReason.GapsUnresolvable),
    ]);

    /// <summary>All claims, ordered by persona and then explicit space.</summary>
    public static IReadOnlyList<DiscoveryTopologyCaseClaim> All => Claims;

    /// <summary>Projects the facts used in comparison without consulting the claim registry.</summary>
    public static DiscoveryTopologyCaseObservation Observe(DiscoveryRunResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new(
            run.State.CustomerId,
            run.ResolvedEmbeddingSpace,
            run.RoutesTaken.ToArray(),
            run.RoutesTaken.Count(static route =>
                string.Equals(route, DiscoveryRouteIds.ReviewToMoreDiscovery, StringComparison.Ordinal)),
            run.State.DiscoveryRound,
            run.State.StopReason);
    }

    /// <summary>Compares a completed run with the registered cell for its persona and space.</summary>
    public static DiscoveryTopologyCaseAssessment Assess(DiscoveryRunResult run)
    {
        var observation = Observe(run);
        if (observation.EmbeddingSpace is not { } space)
        {
            return new(
                DiscoveryTopologyCaseOutcome.NotMeasured,
                null,
                observation,
                ["The run did not record a resolved embedding space."]);
        }

        if (string.IsNullOrWhiteSpace(run.RequestedPersonaId))
        {
            return new(
                DiscoveryTopologyCaseOutcome.NotMeasured,
                null,
                observation,
                ["The composition root did not record the authored persona identity."]);
        }

        var claim = Claims.SingleOrDefault(candidate =>
            string.Equals(candidate.PersonaId, run.RequestedPersonaId, StringComparison.Ordinal) &&
            candidate.EmbeddingSpace == space);
        if (claim is null)
        {
            return new(
                DiscoveryTopologyCaseOutcome.NotMeasured,
                null,
                observation,
                [$"No authored topology claim exists for '{run.RequestedPersonaId}' in {space}."]);
        }

        return Assess(run, claim);
    }

    /// <summary>
    /// Compares a run with an explicit claim. This overload is the ablation seam: changing any one
    /// claim field must turn the comparison red while leaving the completed run untouched.
    /// </summary>
    public static DiscoveryTopologyCaseAssessment Assess(
        DiscoveryRunResult run,
        DiscoveryTopologyCaseClaim claim)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Assess(Observe(run), claim);
    }

    /// <summary>Compares a frozen run observation with an explicit authored claim.</summary>
    public static DiscoveryTopologyCaseAssessment Assess(
        DiscoveryTopologyCaseObservation observation,
        DiscoveryTopologyCaseClaim claim)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(claim);

        if (observation.EmbeddingSpace is null)
        {
            return new(
                DiscoveryTopologyCaseOutcome.NotMeasured,
                claim,
                observation,
                ["The run did not record a resolved embedding space."]);
        }

        var differences = new List<string>();

        if (!string.Equals(claim.PersonaId, observation.PersonaId, StringComparison.Ordinal))
            differences.Add($"persona expected '{claim.PersonaId}', observed '{observation.PersonaId}'");
        if (claim.EmbeddingSpace != observation.EmbeddingSpace)
            differences.Add($"space expected {claim.EmbeddingSpace}, observed {observation.EmbeddingSpace}");
        if (!claim.RouteIds.SequenceEqual(observation.RouteIds, StringComparer.Ordinal))
            differences.Add($"routes expected [{string.Join(", ", claim.RouteIds)}], observed [{string.Join(", ", observation.RouteIds)}]");
        if (claim.LoopBackCount != observation.LoopBackCount)
            differences.Add($"loop-backs expected {claim.LoopBackCount}, observed {observation.LoopBackCount}");
        if (claim.Rounds != observation.Rounds)
            differences.Add($"rounds expected {claim.Rounds}, observed {observation.Rounds}");
        if (claim.StopReason != observation.StopReason)
            differences.Add($"stop reason expected {claim.StopReason}, observed {observation.StopReason}");

        return new(
            differences.Count == 0
                ? DiscoveryTopologyCaseOutcome.Match
                : DiscoveryTopologyCaseOutcome.Mismatch,
            claim,
            observation,
            differences);
    }

    private static DiscoveryTopologyCaseClaim Claim(
        string personaId,
        EmbeddingSpaceChoice space,
        int loopBacks,
        int rounds,
        DiscoveryStopReason stopReason) =>
        new(personaId, space, RouteTrace(loopBacks), loopBacks, rounds, stopReason);

    private static IReadOnlyList<string> RouteTrace(int loopBacks)
    {
        var routes = new List<string>
        {
            DiscoveryRouteIds.MapToDiscovery,
            DiscoveryRouteIds.DiscoveryToReview,
        };

        for (var i = 0; i < loopBacks; i++)
        {
            routes.Add(DiscoveryRouteIds.ReviewToMoreDiscovery);
            routes.Add(DiscoveryRouteIds.DiscoveryToReview);
        }

        routes.Add(DiscoveryRouteIds.ReviewToRanker);
        routes.Add(DiscoveryRouteIds.RankerToPresenter);
        return routes.ToArray();
    }

    private static IReadOnlyList<DiscoveryTopologyCaseClaim> Validate(
        IReadOnlyList<DiscoveryTopologyCaseClaim> claims)
    {
        var spaces = Enum.GetValues<EmbeddingSpaceChoice>()
            .Where(static space => space != EmbeddingSpaceChoice.Auto)
            .ToArray();
        var expectedCells = AuthoredPersonaIds
            .SelectMany(persona => spaces.Select(space => (Persona: persona, Space: space)))
            .ToHashSet();
        var actualCells = claims
            .Select(static claim => (Persona: claim.PersonaId, Space: claim.EmbeddingSpace))
            .ToArray();

        if (actualCells.Length != actualCells.Distinct().Count() ||
            !expectedCells.SetEquals(actualCells))
        {
            throw new InvalidOperationException(
                "The topology-case registry must contain exactly one claim for every authored persona/vector-space cell.");
        }

        foreach (var claim in claims)
        {
            if (claim.Rounds < 1 || claim.LoopBackCount < 0 || claim.LoopBackCount != claim.Rounds - 1)
                throw new InvalidOperationException($"Topology claim '{claim.PersonaId}'/{claim.EmbeddingSpace} has inconsistent round counts.");
            if (claim.StopReason is DiscoveryStopReason.None or DiscoveryStopReason.GapsRemain)
                throw new InvalidOperationException($"Topology claim '{claim.PersonaId}'/{claim.EmbeddingSpace} is not terminal.");
            if (claim.RouteIds.Count(static route =>
                    string.Equals(route, DiscoveryRouteIds.ReviewToMoreDiscovery, StringComparison.Ordinal)) !=
                claim.LoopBackCount)
            {
                throw new InvalidOperationException($"Topology claim '{claim.PersonaId}'/{claim.EmbeddingSpace} disagrees with its route trace.");
            }
        }

        return claims.ToArray();
    }
}
