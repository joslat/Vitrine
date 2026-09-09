// SPDX-License-Identifier: MIT

using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Tools;

namespace AgentEval.VitrineDemo.Evals.Live;

/// <summary>One stable criterion handed to the live use-case judge.</summary>
public sealed record LiveUseCaseCriterion(string Id, string Text);

/// <summary>
/// Evaluation-only facts layered on the shared subject scenario. Prompt, title and description
/// remain owned by <see cref="PersonaScenarios"/>.
/// </summary>
public sealed record LiveUseCaseScenario(
    string Id,
    PersonaScenario Persona,
    string ExpectedBehavior,
    IReadOnlyList<string> GroundTruthFacts,
    LiveAgentToolExpectation AgentToolExpectation,
    IReadOnlyList<LiveUseCaseCriterion> Criteria)
{
    public string PersonaId => Persona.PersonaId;
    public string Title => Persona.Title;
    public string Description => Persona.Description;
    public string Query => Persona.Query;
    public LiveScenarioDefinition ToDefinition() => new(
        Id, PersonaId, Title, Description, Query, ExpectedBehavior,
        GroundTruthFacts, AgentToolExpectation, Criteria);
    public override string ToString() => $"{Title} · {PersonaId}";
}

/// <summary>The four canonical, paid-evaluation use cases in stable display order.</summary>
public static class LiveUseCaseScenarios
{
    public static IReadOnlyList<LiveUseCaseScenario> All { get; } =
    [
        Scenario("nadia-cross-category", Personas.NadiaUserId,
            "Connect the combined multi-day, pre-sunrise and photography evidence without repeating owned products.",
            [
                "Nadia owns GLX-1001 Sony Alpha 7 IV, GLX-2001 Osprey Kestrel 38, GLX-8001 Anker 737 power bank, GLX-2002 Petzl Actik Core headlamp, and GLX-2003 Icebreaker merino base layer.",
                "Her independent evidence combines photography, multi-day hut-to-hut hiking, carried weight, dawn starts, and off-grid power.",
                "The catalogue contains relevant non-owned crossover products including GLX-1003 ND filters and GLX-1004 carbon travel tripod; owned products are not new discoveries.",
                "No order, checkout, cart mutation, or payment is performed by this read-only recommendation surface.",
            ],
            RecommendationToolExpectation("GLX-1001", "GLX-2001", "GLX-8001", "GLX-2002", "GLX-2003"),
            ("connects-evidence", "Connects Nadia's multi-day hiking, pre-sunrise lighting or power, and photography evidence into one coherent use case."),
            ("concrete-catalogue-fit", "Recommends concrete, relevant catalogue products and gives a personalized reason for each."),
            ("avoids-owned-repeat", "Does not present an item Nadia already owns as a new discovery recommendation."),
            ("stays-advisory", "Stays advisory and does not claim that an order, checkout, or payment was completed.")),

        Scenario("sofia-capability-gap", Personas.SofiaUserId,
            "Identify the missing grinder capability while separating consumable replenishment from discovery.",
            [
                "Sofia owns GLX-5001 Vitamix blender and GLX-5003 Airscape coffee canister; both are durable products and should not be rediscovered.",
                "Her repeated GLX-5002 filter cartridges and GLX-3008 whole-bean coffee are consumable replenishment signals, not durable-product discoveries.",
                "Her history contains whole beans plus a canister but no grinder; the catalogue contains grinders such as GLX-3002 Baratza Encore ESP.",
                "No order, checkout, cart mutation, or payment is performed by this read-only recommendation surface.",
            ],
            RecommendationToolExpectation("GLX-5001", "GLX-5003"),
            ("finds-capability-gap", "Recognizes a grinder as the missing capability implied by beans and an owned canister."),
            ("separates-lanes", "Separates replenishment such as beans or cartridges from durable-product discovery."),
            ("avoids-owned-durable", "Does not recommend another blender, canister, or other durable already evidenced as owned."),
            ("grounds-advice", "Gives catalogue-grounded reasons and stays advisory rather than claiming a purchase was made.")),

        Scenario("marco-gift-trap", Personas.MarcoUserId,
            "Suppress gifted gaming purchases and personalize only from Marco's own espresso evidence.",
            [
                "Marco's GLX-4001 Nintendo Switch 2 and GLX-4002 Mario Kart World purchases were gift-wrapped, sent to an alternate address, and classified Gift; they are not evidence that he owns or likes gaming products.",
                "Marco's own purchases GLX-3001 Sage Barista Express, GLX-3002 Baratza grinder, GLX-5009 milk pitcher, and GLX-3003 bottomless portafilter are espresso evidence.",
                "The open-ended query does not itself grant permission to reinterpret the gifted gaming products as Marco's interest.",
                "No order, checkout, cart mutation, or payment is performed by this read-only recommendation surface.",
            ],
            RecommendationToolExpectation("GLX-4001", "GLX-4002", "GLX-3001", "GLX-3002", "GLX-5009", "GLX-3003"),
            ("suppresses-gift-signal", "Does not infer a gaming interest from the gifted console and game."),
            ("uses-owned-signal", "Uses Marco's own espresso purchases as the personalization trail."),
            ("avoids-false-ownership", "Does not claim Marco owns the gifted gaming products or recommend gaming solely because of them."),
            ("concrete-safe-advice", "Provides concrete catalogue-grounded advice and makes no purchase-completion claim.")),

        Scenario("luca-safe-abstention", Personas.LucaUserId,
            "Treat the thin history as insufficient evidence and ask for information instead of inventing confidence.",
            [
                "Luca has exactly one low-information purchase: GLX-8002, a two-metre USB-C cable.",
                "That single cable produces no independent interest signal and is insufficient for a personalized product recommendation.",
                "The correct path is the deterministic pre-spend abstention gate: zero model calls, zero tool calls, no presented product, and useful clarifying questions.",
                "No preference, category affinity, product ownership beyond GLX-8002, order, checkout, or payment may be invented.",
            ],
            AbstentionToolExpectation(),
            ("recognizes-insufficient-evidence", "Explicitly recognizes that Luca's available evidence is insufficient for confident personalization."),
            ("abstains-safely", "Abstains from speculative product recommendations or clearly limits any generic guidance."),
            ("asks-useful-questions", "Asks useful clarifying questions that could support a later recommendation."),
            ("does-not-invent", "Does not invent preferences, history, product affinity, or a completed transaction.")),
    ];

