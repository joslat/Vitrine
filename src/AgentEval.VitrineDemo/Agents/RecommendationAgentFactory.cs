// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Providers;
using Galaxus.RecommendationAgent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace Galaxus.RecommendationAgent.Agents;

/// <summary>
/// Builds Robin, the advisory recommendation agent against the MAF 1.17.0 API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two factories, on purpose.</b> <see cref="Create()"/> registers the
/// THIRTEEN read-only tools and nothing else — that is what Demo 1 ships, and
/// <see cref="ToolSurfaceInvariant.AssertReadOnly"/> throws at construction if anything
/// mutating ever creeps in, so adding a purchase tool later cannot be done by accident: the app
/// fails to start. <see cref="CreateWithCommitTools()"/> additionally registers
/// <c>AddToCart</c> and <c>PlaceOrder</c>, wrapped in
/// <see cref="ApprovalRequiredAIFunction"/>, and is used ONLY by the two eval cases that test
/// the human-confirmation gate.
/// </para>
/// <para>
/// Both statements are then true at once, which they were not before: read-only is a property
/// of the SHIPPED configuration, and the approval gate is a property of the TESTED one.
/// <c>NeverCallTool("PlaceOrder")</c> against an agent that has no <c>PlaceOrder</c> has a
/// chance floor of 1.0 and proves nothing — a prohibition has to be tempting before refusing it
/// means anything.
/// </para>
/// <para>
/// <b>Verified API surface at MAF 1.17.0:</b> <c>CreateAIAgent</c> does NOT exist — construction
/// is <c>new ChatClientAgent(IChatClient, ChatClientAgentOptions)</c>; <c>Instructions</c> and
/// <c>Tools</c> live on <see cref="ChatOptions"/>, not on <see cref="ChatClientAgentOptions"/>;
/// the <c>ChatOptions</c> alias above is house style, guarding against the same-named type in
/// other namespaces. <c>ApprovalRequiredAIFunction</c> is MEAI's own type (10.7.0) — this
/// the subject project remains independent from the evaluation project and takes no dependency on AgentEval.
/// </para>
/// </remarks>
public static class RecommendationAgentFactory
{
    /// <summary>The agent's name, as it appears in traces and in the console header.</summary>
    public const string AgentName = "Robin";

    /// <summary>The agent's one-line description, shared by both configurations.</summary>
    public const string AgentDescription =
        "Advisory product recommender: builds an interest map from a customer's signals, searches by meaning "
      + "across categories, and explains every suggestion with two-sided evidence. Recommends only — never buys.";

    /// <summary>
    /// Creates the SHIPPED agent: thirteen read-only tools, connected to Azure OpenAI using
    /// <see cref="Config"/>'s three-step deployment ladder.
    /// </summary>
    /// <exception cref="InvalidOperationException">Azure live configuration is not ready, or the tool surface is not read-only.</exception>
    public static ChatClientAgent Create() => Create(CreateConfiguredChatClient());

    /// <summary>Creates the live Azure-backed shipped agent with invocation-time observation.</summary>
    public static ChatClientAgent Create(IRecommendationRuntimeEventSink events) =>
        Create(CreateConfiguredChatClient(), events);

    /// <summary>
    /// Creates the SHIPPED agent over a caller-supplied chat client — the seam the eval lane and
    /// any offline test use.
    /// </summary>
    /// <param name="chatClient">The chat client to run against.</param>
    /// <exception cref="InvalidOperationException">The tool surface is not read-only.</exception>
    public static ChatClientAgent Create(IChatClient chatClient) =>
        Create(chatClient, NullRecommendationRuntimeEventSink.Instance);

    /// <summary>Creates the shipped agent with invocation-time model and tool observation.</summary>
    public static ChatClientAgent Create(
        IChatClient chatClient,
        IRecommendationRuntimeEventSink events)
        => Create(chatClient, events, PrepareReadOnlyTools(events));

    /// <summary>
    /// Creates the shipped agent over the exact observed tool instances prepared by the
    /// composition root. The UI uses this overload so graph metadata and agent registration have
    /// identity-level provenance rather than merely matching names.
    /// </summary>
    public static ChatClientAgent Create(
        IChatClient chatClient,
        IRecommendationRuntimeEventSink events,
        RecommendationToolSet registeredTools)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(registeredTools);
        var tools = registeredTools.Tools.ToArray();

        // Mechanical guarantee, not a promise: throws if the registered set differs from the
        // thirteen-name read-only allow-list in either direction. The list is AUTHORED in
        // ToolSurfaceInvariant.ReadOnlyToolNames as literal strings; the array below is
        // ASSEMBLED from method groups. Their independent authorship makes drift fail at startup
        // instead of letting the surface validate against a second copy of itself.
        ToolSurfaceInvariant.AssertReadOnly(tools);

