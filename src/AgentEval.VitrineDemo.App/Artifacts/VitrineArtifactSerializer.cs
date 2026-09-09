// SPDX-License-Identifier: MIT

using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.App.Artifacts;

public static class VitrineArtifactSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static VitrineRunArtifact Create(VitrineRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (ContainsConfiguredSecret(outcome))
            throw new InvalidDataException("Artifact contains disallowed secret-bearing content.");
        var request = outcome.Request;
        var recommendation = outcome.Recommendation;
        var workflow = outcome.Workflow;
        var evaluation = outcome.Evaluation;
        var liveEvaluation = outcome.LiveEvaluation;
        var result = new VitrineResultSnapshot(
            Status: ResultStatus(outcome),
            FailureKind: SafeOrNull(outcome.FailureKind),
            ProcessEquivalentExitCode: outcome.ProcessEquivalentExitCode,
            ModelCalls: recommendation?.ModelCalls ?? workflow?.State.ModelCalls
                ?? evaluation?.Execution?.TotalModelCalls,
            ToolCalls: recommendation?.ToolCallsUsed,
            Presented: recommendation?.Presented.Count ?? workflow?.State.Presented.Count,
            Survived: recommendation?.Outcome?.Cleaned.PresentedCount
                ?? workflow?.State.Screened?.Outcome.Cleaned.PresentedCount,
            WorkflowLooped: workflow?.Looped,
            WorkflowSuperSteps: workflow?.SuperSteps,
            WorkflowStopReason: SafeOrNull(workflow?.State.StopReason.ToString()),
            Gates: evaluation?.Gates.Select(gate => new VitrineGateSnapshot(
                Safe(gate.Name), gate.Passed, gate.Score, ProjectFloor(gate.ChanceFloor), Safe(gate.Evidence), gate.Outcome,
                Provenance(gate.AgentEval))).ToArray() ?? [],
            Controls: evaluation?.Controls.Select(control => new VitrineControlSnapshot(
                Safe(control.Id), Safe(control.Name), Safe(control.Category), Safe(control.Target),
                Safe(control.ObservationProducer), Safe(control.Evaluator), control.ScopeClass, Safe(control.Tranche),
                control.HealthyOutcome, control.BrokenOutcome, control.RestoredOutcome, control.BrokenWentRed,
                control.RestoredWentGreen, Safe(control.Evidence))).ToArray() ?? [],
            Demo01: recommendation is null ? null : CreateDemo01(recommendation),
            Demo02: workflow is null ? null : CreateDemo02(workflow),
            OfflineBenchmark: Benchmark(evaluation?.OfflineBenchmark),
            EvaluationExecution: Execution(evaluation?.Execution),
            LiveEvaluation: LiveEvaluation(liveEvaluation, request.LiveScenarioId));
        var draft = Freeze(new VitrineRunArtifact(
            VitrineRunArtifact.CurrentSchemaVersion,
            outcome.RunId,
            DateTimeOffset.UtcNow,
            request.Mode,
            Safe(request.UserId),
            request.Mode is ViewModels.VitrineRunMode.Demo01 or ViewModels.VitrineRunMode.Demo02
                ? request.Demo01Arm.ToString()
                : liveEvaluation?.Plan.ToString()
                  ?? evaluation?.Execution?.Profile.ToString()
                  ?? "EvaluationPlanNotRecorded",
            !request.PersonalizationEnabled,
            SanitizeGraph(outcome.Graph),
            outcome.Events.Select(SanitizeEvent).ToArray(),
            result,
            string.Empty));
        return draft with { IntegritySha256 = ComputeIntegrity(draft) };
    }

    public static string Serialize(VitrineRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        EnsureSafeIntegrityInput(artifact);
        var frozen = Freeze(artifact);
        if (!Verify(frozen)) throw new InvalidDataException("Artifact integrity verification failed.");
        return JsonSerializer.Serialize(frozen, Options);
    }

    public static VitrineRunArtifact Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var artifact = JsonSerializer.Deserialize<VitrineRunArtifact>(json, Options)
            ?? throw new InvalidDataException("The JSON did not contain a VITRINE artifact.");
        if (artifact.SchemaVersion is < VitrineRunArtifact.MinimumSupportedSchemaVersion
            or > VitrineRunArtifact.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported VITRINE artifact schema {artifact.SchemaVersion}.");
        EnsureSafeIntegrityInput(artifact);
        var frozen = Freeze(artifact);
        if (!Verify(frozen)) throw new InvalidDataException("Artifact integrity verification failed.");
        return frozen;
    }

    public static bool Verify(VitrineRunArtifact artifact)
    {
        if (artifact is null || !IsIntegrityHash(artifact.IntegritySha256) || ContainsUnsafeString(artifact)) return false;
        var expected = ComputeIntegrity(artifact with { IntegritySha256 = string.Empty });
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(artifact.IntegritySha256));
    }

    private static string ComputeIntegrity(VitrineRunArtifact artifact)
    {
        EnsureSafeIntegrityInput(artifact);
        var bytes = Encoding.UTF8.GetBytes(SerializeIntegrityPayloadCore(artifact));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    internal static string SerializeIntegrityPayload(VitrineRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        EnsureSafeIntegrityInput(artifact);
        return SerializeIntegrityPayloadCore(artifact);
    }

    private static string SerializeIntegrityPayloadCore(VitrineRunArtifact artifact) =>
        JsonSerializer.Serialize(artifact with { IntegritySha256 = string.Empty }, Options);

    private static void EnsureSafeIntegrityInput(VitrineRunArtifact artifact)
    {
        if (ContainsUnsafeString(artifact))
            throw new InvalidDataException("Artifact contains disallowed secret-bearing content.");
    }

    /// <summary>
    /// Walks the strongly typed artifact graph without serializing or hashing any caller value.
    /// This deliberately follows every public string-bearing branch, including future additive
    /// snapshot fields, before integrity serialization is allowed to begin.
    /// </summary>
    private static bool ContainsUnsafeString(object value)
    {
        var configuredApiKey = NonBlankEnvironmentValue("AZURE_OPENAI_API_KEY");
        var configuredEndpoint = NonBlankEnvironmentValue("AZURE_OPENAI_ENDPOINT");
        return ContainsString(value, text =>
            configuredApiKey is not null && text.Contains(configuredApiKey, StringComparison.Ordinal)
            || configuredEndpoint is not null && text.Contains(configuredEndpoint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(PayloadPreviewPolicy.Sanitize(text), text, StringComparison.Ordinal));
    }

    private static bool ContainsConfiguredSecret(object value)
    {
        var configuredApiKey = NonBlankEnvironmentValue("AZURE_OPENAI_API_KEY");
        var configuredEndpoint = NonBlankEnvironmentValue("AZURE_OPENAI_ENDPOINT");
        return ContainsString(value, text =>
            configuredApiKey is not null && text.Contains(configuredApiKey, StringComparison.Ordinal)
            || configuredEndpoint is not null && text.Contains(configuredEndpoint, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsString(object value, Func<string, bool> predicate)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Visit(value);

        bool Visit(object? current)
        {
            if (current is null) return false;
            if (current is string text) return predicate(text);

            var type = current.GetType();
            if (type.IsValueType) return false;
            if (!visited.Add(current)) return false;

            if (current is IEnumerable items)
            {
                foreach (var item in items)
                    if (Visit(item)) return true;
                return false;
            }

            if (type.Assembly != typeof(VitrineRunArtifact).Assembly) return false;
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length == 0 && Visit(property.GetValue(current))) return true;
            }

            return false;
        }
    }

    private static string? NonBlankEnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool IsIntegrityHash(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static VitrineGraphSnapshot SanitizeGraph(VitrineGraphSnapshot graph) => new(
        Safe(graph.Name),
        graph.Source,
        graph.Nodes.Select(node => new VitrineGraphNode(Safe(node.Id), Safe(node.Label), Safe(node.Kind),
            SafeOrNull(node.Description), node.IsEntry, node.IsExit)).ToArray(),
        graph.Edges.Select(edge => new VitrineGraphEdge(Safe(edge.Id), Safe(edge.SourceId), Safe(edge.TargetId),
            Safe(edge.Label), edge.IsConditional, edge.IsLoopBack)).ToArray(),
        graph.RuntimeSourceId);

    private static VitrineEvent SanitizeEvent(VitrineEvent item) => item with
    {
        Kind = Safe(item.Kind),
        SourceId = Safe(item.SourceId),
        TargetId = Safe(item.TargetId),
        OperationId = SafeOrNull(item.OperationId),
        Title = Safe(item.Title),
        Detail = Safe(item.Detail),
        SanitizedPayload = SafeOrNull(item.SanitizedPayload),
    };

    private static string Safe(string? value) => PayloadPreviewPolicy.Sanitize(value);
    private static string? SafeOrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : Safe(value);

    internal static VitrineRunArtifact Freeze(VitrineRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Graph is null || artifact.Result is null || artifact.Events is null
            || artifact.Graph.Nodes is null || artifact.Graph.Edges is null
            || artifact.Result.Gates is null || artifact.Result.Controls is null
            || artifact.Result.OfflineBenchmark is { Cases: null }
            || artifact.Result.OfflineBenchmark is { Checks: null }
            || artifact.Result.LiveEvaluation is { Scenarios: null }
            || artifact.Result.LiveEvaluation is { Runs: null }
            || artifact.Result.LiveEvaluation is { Trials: null }
            || artifact.Result.LiveEvaluation is { Arms: null }
            || artifact.Result.LiveEvaluation is { Comparisons: null }
            || artifact.Result.LiveEvaluation is { Failures: null }
            || HasInvalidArtifactShape(artifact)
            || HasInvalidLiveShape(artifact.Result.LiveEvaluation, artifact.SchemaVersion))
        {
            throw new InvalidDataException("The artifact is missing a required collection or contains invalid required data.");
        }

        var graph = artifact.Graph with
        {
            Nodes = Array.AsReadOnly(artifact.Graph.Nodes.ToArray()),
            Edges = Array.AsReadOnly(artifact.Graph.Edges.ToArray()),
        };
        var result = artifact.Result with
        {
            Gates = Array.AsReadOnly(artifact.Result.Gates.Select(gate => gate with
            {
                AgentEval = Freeze(gate.AgentEval),
            }).ToArray()),
            Controls = Array.AsReadOnly(artifact.Result.Controls.ToArray()),
            Demo01 = Freeze(artifact.Result.Demo01),
            Demo02 = Freeze(artifact.Result.Demo02),
            OfflineBenchmark = Freeze(artifact.Result.OfflineBenchmark),
            LiveEvaluation = Freeze(artifact.Result.LiveEvaluation),
        };
        return artifact with
        {
            Graph = graph,
            Events = Array.AsReadOnly(artifact.Events.ToArray()),
            Result = result,
        };
    }

    private static VitrineDemo01Snapshot CreateDemo01(Galaxus.RecommendationAgent.Demos.RecommendationRunResult run)
    {
        var outcome = run.Outcome;
        return new VitrineDemo01Snapshot(
            Safe(RecommendationArtifactComposer.Compose(run)),
            SafeOrNull(run.RetrieverName),
            SafeOrNull(run.BudgetSummary),
            run.RegisteredToolNames.Select(Safe).ToArray(),
            outcome is null ? [] : Recommendations(outcome),
            outcome is null ? null : Ledger(outcome.Ledger));
    }

    private static VitrineDemo02Snapshot CreateDemo02(DiscoveryRunResult run)
    {
        var state = run.State;
        return new VitrineDemo02Snapshot(
            Safe(state.FinalAnswer),
            state.CoverageApproved,
            state.IsPartialAnswer,
            state.DiscoveryRound,
            state.MaxRounds,
            state.SearchesRun,
            state.ModelCalls,
            state.SelectionWasDeterministic,
            run.ExecutorIds.Select(Safe).ToArray(),
            run.RoutesTaken.Select(Safe).ToArray(),
            state.Interests.Select(interest => new VitrineWorkflowInterestSnapshot(
                Safe(interest.Id), Safe(interest.Label), Safe(interest.Kind.ToString()),
                Safe(interest.Origin.ToString()), interest.Confidence)).ToArray(),
            state.Presented.Select(item => Recommendation(
                item.ProductId, "shown", item.WhyThis, item.Evidence, item.Confidence,
                state.Screened?.Outcome.VerifiedPrices)).ToArray(),
            state.OpenGaps.Select(gap => Safe($"{gap.InterestId}: {gap.WhyUncovered} → {gap.NextQuery}")).ToArray(),
            state.DroppedSkus.Select(drop => Safe($"{drop.ProductId}: {drop.Reason}")).ToArray(),
            state.DegradedNotes.Select(Safe).ToArray(),
            run.ExecutorFailures.Select(Safe).ToArray(),
            state.Screened is null ? null : Ledger(state.Screened.Outcome.Ledger));
    }

    private static IReadOnlyList<VitrineRecommendationSnapshot> Recommendations(GuardrailOutcome outcome) =>
        outcome.Cleaned.Recommendations.Select(item => Recommendation(item, "recommendation", outcome.VerifiedPrices))
            .Concat(outcome.Cleaned.AlsoConsider.Select(item => Recommendation(item, "also-consider", outcome.VerifiedPrices)))
            .ToArray();

    private static VitrineRecommendationSnapshot Recommendation(
        RecommendationDto item,
        string tray,
        IReadOnlyDictionary<string, PriceStockSnapshot> prices) =>
        Recommendation(item.ProductId, tray, item.WhyThis, item.Evidence.Citation.ToString(), item.Confidence, prices);

    private static VitrineRecommendationSnapshot Recommendation(
        string sku,
        string tray,
        string reason,
        string evidence,
        double confidence,
        IReadOnlyDictionary<string, PriceStockSnapshot>? prices)
    {
        _ = Catalogue.Default.TryGet(sku, out var product);
        PriceStockSnapshot? price = null;
        prices?.TryGetValue(sku, out price);
        return new VitrineRecommendationSnapshot(
            Safe(sku), Safe(product?.Name ?? sku), Safe(tray), Safe(reason), Safe(evidence), confidence,
            price?.PriceChf, price?.StockUnits, price?.DeliveryEstimateDays);
    }

    private static VitrineLedgerSnapshot Ledger(GuardrailLedger ledger) => new(
        ledger.InputCount,
        ledger.OutputCount,
        ledger.DroppedCount,
        ledger.DemotedCount,
        ledger.NotedCount,
        ledger.GiftExcluded,
        ledger.PriceStockRequested,
        ledger.PriceStockVerified,
        ledger.ToolCallCap > 0 ? ledger.ToolCallsUsed : null,
        ledger.ToolCallCap > 0 ? ledger.ToolCallCap : null,
        ledger.Entries.Select(entry => new VitrineLedgerEntrySnapshot(
            Safe(entry.Stage.ToString()), Safe(entry.Action.ToString()), Safe(entry.Reason),
            Safe(entry.Subject), Safe(entry.Detail))).ToArray());

    private static VitrineDemo01Snapshot? Freeze(VitrineDemo01Snapshot? snapshot) => snapshot is null ? null : snapshot with
    {
        RegisteredTools = Array.AsReadOnly(snapshot.RegisteredTools.ToArray()),
        Recommendations = Array.AsReadOnly(snapshot.Recommendations.ToArray()),
        Ledger = Freeze(snapshot.Ledger),
    };

    private static VitrineDemo02Snapshot? Freeze(VitrineDemo02Snapshot? snapshot) => snapshot is null ? null : snapshot with
    {
        ExecutorIds = Array.AsReadOnly(snapshot.ExecutorIds.ToArray()),
        RoutesTaken = Array.AsReadOnly(snapshot.RoutesTaken.ToArray()),
        Interests = Array.AsReadOnly(snapshot.Interests.ToArray()),
        Recommendations = Array.AsReadOnly(snapshot.Recommendations.ToArray()),
        OpenGaps = Array.AsReadOnly(snapshot.OpenGaps.ToArray()),
        DroppedSkus = Array.AsReadOnly(snapshot.DroppedSkus.ToArray()),
        DegradedNotes = Array.AsReadOnly(snapshot.DegradedNotes.ToArray()),
        ExecutorFailures = Array.AsReadOnly(snapshot.ExecutorFailures.ToArray()),
        Ledger = Freeze(snapshot.Ledger),
    };

    private static VitrineLedgerSnapshot? Freeze(VitrineLedgerSnapshot? snapshot) => snapshot is null ? null : snapshot with
    {
        Entries = Array.AsReadOnly(snapshot.Entries.ToArray()),
    };

    private static VitrineOfflineBenchmarkSnapshot? Freeze(VitrineOfflineBenchmarkSnapshot? snapshot) =>
        snapshot is null ? null : snapshot with
        {
            Cases = Array.AsReadOnly(snapshot.Cases.ToArray()),
            Checks = Array.AsReadOnly(snapshot.Checks.ToArray()),
            Arms = Array.AsReadOnly(snapshot.Arms.Select(arm => arm with
            {
                Checks = Array.AsReadOnly(arm.Checks.ToArray()),
            }).ToArray()),
            Runs = Array.AsReadOnly(snapshot.Runs.ToArray()),
            ReferenceComparisons = Array.AsReadOnly(snapshot.ReferenceComparisons.ToArray()),
        };

    private static VitrineLiveEvaluationSnapshot? Freeze(VitrineLiveEvaluationSnapshot? snapshot) =>
        snapshot is null ? null : snapshot with
        {
            Scenarios = Array.AsReadOnly(snapshot.Scenarios.Select(scenario => scenario with
            {
                Criteria = Array.AsReadOnly(scenario.Criteria.ToArray()),
                GroundTruthFacts = Array.AsReadOnly(scenario.GroundTruthFacts.ToArray()),
                AgentToolExpectation = scenario.AgentToolExpectation is null ? null
                    : scenario.AgentToolExpectation with
                    {
                        RequiredTools = Array.AsReadOnly(scenario.AgentToolExpectation.RequiredTools.ToArray()),
                        ForbiddenTools = Array.AsReadOnly(scenario.AgentToolExpectation.ForbiddenTools.ToArray()),
                        ForbiddenPresentedSkus = Array.AsReadOnly(
                            scenario.AgentToolExpectation.ForbiddenPresentedSkus.ToArray()),
                    },
            }).ToArray()),
            Runs = Array.AsReadOnly(snapshot.Runs.ToArray()),
            Trials = Array.AsReadOnly(snapshot.Trials.Select(trial => trial with
            {
                Tools = trial.Tools with
                {
                    ToolNames = Array.AsReadOnly(trial.Tools.ToolNames.ToArray()),
                    Calls = Array.AsReadOnly(trial.Tools.Calls.Select(call => call with
                    {
                        Arguments = Array.AsReadOnly(call.Arguments.ToArray()),
                    }).ToArray()),
                },
                Workflow = trial.Workflow is null ? null : trial.Workflow with
                {
                    Executors = Array.AsReadOnly(trial.Workflow.Executors.ToArray()),
                    Routes = Array.AsReadOnly(trial.Workflow.Routes.ToArray()),
                    DegradationKinds = Array.AsReadOnly(trial.Workflow.DegradationKinds.ToArray()),
                },
                Checks = Array.AsReadOnly(trial.Checks.ToArray()),
                Criteria = Array.AsReadOnly(trial.Criteria.ToArray()),
            }).ToArray()),
            Arms = Array.AsReadOnly(snapshot.Arms.Select(arm => arm with
            {
                Checks = Array.AsReadOnly(arm.Checks.ToArray()),
            }).ToArray()),
            Comparisons = Array.AsReadOnly(snapshot.Comparisons.ToArray()),
            Configuration = snapshot.Configuration is null ? null : snapshot.Configuration with
            {
                Subjects = Array.AsReadOnly(snapshot.Configuration.Subjects.ToArray()),
                Safety = snapshot.Configuration.Safety is null ? null : snapshot.Configuration.Safety with
                {
                    Attacks = Array.AsReadOnly(snapshot.Configuration.Safety.Attacks.ToArray()),
                },
            },
            Failures = Array.AsReadOnly(snapshot.Failures.ToArray()),
            Safety = snapshot.Safety is null ? null : snapshot.Safety with
            {
                Attacks = Array.AsReadOnly(snapshot.Safety.Attacks.ToArray()),
                Probes = Array.AsReadOnly(snapshot.Safety.Probes.ToArray()),
            },
        };

    private static VitrineAgentEvalProvenanceSnapshot? Freeze(VitrineAgentEvalProvenanceSnapshot? snapshot) =>
        snapshot is null ? null : snapshot with
        {
            Observations = Array.AsReadOnly(snapshot.Observations.ToArray()),
        };

    private static VitrineAgentEvalProvenanceSnapshot? Provenance(AgentEvalProvenance? provenance) =>
        provenance is null ? null : new(
            Safe(provenance.IntegrationId),
            Safe(provenance.LibraryType),
            Safe(provenance.Mechanism),
            Safe(provenance.Subject),
            Safe(provenance.SnapshotPolicy),
            Safe(provenance.ObservationProducer),
            Safe(provenance.AcceptanceEvaluator),
            provenance.SubjectSuppliedPassFail,
            provenance.Observations.Select(observation => new VitrineAgentEvalObservationSnapshot(
                Safe(observation.Id),
                Safe(observation.Outcome),
                observation.Score,
                SafeOrNull(observation.Surface),
                observation.SampleCount)).ToArray());

    private static VitrineOfflineBenchmarkSnapshot? Benchmark(VitrineOfflineBenchmarkResult? benchmark) =>
        benchmark is null ? null : new(
            Safe(benchmark.DefinitionKey),
            Safe(benchmark.DefinitionVersion),
            Safe(benchmark.ArmId),
            Safe(benchmark.RunId),
            Safe(benchmark.WorkspaceRoot),
            Safe(benchmark.RunDirectory),
            benchmark.Cases.Select(item => new VitrineBenchmarkCaseSnapshot(
                Safe(item.Id), Safe(item.Name))).ToArray(),
            benchmark.Checks.Select(BenchmarkCheck).ToArray())
        {
            Repetitions = benchmark.Repetitions,
            Arms = benchmark.Arms.Select(arm => new VitrineBenchmarkArmSnapshot(
                Safe(arm.ArmId), Safe(arm.SubjectKind), Safe(arm.SubjectName),
                arm.Checks.Select(BenchmarkCheck).ToArray())).ToArray(),
            Runs = benchmark.Runs.Select(run => new VitrineBenchmarkRunSnapshot(
                Safe(run.ArmId), run.Repetition, Safe(run.SubjectKind), Safe(run.SubjectName),
                Safe(run.RunId), Safe(run.RunDirectory))).ToArray(),
            ReferenceComparisons = benchmark.ReferenceComparisons.Select(row =>
                new VitrineBenchmarkReferenceSnapshot(
                    Safe(row.CheckKey), Safe(row.ReferenceArmId), Safe(row.ChallengerArmId),
                    row.Wins, row.Losses, row.Ties, row.EffectiveN, row.PValue,
                    row.MinimumAttainableP, row.MeanDelta,
                    new(row.Census.Measured, row.Census.NotApplicable, row.Census.NotMeasured,
                        row.Census.Total),
                    row.Cases, row.TotalRepObservations, Safe(row.RepCollapse),
                    row.UnderpoweredByConstruction)).ToArray(),
        };

    private static VitrineBenchmarkCheckSnapshot BenchmarkCheck(VitrineBenchmarkCheckFact item) => new(
        Safe(item.CheckKey),
        new(
            Safe(item.Floor.Kind), Safe(item.Floor.State), item.Floor.Value,
            item.Floor.ComparisonBar, item.Floor.IntervalHigh, item.Floor.Draws,
            item.Floor.PoolSize, Safe(item.Floor.Derivation)),
        new(item.Census.Measured, item.Census.NotApplicable, item.Census.NotMeasured,
            item.Census.Total),
        item.Successes, item.Trials, item.PValue, item.MinimumAttainableP,
        item.AboveFloor, item.UnderpoweredByConstruction);

    private static VitrineChanceFloorSnapshot? ProjectFloor(ChanceFloor? floor) =>
        floor is null ? null : new(
            Safe(floor.Kind), floor.State.ToString(),
            floor.State == FloorState.Derived ? floor.Value : null,
            floor.State == FloorState.Derived ? floor.ComparisonBar : null,
            floor.IntervalHigh is { } high && double.IsFinite(high) ? high : null,
            floor.Draws, floor.PoolSize, Safe(floor.Derivation));

    private static VitrineEvaluationExecutionSnapshot? Execution(EvaluationExecutionProvenance? execution) =>
        execution is null ? null : new(
            execution.Profile,
            Safe(execution.DemoScope),
            Safe(execution.SubjectEngine),
            Safe(execution.EvaluatorEngine),
            SafeOrNull(execution.DeploymentName),
            execution.Demo01SubjectModelCalls,
            execution.Demo02SubjectModelCalls,
            execution.JudgeModelCalls,
            execution.TotalModelCalls,
            execution.Demo01SubjectTokens,
            execution.Demo02SubjectTokens,
            execution.JudgeTokens,
            execution.EstimatedCostUsd,
            execution.UsesExternalModels);

    private static VitrineLiveEvaluationSnapshot? LiveEvaluation(
        LiveEvalResult? result,
        string? requestedScenarioId)
    {
        if (result is null) return null;
        var descriptor = VitrineEvaluationPlans.Require(result.Plan);
        var scenarioIds = result.Trials.Select(static trial => trial.ScenarioId)
            .Append(requestedScenarioId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        // A null request selection means "all authored scenarios". A partial/cancelled session
        // may contain only the trials completed so far, so trial IDs cannot be used to shrink the
        // scenario plan carried by the replay artifact.
        var registryScenarios = string.IsNullOrWhiteSpace(requestedScenarioId)
            && result.Workload.ScenarioCount == LiveUseCaseScenarios.All.Count
                ? LiveUseCaseScenarios.All
                : scenarioIds.Select(id => LiveUseCaseScenarios.All.FirstOrDefault(item =>
                        string.Equals(item.Id, id, StringComparison.Ordinal)))
                    .Where(static scenario => scenario is not null)
                    .Cast<LiveUseCaseScenario>()
                    .ToArray();
        var scenarios = result.Scenarios.Count > 0
            ? result.Scenarios
            : registryScenarios.Select(static scenario => scenario.ToDefinition()).ToArray();
        return new(
            Safe(result.Plan.ToString()),
            Safe(descriptor.Label),
            Safe(descriptor.Description),
            Safe(result.TerminalStatus.ToString()),
            result.ExitCode,
            Safe(result.SessionId),
            result.StartedAtUtc,
            result.CompletedAtUtc,
            new VitrineLiveWorkloadSnapshot(
                result.Workload.ScenarioCount,
                result.Workload.ArmCount,
                result.Workload.Repetitions,
                result.Workload.PlannedSubjectCalls,
                result.Workload.PlannedJudgeEvaluations)
            {
                SafetyAttackCount = result.Workload.SafetyAttackCount,
                PlannedSafetyProbes = result.Workload.PlannedSafetyProbes,
                MaximumSafetyModelCalls = result.Workload.MaximumSafetyModelCalls,
            },
            result.Plan == VitrineEvaluationPlan.LiveEval06SafetyProbes
                ? null
                : RequirePassThreshold(result.PassThreshold),
            scenarios.Select(scenario => new VitrineLiveScenarioSnapshot(
                Safe(scenario.Id),
                Safe(scenario.PersonaId),
                Safe(scenario.Title),
                Safe(scenario.Description),
                Safe(scenario.Query),
                Safe(scenario.ExpectedBehavior),
                scenario.Criteria.Select(criterion => new VitrineLiveCriterionDefinitionSnapshot(
                    Safe(criterion.Id), Safe(criterion.Text))).ToArray())
            {
                GroundTruthFacts = scenario.GroundTruthFacts.Select(Safe).ToArray(),
                AgentToolExpectation = new(
                    scenario.AgentToolExpectation.RequiresAbstention,
                    scenario.AgentToolExpectation.RequiredTools.Select(Safe).ToArray(),
                    scenario.AgentToolExpectation.ForbiddenTools.Select(Safe).ToArray(),
                    scenario.AgentToolExpectation.ForbiddenPresentedSkus.Select(Safe).ToArray()),
            }).ToArray(),
            result.Runs.Select(run => new VitrineLiveRunSnapshot(
                Safe(run.RunId), Safe(run.ArmId), run.Repetition, Safe(run.RelativeDirectory))).ToArray(),
            result.Trials.Select(trial => new VitrineLiveTrialSnapshot(
                Safe(trial.ScenarioId),
                Safe(trial.PersonaId),
                Safe(trial.ArmId),
                Safe(trial.Architecture.ToString()),
                trial.Repetition,
                Safe(trial.Measurement.ToString()),
                trial.Passed,
                Safe(trial.SubjectStatus.ToString()),
                Safe(trial.ResponsePreview),
                new VitrineLiveToolSnapshot(
                    trial.Tools.JournalObserved,
                    trial.Tools.ToolNames.Select(Safe).ToArray(),
                    trial.Tools.Executed,
                    trial.Tools.Completed,
                    trial.Tools.Failed,
                    trial.Tools.Cancelled,
                    trial.Tools.UnknownNameCount)
                {
                    Calls = trial.Tools.Calls.Select(call => new VitrineLiveToolCallSnapshot(
                        Safe(call.OperationId),
                        Safe(call.ToolName),
                        Safe(call.Status.ToString()),
                        call.Arguments.Select(argument => new VitrineLiveToolArgumentSnapshot(
                            Safe(argument.Name), Safe(argument.Value))).ToArray())).ToArray(),
                    UnreconciledCount = trial.Tools.UnreconciledCount,
                },
                trial.Workflow is null ? null : new VitrineLiveWorkflowSnapshot(
                    trial.Workflow.Executors.Select(executor => new VitrineLiveExecutorSnapshot(
                        Safe(executor.ExecutorId), executor.ExecutionCount)).ToArray(),
                    trial.Workflow.Routes.Select(Safe).ToArray(),
                    trial.Workflow.DiscoveryRounds,
                    trial.Workflow.MaximumRounds,
                    trial.Workflow.SuperSteps,
                    Safe(trial.Workflow.StopReason),
                    trial.Workflow.Looped,
                    trial.Workflow.FailureCount,
                    trial.Workflow.DegradationCount,
                    trial.Workflow.DegradationKinds.Select(Safe).ToArray(),
                    trial.Workflow.UnknownExecutorCount,
                    trial.Workflow.UnknownRouteCount),
                trial.Checks.Select(check => new VitrineLiveCheckFactSnapshot(
                    Safe(check.Key), Safe(check.Name), Safe(check.Measurement.ToString()),
                    Finite(check.Score), check.Passed)).ToArray(),
                trial.Criteria.Select(criterion => new VitrineLiveCriterionVerdictSnapshot(
                    Safe(criterion.Id), Safe(criterion.Measurement.ToString()), criterion.Met,
                    Safe(criterion.Explanation))).ToArray(),
                LiveUsage(trial.SubjectUsage),
                LiveUsage(trial.JudgeUsage))
            {
                Failure = Failure(trial.Failure),
            }).ToArray(),
            result.Arms.Select(arm => new VitrineLiveArmSnapshot(
                Safe(arm.ArmId),
                Safe(arm.Architecture.ToString()),
                arm.Repetitions,
                arm.Checks.Select(check => new VitrineLiveCheckSummarySnapshot(
                    Safe(check.Key),
                    Safe(check.Name),
                    LiveCensus(check.Census),
                    new(
                        Safe(check.Reliability.Measurement.ToString()),
                        check.Reliability.Successes,
                        check.Reliability.Total,
                        Finite(check.Reliability.Estimate),
                        Finite(check.Reliability.Lower),
                        Finite(check.Reliability.Upper)))).ToArray())).ToArray(),
            result.Comparisons.Select(comparison => new VitrineLiveComparisonSnapshot(
                Safe(comparison.CheckKey),
                Safe(comparison.CheckName),
                Safe(comparison.ReferenceArm),
                Safe(comparison.ChallengerArm),
                comparison.Wins,
                comparison.Losses,
                comparison.Ties,
                comparison.EffectiveN,
                Finite(comparison.PValue),
                Finite(comparison.MinimumAttainableP),
                Finite(comparison.MeanDelta),
                comparison.Cases,
                comparison.TotalRepObservations,
                Finite(comparison.MeanRepetitionsPerCase),
                Safe(comparison.RepCollapse),
                LiveCensus(comparison.Census),
                comparison.UnderpoweredByConstruction)
            {
                Undecidable = comparison.Undecidable,
            }).ToArray(),
            new(
                Safe(result.Persistence.WorkspaceRoot),
                Safe(result.Persistence.SessionDirectory),
                Safe(result.Persistence.OutcomePath),
                Safe(result.Persistence.IndexPath)))
        {
            Configuration = Configuration(result.Configuration),
            Failures = result.Failures.Select(Failure).Where(static failure => failure is not null)
                .Cast<VitrineLiveFailureSnapshot>().ToArray(),
            Safety = Safety(result.Safety),
        };
    }

    private static VitrineLiveConfigurationSnapshot Configuration(LiveEvalConfiguration configuration) => new(
        Safe(configuration.DefinitionKey),
        Safe(configuration.DefinitionVersion),
        Safe(configuration.JudgeModelId),
        Safe(configuration.JudgePromptId),
        Safe(configuration.JudgeRubricHash),
        configuration.SubjectMaxOutputTokens,
        configuration.JudgeMaxOutputTokens,
        configuration.ResponsePreviewCharacters,
        configuration.Subjects.Select(subject => new VitrineLiveSubjectProvenanceSnapshot(
            Safe(subject.ArmId), Safe(subject.Architecture.ToString()), Safe(subject.ModelId),
            Safe(subject.JudgeSubjectRelation.ToString()))).ToArray())
    {
        Safety = configuration.Safety is null ? null : new(
            configuration.Safety.Attacks.Select(Safe).ToArray(),
            configuration.Safety.MaxProbesPerAttack,
            configuration.Safety.TimeoutSeconds,
            configuration.Safety.MaxTargetModelCallsPerProbe,
            configuration.Safety.MaximumModelCalls,
            Safe(configuration.Safety.JudgeMode),
            configuration.Safety.EvidencePersisted),
    };

    private static VitrineLiveFailureSnapshot? Failure(LiveEvalFailure? failure) =>
        failure is null ? null : new(
            Safe(failure.Code.ToString()), Safe(failure.Detail), SafeOrNull(failure.ScenarioId),
            SafeOrNull(failure.ArmId), failure.Repetition, SafeOrNull(failure.CheckKey));

    private static VitrineLiveSafetySnapshot? Safety(LiveSafetySummary? safety) => safety is null ? null : new(
        Safe(safety.Target), Safe(safety.Measurement.ToString()), safety.Passed,
        safety.Total, safety.Resisted, safety.Compromised, safety.Inconclusive, safety.Errored,
        safety.Truncated, safety.Skipped,
        safety.Attacks.Select(attack => new VitrineLiveSafetyAttackSnapshot(
            Safe(attack.Attack), Safe(attack.OwaspId), attack.Total, attack.Resisted,
            attack.Compromised, attack.Inconclusive, attack.Errored)).ToArray(),
        safety.Probes.Select(probe => new VitrineLiveSafetyProbeSnapshot(
            Safe(probe.Attack), Safe(probe.ProbeId), Safe(probe.Outcome.ToString()),
            Safe(probe.ErrorKind.ToString()), Safe(probe.Severity), Safe(probe.Fidelity),
            Safe(probe.Technique), Safe(probe.Diagnostic),
            probe.Failure is null ? null : new VitrineLiveSafetyProbeFailureSnapshot(
                Safe(probe.Failure.Stage), Safe(probe.Failure.Code.ToString()),
                Safe(probe.Failure.Detail)))).ToArray(),
        LiveUsage(safety.SubjectUsage), LiveUsage(safety.JudgeUsage));

    private static VitrineLiveUsageSnapshot LiveUsage(LiveUsageEvidence usage) => new(
        Safe(usage.Status),
        usage.ModelCalls,
        usage.InputTokens,
        usage.OutputTokens,
        usage.TotalTokens,
        Finite(usage.EstimatedCostUsd));

    private static VitrineLiveCensusSnapshot LiveCensus(LiveObservationCensus census) => new(
        census.Measured,
        census.NotApplicable,
        census.NotMeasured,
        census.Total);

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number)
        ? number
        : null;

    private static double RequirePassThreshold(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1
            ? value
            : throw new InvalidDataException("The live evaluation quality pass threshold must be between zero and one.");

    private static bool HasInvalidLiveShape(
        VitrineLiveEvaluationSnapshot? live,
        int schemaVersion)
    {
        if (live is null) return false;
        var safetyPlan = string.Equals(live.Plan, nameof(VitrineEvaluationPlan.LiveEval06SafetyProbes),
            StringComparison.Ordinal);
        if (live.Workload is null || live.Persistence is null
            || safetyPlan && live.PassThreshold is not null
            || !safetyPlan && live.PassThreshold is null
            || live.PassThreshold is { } threshold
                && (!double.IsFinite(threshold) || threshold is < 0 or > 1)) return true;
        if (live.Scenarios.Any(static scenario => scenario is null
            || scenario.Criteria is null
            || scenario.Criteria.Any(static criterion => criterion is null)
            || scenario.GroundTruthFacts is null
            || scenario.GroundTruthFacts.Any(static fact => fact is null)
            || scenario.AgentToolExpectation is { RequiredTools: null }
            || scenario.AgentToolExpectation is { ForbiddenTools: null }
            || scenario.AgentToolExpectation is { ForbiddenPresentedSkus: null }
            || scenario.AgentToolExpectation is { RequiredTools: { } required }
                && required.Any(static tool => tool is null)
            || scenario.AgentToolExpectation is { ForbiddenTools: { } forbidden }
                && forbidden.Any(static tool => tool is null)
            || scenario.AgentToolExpectation is { ForbiddenPresentedSkus: { } forbiddenSkus }
                && forbiddenSkus.Any(static sku => sku is null))) return true;
        if (live.Runs.Any(static run => run is null)) return true;
        if (live.Trials.Any(static trial => trial is null
            || trial.Tools is null
            || trial.Tools.ToolNames is null
            || trial.Tools.ToolNames.Any(static name => name is null)
            || trial.Tools.Calls is null
            || trial.Tools.Calls.Any(static call => call is null
                || call.Arguments is null
                || call.Arguments.Any(static argument => argument is null))
            || trial.Checks is null
            || trial.Checks.Any(static check => check is null)
            || trial.Criteria is null
            || trial.Criteria.Any(static criterion => criterion is null)
            || trial.SubjectUsage is null
            || trial.JudgeUsage is null
            || trial.Workflow is { Executors: null }
            || trial.Workflow is { Executors: { } executors }
                && executors.Any(static executor => executor is null)
            || trial.Workflow is { Routes: null }
            || trial.Workflow is { Routes: { } routes }
                && routes.Any(static route => route is null)
            || trial.Workflow is { DegradationKinds: null })) return true;
        if (live.Trials.Any(static trial => trial?.Workflow is { DegradationKinds: { } kinds }
            && kinds.Any(static kind => kind is null))) return true;
        if (live.Arms.Any(static arm => arm is null
            || arm.Checks is null
            || arm.Checks.Any(static check => check is null
                || check.Census is null
                || check.Reliability is null))) return true;
        if (live.Comparisons.Any(static comparison => comparison is null || comparison.Census is null)) return true;
        if (live.Failures.Any(static failure => failure is null)) return true;
        if (live.Configuration is { Subjects: null }
            || live.Configuration is { Subjects: { } subjects }
                && subjects.Any(static subject => subject is null)
            || live.Configuration?.Safety is { Attacks: null }
            || live.Configuration?.Safety is { Attacks: { } attacks }
                && attacks.Any(static attack => attack is null)) return true;
        return HasInvalidSafetyShape(live, safetyPlan, schemaVersion);
    }

    private static bool HasInvalidSafetyShape(
        VitrineLiveEvaluationSnapshot live,
        bool safetyPlan,
        int schemaVersion)
    {
        if (!safetyPlan) return live.Safety is not null;
        if (live.Safety is null) return false;
        var safety = live.Safety;
        if (safety.Attacks is null || safety.Probes is null || safety.SubjectUsage is null ||
            safety.JudgeUsage is null || safety.Attacks.Any(static attack => attack is null) ||
            safety.Probes.Any(static probe => probe is null)) return true;

        var schemaRequiresTypedDiagnostics = schemaVersion >= 8;
        foreach (var probe in safety.Probes)
        {
            if (!LiveSafetyProbeDiagnostics.IsKnown(probe.Outcome, probe.ErrorKind)) return true;
            var hasError = !string.Equals(
                probe.ErrorKind, nameof(LiveSafetyProbeErrorKind.None), StringComparison.Ordinal);
            if (hasError && !string.Equals(
                    probe.Outcome, nameof(LiveSafetyProbeOutcome.Inconclusive), StringComparison.Ordinal)) return true;
            if (schemaRequiresTypedDiagnostics && probe.Diagnostic is null ||
                probe.Diagnostic is not null && !LiveSafetyProbeDiagnostics.IsCanonical(
                    probe.Diagnostic, probe.Outcome, probe.ErrorKind)) return true;
            if (schemaRequiresTypedDiagnostics && hasError != (probe.Failure is not null)) return true;
            if (!hasError && probe.Failure is not null) return true;
            if (probe.Failure is { } failure &&
                (!string.Equals(failure.Stage, "probe-execution", StringComparison.Ordinal) ||
                 !string.Equals(failure.Code, probe.ErrorKind, StringComparison.Ordinal) ||
                 !LiveSafetyProbeDiagnostics.IsCanonical(
                     failure.Detail, probe.Outcome, probe.ErrorKind))) return true;
        }

        var resisted = safety.Probes.Count(static probe =>
            string.Equals(probe.Outcome, nameof(LiveSafetyProbeOutcome.Resisted), StringComparison.Ordinal));
        var compromised = safety.Probes.Count(static probe =>
            string.Equals(probe.Outcome, nameof(LiveSafetyProbeOutcome.Compromised), StringComparison.Ordinal));
        var inconclusive = safety.Probes.Count(static probe =>
            string.Equals(probe.Outcome, nameof(LiveSafetyProbeOutcome.Inconclusive), StringComparison.Ordinal));
        var errored = safety.Probes.Count(static probe =>
            !string.Equals(probe.ErrorKind, nameof(LiveSafetyProbeErrorKind.None), StringComparison.Ordinal));
        if (safety.Total < 0 || safety.Resisted < 0 || safety.Compromised < 0 ||
            safety.Inconclusive < 0 || safety.Errored < 0 || safety.Skipped < 0 ||
            safety.Total != safety.Probes.Count ||
            safety.Total != safety.Resisted + safety.Compromised + safety.Inconclusive ||
            safety.Errored > safety.Inconclusive ||
            (safety.Resisted, safety.Compromised, safety.Inconclusive, safety.Errored) !=
            (resisted, compromised, inconclusive, errored)) return true;

        if (safety.Attacks.Select(static attack => attack.Attack)
            .Distinct(StringComparer.Ordinal).Count() != safety.Attacks.Count) return true;
        foreach (var attack in safety.Attacks)
        {
            if (attack.Total < 0 || attack.Resisted < 0 || attack.Compromised < 0 ||
                attack.Inconclusive < 0 || attack.Errored < 0 ||
                attack.Total != attack.Resisted + attack.Compromised + attack.Inconclusive ||
                attack.Errored > attack.Inconclusive) return true;
            var probes = safety.Probes.Where(probe =>
                string.Equals(probe.Attack, attack.Attack, StringComparison.Ordinal)).ToArray();
            if (attack.Total != probes.Length ||
                attack.Resisted != probes.Count(static probe =>
                    string.Equals(probe.Outcome, nameof(LiveSafetyProbeOutcome.Resisted), StringComparison.Ordinal)) ||
                attack.Compromised != probes.Count(static probe =>
                    string.Equals(probe.Outcome, nameof(LiveSafetyProbeOutcome.Compromised), StringComparison.Ordinal)) ||
                attack.Inconclusive != probes.Count(static probe =>
                    string.Equals(probe.Outcome, nameof(LiveSafetyProbeOutcome.Inconclusive), StringComparison.Ordinal)) ||
                attack.Errored != probes.Count(static probe =>
                    !string.Equals(probe.ErrorKind, nameof(LiveSafetyProbeErrorKind.None), StringComparison.Ordinal))) return true;
        }

        return safety.Attacks.Sum(static attack => attack.Total) != safety.Total ||
               safety.Attacks.Sum(static attack => attack.Resisted) != safety.Resisted ||
               safety.Attacks.Sum(static attack => attack.Compromised) != safety.Compromised ||
               safety.Attacks.Sum(static attack => attack.Inconclusive) != safety.Inconclusive ||
               safety.Attacks.Sum(static attack => attack.Errored) != safety.Errored;
    }

    private static bool HasInvalidArtifactShape(VitrineRunArtifact artifact)
    {
        if (artifact.Graph.Nodes.Any(static node => node is null)
            || artifact.Graph.Edges.Any(static edge => edge is null)
            || artifact.Events.Any(static item => item is null)
            || artifact.Result.Gates.Any(static gate => gate is null
                || gate.AgentEval is { Observations: null }
                || gate.AgentEval is { Observations: { } observations }
                    && observations.Any(static observation => observation is null))
            || artifact.Result.Controls.Any(static control => control is null)) return true;

        var demo01 = artifact.Result.Demo01;
        if (demo01 is not null && (demo01.RegisteredTools is null
            || demo01.RegisteredTools.Any(static item => item is null)
            || demo01.Recommendations is null
            || demo01.Recommendations.Any(static item => item is null)
            || HasInvalidLedger(demo01.Ledger))) return true;

        var demo02 = artifact.Result.Demo02;
        if (demo02 is not null && (demo02.ExecutorIds is null
            || demo02.ExecutorIds.Any(static item => item is null)
            || demo02.RoutesTaken is null
            || demo02.RoutesTaken.Any(static item => item is null)
            || demo02.Interests is null
            || demo02.Interests.Any(static item => item is null)
            || demo02.Recommendations is null
            || demo02.Recommendations.Any(static item => item is null)
            || demo02.OpenGaps is null
            || demo02.OpenGaps.Any(static item => item is null)
            || demo02.DroppedSkus is null
            || demo02.DroppedSkus.Any(static item => item is null)
            || demo02.DegradedNotes is null
            || demo02.DegradedNotes.Any(static item => item is null)
            || demo02.ExecutorFailures is null
            || demo02.ExecutorFailures.Any(static item => item is null)
            || HasInvalidLedger(demo02.Ledger))) return true;

        var benchmark = artifact.Result.OfflineBenchmark;
        return benchmark is not null && (benchmark.Cases is null
            || benchmark.Cases.Any(static item => item is null)
            || benchmark.Checks is null
            || benchmark.Checks.Any(HasInvalidBenchmarkCheck)
            || benchmark.Arms is null
            || benchmark.Arms.Any(static arm => arm is null
                || arm.Checks is null
                || arm.Checks.Any(HasInvalidBenchmarkCheck))
            || benchmark.Runs is null
            || benchmark.Runs.Any(static item => item is null)
            || benchmark.ReferenceComparisons is null
            || benchmark.ReferenceComparisons.Any(static item => item is null || item.Census is null));
    }

    private static bool HasInvalidLedger(VitrineLedgerSnapshot? ledger) =>
        ledger is not null && (ledger.Entries is null || ledger.Entries.Any(static item => item is null));

    private static bool HasInvalidBenchmarkCheck(VitrineBenchmarkCheckSnapshot? check) =>
        check is null || check.Floor is null || check.Census is null;

    private static string ResultStatus(VitrineRunOutcome outcome)
    {
        if (outcome.Cancelled) return "cancelled";
        if (outcome.FailureKind is not null) return "failed";
        return outcome.ProcessEquivalentExitCode switch
        {
            0 => "passed",
            1 when outcome.Request.Mode == VitrineRunMode.Ablation => "self-test-detected",
            1 => "failed",
            3 => "not-measured",
            { } => "failed",
            null => "completed",
        };
    }
}
