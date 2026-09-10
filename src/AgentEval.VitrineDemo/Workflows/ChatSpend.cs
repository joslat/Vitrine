// SPDX-License-Identifier: MIT
// Copyright (c) 2026 José Luis Latorre Millas

using System.Globalization;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>
/// What the CHAT lane spent, accumulated from the provider's own usage blocks and from nothing
/// else.
/// </summary>
/// <remarks>
/// <para>
/// <c>DiscoveryModelCall.RunAsync</c> records
/// <c>Microsoft.Agents.AI.AgentResponse.Usage</c> from the same provider response that carries the
/// text. This meter never estimates from replayed workflow text, whose content and tokenizer do
/// not represent the provider's bill.
/// </para>
/// <para>
/// <b>The one rule this type is built around: an ABSENCE is not a ZERO.</b> A call whose response
/// carried no usage block contributes nothing to the token totals and increments
/// <see cref="CallsWithoutUsage"/> instead, and <see cref="Describe"/> renders the two cases in
/// different words. A run of three calls that all reported <c>0</c> tokens and a run of three calls
/// that reported nothing at all must never print the same line — the first is a measurement, the
/// second is an unknown.
/// </para>
/// <para>
/// The rule applies to each half of the usage block. A response with 1,234 prompt tokens and no
/// completion count is <see cref="CallsWithPartialUsage"/>: the reported half is summed, the absent
/// half remains unknown, the total is a LOWER BOUND, and <see cref="Complete"/> stays false so it
/// cannot be published as a complete measured total.
/// </para>
/// <para>
/// <b>It never estimates, and it never sees our own text.</b> There is no path in this type from a
/// string to a token count. That is deliberate: a meter that counts our own tokens is measuring our
/// tokenizer, not the provider's bill. If a later change wants a fallback, it belongs somewhere
/// that is not called a measurement.
/// </para>
/// <para>
/// <b>No currency here, on purpose.</b> This project has no AgentEval dependency (see the csproj)
/// and therefore no rate table, so it cannot turn tokens into money without inventing a rate.
/// Consumers that DO have a declared rate source apply it themselves and name it; the one that does
/// not says the money is UNKNOWN. Tokens are the measurement either way.
/// </para>
/// </remarks>
public sealed class ChatSpend
{
    private readonly object _gate = new();

    /// <summary>Calls whose response carried a usage block that was read.</summary>
    public int CallsWithUsage { get; private set; }

    /// <summary>
    /// Calls that were made and whose usage is UNKNOWN — no usage block on the response, or no
    /// response at all (a timeout, a transport failure). Those calls may well have been billed.
    /// </summary>
    public int CallsWithoutUsage { get; private set; }

    /// <summary>
    /// Calls whose usage block carried ONE of the two counts and not the other — a prompt count with
    /// no completion count, or the reverse.
    /// </summary>
    /// <remarks>
    /// The reported half is still summed because the provider measured it. The missing half adds
    /// nothing, but prevents <see cref="Complete"/> from becoming true; absence is never recast as
    /// a measured zero.
    /// </remarks>
    public int CallsWithPartialUsage { get; private set; }

    /// <summary>Every chat call this meter was told about.</summary>
    public int Calls => CallsWithUsage + CallsWithPartialUsage + CallsWithoutUsage;

    /// <summary>
    /// Prompt tokens the PROVIDER reported, over the calls that reported one. A call counted in
    /// <see cref="CallsWithPartialUsage"/> contributes the half it reported and nothing for the half
    /// it did not.
    /// </summary>
    public long PromptTokens { get; private set; }

    /// <summary>Completion tokens the PROVIDER reported, on the same terms as <see cref="PromptTokens"/>.</summary>
    public long CompletionTokens { get; private set; }

    /// <summary>The reported total.</summary>
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>
    /// True when every call this meter saw reported BOTH counts, so the totals are complete rather
    /// than a lower bound. False for a lane that made no call at all — nothing is a complete
    /// measurement of nothing — and false when any call reported only half of its usage.
    /// </summary>
    public bool Complete => CallsWithUsage > 0 && CallsWithoutUsage == 0 && CallsWithPartialUsage == 0;

