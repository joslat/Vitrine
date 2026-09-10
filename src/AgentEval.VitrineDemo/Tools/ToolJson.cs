// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using System.Text.Json;

namespace Galaxus.RecommendationAgent.Tools;

/// <summary>
/// The single JSON writer for every tool return value (the evaluation design).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why every tool returns <see cref="string"/> and not a DTO.</b> MEAI's
/// <c>AIFunctionFactory</c> will happily JSON-serialize a returned record, but that puts the
/// demo's compile-time reliability at the mercy of the serializer's default resolver — and
/// whether <c>AIFunctionFactory.Create</c> needs an explicit serializer-options object for
/// record return types in MEAI 10.7.0 is UNVERIFIED. Serialising here, through one pinned
/// <see cref="JsonSerializerOptions"/>, gives the model identical JSON, keeps VITRINE's
/// "tools return string" convention, and removes an entire failure class.
/// </para>
/// <para>
/// The options are pinned (camelCase via <see cref="JsonSerializerDefaults.Web"/>, not
/// indented) so the wire shape is a property of THIS file rather than of whatever ambient
/// defaults happen to be in force. Tool payloads are asserted on by the eval lane; a
/// silently-renamed property is exactly the split-contract drift this shared schema prevents.
/// </para>
/// </remarks>
public static class ToolJson
{
    /// <summary>
    /// The pinned serializer options every tool payload goes through. Web defaults give
    /// camelCase property names and case-insensitive reads; indentation is off so a tool
    /// result costs the fewest possible context tokens.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>Serialises a successful tool payload.</summary>
    /// <typeparam name="T">Payload shape — normally an anonymous object authored at the call site.</typeparam>
    /// <param name="payload">The payload. Author it with a <c>status = "ok"</c> member so the
    /// model (and the eval) can branch on one field for every tool alike.</param>
    public static string Ok<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>
    /// A typed refusal — never an empty result.
    /// </summary>
    /// <remarks>
    /// An empty array would let "no data" masquerade as "no interests", and the agent would
    /// silently produce a worse answer with no signal that anything had been withheld. A
    /// refusal is a fact the model can read, print and reason about.
    /// </remarks>
    /// <param name="code">A frozen machine code from <see cref="ToolRefusalCodes"/>.</param>
    /// <param name="reason">One sentence, addressed to the model, saying what to do instead.</param>
    public static string Refused(string code, string reason) =>
        JsonSerializer.Serialize(new { status = "refused", code, reason }, Options);

    /// <summary>
    /// The per-run tool-call budget is spent.
    /// </summary>
    /// <remarks>
    /// The instruction deliberately names the one tool that still works. <c>PresentRecommendation</c>
    /// is the ANSWER channel, not a spend, so it is counted but never refused — see
    /// <see cref="ToolCallBudget"/>. Refusing it too would turn "out of budget" into "presented
    /// nothing", which reads on a report as a clean abstention: an instrument that scores silence
    /// as a pass is broken, not cautious.
    /// </remarks>
    /// <param name="used">Calls consumed so far in this run.</param>
    /// <param name="cap">The cap for this run.</param>
    public static string BudgetExhausted(int used, int cap) =>
        JsonSerializer.Serialize(new
        {
            status = "budget_exhausted",
            code = ToolRefusalCodes.BudgetExhausted,
            used,
            cap,
            reason = "Tool-call budget for this turn is spent. Answer with what you already have, or abstain. "
                   + "PresentRecommendation still works: present only products you have already verified with "
                   + "GetProductDetails, or say you cannot recommend anything yet and ask a clarifying question."
        }, Options);

    /// <summary>
    /// The per-turn DISTINCT-search cap is spent (the per-turn tool-call budget, <see cref="ToolCallBudget.DistinctSearchCap"/>).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="BudgetExhausted"/> on purpose: the model is told which cap it hit,
    /// and that re-running a search it already ran costs nothing — so the right move is to read
    /// what it already has, not to rephrase the same need a ninth way.
    /// </remarks>
    /// <param name="distinctSearches">Distinct searches run this turn.</param>
    /// <param name="cap">The distinct-search cap.</param>
    public static string SearchCapExhausted(int distinctSearches, int cap) =>
        JsonSerializer.Serialize(new
        {
            status = "budget_exhausted",
            code = ToolRefusalCodes.SearchCapExhausted,
            distinctSearches,
            cap,
            reason = "The distinct-search cap for this turn is spent. Lookups (GetProductDetails, GetReviewDigest, "
                   + "CheckStockAndPrice, BrowseCategory) still work, and repeating a search you already ran this turn "
                   + "is answered from memory at no cost. Verify and present what your searches already returned, or "
                   + "say plainly what you could not find."
        }, Options);

