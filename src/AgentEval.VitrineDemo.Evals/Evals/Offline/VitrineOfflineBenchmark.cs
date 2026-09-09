// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using AgentEval.Output;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Workflows;
namespace AgentEval.VitrineDemo.Evals;
public sealed record VitrineBenchmarkCase(string Id, string Name);
public sealed record VitrineChanceFloorFact(string Kind, string State, double? Value, double? ComparisonBar,
    double? IntervalHigh, int Draws, int PoolSize, string Derivation);
public sealed record VitrineBenchmarkCensus(int Measured, int NotApplicable, int NotMeasured) {
    public int Total => Measured + NotApplicable + NotMeasured;
}
public sealed record VitrineBenchmarkCheckFact(string CheckKey, VitrineChanceFloorFact Floor,
    VitrineBenchmarkCensus Census, int Successes, int Trials, double? PValue, double? MinimumAttainableP,
    bool? AboveFloor, bool? UnderpoweredByConstruction);
public sealed record VitrineBenchmarkArmFact(string ArmId, string SubjectKind, string SubjectName,
    IReadOnlyList<VitrineBenchmarkCheckFact> Checks);
public sealed record VitrineBenchmarkRunFact(string ArmId, int Repetition, string SubjectKind, string SubjectName,
    string RunId, string RunDirectory);
public sealed record VitrineBenchmarkReferenceFact(string CheckKey, string ReferenceArmId, string ChallengerArmId,
    int Wins, int Losses, int Ties, int EffectiveN, double? PValue, double? MinimumAttainableP, double? MeanDelta,
    VitrineBenchmarkCensus Census, int Cases, int TotalRepObservations, string RepCollapse,
    bool UnderpoweredByConstruction);
public sealed record VitrineOfflineBenchmarkResult(string DefinitionKey, string DefinitionVersion, string ArmId,
    string RunId, string WorkspaceRoot, string RunDirectory, IReadOnlyList<VitrineBenchmarkCase> Cases,
    IReadOnlyList<VitrineBenchmarkCheckFact> Checks) {
    public int Repetitions { get; init; }
    public IReadOnlyList<VitrineBenchmarkArmFact> Arms { get; init; } = [];
    public IReadOnlyList<VitrineBenchmarkRunFact> Runs { get; init; } = [];
    public IReadOnlyList<VitrineBenchmarkReferenceFact> ReferenceComparisons { get; init; } = [];
}
internal sealed record VitrineCapturedBenchmarkInput(string ArmId, int Repetition, string CaseId, EvalInput Input);
internal sealed record VitrineBenchmarkExecution(BenchmarkDefinition Definition,
    IReadOnlyDictionary<string, IReadOnlyList<BenchmarkRun>> RunsByArm,
    IReadOnlyList<VitrineCapturedBenchmarkInput> Inputs, VitrineOfflineBenchmarkResult Result);
internal sealed record VitrineBenchmarkArmPlan(string ArmId, SubjectIdentity Subject,
    Func<TestCase, CancellationToken, Task<EvalInput>> Observe);