        return new ChatClientAgent(new ObservedChatClient(chatClient, events), new ChatClientAgentOptions
        {
            Name = AgentName,
            Description = AgentDescription,
            ChatOptions = new ChatOptions
            {
                Instructions = RecommendationInstructions.Instructions,
                Tools = tools
            }
        });
    }

    /// <summary>
    /// Creates the TESTED agent: the thirteen read-only tools plus <c>AddToCart</c> and
    /// <c>PlaceOrder</c> behind an approval requirement. Used only by the two eval cases that
    /// exercise the human-confirmation gate. Never shipped in Demo 1.
    /// </summary>
    /// <exception cref="InvalidOperationException">Azure live configuration is not ready, or the commit tools are not approval-gated.</exception>
    public static ChatClientAgent CreateWithCommitTools() => CreateWithCommitTools(CreateConfiguredChatClient());

    /// <summary>
    /// Creates the TESTED agent over a caller-supplied chat client.
    /// </summary>
    /// <param name="chatClient">The chat client to run against.</param>
    /// <exception cref="InvalidOperationException">The commit tools are not approval-gated.</exception>
    public static ChatClientAgent CreateWithCommitTools(IChatClient chatClient)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        AITool[] tools = [.. BuildReadOnlyTools(), .. BuildApprovalGatedCommitTools()];

        // The mirror-image invariant: this configuration MUST carry both commit tools, and both
        // MUST be approval-required. A commit tool that slipped in un-gated would make the
        // confirmation case pass for the wrong reason.
        ToolSurfaceInvariant.AssertReadOnlyWithApprovalGatedCommitTools(tools);

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = AgentName,
            Description = AgentDescription + " In this configuration it also holds two approval-gated commit tools, "
                        + "so the prohibition against ordering without confirmation is testable.",
            ChatOptions = new ChatOptions
            {
                Instructions = RecommendationInstructions.Instructions + RecommendationInstructions.CommitToolsAddendum,
                Tools = tools
            }
        });
    }

    /// <summary>
    /// The thirteen read-only tools, in the order they are described to the model: three semantic,
    /// nine structured, then the one recommendation channel.
    /// </summary>
    /// <remarks>
    /// A fresh array on every call — <see cref="AIFunctionFactory"/> instances are cheap, and
    /// sharing one mutable array between two agent configurations is how a "read-only" surface
    /// silently acquires a commit tool.
    /// </remarks>
    public static AITool[] BuildReadOnlyTools() => BuildReadOnlyTools(null);

    /// <summary>Prepares one identity-bearing observed tool set for graph and agent composition.</summary>
    public static RecommendationToolSet PrepareReadOnlyTools(IRecommendationRuntimeEventSink? events = null) =>
        new(BuildReadOnlyTools(events));

    /// <summary>The thirteen read-only tools, optionally decorated for invocation-time events.</summary>
    public static AITool[] BuildReadOnlyTools(IRecommendationRuntimeEventSink? events)
    {
        AIFunction[] functions =
    [
        // Discovery (semantic) — recall-oriented candidates, may be wrong by design.
        AIFunctionFactory.Create(GalaxusTools.SearchProductsByMeaning),
        AIFunctionFactory.Create(GalaxusTools.FindSimilarProducts),
        AIFunctionFactory.Create(GalaxusTools.FindComplements),

        // Profile & signals (structured) — facts.
        AIFunctionFactory.Create(GalaxusTools.GetUserProfile),
        AIFunctionFactory.Create(GalaxusTools.GetPurchaseHistory),
        AIFunctionFactory.Create(GalaxusTools.GetInterestMap),

        // Product facts (structured) — the authorities the guardrails verify the model against.
        AIFunctionFactory.Create(GalaxusTools.GetProductDetails),
        AIFunctionFactory.Create(GalaxusTools.GetReviewDigest),
        AIFunctionFactory.Create(GalaxusTools.BrowseCategory),
        AIFunctionFactory.Create(GalaxusTools.CheckStockAndPrice),
        AIFunctionFactory.Create(GalaxusTools.ListDepartments),
        AIFunctionFactory.Create(GalaxusTools.GetCatalogueStatistics),

        // The one sanctioned recommendation channel.
        AIFunctionFactory.Create(GalaxusTools.PresentRecommendation)
    ];

        return events is null
            ? functions
            : [.. functions.Select(function => (AITool)new ObservedAIFunction(function, events))];
    }

    /// <summary>
    /// The two mutating tools, each wrapped so MAF's approval flow pauses on them. Registered
    /// only by <see cref="CreateWithCommitTools(IChatClient)"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ApprovalRequiredAIFunction"/> marks the function; it does not by itself enforce
    /// anything — obtaining the approval is the invoker's job. Wire it to a human with MAF's
    /// <c>UseToolApproval</c>, or to AgentEval's Gatekeeper bridge in the eval project, which
    /// fails closed to a person on any gate escalation. This project stays AgentEval-free.
    /// </remarks>
    public static AITool[] BuildApprovalGatedCommitTools() =>
    [
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GalaxusTools.AddToCart)),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(GalaxusTools.PlaceOrder))
    ];

    /// <summary>Builds the chat client for the resolved host from <see cref="Config"/>.</summary>
    public static IChatClient CreateConfiguredChatClient() =>
        CreateConfiguredChatClient(Config.Model);

    /// <summary>
    /// Builds a chat client for an explicit model through the same host and authentication
    /// composition used by the subject and embedding paths. Evals use this overload with
    /// <see cref="Config.JudgeDeployment"/> so judge routing can be independent.
    /// </summary>
    /// <param name="deployment">Model or deployment name on the resolved host.</param>
    public static IChatClient CreateConfiguredChatClient(string deployment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        if (!Config.IsValidModelNameForResolvedProvider(deployment))
            throw new ArgumentException("The model name contains unsupported characters.", nameof(deployment));
        return InferenceClientFactory.CreateChatClient(deployment);
    }
}

/// <summary>
/// One prepared registration surface. It is a composition object, never part of a run result;
/// only its opaque identity crosses the result boundary for provenance checks.
/// </summary>
public sealed class RecommendationToolSet
{
    public RecommendationToolSet(IEnumerable<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        Tools = Array.AsReadOnly(tools.ToArray());
        if (Tools.Count == 0) throw new ArgumentException("A tool set cannot be empty.", nameof(tools));
    }

    public Guid Id { get; } = Guid.NewGuid();
    public IReadOnlyList<AITool> Tools { get; }
}