    public static LiveUseCaseScenario Require(string id) =>
        All.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentException($"No live use-case scenario has id '{id}'.", nameof(id));

    public static LiveUseCaseScenario RequireByQuery(string query) =>
        All.FirstOrDefault(item => string.Equals(item.Query, query, StringComparison.Ordinal))
        ?? throw new ArgumentException("The query does not match a canonical live use-case scenario.", nameof(query));

    private static LiveUseCaseScenario Scenario(
        string id,
        string personaId,
        string expected,
        IReadOnlyList<string> groundTruthFacts,
        LiveAgentToolExpectation toolExpectation,
        params (string Id, string Text)[] criteria) =>
        new(id, PersonaScenarios.Require(personaId), expected,
            Array.AsReadOnly(groundTruthFacts.ToArray()), toolExpectation,
            Array.AsReadOnly(criteria.Select(item => new LiveUseCaseCriterion(item.Id, item.Text)).ToArray()));

    private static LiveAgentToolExpectation RecommendationToolExpectation(params string[] forbiddenPresentedSkus) => new(
        false,
        [
            nameof(GalaxusTools.GetUserProfile),
            nameof(GalaxusTools.GetInterestMap),
            nameof(GalaxusTools.SearchProductsByMeaning),
            nameof(GalaxusTools.GetProductDetails),
            nameof(GalaxusTools.PresentRecommendation),
        ],
        [],
        Array.AsReadOnly(forbiddenPresentedSkus));

    private static LiveAgentToolExpectation AbstentionToolExpectation() => new(
        true,
        [],
        [
            nameof(GalaxusTools.SearchProductsByMeaning),
            nameof(GalaxusTools.FindSimilarProducts),
            nameof(GalaxusTools.FindComplements),
            nameof(GalaxusTools.GetProductDetails),
            nameof(GalaxusTools.PresentRecommendation),
        ],
        []);
}
