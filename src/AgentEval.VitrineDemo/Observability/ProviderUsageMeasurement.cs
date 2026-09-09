// SPDX-License-Identifier: MIT

using System.Globalization;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Observability;

/// <summary>The four honest states of a provider-token measurement.</summary>
public enum ProviderUsageStatus
{
    /// <summary>Calls happened, but the provider supplied no usable token count.</summary>
    Missing,

    /// <summary>The provider (or a zero-call execution arm) established an exact zero.</summary>
    MeasuredZero,

    /// <summary>The provider supplied a complete, non-zero total.</summary>
    Measured,

    /// <summary>Some tokens were reported, but at least one part or call remains unknown.</summary>
    LowerBound,
}

/// <summary>
/// One typed interpretation shared by the Demo01 result, Demo02 state, UI, and evaluations.
/// Nullable counts stay nullable: the projection never manufactures zero from an absence.
/// </summary>
public sealed record ProviderUsageMeasurement(
    ProviderUsageStatus Status,
    int? ModelCalls,
    long? PromptTokens,
    long? CompletionTokens,
    long? TotalTokens,
    string Unit = "tokens")
{
    /// <summary>Validates the public projection without turning an absent amount into zero.</summary>
    public bool IsConsistent() => !string.IsNullOrWhiteSpace(Unit) && Status switch
    {
        ProviderUsageStatus.Missing => TotalTokens is null,
        ProviderUsageStatus.MeasuredZero => TotalTokens == 0,
        ProviderUsageStatus.Measured => TotalTokens > 0,
        ProviderUsageStatus.LowerBound => TotalTokens >= 0,
        _ => false,
    };

    /// <summary>Projects Demo01's provider usage together with its independently counted calls.</summary>
    public static ProviderUsageMeasurement FromDemo01(int? modelCalls, UsageDetails? usage)
    {
        ValidateNonNegative(modelCalls, nameof(modelCalls));
        var prompt = usage?.InputTokenCount;
        var completion = usage?.OutputTokenCount;
        var providerTotal = usage?.TotalTokenCount;
        ValidateNonNegative(prompt, nameof(UsageDetails.InputTokenCount));
        ValidateNonNegative(completion, nameof(UsageDetails.OutputTokenCount));
        ValidateNonNegative(providerTotal, nameof(UsageDetails.TotalTokenCount));

        if (modelCalls == 0)
        {
            if ((prompt ?? 0) != 0 || (completion ?? 0) != 0 || (providerTotal ?? 0) != 0)
                throw new InvalidOperationException("A zero-call run cannot carry non-zero provider usage.");
            return new(ProviderUsageStatus.MeasuredZero, 0, 0, 0, 0);
        }

        if (providerTotal is { } exactTotal)
        {
            var knownParts = (prompt ?? 0) + (completion ?? 0);
            if (exactTotal < knownParts || (prompt is not null && completion is not null && exactTotal != knownParts))
                throw new InvalidOperationException("Provider total usage conflicts with its reported token parts.");
            return new(exactTotal == 0 ? ProviderUsageStatus.MeasuredZero : ProviderUsageStatus.Measured,
                modelCalls, prompt, completion, exactTotal);
        }

        if (prompt is null && completion is null)
            return new(ProviderUsageStatus.Missing, modelCalls, null, null, null);

        var observed = (prompt ?? 0) + (completion ?? 0);
        if (prompt is null || completion is null || modelCalls is null)
            return new(ProviderUsageStatus.LowerBound, modelCalls, prompt, completion, observed);

        return new(observed == 0 ? ProviderUsageStatus.MeasuredZero : ProviderUsageStatus.Measured,
            modelCalls, prompt, completion, observed);
    }

    /// <summary>Projects Demo02's immutable provider-usage ledger.</summary>
    public static ProviderUsageMeasurement FromDemo02(ChatSpendSnapshot spend)
    {
        ValidateNonNegative(spend.CallsWithUsage, nameof(spend.CallsWithUsage));
        ValidateNonNegative(spend.CallsWithPartialUsage, nameof(spend.CallsWithPartialUsage));
        ValidateNonNegative(spend.CallsWithoutUsage, nameof(spend.CallsWithoutUsage));
        ValidateNonNegative(spend.PromptTokens, nameof(spend.PromptTokens));
        ValidateNonNegative(spend.CompletionTokens, nameof(spend.CompletionTokens));

        if (spend.Calls == 0)
        {
            if (spend.TotalTokens != 0)
                throw new InvalidOperationException("A zero-call spend snapshot cannot carry non-zero tokens.");
            return new(ProviderUsageStatus.MeasuredZero, 0, 0, 0, 0);
        }

        if (spend.CallsWithUsage == 0 && spend.CallsWithPartialUsage == 0)
        {
            if (spend.TotalTokens != 0)
                throw new InvalidOperationException("An unreported spend snapshot cannot carry reported tokens.");
            return new(ProviderUsageStatus.Missing, spend.Calls, null, null, null);
        }

        var total = spend.TotalTokens;
        if (spend.CallsWithoutUsage > 0 || spend.CallsWithPartialUsage > 0)
            return new(ProviderUsageStatus.LowerBound, spend.Calls,
                spend.PromptTokens, spend.CompletionTokens, total);

        return new(total == 0 ? ProviderUsageStatus.MeasuredZero : ProviderUsageStatus.Measured,
            spend.Calls, spend.PromptTokens, spend.CompletionTokens, total);
    }

    /// <summary>Compact invariant-culture text for UI and console summaries.</summary>
    public string ToDisplayString() => Status switch
    {
        ProviderUsageStatus.Missing => "NOT MEASURED",
        ProviderUsageStatus.LowerBound => string.Create(CultureInfo.InvariantCulture,
            $"≥{TotalTokens:N0} {Unit} (lower bound)"),
        _ => string.Create(CultureInfo.InvariantCulture,
            $"{TotalTokens:N0} {Unit} (measured)"),
    };

    private static void ValidateNonNegative(long? value, string name)
    {
        if (value < 0) throw new InvalidOperationException($"{name} cannot be negative.");
    }
}