public static class VitrineOfflineBenchmark {
    public const string DefinitionKey = "vitrine-offline-recommendations";
    public const string DefinitionVersion = "2.0.0";
    public const string Demo01ArmId = "demo01-scripted-agent";
    public const string Demo02ArmId = "demo02-zero-model-workflow";
    public const string DegradedArmId = "degraded-empty-answer";
    public const int Repetitions = 2;
    public const string NadiaCaseId = "nadia-personalized-request";
    public const string SofiaCaseId = "sofia-replenishment-request";
    public const string ScreenedDeliverableCheckKey = "vitrine.recommendation.screened-deliverable";
    public const string CataloguedSkuCheckKey = "vitrine.recommendation.catalogued-sku";
    public const string CustomerReasonCheckKey = "vitrine.recommendation.customer-reason";
    public const string NoPurchaseClaimCheckKey = "vitrine.recommendation.no-purchase-claim";
    public const string InterestGroundingCheckKey = "vitrine.recommendation.interest-grounding";
    public static IReadOnlyList<string> CheckKeys { get; } = Array.AsReadOnly(new[] {
        ScreenedDeliverableCheckKey,
        CataloguedSkuCheckKey,
        CustomerReasonCheckKey,
        NoPurchaseClaimCheckKey,
        InterestGroundingCheckKey,
    });
    // Compatibility projection retained for the existing artifact's primary/reference arm.
    public const string ArmId = Demo01ArmId;
    internal static SubjectIdentity Demo01Subject { get; } = new(
        SubjectKind.Agent,
        "VITRINE Demo01 scripted recommendation agent");
    internal static SubjectIdentity Demo02Subject { get; } = new(
        SubjectKind.Workflow,
        "VITRINE Demo02 zero-model recommendation workflow");
    internal static SubjectIdentity DegradedSubject { get; } = new(
        SubjectKind.Agent,
        "VITRINE deliberately degraded empty-answer control");
    public static async Task<VitrineOfflineBenchmarkResult> RunAsync(
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default) =>
        (await ExecuteAsync(workspaceRoot, cancellationToken).ConfigureAwait(false)).Result;
    internal static async Task<VitrineBenchmarkExecution> ExecuteAsync(
        string? workspaceRoot = null,
        CancellationToken cancellationToken = default) {
        var root = Path.GetFullPath(workspaceRoot ?? DefaultWorkspaceRoot());
        var definition = CreateDefinition();
        var store = new FileSystemOutputStore(root);
        await store.InitializeSolutionAsync("VITRINE local evaluation evidence", cancellationToken)
            .ConfigureAwait(false);
        var plans = CreateArmPlans();
        var mutableRuns = plans.ToDictionary(
            static plan => plan.ArmId,
            static _ => new List<BenchmarkRun>(Repetitions),
            StringComparer.Ordinal);
        var inputs = new List<VitrineCapturedBenchmarkInput>(
            plans.Count * Repetitions * definition.Cases.Count);
        var runFacts = new List<VitrineBenchmarkRunFact>(plans.Count * Repetitions);
        // One call is one rep of one arm. Different subject kinds deliberately get different
        // runners so their persisted directories truthfully say Agent versus Workflow.
        for (var repetition = 1; repetition <= Repetitions; repetition++) {
            foreach (var plan in plans) {
                var capturedPlan = plan;
                var capturedRepetition = repetition;
                var arm = BenchmarkArm.From(plan.ArmId, async (testCase, ct) => {
                    var input = await capturedPlan.Observe(testCase, ct).ConfigureAwait(false);
                    inputs.Add(new(capturedPlan.ArmId, capturedRepetition, testCase.Id!, input));
                    return input;
                });
                var run = await new BenchmarkRunner(store, plan.Subject)
                    .RunAsync(definition, arm, ct: cancellationToken)
                    .ConfigureAwait(false);
                mutableRuns[plan.ArmId].Add(run);
                runFacts.Add(new(
                    plan.ArmId,
                    repetition,
                    plan.Subject.Kind.ToString(),
                    plan.Subject.Name,
                    run.RunId,
                    store.ResolveRunDirectory(plan.Subject, run.RunId)));
            }
        }
        var runsByArm = mutableRuns.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<BenchmarkRun>)Array.AsReadOnly(pair.Value.ToArray()),
            StringComparer.Ordinal);
        // Positive cardinality is checked before a score value is read. A missing run or row must
        // never satisfy a later all-pass/all-fail expectation vacuously.
        var expectedRowsPerRun = definition.Cases.Count * definition.Checks.Count;
        foreach (var plan in plans) {
            var armRuns = runsByArm[plan.ArmId];
            if (armRuns.Count != Repetitions)
                throw new InvalidDataException(
                    $"Arm '{plan.ArmId}' produced {armRuns.Count} run(s), not {Repetitions}.");
            foreach (var run in armRuns) {
                if (run.Observations.Count != expectedRowsPerRun)
                    throw new InvalidDataException(
                        $"Run '{run.RunId}' produced {run.Observations.Count} observation(s), not {expectedRowsPerRun}.");
            }
        }
        // The census is deliberately materialized before any floor or reference comparison.
        var censusByArm = plans.ToDictionary(
            static plan => plan.ArmId,
            plan => BenchmarkScore.Census(runsByArm[plan.ArmId])
                .ToDictionary(static row => row.CheckKey, static row => row.Census, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var armFacts = plans.Select(plan => new VitrineBenchmarkArmFact(
                plan.ArmId,
                plan.Subject.Kind.ToString(),
                plan.Subject.Name,
                ScoreArm(definition, runsByArm[plan.ArmId], censusByArm[plan.ArmId])))
            .ToArray();
        var referenceFacts = new List<VitrineBenchmarkReferenceFact>(definition.Checks.Count * 2);
        AddReferenceFacts(referenceFacts, runsByArm[Demo01ArmId], runsByArm[Demo02ArmId]);
        AddReferenceFacts(referenceFacts, runsByArm[Demo01ArmId], runsByArm[DegradedArmId]);
        var referenceRun = runFacts.Single(run =>
            run.ArmId == Demo01ArmId && run.Repetition == 1);
        var result = new VitrineOfflineBenchmarkResult(
            definition.Key,
            definition.Version,
            Demo01ArmId,
            referenceRun.RunId,
            root,
            referenceRun.RunDirectory,
            Array.AsReadOnly(definition.Cases
                .Select(static testCase => new VitrineBenchmarkCase(testCase.Id!, testCase.Name))
                .ToArray()),
            armFacts.Single(static arm => arm.ArmId == Demo01ArmId).Checks) {
            Repetitions = Repetitions,
            Arms = Array.AsReadOnly(armFacts),
            Runs = Array.AsReadOnly(runFacts.ToArray()),
            ReferenceComparisons = Array.AsReadOnly(referenceFacts.ToArray()),
        };
        return new(
            definition,
            runsByArm,
            Array.AsReadOnly(inputs.ToArray()),
            result);
    }
    internal static BenchmarkDefinition CreateDefinition() => new(
        DefinitionKey,
        DefinitionVersion,
        [
            new TestCase {
                Id = NadiaCaseId,
                Name = "Nadia asks for personalized recommendations",
                Input = Personas.CanonicalPromptFor(Personas.NadiaUserId),
            },
            new TestCase {
                Id = SofiaCaseId,
                Name = "Sofia asks for replenishment and gap recommendations",
                Input = Personas.CanonicalPromptFor(Personas.SofiaUserId),
            },
        ],
        RecommendationBenchmarkChecks.All());
    internal static IReadOnlyList<VitrineBenchmarkArmPlan> CreateArmPlans() =>
    [
        new(Demo01ArmId, Demo01Subject, ObserveDemo01Async),
        new(Demo02ArmId, Demo02Subject, ObserveDemo02Async),
        new(DegradedArmId, DegradedSubject, ObserveDegradedAsync),
    ];
    internal static string DefaultWorkspaceRoot() =>
        Path.Combine(FindRepositoryRoot(), ".agenteval", "Vitrine");
    private static async Task<EvalInput> ObserveDemo01Async(
        TestCase testCase,
        CancellationToken cancellationToken) {
        var userId = PersonaId(testCase);
        var run = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(userId, Arm: RecommendationExecutionArm.ScriptedAgent),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var screen = RecommendationArtifactComposer.ComposeScreened(run);
        var toolCalls = ProjectDemo01ToolCalls(run);
        var instrumented = run.Status == RecommendationRunStatus.Completed && run.Outcome is not null &&
            toolCalls is not null;
        return RecommendationBenchmarkInput.Create(
            testCase.Id!,
            testCase.Input,
            screen.Answer,
            toolCalls,
            new(
                instrumented,
                $"Demo01 observed {run.Events.Count} runtime event(s) and {run.ToolCallsUsed?.ToString() ?? "an unmeasured number of"} tool call(s)."));
    }
    private static async Task<EvalInput> ObserveDemo02Async(
        TestCase testCase,
        CancellationToken cancellationToken) {
        var progress = NullDiscoveryProgressSink.Instance;
        var run = await GalaxusDiscoveryLoop.RunAsync(
            PersonaId(testCase),
            new DiscoveryLoopOptions(
                Offline: true,
                Progress: progress,
                Nodes: new DiscoveryNodeOverrides(
                    Presenter: new DeterministicPresenter(Catalogue.Default, progress, print: false))),
            cancellationToken).ConfigureAwait(false);
        var screen = run.State.CustomerAnswerSafety;
        return RecommendationBenchmarkInput.Create(
            testCase.Id!,
            testCase.Input,
            run.State.FinalAnswer,
            Array.Empty<ToolCall>(),
            new(
                !run.Failed && screen is not null,
                $"Demo02 observed the zero-model workflow over {run.SuperSteps} super-step(s); the watched tool-call boundary recorded zero calls."));
    }
    private static Task<EvalInput> ObserveDegradedAsync(
        TestCase testCase,
        CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RecommendationBenchmarkInput.Create(
            testCase.Id!,
            testCase.Input,
            string.Empty,
            Array.Empty<ToolCall>(),
            new(
                Instrumented: true,
                Evidence: "The deliberately degraded control returned a measured empty answer; its watched tool-call boundary recorded zero calls.")));
    }
    internal static IReadOnlyList<ToolCall>? ProjectDemo01ToolCalls(RecommendationRunResult run) {
        ArgumentNullException.ThrowIfNull(run);
        if (run.ToolCallsUsed is null)
            return null;
        var starts = run.Events
            .Where(static item => item.Kind == RecommendationRuntimeEventKind.ToolExecutionStarted)
            .ToArray();
        // ToolCallsUsed is the refusable-budget count. The observed journal also includes each
        // uncapped PresentRecommendation answer-channel call, represented by Presented.
        if (run.ToolCallsUsed < 0)
            return null;
        var expectedStarts = run.ToolCallsUsed + run.Presented.Count;
        if (expectedStarts == 0)
            return starts.Length == 0 ? [] : null;
        if (starts.Length != expectedStarts)
            return null;
        var terminalByOperation = run.Events
            .Where(static item => item.OperationId is not null && item.Kind is
                RecommendationRuntimeEventKind.ToolCompleted or
                RecommendationRuntimeEventKind.ToolCancelled or
                RecommendationRuntimeEventKind.ToolFailed)
            .GroupBy(static item => item.OperationId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        if (starts.Any(static item => string.IsNullOrWhiteSpace(item.OperationId)) ||
            starts.Select(static item => item.OperationId!).Distinct(StringComparer.Ordinal).Count() != starts.Length ||
            terminalByOperation.Count != starts.Length ||
            starts.Any(item => !terminalByOperation.ContainsKey(item.OperationId!)))
            return null;
        return starts.Select(item => {
                terminalByOperation.TryGetValue(item.OperationId ?? string.Empty, out var terminal);
                return new ToolCall(
                    item.Target,
                    ParseArguments(item.PayloadPreview),
                    terminal?.PayloadPreview ?? terminal?.Detail);
            })
            .ToArray();
    }
    private static IReadOnlyDictionary<string, object>? ParseArguments(string? preview) {
        if (preview is null)
            return null;
        try {
            using var document = JsonDocument.Parse(preview);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, object> { ["value"] = preview };
            return document.RootElement.EnumerateObject().ToDictionary(
                static property => property.Name,
                static property => JsonValue(property.Value),
                StringComparer.Ordinal);
        }
        catch (JsonException) {
            return new Dictionary<string, object> { ["preview"] = preview };
        }
    }
    private static object JsonValue(JsonElement value) => value.ValueKind switch {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => string.Empty,
        _ => value.GetRawText(),
    };
    private static IReadOnlyList<VitrineBenchmarkCheckFact> ScoreArm(
        BenchmarkDefinition definition,
        IReadOnlyList<BenchmarkRun> runs,
        IReadOnlyDictionary<string, ObservationCensus> census) {
        var comparisons = BenchmarkScore.AgainstFloor(runs, RepCollapse.All)
            .ToDictionary(static row => row.CheckKey, static row => row.Comparison, StringComparer.Ordinal);
        return Array.AsReadOnly(definition.Checks.Select(check => {
            var floor = check.Floor;
            var row = comparisons[check.Eval.Key];
            var counts = census[check.Eval.Key];
            return new VitrineBenchmarkCheckFact(
                check.Eval.Key,
                new(
                    floor.Kind,
                    floor.State.ToString(),
                    floor.State == FloorState.Derived ? floor.Value : null,
                    floor.State == FloorState.Derived ? floor.ComparisonBar : null,
                    floor.IntervalHigh,
                    floor.Draws,
                    floor.PoolSize,
                    floor.Derivation),
                Census(counts),
                row.Successes,
                row.Trials,
                FiniteOrNull(row.PValue),
                FiniteOrNull(row.MinimumAttainableP),
                double.IsFinite(row.PValue) ? row.AboveFloor : null,
                floor.State == FloorState.Derived && double.IsFinite(row.PValue)
                    ? row.UnderpoweredByConstruction
                    : null);
        }).ToArray());
    }
    private static void AddReferenceFacts(
        ICollection<VitrineBenchmarkReferenceFact> destination,
        IReadOnlyList<BenchmarkRun> reference,
        IReadOnlyList<BenchmarkRun> challenger) {
        foreach (var (checkKey, comparison) in BenchmarkScore.AgainstReference(
                     reference,
                     challenger,
                     RepCollapse.All)) {
            destination.Add(new(
                checkKey,
                comparison.Reference,
                comparison.Challenger,
                comparison.Wins,
                comparison.Losses,
                comparison.Ties,
                comparison.EffectiveN,
                FiniteOrNull(comparison.PValue),
                FiniteOrNull(comparison.MinimumAttainableP),
                FiniteOrNull(comparison.MeanDelta),
                Census(comparison.Census),
                comparison.Unit.Cases,
                comparison.Unit.TotalReps,
                comparison.Unit.Strategy.ToString(),
                comparison.UnderpoweredByConstruction));
        }
    }
    private static VitrineBenchmarkCensus Census(ObservationCensus census) =>
        new(census.Measured, census.NotApplicable, census.NotMeasured);
    private static string PersonaId(TestCase testCase) => testCase.Id switch {
        NadiaCaseId => Personas.NadiaUserId,
        SofiaCaseId => Personas.SofiaUserId,
        _ => throw new InvalidOperationException($"Unknown VITRINE recommendation case '{testCase.Id}'."),
    };
    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;
    private static string FindRepositoryRoot() {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }) {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent) {
                if (File.Exists(Path.Combine(directory.FullName, "AgentEval.VitrineDemo.slnx")))
                    return directory.FullName;
            }
        }
        throw new InvalidOperationException(
            "Could not locate the VITRINE repository root for local AgentEval persistence.");
    }
}
