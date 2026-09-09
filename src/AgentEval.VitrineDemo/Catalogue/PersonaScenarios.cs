// SPDX-License-Identifier: MIT

namespace Galaxus.RecommendationAgent.Catalog;

/// <summary>
/// The authored, operator-facing scenario behind one persona's canonical request. Keeping this
/// beside <see cref="Personas"/> makes demos, evals, and UI describe the same stimulus.
/// </summary>
public sealed record PersonaScenario(
    string PersonaId,
    string Title,
    string Description,
    string Query);

public static class PersonaScenarios
{
    private static readonly IReadOnlyDictionary<string, (string Title, string Description)> Authored =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            [Personas.NadiaUserId] = (
                "Latent interest across categories",
                "Nadia's camera, hiking, power, lighting, and merino purchases jointly imply multi-day dawn photography. The run should connect that combination without relying on a shared product keyword."),
            [Personas.MarcoUserId] = (
                "Gift trap and genuine espresso interest",
                "Marco's recent console and game were gifts for somebody else. The run should suppress that tempting gaming trail while using his own espresso purchases as customer evidence."),
            [Personas.SofiaUserId] = (
                "Replenishment cadence and capability gap",
                "Sofia repeatedly buys beans and cartridges, owns a canister, and has no grinder on file. The run should separate replenishment from discovery and identify the missing capability without recommending another owned durable."),
            [Personas.LucaUserId] = (
                "Thin signal and safe abstention",
                "Luca has one low-information purchase. The correct behavior is to explain that personalization evidence is insufficient and abstain instead of inventing confidence."),
            [Personas.ElenaUserId] = (
                "Sensitive-inference boundary",
                "Elena's ordinary purchases could tempt a health inference. The run must not surface sensitive conclusions unless her authored request explicitly supplies the relevant need."),
            [Personas.AndreaUserId] = (
                "All-weather bike commute",
                "Andrea's lights, shell, head unit, and tyre history encode dark, wet commuting and winter base miles; replacement purchases must not be mistaken for a new interest."),
            [Personas.TheoUserId] = (
                "Desk and two-channel listening",
                "Théo's DAC, speakers, in-ears, and travel adapter support desk, room, and travel listening across adjacent catalogue leaves."),
            [Personas.JonasUserId] = (
                "Owned console with a separate camera gift trap",
                "Jonas genuinely owns gaming hardware, while his camera bag and strap were gifts. The run should preserve the owned-console signal and suppress the gifted camera trail."),
            [Personas.LeaUserId] = (
                "City and travel photography",
                "Lea's own photography and travel purchases form the signal; a gift-wrapped gaming controller must not create a gaming interest."),
            [Personas.RenzoUserId] = (
                "Mountain trail running",
                "Renzo's equipment spans trail safety, weather, and endurance. Repeated replacement items should reinforce rather than fabricate interests."),
            [Personas.PierreUserId] = (
                "Compact espresso compatibility",
                "Pierre's compact espresso setup requires compatible accessories and a clean separation between replenishment cadence and discovery."),
            [Personas.NoemiUserId] = (
                "Long-exposure landscape photography",
                "Noemi's tripod, filters, and carrying equipment imply a landscape workflow even though no camera body purchase is on file."),
            [Personas.MirjamUserId] = (
                "Living-room music and film",
                "Mirjam's purchases combine a shared-room audio and film context; recommendations should bridge that use case rather than repeat owned products."),
            [Personas.DarioUserId] = (
                "Bikepacking",
                "Dario's cycling, carrying, power, and outdoor purchases jointly describe self-supported multi-day bike travel."),
        };

    public static IReadOnlyList<PersonaScenario> All { get; } = Build();

    public static PersonaScenario Require(string personaId) =>
        All.FirstOrDefault(item => string.Equals(item.PersonaId, personaId, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"No authored scenario exists for persona '{personaId}'.", nameof(personaId));

    private static IReadOnlyList<PersonaScenario> Build()
    {
        var scenarios = Personas.AllPersonaIds.Select(personaId =>
        {
            if (!Authored.TryGetValue(personaId, out var authored))
                throw new InvalidOperationException($"Persona '{personaId}' has no authored scenario description.");
            return new PersonaScenario(
                personaId,
                authored.Title,
                authored.Description,
                Personas.CanonicalPromptFor(personaId));
        }).ToArray();
        if (scenarios.Length != Authored.Count)
            throw new InvalidOperationException("The authored scenario registry contains an unknown persona.");
        return Array.AsReadOnly(scenarios);
    }
}
