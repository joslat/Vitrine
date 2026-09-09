// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>Four states that keep absence, bypass, clean, and unsafe distinct.</summary>
public enum CustomerAnswerScreenStatus
{
    /// <summary>No customer answer existed, so nothing was screened.</summary>
    Missing,

    /// <summary>An answer existed but did not cross the screen.</summary>
    Unscreened,

    /// <summary>The answer crossed the screen and contains no unraised sensitive term.</summary>
    ScreenedClean,

    /// <summary>The answer crossed the screen and contains at least one unraised sensitive term.</summary>
    ScreenedUnsafe,
}

/// <summary>
/// Typed customer-answer safety result. The original answer is retained unchanged for audit; a
/// caller must use <see cref="RequireDeliverableText"/> before exposing it to a customer.
/// </summary>
public sealed record CustomerAnswerScreenResult(
    CustomerAnswerScreenStatus Status,
    string? Answer,
    bool WasScreened,
    IReadOnlyList<string> Leaks,
    IReadOnlyList<string> CustomerRaisedExemptTerms)
{
    /// <summary>True only for an answer that was actually screened and found clean.</summary>
    public bool IsSafe => Status == CustomerAnswerScreenStatus.ScreenedClean;

    /// <summary>
    /// Returns the byte-for-byte answer when safe, preserves the historical empty result when no
    /// artifact exists, and fails closed for an unsafe or explicitly unscreened artifact.
    /// </summary>
    public string RequireDeliverableText() => Status switch
    {
        CustomerAnswerScreenStatus.ScreenedClean => Answer!,
        CustomerAnswerScreenStatus.Missing => string.Empty,
        CustomerAnswerScreenStatus.ScreenedUnsafe => throw new InvalidOperationException(
            $"The customer-facing answer contains {Leaks.Count} unraised sensitive term(s)."),
        CustomerAnswerScreenStatus.Unscreened => throw new InvalidOperationException(
            "The customer-facing answer did not cross CustomerAnswerScreen."),
        _ => throw new InvalidOperationException("Unknown customer-answer screen status."),
    };
}

/// <summary>
/// Screens the final customer-visible prose with the same token rules and per-term customer-raised
/// exemption used by the recommendation guardrail. It reports; it never rewrites the answer.
/// </summary>
public static class CustomerAnswerScreen
{
    /// <summary>Runs the production answer screen.</summary>
    public static CustomerAnswerScreenResult Screen(string? answer, string? customerUtterance)
    {
        if (answer is null)
            return new(CustomerAnswerScreenStatus.Missing, null, false, [], []);

        var raised = SensitiveInferenceBlocklist.TermsMentionedIn(customerUtterance);
        var answerTerms = SensitiveInferenceBlocklist.AllSpecialCategoryTerms(answer);
        var leaks = SensitiveInferenceBlocklist.UnraisedSpecialCategoryTerms(answer, raised);
        var leakedSet = leaks.ToHashSet(StringComparer.Ordinal);
        var exempt = answerTerms
            .Where(term => raised.Contains(term) && !leakedSet.Contains(term))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new(
            leaks.Count == 0
                ? CustomerAnswerScreenStatus.ScreenedClean
                : CustomerAnswerScreenStatus.ScreenedUnsafe,
            answer,
            true,
            Array.AsReadOnly(leaks.ToArray()),
            Array.AsReadOnly(exempt));
    }

    /// <summary>
    /// Records an explicit bypass for diagnostics and ablation. This is never used by a shipped
    /// customer-facing path; keeping it typed prevents "not run" from rendering as zero leaks.
    /// </summary>
    public static CustomerAnswerScreenResult Unscreened(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new(CustomerAnswerScreenStatus.Unscreened, answer, false, [], []);
    }
}
