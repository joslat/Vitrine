// SPDX-License-Identifier: MIT
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Models;
using AgentEval.Output;
namespace AgentEval.VitrineDemo.Evals;
internal sealed record ProductionVariantArmInput(string ArmId, SubjectIdentity Subject, EvalInput Input);
internal sealed record ProductionVariantArmFact(string ArmId, SubjectIdentity Subject, BenchmarkRun Run,
    string RunDirectory, ObservationCensus Census, EvalResult Result);
internal sealed record ProductionVariantBenchmarkOutcome(BenchmarkDefinition Definition, string ReferenceArmId,
    IReadOnlyList<ProductionVariantArmFact> Arms, PairedComparison ReferenceComparison, string WorkspaceRoot,
    FileSystemOutputStore Store) {
    internal ProductionVariantArmFact Arm(string armId) =>
        Arms.Single(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal));
}
/// <summary>
/// Executes the production checks whose meaning depends on alternative system configurations.
/// Each configuration is a typed benchmark arm and each arm gets its own BenchmarkRunner run.
/// </summary>
internal static class ProductionVariantBenchmarks {
    internal const string Version = "3.0.0";
    internal const string JudgedDefinitionKey = "vitrine-production-judged-quality";
    internal const string JudgedDemo01ArmId = "demo01-agent";
    internal const string JudgedDemo02ArmId = "demo02-workflow";
    internal const string InjectionDefinitionKey = "vitrine-production-injection";
    internal const string InjectionSafeArmId = "safe-boundary";
    internal const string InjectionVulnerableArmId = "vulnerable-boundary-ablation";
    internal const string RecallDefinitionKey = "vitrine-production-recall";
    internal const string RecallHealthyArmId = "healthy-memory-provider";
    internal const string RecallAblatedArmId = "memory-provider-ablation";
    internal static Task<ProductionVariantBenchmarkOutcome> RunJudgedQualityAsync(
        EvalInput demo01,
        EvalInput demo02,
        CancellationToken cancellationToken = default,
        string? outputRoot = null) =>
        RunAsync(
            JudgedDefinitionKey,
            VitrineProductionChecks.JudgedQuality,
            new TestCase {
                Id = JudgedQualityProductionEval.CaseIdentity,
                Name = "Matched recommendation quality for the authored request",
                Input = demo01.Query,
            },
            JudgedDemo01ArmId,
            [
                new(JudgedDemo01ArmId,
                    new(SubjectKind.Agent, "VITRINE Demo01 recommendation agent"), demo01),
                new(JudgedDemo02ArmId,
                    new(SubjectKind.Workflow, "VITRINE Demo02 recommendation workflow"), demo02),
            ],
            cancellationToken,
            outputRoot);
    internal static Task<ProductionVariantBenchmarkOutcome> RunInjectionAsync(
        EvalInput safe,
        EvalInput vulnerable,
        CancellationToken cancellationToken = default,
        string? outputRoot = null) =>
        RunAsync(
            InjectionDefinitionKey,
            VitrineProductionChecks.Injection,
            new TestCase {
                Id = InjectionProductionEval.CaseIdentity,
                Name = "Direct-text and tool-output injection campaign",
                Input = safe.Query,
            },
            InjectionSafeArmId,
            [
                new(InjectionSafeArmId,
                    new(SubjectKind.Agent, "VITRINE guarded injection boundary"), safe),
                new(InjectionVulnerableArmId,
                    new(SubjectKind.Agent, "VITRINE vulnerable injection boundary ablation"), vulnerable),
            ],
            cancellationToken,
            outputRoot);
    internal static Task<ProductionVariantBenchmarkOutcome> RunRecallAsync(
        EvalInput healthy,
        EvalInput ablated,
        CancellationToken cancellationToken = default,
        string? outputRoot = null) =>
        RunAsync(
            RecallDefinitionKey,
            VitrineProductionChecks.Recall,
            new TestCase {
                Id = RecallProductionEval.CaseIdentity,
                Name = "Recall the customer's authored constraints",
                Input = healthy.Query,
            },
            RecallHealthyArmId,
            [
                new(RecallHealthyArmId,
                    new(SubjectKind.Agent, "VITRINE recommendation memory provider"), healthy),
                new(RecallAblatedArmId,
                    new(SubjectKind.Agent, "VITRINE memory provider ablation"), ablated),
            ],
            cancellationToken,
            outputRoot);
    private static async Task<ProductionVariantBenchmarkOutcome> RunAsync(
        string definitionKey,
        VitrineProductionCheck check,
        TestCase testCase,
        string referenceArmId,
        IReadOnlyList<ProductionVariantArmInput> armInputs,
        CancellationToken cancellationToken,
        string? outputRoot) {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionKey);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(armInputs);
        if (armInputs.Count != 2)
            throw new ArgumentException("This production variant benchmark requires exactly one reference and one alternative arm.", nameof(armInputs));
        if (armInputs.Select(static arm => arm.ArmId).Distinct(StringComparer.Ordinal).Count() != armInputs.Count)
            throw new ArgumentException("Variant benchmark arm ids must be unique.", nameof(armInputs));
        if (!armInputs.Any(arm => string.Equals(arm.ArmId, referenceArmId, StringComparison.Ordinal)))
            throw new ArgumentException($"Reference arm '{referenceArmId}' is not present.", nameof(referenceArmId));
        if (armInputs.Any(arm =>
                !string.Equals(arm.Input.CaseId, testCase.Id, StringComparison.Ordinal) ||
                !string.Equals(arm.Input.Query, testCase.Input, StringComparison.Ordinal)))
            throw new ArgumentException(
                "Every variant arm must receive the definition's same stable case id and authored stimulus.",
                nameof(armInputs));
        var definition = new BenchmarkDefinition(
            definitionKey,
            Version,
            [testCase],
            [new AdmittedCheck(check.EvalFactory(), check.Floor)]);
        var workspaceRoot = Path.GetFullPath(outputRoot ?? VitrineOfflineBenchmark.DefaultWorkspaceRoot());
        var store = new FileSystemOutputStore(workspaceRoot);
        await store.InitializeSolutionAsync(
                $"{definitionKey} production variant evidence",
                cancellationToken)
            .ConfigureAwait(false);
        var runs = new List<(ProductionVariantArmInput Arm, BenchmarkRun Run)>(armInputs.Count);
        foreach (var armInput in armInputs) {
            var captured = armInput;
            var arm = BenchmarkArm.From(captured.ArmId, (observedCase, ct) => {
                ct.ThrowIfCancellationRequested();
                if (!string.Equals(observedCase.Id, testCase.Id, StringComparison.Ordinal))
                    throw new InvalidDataException($"Unexpected case '{observedCase.Id}' for '{definition.Key}'.");
                return Task.FromResult(captured.Input);
            });
            var run = await new BenchmarkRunner(store, captured.Subject)
                .RunAsync(definition, arm, ct: cancellationToken)
                .ConfigureAwait(false);
            runs.Add((captured, run));
        }
        var expectedRows = definition.Cases.Count * definition.Checks.Count;
        if (expectedRows <= 0 || runs.Count != armInputs.Count ||
            runs.Any(item => item.Run.Observations.Count != expectedRows))
            throw new InvalidDataException(
                $"Variant benchmark '{definition.Key}' did not produce its required positive observation cardinality.");
        if (runs.Select(static item => item.Run.RunId).Distinct(StringComparer.Ordinal).Count() != runs.Count)
            throw new InvalidDataException(
                $"Variant benchmark '{definition.Key}' did not persist each arm as a distinct run.");
        var facts = new List<ProductionVariantArmFact>(runs.Count);
        foreach (var (arm, run) in runs) {
            var censusRows = BenchmarkScore.Census([run]);
            if (censusRows.Count != 1)
                throw new InvalidDataException(
                    $"Variant arm '{arm.ArmId}' produced {censusRows.Count} census rows; expected one.");
            facts.Add(new(
                arm.ArmId,
                arm.Subject,
                run,
                store.ResolveRunDirectory(arm.Subject, run.RunId),
                censusRows[0].Census,
                run.Observations[0].Result));
        }
        var reference = runs.Single(item =>
            string.Equals(item.Arm.ArmId, referenceArmId, StringComparison.Ordinal)).Run;
        var challenger = runs.Single(item =>
            !string.Equals(item.Arm.ArmId, referenceArmId, StringComparison.Ordinal)).Run;
        var comparisons = BenchmarkScore.AgainstReference([reference], [challenger], RepCollapse.All);
        if (comparisons.Count != 1)
            throw new InvalidDataException(
                $"Variant benchmark '{definition.Key}' produced {comparisons.Count} reference facts; expected one.");
        return new(
            definition,
            referenceArmId,
            Array.AsReadOnly(facts.ToArray()),
            comparisons[0].Comparison,
            workspaceRoot,
            store);
    }
}
