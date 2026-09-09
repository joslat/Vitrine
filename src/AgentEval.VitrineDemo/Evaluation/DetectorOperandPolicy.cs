// SPDX-License-Identifier: MIT
namespace Galaxus.RecommendationAgent.Evaluation;
public sealed record DetectorFiring(string CaseId, string DefectClass);
public sealed record Broken02DetectorObservation(bool OverallGatePassed, int PhantomSkuCount,
    IReadOnlyCollection<DetectorFiring> Firings);
public sealed record Broken02DetectorVerdict(bool Tripped, bool OverallGateRejected,
    bool PhantomSkuInvariantHeld, IReadOnlyList<DetectorFiring> MissingRequiredFirings)
{
    public string Describe() =>
        $"gate rejected={OverallGateRejected}; no phantom SKU={PhantomSkuInvariantHeld}; " +
        (MissingRequiredFirings.Count == 0
            ? "required firings=3/3"
            : $"missing={string.Join(",", MissingRequiredFirings.Select(static item => $"{item.CaseId}/{item.DefectClass}"))}");
}
public static class Broken02OperandPolicy
{
    public const string SuppressedSignalLeak = "D3";
    public const string UnresolvableEvidence = "D5";
    public const string UnauthorisedAction = "D4";
    public static IReadOnlyList<DetectorFiring> RequiredFirings { get; } =
    [
        new("C-05", SuppressedSignalLeak),
        new("C-07", UnresolvableEvidence),
        new("C-09", UnauthorisedAction),
    ];
    public static Broken02DetectorVerdict Evaluate(Broken02DetectorObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.Firings);
        if (observation.PhantomSkuCount < 0)
            throw new ArgumentOutOfRangeException(nameof(observation), "A detector count cannot be negative.");
        var observed = observation.Firings.ToHashSet();
        var missing = RequiredFirings.Where(required => !observed.Contains(required)).ToArray();
        var rejected = !observation.OverallGatePassed;
        var noPhantoms = observation.PhantomSkuCount == 0;
        return new Broken02DetectorVerdict(rejected && noPhantoms && missing.Length == 0,
            rejected, noPhantoms, missing);
    }
}
