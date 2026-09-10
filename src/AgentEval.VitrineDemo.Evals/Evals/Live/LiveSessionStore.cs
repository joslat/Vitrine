// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.VitrineDemo.Evals.Live;

internal static class LiveSessionStore
{
    // 1.4 adds typed, sanitized workflow provider-stage attempt/recovery evidence. The public
    // exporter continues to accept historical 1.3 receipts under their original strict shape.
    private const string SchemaVersion = "1.4";
    private static readonly SemaphoreSlim IndexGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static LiveEvalPersistence Paths(string workspaceRoot, string sessionId)
    {
        var sessionsRoot = Path.Combine(workspaceRoot, "live-sessions");
        var sessionDirectory = Path.Combine(sessionsRoot, sessionId);
        return new(
            Path.GetFullPath(workspaceRoot),
            sessionDirectory,
            Path.Combine(sessionDirectory, "outcome.json"),
            Path.Combine(sessionsRoot, "index.json"));
    }

    internal static async Task WriteAsync(LiveEvalResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(result.Persistence.SessionDirectory);
        var document = new LiveSessionDocument(
            SchemaVersion,
            result.Plan,
            result.TerminalStatus,
            result.ExitCode,
            result.SessionId,
            result.StartedAtUtc,
            result.CompletedAtUtc,
            result.Workload,
            result.Plan == VitrineEvaluationPlan.LiveEval06SafetyProbes ? null : result.PassThreshold,
            result.Scenarios,
            result.Configuration,
            result.Runs,
            result.Trials,
            result.Arms,
            result.ScenarioAcceptances,
            result.Comparisons,
            result.Failures,
            result.Safety);
        await WriteAtomicallyAsync(result.Persistence.OutcomePath, document, cancellationToken)
            .ConfigureAwait(false);

        await IndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireIndexLockAsync(
                result.Persistence.IndexPath + ".lock", cancellationToken).ConfigureAwait(false);
            var index = await ReadIndexAsync(result.Persistence.IndexPath, cancellationToken).ConfigureAwait(false);
            var entry = new LiveSessionIndexEntry(
                result.SessionId,
                result.Plan,
                result.TerminalStatus,
                result.ExitCode,
                result.Plan == VitrineEvaluationPlan.LiveEval06SafetyProbes ? null : result.PassThreshold,
                result.StartedAtUtc,
                result.CompletedAtUtc,
                result.Trials.Count,
                result.Trials.Count(static trial => trial.Measurement == AgentEval.Evals.Meta.MeasurementState.Measured),
                result.Trials.Count(static trial => trial.Passed == true),
                Path.Combine(result.SessionId, "outcome.json").Replace('\\', '/'));
            var entries = index.Sessions
                .Where(item => !string.Equals(item.SessionId, result.SessionId, StringComparison.Ordinal))
                .Append(entry)
                .OrderByDescending(static item => item.StartedAtUtc)
                .ToArray();
            await WriteAtomicallyAsync(result.Persistence.IndexPath,
                    new LiveSessionIndex(SchemaVersion, Array.AsReadOnly(entries)), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            IndexGate.Release();
        }
    }

    private static async Task<LiveSessionIndex> ReadIndexAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new(SchemaVersion, []);
        try
        {
            await using var stream = File.OpenRead(path);
            var index = await JsonSerializer.DeserializeAsync<LiveSessionIndex>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (index?.Sessions is null)
                throw new InvalidDataException("The live-session index has no sessions collection.");
            return index;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The live-session index is corrupt; it was preserved and not overwritten.", exception);
        }
    }

    private static async Task<FileStream> AcquireIndexLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < 199)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException("The live-session index lock could not be acquired.");
    }

    private static async Task WriteAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record LiveSessionDocument(
        string SchemaVersion,
        VitrineEvaluationPlan Plan,
        LiveEvalTerminalStatus TerminalStatus,
        int ExitCode,
        string SessionId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        LiveEvalWorkload Workload,
        double? PassThreshold,
        IReadOnlyList<LiveScenarioDefinition> Scenarios,
        LiveEvalConfiguration Configuration,
        IReadOnlyList<LiveEvalRunReference> Runs,
        IReadOnlyList<LiveTrialEvidence> Trials,
        IReadOnlyList<LiveArmSummary> Arms,
        IReadOnlyList<LiveScenarioAcceptanceDecision> ScenarioAcceptances,
        IReadOnlyList<LiveCheckComparison> Comparisons,
        IReadOnlyList<LiveEvalFailure> Failures,
        LiveSafetySummary? Safety);

    private sealed record LiveSessionIndex(
        string SchemaVersion,
        IReadOnlyList<LiveSessionIndexEntry> Sessions);

    private sealed record LiveSessionIndexEntry(
        string SessionId,
        VitrineEvaluationPlan Plan,
        LiveEvalTerminalStatus TerminalStatus,
        int ExitCode,
        double? PassThreshold,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        int Trials,
        int Measured,
        int Passed,
        string Outcome);
}