    /// <summary>
    /// Records one chat call from the provider's usage block.
    /// </summary>
    /// <param name="usage">
    /// The response's usage, or null. Null — and a block carrying neither an input nor an output
    /// count — is recorded as an ABSENCE, never as a zero. A block carrying exactly ONE of the two
    /// counts is recorded as a PARTIAL reading: the reported half is summed, the missing half adds
    /// nothing, and the meter stops calling itself <see cref="Complete"/>.
    /// </param>
    public void Record(UsageDetails? usage)
    {
        lock (_gate)
        {
            long? input  = usage?.InputTokenCount;
            long? output = usage?.OutputTokenCount;

            if (input is null && output is null)
            {
                CallsWithoutUsage++;
                return;
            }

            if (input is null || output is null)
            {
                CallsWithPartialUsage++;
            }
            else
            {
                CallsWithUsage++;
            }

            PromptTokens     += input  ?? 0;
            CompletionTokens += output ?? 0;
        }
    }

    /// <summary>
    /// Records a call that was made and produced no response to read usage from — a timeout, or a
    /// transport error.
    /// </summary>
    /// <remarks>
    /// This is the honest half of "nothing came back". The loop's own <c>ModelCalls</c> counter has
    /// already counted the attempt; silently leaving it out of the meter would make an abandoned —
    /// and quite possibly billed — call read as if it had never happened.
    /// </remarks>
    public void RecordNoResponse()
    {
        lock (_gate) CallsWithoutUsage++;
    }

    /// <summary>
    /// The lane's spend, in lines, ready to print. Empty when no chat call was made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three renderings, and they are deliberately not interchangeable: <b>nothing to say</b> (no
    /// call), <b>UNKNOWN</b> (calls made, no usage block anywhere), and <b>a figure</b> — which
    /// carries its own LOWER BOUND clause when some but not all of the calls reported. A measured
    /// zero lands in the third form and reads as a measurement, which is what it is.
    /// </para>
    /// <para>
    /// Numeric output uses invariant culture so logs are machine-comparable across locales. A
    /// locale-specific thousands separator changes the serialized observation and makes downstream
    /// extraction depend on the machine that rendered it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Describe()
    {
        lock (_gate)
        {
            if (Calls == 0) return [];

            if (CallsWithUsage == 0 && CallsWithPartialUsage == 0)
            {
                return
                [
                    $"{Calls} model call(s) · usage NOT REPORTED by the provider for any of them.",
                    "The spend on this lane is UNKNOWN. That is not the same as zero, and it is not "
                        + "estimated from our own text — see ChatSpend's remarks.",
                ];
            }

            var lines = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture,
                    $"{Calls} model call(s) · {PromptTokens:N0} prompt + {CompletionTokens:N0} completion "
                  + $"= {TotalTokens:N0} token(s), read from the provider's own usage blocks."),
            };

            if (CallsWithoutUsage > 0)
            {
                lines.Add($"⚠ LOWER BOUND: {CallsWithoutUsage} of {Calls} call(s) returned no usage block. Those "
                        + "calls' tokens are UNKNOWN, not zero, and are absent from the total above.");
            }

            if (CallsWithPartialUsage > 0)
            {
                lines.Add($"⚠ LOWER BOUND: {CallsWithPartialUsage} of {Calls} call(s) reported only ONE of the two "
                        + "counts. The half the provider gave is in the total above; the other half is UNKNOWN and "
                        + "is NOT counted as zero.");
            }

            return lines;
        }
    }

    /// <summary>An immutable copy, for a consumer that keeps one per repetition.</summary>
    public ChatSpendSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ChatSpendSnapshot(
                CallsWithUsage, CallsWithPartialUsage, CallsWithoutUsage, PromptTokens, CompletionTokens);
        }
    }
}

/// <summary>One run's chat spend, frozen.</summary>
/// <param name="CallsWithUsage">Calls whose response carried BOTH token counts.</param>
/// <param name="CallsWithPartialUsage">Calls whose response carried exactly one of the two counts.</param>
/// <param name="CallsWithoutUsage">Calls whose token cost is UNKNOWN.</param>
/// <param name="PromptTokens">Provider-reported prompt tokens.</param>
/// <param name="CompletionTokens">Provider-reported completion tokens.</param>
public readonly record struct ChatSpendSnapshot(
    int CallsWithUsage,
    int CallsWithPartialUsage,
    int CallsWithoutUsage,
    long PromptTokens,
    long CompletionTokens)
{
    /// <summary>Every chat call in the run.</summary>
    public int Calls => CallsWithUsage + CallsWithPartialUsage + CallsWithoutUsage;

    /// <summary>The reported total.</summary>
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>True when every call in this run reported BOTH counts.</summary>
    public bool Complete => CallsWithUsage > 0 && CallsWithoutUsage == 0 && CallsWithPartialUsage == 0;
}