    /// <summary>
    /// A refusable call whose arguments were IDENTICAL to one already answered this turn. The
    /// work is not re-run; the model is pointed back at the answer it already has.
    /// </summary>
    /// <remarks>
    /// In a measured C-09 live turn, repeated searches consumed twelve of twenty-four refusable
    /// slots and roughly half of 148 seconds despite representing about three distinct queries. The replay
    /// carries the product ids the first answer carried so the model can recover without a second
    /// round trip, and it consumes no budget (<see cref="ToolCallBudget"/>).
    /// </remarks>
    /// <param name="toolName">The tool.</param>
    /// <param name="firstReturnedAsCall">1-based position of the call that first answered these arguments.</param>
    /// <param name="productIds">The product ids that first answer carried.</param>
    public static string AlreadyReturned(string toolName, int firstReturnedAsCall, IReadOnlyList<string> productIds) =>
        JsonSerializer.Serialize(new
        {
            status = "already_returned_this_turn",
            code = ToolRefusalCodes.AlreadyReturned,
            tool = toolName,
            firstReturnedAsCall,
            productIds,
            reason = "You already called this tool with exactly these arguments in this turn, and the answer has not "
                   + "changed — the catalogue does not move within a turn. Re-read that result above; the product ids it "
                   + "returned are listed here so you need not run it again. This replay consumed no budget."
        }, Options);

    /// <summary>
    /// A tool call that was accepted but is defective in a way the model can still fix.
    /// </summary>
    /// <remarks>
    /// Used only by <c>PresentRecommendation</c>. The arguments are recorded VERBATIM before
    /// this is returned — the tool never silently repairs them, because the eval reads the
    /// arguments and a repaired argument is a defect that can never fire.
    /// </remarks>
    /// <param name="payload">The accepted payload, carrying <c>status = "accepted_with_warning"</c>.</param>
    public static string AcceptedWithWarning<T>(T payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>
    /// Parses the refusal wire contract consumed by agents, event adapters, and offline evals.
    /// A normal/invalid payload is not a refusal; a refusal without a non-empty machine code or
    /// reason is malformed and is rejected rather than treated as an empty success.
    /// </summary>
    public static bool TryParseRefusal(string json, out ToolRefusalPayload? refusal) =>
        TryParseRefusal((object?)json, out refusal);

    /// <summary>
    /// Parses a refusal from the value that crosses the MEAI function-result boundary. In
    /// particular, <see cref="Microsoft.Extensions.AI.AIFunction.InvokeAsync"/> represents a
    /// tool's returned JSON string as a <see cref="JsonElement"/> whose value is itself that JSON
    /// string. Accepting <see cref="object"/> here handles both boundary representations without a
    /// type-specific detector that misses wrapped results.
    /// </summary>
    public static bool TryParseRefusal(object? result, out ToolRefusalPayload? refusal)
    {
        refusal = null;
        if (!TryReadObject(result, out var root) ||
            !root.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            !string.Equals(status.GetString(), "refused", StringComparison.Ordinal) ||
            !root.TryGetProperty("code", out var codeElement) ||
            codeElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("reason", out var reasonElement) ||
            reasonElement.ValueKind != JsonValueKind.String)
            return false;

        var code = codeElement.GetString();
        var reason = reasonElement.GetString();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(reason)) return false;
        refusal = new ToolRefusalPayload(code, reason, root.EnumerateObject().Count());
        return true;
    }

    /// <summary>
    /// Reads the exact machine-code field from any supported tool-result shape. This deliberately
    /// does not require <c>status == refused</c>: budget exhaustion and replay payloads also carry
    /// values from <see cref="ToolRefusalCodes"/>, and callers must distinguish those values by
    /// the declared <c>code</c>, never by a substring elsewhere in the payload.
    /// </summary>
    public static bool TryReadDeclaredCode(object? result, out ToolResultCodePayload? payload)
    {
        payload = null;
        if (!TryReadObject(result, out var root) ||
            !root.TryGetProperty("code", out var codeElement) ||
            codeElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(codeElement.GetString()))
            return false;

        var status = root.TryGetProperty("status", out var statusElement) &&
                     statusElement.ValueKind == JsonValueKind.String
            ? statusElement.GetString()
            : null;
        payload = new ToolResultCodePayload(
            codeElement.GetString()!, status, root.EnumerateObject().Count());
        return true;
    }

    /// <summary>True only when the result's declared <c>code</c> equals <paramref name="code"/>.</summary>
    public static bool HasDeclaredCode(object? result, string code) =>
        !string.IsNullOrWhiteSpace(code) &&
        TryReadDeclaredCode(result, out var payload) &&
        string.Equals(payload!.Code, code, StringComparison.Ordinal);

    private static bool TryReadObject(object? value, out JsonElement root)
    {
        root = default;
        if (value is null) return false;

        try
        {
            JsonElement element;
            switch (value)
            {
                case JsonDocument document:
                    element = document.RootElement;
                    break;
                case JsonElement jsonElement:
                    element = jsonElement;
                    break;
                case string text when !string.IsNullOrWhiteSpace(text):
                    using (var document = JsonDocument.Parse(text))
                        element = document.RootElement.Clone();
                    break;
                default:
                    element = JsonSerializer.SerializeToElement(value, value.GetType(), Options);
                    break;
            }

            // MEAI's live AIFunction shape is a JsonElement STRING containing the JSON returned by
            // the Task<string> tool. Unwrap it exactly once per layer, with a small hard bound so a
            // hostile recursively-quoted value cannot turn parsing into unbounded work.
            for (var depth = 0; depth < 3 && element.ValueKind == JsonValueKind.String; depth++)
            {
                var text = element.GetString();
                if (string.IsNullOrWhiteSpace(text)) return false;
                using var nested = JsonDocument.Parse(text);
                element = nested.RootElement.Clone();
            }

            if (element.ValueKind != JsonValueKind.Object) return false;
            root = element.Clone();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>A validated refusal read from the tool JSON boundary.</summary>
public sealed record ToolRefusalPayload(string Code, string Reason, int FieldCount);

/// <summary>An exact declared machine code read from a typed tool-result boundary.</summary>
public sealed record ToolResultCodePayload(string Code, string? Status, int FieldCount);

/// <summary>
/// The frozen machine codes a tool refusal can carry. Constants rather than an enum because
/// they are serialised into tool JSON and asserted on by name in the eval lane.
/// </summary>
public static class ToolRefusalCodes
{
    /// <summary>The customer id does not exist. No silent fallback to a default persona.</summary>
    public const string UnknownUser = "unknown_user";

    /// <summary>The product id does not exist in the catalogue — the phantom-SKU signal (defect class D1).</summary>
    public const string UnknownProduct = "unknown_product";

    /// <summary>The category path does not exist in the category tree.</summary>
    public const string UnknownCategory = "unknown_category";

    /// <summary>
    /// FDPIC one-click opt-out is on for this customer. Behavioural history is not
    /// available — enforced in the tool, not requested in the prompt.
    /// </summary>
    public const string PersonalizationDisabled = "personalization_disabled";

    /// <summary>The per-run tool-call budget is spent.</summary>
    public const string BudgetExhausted = "budget_exhausted";

    /// <summary>The per-turn DISTINCT-search cap is spent. Lookups and replays still work.</summary>
    public const string SearchCapExhausted = "search_cap_exhausted";

    /// <summary>
    /// The call repeated arguments already answered this turn and was answered from memory,
    /// consuming nothing. Not a refusal — the model has the answer already.
    /// </summary>
    public const string AlreadyReturned = "already_returned_this_turn";

    /// <summary>
    /// No retriever was bound before the run. The composition root did not call
    /// <see cref="GalaxusTools.Bind"/>; the semantic leg is unavailable and says so loudly
    /// rather than returning zero hits, which would read as "nothing matched".
    /// </summary>
    public const string RetrieverUnbound = "retriever_unbound";

    /// <summary>A required argument was empty or unusable.</summary>
    public const string InvalidArgument = "invalid_argument";
}
