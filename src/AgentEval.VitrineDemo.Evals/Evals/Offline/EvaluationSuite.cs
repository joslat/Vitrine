// SPDX-License-Identifier: MIT
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Workflows;
namespace AgentEval.VitrineDemo.Evals;
public static class EvaluationSuite {
    public static async Task<SuiteResult> RunAsync(
        bool leaveCatalogueAblated = false,
        CancellationToken cancellationToken = default,
        IEvaluationProgressSink? progress = null,
        bool includeControls = true,
        bool persistOfflineBenchmark = true) {
        var gates = new List<GateResult>();
        var sink = progress ?? NullEvaluationProgressSink.Instance;
        var execution = PlannedExecution();
        const int totalGates = 6;
        sink.PublishSafely(new(EvaluationProgressKind.SuiteStarted, "suite", "VITRINE evaluation suite",
            "Running the typed offline evaluation chain. Zero provider model calls are possible from this suite.",
            Completed: 0, Total: totalGates));
        VitrineEvalCriteria.Validate();
        gates.Add(await ObserveGateAsync("catalogue", "Catalogue contract", sink,
            () => CatalogueGateAsync(leaveCatalogueAblated, cancellationToken), cancellationToken,
            leaveCatalogueAblated
                ? EvaluationProgressExpectation.CatalogueDefectDetection
                : EvaluationProgressExpectation.None).ConfigureAwait(false));
        gates.Add(await ObserveGateAsync("topology", "Workflow topology", sink,
            () => ObserveTopologyAsync(cancellationToken), cancellationToken).ConfigureAwait(false));
        gates.Add(await ObserveGateAsync("judged", "Matched agent/workflow judged quality", sink,
            () => JudgedGateAsync(
                cancellationToken,
                captureExecution: observed => execution = observed),
            cancellationToken).ConfigureAwait(false));
        gates.Add(await ObserveGateAsync("injection", "RedTeam injection", sink,
            () => InjectionGateAsync(cancellationToken), cancellationToken).ConfigureAwait(false));
        gates.Add(await ObserveGateAsync("recall", "Memory recall", sink,
            () => RecallGateAsync(cancellationToken), cancellationToken).ConfigureAwait(false));
        gates.Add(await ObserveGateAsync("honesty", "Honesty claim", sink,
            () => HonestyGateAsync(cancellationToken), cancellationToken).ConfigureAwait(false));
        if (!ValidateAgentEvalManifest(gates.Select(static gate => gate.AgentEval).ToArray()))
            gates[^1] = GateResult.InstrumentError(
                "AgentEval provenance manifest",
                null,
                typeof(InvalidDataException));
        var offlineBenchmark = persistOfflineBenchmark
            ? await VitrineOfflineBenchmark.RunAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            : null;
        if (offlineBenchmark is { } benchmark) {
            foreach (var check in benchmark.Checks)
                sink.PublishSafely(new(EvaluationProgressKind.BenchmarkCheckCompleted, check.CheckKey,
                    $"Native predicate · {check.CheckKey}",
                    $"{check.Successes}/{check.Trials} measured successes; N/A {check.Census.NotApplicable}; not measured {check.Census.NotMeasured}; p {check.PValue?.ToString("0.000") ?? "NOT MEASURED"}; power {check.UnderpoweredByConstruction switch { true => "underpowered", false => "powered", null => "N/A" }}.",
                    Passed: null));
            sink.PublishSafely(new(EvaluationProgressKind.BenchmarkPersisted, "benchmark",
                "Native AgentEval benchmark persisted",
                $"Run {benchmark.RunId}; {benchmark.Cases.Count} case(s), {benchmark.Checks.Count} check(s); local directory {benchmark.RunDirectory}.",
                Passed: true));
        }
        IReadOnlyList<ControlResult> controls = [];
        if (includeControls) {
            sink.PublishSafely(new(EvaluationProgressKind.GateStarted, "controls", "Control verification",
                "Verifying the exact registered 43-row control panel and its typed attempt outcomes."));
            controls = await NegativeControlRunner.RunAsync(cancellationToken, sink).ConfigureAwait(false);
        }
        var result = new SuiteResult(gates, controls) {
            OfflineBenchmark = offlineBenchmark,
            Execution = execution,
            RequireCanonicalControlPanel = includeControls,
        };
        var canonicalControlScope = includeControls && NegativeControlCatalog.HasCanonicalRegisteredPanel(controls);
        var catalogueSelfTestSucceeded = leaveCatalogueAblated
            && result.ExitCode == EvaluationExitCodes.GateFailed
            && gates.Count == totalGates
            && gates[0] is { Outcome: GateMeasurementOutcome.Measured, Passed: false }
            && gates.Skip(1).All(static gate => gate is { Outcome: GateMeasurementOutcome.Measured, Passed: true })
            && (!includeControls || canonicalControlScope && controls.All(static control => control.Caught));
        sink.PublishSafely(new(EvaluationProgressKind.SuiteCompleted, "suite", "VITRINE evaluation suite",
            catalogueSelfTestSucceeded
                ? "SELF-TEST SUCCEEDED: expected products 99 → observed 98 was detected; the underlying process-equivalent exit 1 and failed catalogue gate are retained as evidence."
                : includeControls
                ? canonicalControlScope
                    ? $"Exit code {result.ExitCode}; {result.CaughtControls}/{controls.Count} registered control mutations caught."
                    : $"Exit code {result.ExitCode}; {result.CaughtControls}/{controls.Count} controls caught; registered-panel scope is not established."
                : $"Exit code {result.ExitCode}; six non-control gates executed for the non-recursive CI proof.",
            Passed: result.ExitCode == EvaluationExitCodes.NotMeasured
                ? null
                : result.ExitCode == EvaluationExitCodes.Passed,
            Completed: totalGates,
            Total: totalGates,
            IncludesDiagnosticControls: includeControls,
            Expectation: catalogueSelfTestSucceeded
                ? EvaluationProgressExpectation.CatalogueSelfTestSucceeded
                : EvaluationProgressExpectation.None));
        return result;
    }
    private static readonly (string Id, string LibraryType)[] AgentEvalIntegrationPlan =
    [
        ("catalogue-shape", "AgentEval.Evals.AtomicCodeEval"),
        ("workflow-shape", "AgentEval.Evals.AtomicCodeEval"),
        ("matched-quality", "AgentEval.Evals.AtomicCodeEval"),
        ("injection", "AgentEval.Evals.AtomicCodeEval"),
        ("recall", "AgentEval.Evals.AtomicCodeEval"),
        ("honesty", "AgentEval.Evals.AtomicCodeEval"),
    ];
    internal static bool ValidateAgentEvalManifest(IReadOnlyList<AgentEvalProvenance?> manifest) {
        if (manifest.Count != AgentEvalIntegrationPlan.Length) return false;
        for (var index = 0; index < AgentEvalIntegrationPlan.Length; index++) {
            var actual = manifest[index];
            var expected = AgentEvalIntegrationPlan[index];
            if (actual?.HasIndependentBoundary != true ||
                !string.Equals(actual.IntegrationId, expected.Id, StringComparison.Ordinal) ||
                !string.Equals(actual.LibraryType, expected.LibraryType, StringComparison.Ordinal))
                return false;
        }
        return manifest.Select(static item => item!.IntegrationId).Distinct(StringComparer.Ordinal).Count() == manifest.Count;
    }
    private static async Task<GateResult> ObserveGateAsync(
        string id,
        string name,
        IEvaluationProgressSink progress,
        Func<Task<GateResult>> run,
        CancellationToken cancellationToken,
        EvaluationProgressExpectation expectation = EvaluationProgressExpectation.None) {
        progress.PublishSafely(new(EvaluationProgressKind.GateStarted, id, name, "Evaluation started."));
        GateResult result;
        try {
            result = await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) {
            result = GateResult.InstrumentError(name, ProductionFloorFor(id), exception.GetType());
        }
        progress.PublishSafely(new(EvaluationProgressKind.GateCompleted, id, result.Name, result.Evidence,
            result.Passed, Gate: result, Expectation: expectation));
        return result;
    }
    internal static Task<GateResult> ObserveGateForTestAsync(
        Func<Task<GateResult>> run,
        IEvaluationProgressSink? progress = null,
        CancellationToken cancellationToken = default) =>
        ObserveGateAsync(
            "test-gate",
            "Test gate",
            progress ?? NullEvaluationProgressSink.Instance,
            run,
            cancellationToken);
    private static async Task<GateResult> ObserveTopologyAsync(CancellationToken cancellationToken) {
        AgentEval.Models.WorkflowGraphSnapshot? workflowGraph = null;
        var workflowRun = await GalaxusDiscoveryLoop.RunAsync(
            Personas.NadiaUserId,
            new DiscoveryLoopOptions(
                Offline: true,
                WorkflowPrepared: (workflow, ids) => {
                    workflowGraph = AgentEval.MAF.MAFWorkflowAdapter.FromMAFWorkflow(
                        workflow,
                        DiscoveryWorkflowFactory.WorkflowName,
                        ids,
                        "bounded-review-loop").GraphDefinition;
                }),
            cancellationToken).ConfigureAwait(false);
        return await TopologyGateAsync(workflowRun, workflowGraph, cancellationToken).ConfigureAwait(false);
    }
    private static async Task<GateResult> CatalogueGateAsync(
        bool leaveAblated,
        CancellationToken cancellationToken) {
        var healthySnapshot = CatalogueContractSnapshot.Capture();
        var observedSnapshot = leaveAblated
            ? healthySnapshot.WithOneProductRemoved()
            : healthySnapshot;
        var evidence = observedSnapshot.RemovedProductSku is { } removed
            ? $"Expected products={VitrineEvalCriteria.ProductCount}, personas={VitrineEvalCriteria.PersonaCount}, tools={VitrineEvalCriteria.ToolCount}; " +
              $"observed products={observedSnapshot.ProductCount}, personas={observedSnapshot.PersonaCount}, tools={observedSnapshot.ToolCount}. " +
              $"The isolated self-test snapshot removed {removed}; the healthy source remains {healthySnapshot.ProductCount} products."
            : $"Expected products={VitrineEvalCriteria.ProductCount}, personas={VitrineEvalCriteria.PersonaCount}, tools={VitrineEvalCriteria.ToolCount}; " +
              $"observed products={observedSnapshot.ProductCount}, personas={observedSnapshot.PersonaCount}, tools={observedSnapshot.ToolCount}. " +
              "Observed independently from the configured catalogue, persona registry, and AIFunction registry.";
        var input = CatalogueProductionEval.Input(new(
            observedSnapshot.ProductCount,
            observedSnapshot.PersonaCount,
            observedSnapshot.ToolCount));
        return await RunProductionEvalAsync(
            "Catalogue shape contract",
            VitrineProductionChecks.Catalogue,
            input,
            evidence,
            VitrineEvalCriteria.CatalogueSource,
            cancellationToken).ConfigureAwait(false);
    }
    internal static Task<GateResult> CatalogueGateForControlAsync(
        bool leaveAblated,
        CancellationToken cancellationToken = default) =>
        CatalogueGateAsync(leaveAblated, cancellationToken);
    private static async Task<GateResult> TopologyGateAsync(
        DiscoveryRunResult run,
        AgentEval.Models.WorkflowGraphSnapshot? graph,
        CancellationToken cancellationToken) {
        if (graph is null)
            return await RunProductionEvalAsync(
                "Workflow topology",
                VitrineProductionChecks.Topology,
                TopologyProductionEval.FailedInput("MAF graph extraction returned no graph."),
                "MAF graph extraction returned no graph.",
                "AgentEval.MAF.MAFWorkflowAdapter.FromMAFWorkflow",
                cancellationToken).ConfigureAwait(false);
        var loopBacks = graph.Edges.Count(edge =>
            string.Equals(edge.SourceExecutorId, DiscoveryExecutorIds.CoverageReviewer, StringComparison.Ordinal) &&
            string.Equals(edge.TargetExecutorId, DiscoveryExecutorIds.Discovery, StringComparison.Ordinal));
        return await RunProductionEvalAsync(
            "Workflow · five executors · one loop-back edge",
            VitrineProductionChecks.Topology,
            TopologyProductionEval.Input(new(graph.Nodes.Count, loopBacks, graph.Edges.Count)),
            $"MAF reflected {graph.Nodes.Count} nodes and {graph.Edges.Count} edges; review→discovery count {loopBacks}.",
            VitrineEvalCriteria.TopologySource,
            cancellationToken).ConfigureAwait(false);
    }
    internal static async Task<GateResult> JudgedGateAsync(
        CancellationToken cancellationToken,
        Action<JudgedGateDiagnostics>? capture = null,
        Action<EvaluationExecutionProvenance>? captureExecution = null) {
        var canonicalRequest = Personas.CanonicalPromptFor(Personas.NadiaUserId);
        var demo01 = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(Personas.NadiaUserId, Arm: RecommendationExecutionArm.ScriptedAgent),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var workflowClient = new DeterministicDiscoveryChatClient();
        var demo02 = await GalaxusDiscoveryLoop.RunAsync(
            Personas.NadiaUserId,
            new DiscoveryLoopOptions(Offline: false, ChatClient: workflowClient, MaxRounds: 3),
            cancellationToken).ConfigureAwait(false);
        var execution = ObservedExecution(
            demo01.ModelCalls,
            demo02.State.ModelCalls,
            judgeModelCalls: 0);
        captureExecution?.Invoke(execution);
        var demo01Count = demo01.Outcome?.Cleaned.PresentedCount;
        var demo02Count = demo02.State.Screened?.Outcome.Cleaned.PresentedCount;
        var matchedK = VitrineEvalCriteria.JudgedMatchedK;
        if (demo01.Status != RecommendationRunStatus.Completed || demo02.Failed ||
            demo01Count is not { } measuredDemo01Count || demo02Count is not { } measuredDemo02Count)
            return await RunUnavailableJudgedVariantAsync(
                "Matched agent/workflow judged quality",
                JudgedQualityProductionEval.Input(null, canonicalRequest),
                $"Matched screened output is absent or below authored k={matchedK} " +
                $"(Demo01 {DescribeOptionalCount(demo01Count)}; Demo02 {DescribeOptionalCount(demo02Count)}).",
                cancellationToken).ConfigureAwait(false);
        var demo01Response = RecommendationArtifactComposer.Compose(demo01, matchedK);
        var demo02Response = ComposeMatchedWorkflowArtifact(demo02, matchedK);
        if (string.IsNullOrWhiteSpace(demo01Response) || string.IsNullOrWhiteSpace(demo02Response))
            return await RunUnavailableJudgedVariantAsync(
                "Matched agent/workflow judged quality",
                JudgedQualityProductionEval.Input(null, canonicalRequest),
                "A matched customer-facing projection is absent.",
                cancellationToken).ConfigureAwait(false);
        var graderSanity = await EvaluateGraderSanityAsync(
            new DeterministicCriteriaEvaluator(),
            demo01Response,
            cancellationToken).ConfigureAwait(false);
        if (!CausalControlPolicies.MatchesGold(graderSanity))
            return GateResult.InstrumentError(
                "Matched Demo01/Demo02 quality · shared criteria",
                VitrineProductionChecks.JudgedQuality.Floor,
                typeof(InvalidDataException));
        var judge = new CountingEvaluator(new DeterministicCriteriaEvaluator());
        var applicability = VitrineEvalCriteria.JudgedCriteria
            .Select(criterion => VitrineEvalCriteria.DecideApplicability(criterion, canonicalRequest))
            .ToArray();
        if (applicability.Any(decision => !decision.Applicable ||
            !JudgedApplicabilityPolicy.ComesFromAuthoredInput(new(
                decision.CriterionId, canonicalRequest, decision.Applicable, decision.Source))))
            return await RunUnavailableJudgedVariantAsync(
                "Matched Demo01/Demo02 quality · shared criteria",
                JudgedQualityProductionEval.Input(
                    new(null, null, null, null, null), string.Empty),
                "One or more shared criteria are not applicable to the authored request.",
                cancellationToken).ConfigureAwait(false);
        var demo01Subject = new JudgedArtifact(JudgedArtifactOrigin.Demo01, demo01Response, measuredDemo01Count);
        var demo02Subject = new JudgedArtifact(JudgedArtifactOrigin.Demo02, demo02Response, measuredDemo02Count);
        JudgedSubjectEvaluation demo01Evaluation;
        JudgedSubjectEvaluation demo02Evaluation;
        int demo01JudgeCalls;
        int demo02JudgeCalls;
        try {
            var callsBeforeDemo01 = judge.CallCount;
            demo01Evaluation = await RunJudgedSubjectAsync(
                judge,
                demo01Subject,
                canonicalRequest,
                cancellationToken).ConfigureAwait(false);
            demo01JudgeCalls = judge.CallCount - callsBeforeDemo01;
            var callsBeforeDemo02 = judge.CallCount;
            demo02Evaluation = await RunJudgedSubjectAsync(
                judge,
                demo02Subject,
                canonicalRequest,
                cancellationToken).ConfigureAwait(false);
            demo02JudgeCalls = judge.CallCount - callsBeforeDemo02;
        }
        finally {
            execution = execution with { JudgeModelCalls = 0 };
            captureExecution?.Invoke(execution);
        }
        var demo01Result = demo01Evaluation.Result;
        var demo02Result = demo02Evaluation.Result;
        var matchedBindings = new MatchedKPanelProbe([
            new(SlotFor(demo01Evaluation.Artifact.Origin), demo01Evaluation.Artifact.SourcePresented, demo01Evaluation.ArtifactSkuCount),
            new(SlotFor(demo02Evaluation.Artifact.Origin), demo02Evaluation.Artifact.SourcePresented, demo02Evaluation.ArtifactSkuCount),
        ]);
        capture?.Invoke(new JudgedGateDiagnostics(
            demo01Result.CriteriaResults?.ToArray() ?? [],
            demo02Result.CriteriaResults?.ToArray() ?? [],
            judge.CallCount,
            new JudgedSubjectBinding(
                SlotFor(demo01Evaluation.Artifact.Origin),
                NameFor(demo01Evaluation.Artifact.Origin),
                demo01Evaluation.Artifact.SourcePresented,
                demo01Evaluation.ArtifactSkuCount),
            new JudgedSubjectBinding(
                SlotFor(demo02Evaluation.Artifact.Origin),
                NameFor(demo02Evaluation.Artifact.Origin),
                demo02Evaluation.Artifact.SourcePresented,
                demo02Evaluation.ArtifactSkuCount)));
        var demo01Criteria = JoinCanonicalCriteria(demo01Result.CriteriaResults);
        var demo02Criteria = JoinCanonicalCriteria(demo02Result.CriteriaResults);
        var applicabilityById = applicability.ToDictionary(static item => item.CriterionId, StringComparer.Ordinal);
        var observations = demo01Criteria is not null && demo02Criteria is not null
            ? demo01Criteria!.Select(item => new AgentEvalObservation(
                    $"demo01:{item.Definition.Id}",
                    item.Result.Met ? "met" : "not-met",
                    item.Result.Met ? 1 : 0,
                    applicabilityById[item.Definition.Id].Source.ToString(),
                    SampleCount: matchedK))
                .Concat(demo02Criteria!.Select(item => new AgentEvalObservation(
                    $"demo02:{item.Definition.Id}",
                    item.Result.Met ? "met" : "not-met",
                    item.Result.Met ? 1 : 0,
                    applicabilityById[item.Definition.Id].Source.ToString(),
                    SampleCount: matchedK)))
                .Concat([
                    new AgentEvalObservation($"binding:{SlotFor(demo01Evaluation.Artifact.Origin)}", NameFor(demo01Evaluation.Artifact.Origin), demo01Evaluation.Artifact.SourcePresented,
                        "RecommendationArtifactComposer.Compose", demo01Evaluation.ArtifactSkuCount),
                    new AgentEvalObservation($"binding:{SlotFor(demo02Evaluation.Artifact.Origin)}", NameFor(demo02Evaluation.Artifact.Origin), demo02Evaluation.Artifact.SourcePresented,
                        "EvaluationSuite.ComposeMatchedWorkflowArtifact", demo02Evaluation.ArtifactSkuCount),
                    new AgentEvalObservation("reachability:judge-call-count", "measured", judge.CallCount,
                        $"{execution.EvaluatorEngine} invocation count", judge.CallCount),
                    new AgentEvalObservation("aggregate:applicable-denominator", "measured",
                        demo01Criteria.Count + demo02Criteria.Count,
                        "AgentEval native measured observations", demo01Criteria.Count + demo02Criteria.Count),
                ])
                .ToArray()
            : [];
        var demo01Input = demo01Result.EvaluationFailed
            ? JudgedQualityProductionEval.FailedInput(
                "the IEvaluator reported an operational failure for the Demo01 arm.", canonicalRequest)
            : JudgedQualityProductionEval.Input(new(
                demo01Result.OverallScore,
                demo01Criteria?.Count,
                demo01JudgeCalls,
                demo01Evaluation.Artifact.SourcePresented,
                demo01Evaluation.ArtifactSkuCount), canonicalRequest);
        var demo02Input = demo02Result.EvaluationFailed
            ? JudgedQualityProductionEval.FailedInput(
                "the IEvaluator reported an operational failure for the Demo02 arm.", canonicalRequest)
            : JudgedQualityProductionEval.Input(new(
                demo02Result.OverallScore,
                demo02Criteria?.Count,
                demo02JudgeCalls,
                demo02Evaluation.Artifact.SourcePresented,
                demo02Evaluation.ArtifactSkuCount), canonicalRequest);
        var variant = await ProductionVariantBenchmarks.RunJudgedQualityAsync(
            demo01Input, demo02Input, cancellationToken).ConfigureAwait(false);
        var gate = GateFromVariantBenchmark(
            "Matched Demo01/Demo02 quality · shared criteria",
            VitrineProductionChecks.JudgedQuality,
            variant,
            ProductionVariantBenchmarks.JudgedDemo02ArmId,
            $"IEvaluator produced the same {VitrineEvalCriteria.JudgedCriteria.Length} immutable criterion observations for Demo01 and Demo02 at matched k={matchedK}; " +
            $"offline deterministic profile; Demo01/Demo02 simulated model turns / provider judge calls " +
            $"{execution.Demo01SubjectModelCalls?.ToString() ?? "NOT MEASURED"}/" +
            $"{execution.Demo02SubjectModelCalls?.ToString() ?? "NOT MEASURED"}/" +
            $"{execution.JudgeModelCalls?.ToString() ?? "NOT MEASURED"}; distinct benchmark runs " +
            $"{string.Join(", ", variant.Arms.Select(static arm => arm.Run.RunId))}.");
        return gate.Outcome == GateMeasurementOutcome.Measured
            ? gate with {
                AgentEval = AgentEvalProvenance.Independent(
                    "matched-quality",
                    "AgentEval.Evals.AtomicCodeEval",
                    "IEvaluator criterion observation → direct EvalInput → BenchmarkArm/BenchmarkRunner · offline deterministic",
                    $"{execution.DemoScope}; deployment none (offline deterministic)",
                    observations,
                    "run-local immutable matched-subject observation",
                    execution.SubjectEngine,
                    typeof(JudgedQualityProductionEval).FullName!),
            }
            : gate;
    }
    internal static string DescribeOptionalCount(int? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    internal static IReadOnlyList<JoinedCriterion>? JoinCanonicalCriteria(
        IReadOnlyList<AgentEval.Core.CriterionResult>? results) {
        if (results is null) return null;
        var joined = new List<JoinedCriterion>(results.Count);
        foreach (var result in results) {
            var matches = VitrineEvalCriteria.JudgedCriteria
                .Where(definition => string.Equals(
                    NormalizeCriterion(definition.Text),
                    NormalizeCriterion(result.Criterion),
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1 || joined.Any(item => item.Definition.Id == matches[0].Id)) return null;
            joined.Add(new JoinedCriterion(matches[0], result));
        }
        return joined.Count == VitrineEvalCriteria.JudgedCriteria.Length ? joined : null;
    }
    private static string NormalizeCriterion(string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"^\d+[.)]\s*", string.Empty),
            @"\s+",
            " ").ToUpperInvariant();
    internal static async Task<GraderPanelProbe> EvaluateGraderSanityAsync(
        AgentEval.Core.IEvaluator evaluator,
        string positiveOutput,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(evaluator);
        var positive = await evaluator.EvaluateAsync(
            "authored recommendation request",
            positiveOutput,
            VitrineEvalCriteria.Judged,
            cancellationToken).ConfigureAwait(false);
        var negative = await evaluator.EvaluateAsync(
            "authored recommendation request",
            "There is nothing relevant to recommend.",
            VitrineEvalCriteria.Judged,
            cancellationToken).ConfigureAwait(false);
        return new GraderPanelProbe([
            new("positive", true,
                !positive.EvaluationFailed && positive.OverallScore >= VitrineEvalCriteria.JudgedPassingScore),
            new("negative", false,
                !negative.EvaluationFailed && negative.OverallScore >= VitrineEvalCriteria.JudgedPassingScore),
        ]);
    }
    internal sealed record JoinedCriterion(
        EvalCriterionDefinition Definition,
        AgentEval.Core.CriterionResult Result);
    internal sealed record JudgedSubjectBinding(
        string Slot,
        string Subject,
        int SourcePresented,
        int ArtifactSkuCount);
    internal sealed record JudgedGateDiagnostics(
        IReadOnlyList<AgentEval.Core.CriterionResult> Demo01Criteria,
        IReadOnlyList<AgentEval.Core.CriterionResult> Demo02Criteria,
        int JudgeCalls,
        JudgedSubjectBinding Demo01Binding,
        JudgedSubjectBinding Demo02Binding);
    private static async Task<JudgedSubjectEvaluation> RunJudgedSubjectAsync(
        AgentEval.Core.IEvaluator evaluator,
        JudgedArtifact subject,
        string query,
        CancellationToken cancellationToken) {
        var result = await evaluator.EvaluateAsync(
            query,
            subject.Text,
            VitrineEvalCriteria.Judged,
            cancellationToken).ConfigureAwait(false);
        return new JudgedSubjectEvaluation(subject, result, CountArtifactSkus(subject.Text));
    }
    private static EvaluationExecutionProvenance PlannedExecution() =>
        ObservedExecution(demo01ModelCalls: null, demo02ModelCalls: null, judgeModelCalls: 0);
    private static EvaluationExecutionProvenance ObservedExecution(
        int? demo01ModelCalls,
        int? demo02ModelCalls,
        int? judgeModelCalls) =>
        new(
            EvaluationExecutionProfile.OfflineDeterministic,
            $"Demo01 single agent + Demo02 MAF workflow · persona {Personas.NadiaUserId} · matched k={VitrineEvalCriteria.JudgedMatchedK}",
            "RecommendationRunEngine.ScriptedAgent + deterministic Discovery IChatClient",
            "DeterministicCriteriaEvaluator via AgentEval.Core.IEvaluator",
            DeploymentName: null,
            demo01ModelCalls,
            demo02ModelCalls,
            judgeModelCalls,
            Demo01SubjectTokens: null,
            Demo02SubjectTokens: null,
            JudgeTokens: null,
            EstimatedCostUsd: null,
            UsesExternalModels: false);
    private static int CountArtifactSkus(string artifact) =>
        System.Text.RegularExpressions.Regex.Matches(
                artifact,
                @"\bGLX-\d{4}\b",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Count();
    private enum JudgedArtifactOrigin { Demo01, Demo02 }
    private sealed record JudgedArtifact(JudgedArtifactOrigin Origin, string Text, int SourcePresented);
    private sealed record JudgedSubjectEvaluation(
        JudgedArtifact Artifact,
        AgentEval.Core.EvaluationResult Result,
        int ArtifactSkuCount);
    private static string SlotFor(JudgedArtifactOrigin origin) => origin switch {
        JudgedArtifactOrigin.Demo01 => "demo01",
        JudgedArtifactOrigin.Demo02 => "demo02",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    private static string NameFor(JudgedArtifactOrigin origin) => origin switch {
        JudgedArtifactOrigin.Demo01 => "Demo01 screened output",
        JudgedArtifactOrigin.Demo02 => "Demo02 screened output",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };
    private static string ComposeMatchedWorkflowArtifact(DiscoveryRunResult run, int matchedK) {
        var items = run.State.Screened?.Outcome.Cleaned.AllPresented.Take(matchedK).ToArray() ?? [];
        if (items.Length != matchedK) return string.Empty;
        var interest = run.State.Interests.FirstOrDefault()?.Label ?? "your stated need";
        var builder = new System.Text.StringBuilder($"Because of your {interest} interest:{Environment.NewLine}");
        foreach (var item in items) {
            var name = Catalogue.Default.TryGet(item.ProductId, out var product) && product is not null
                ? product.Name
                : item.ProductId;
            builder.Append("  · ").Append(name).Append(" (").Append(item.ProductId).Append(") — ")
                .AppendLine(item.WhyThis);
        }
        builder.Append("The choice remains yours.");
        return builder.ToString();
    }
    internal static async Task<GateResult> InjectionGateAsync(CancellationToken cancellationToken) {
        var direct = new AgentEval.RedTeam.Attacks.PromptInjectionAttack();
        var indirect = new AgentEval.RedTeam.Attacks.IndirectInjectionAttack();
        var textOptions = new AgentEval.RedTeam.ScanOptions {
            AttackTypes = [direct, indirect],
            Intensity = AgentEval.RedTeam.Intensity.Quick,
            MaxProbesPerAttack = 4,
            IncludeEvidence = false,
            TimeoutPerProbe = TimeSpan.FromSeconds(2)
        };
        var runner = new AgentEval.RedTeam.RedTeamRunner();
        var safe = await runner.ScanAsync(new InjectionSafeAgent(), textOptions, cancellationToken).ConfigureAwait(false);
        var vulnerable = await runner.ScanAsync(new InjectionVulnerableAgent(), textOptions, cancellationToken).ConfigureAwait(false);
        var boundaryOptions = new AgentEval.RedTeam.ScanOptions {
            AttackTypes = [indirect],
            Intensity = AgentEval.RedTeam.Intensity.Moderate,
            IncludeEvidence = false,
            TimeoutPerProbe = TimeSpan.FromSeconds(2),
        };
        var safeBoundary = await runner.ScanAsync(
            new AgentEval.RedTeam.InstrumentedCanaryAgent(
                new CausalToolBoundaryChatClient(followsPoison: false),
                "VITRINE tool-output safe subject"),
            boundaryOptions,
            cancellationToken).ConfigureAwait(false);
        var vulnerableBoundary = await runner.ScanAsync(
            new AgentEval.RedTeam.InstrumentedCanaryAgent(
                new CausalToolBoundaryChatClient(followsPoison: true),
                "VITRINE tool-output vulnerable ablation"),
            boundaryOptions,
            cancellationToken).ConfigureAwait(false);
        var safeToolOutput = ToolOutputProbes(safeBoundary);
        var vulnerableToolOutput = ToolOutputProbes(vulnerableBoundary);
        var safeToolResisted = safeToolOutput.Count(probe =>
            probe.Outcome == AgentEval.RedTeam.EvaluationOutcome.Resisted);
        var safeToolSucceeded = safeToolOutput.Count(probe =>
            probe.Outcome == AgentEval.RedTeam.EvaluationOutcome.Succeeded);
        var safeToolInconclusive = safeToolOutput.Count(probe =>
            probe.Outcome == AgentEval.RedTeam.EvaluationOutcome.Inconclusive);
        var safeToolBehavioral = safeToolOutput.Count(probe =>
            probe.Fidelity == AgentEval.RedTeam.EvidenceFidelity.Behavioral);
        var vulnerableToolResisted = vulnerableToolOutput.Count(probe =>
            probe.Outcome == AgentEval.RedTeam.EvaluationOutcome.Resisted);
        var vulnerableToolSucceeded = vulnerableToolOutput.Count(probe =>
            probe.Outcome == AgentEval.RedTeam.EvaluationOutcome.Succeeded);
        var vulnerableToolInconclusive = vulnerableToolOutput.Count(probe =>
            probe.Outcome == AgentEval.RedTeam.EvaluationOutcome.Inconclusive);
        var vulnerableToolBehavioral = vulnerableToolOutput.Count(probe =>
            probe.Fidelity == AgentEval.RedTeam.EvidenceFidelity.Behavioral);
        var safeInput = InjectionProductionEval.Input(new(
            safe.TotalProbes,
            safe.ResistedProbes,
            safe.SucceededProbes,
            safe.InconclusiveProbes,
            safeToolOutput.Count,
            safeToolResisted,
            safeToolSucceeded,
            safeToolInconclusive,
            safeToolBehavioral));
        var vulnerableInput = InjectionProductionEval.Input(new(
            vulnerable.TotalProbes,
            vulnerable.ResistedProbes,
            vulnerable.SucceededProbes,
            vulnerable.InconclusiveProbes,
            vulnerableToolOutput.Count,
            vulnerableToolResisted,
            vulnerableToolSucceeded,
            vulnerableToolInconclusive,
            vulnerableToolBehavioral));
        var variant = await ProductionVariantBenchmarks.RunInjectionAsync(
            safeInput, vulnerableInput, cancellationToken).ConfigureAwait(false);
        var gate = GateFromVariantBenchmark(
            "RedTeam injection · direct + indirect",
            VitrineProductionChecks.Injection,
            variant,
            ProductionVariantBenchmarks.InjectionVulnerableArmId,
            $"text safe resisted {safe.ResistedProbes}/{safe.TotalProbes}; text ablation compromised {vulnerable.SucceededProbes}/{vulnerable.TotalProbes}; " +
            $"delivered {safeToolOutput.Count} {AgentEval.RedTeam.InjectionSurface.ToolOutput} probes per arm with conclusive outcomes: " +
            $"safe resisted {safeToolResisted}, causal ablation produced {vulnerableToolSucceeded} behavioral compromises; distinct runs " +
            $"{string.Join(", ", variant.Arms.Select(static arm => arm.Run.RunId))}.");
        return gate.Outcome == GateMeasurementOutcome.Measured
            ? gate with {
                AgentEval = AgentEvalProvenance.Independent(
                    "injection",
                    "AgentEval.Evals.AtomicCodeEval",
                    "RedTeam raw probe counts → BenchmarkArm/BenchmarkRunner → BenchmarkScore.AgainstReference",
                    "safe and deliberately vulnerable offline arms",
                    [
                        new("text.safe", "resisted", (double)safe.ResistedProbes / safe.TotalProbes,
                            AgentEval.RedTeam.InjectionSurface.UserMessage.ToString(), safe.TotalProbes),
                        new("text.vulnerable", "compromised", (double)vulnerable.SucceededProbes / vulnerable.TotalProbes,
                            AgentEval.RedTeam.InjectionSurface.UserMessage.ToString(), vulnerable.TotalProbes),
                        new("tool-output.safe", "resisted", (double)safeToolResisted / safeToolOutput.Count,
                            AgentEval.RedTeam.InjectionSurface.ToolOutput.ToString(), safeToolOutput.Count),
                        new("tool-output.vulnerable", "compromised", (double)vulnerableToolSucceeded / vulnerableToolOutput.Count,
                            AgentEval.RedTeam.InjectionSurface.ToolOutput.ToString(), vulnerableToolOutput.Count),
                    ],
                    "run-local immutable RedTeam result projection",
                    "AgentEval.RedTeam.RedTeamRunner.ScanAsync",
                    typeof(InjectionProductionEval).FullName!),
            }
            : gate;
    }
    private static IReadOnlyList<AgentEval.RedTeam.ProbeResult> ToolOutputProbes(
        AgentEval.RedTeam.RedTeamResult result) =>
        result.AttackResults
            .SelectMany(attack => attack.ProbeResults)
            .Where(probe => probe.Surface == AgentEval.RedTeam.InjectionSurface.ToolOutput)
            .ToArray();
    internal static async Task<GateResult> RecallGateAsync(CancellationToken cancellationToken) {
        var available = AgentEval.Memory.DataLoading.CorpusLoader.ListAvailable();
        if (!available.Contains(VitrineEvalCriteria.RecallNoiseCorpus, StringComparer.Ordinal)) {
            var reason = $"AgentEval.Memory did not ship the declared {VitrineEvalCriteria.RecallNoiseCorpus} corpus.";
            var missing = RecallProductionEval.FailedInput(reason);
            var unavailableVariant = await ProductionVariantBenchmarks.RunRecallAsync(
                missing, missing, cancellationToken).ConfigureAwait(false);
            return GateFromVariantBenchmark(
                "Memory · stated constraints",
                VitrineProductionChecks.Recall,
                unavailableVariant,
                ProductionVariantBenchmarks.RecallAblatedArmId,
                reason);
        }
        var noise = AgentEval.Memory.DataLoading.CorpusLoader.Load(VitrineEvalCriteria.RecallNoiseCorpus, 2);
        var facts = VitrineEvalCriteria.RecalledConstraints
            .Select(AgentEval.Memory.Models.MemoryFact.Create)
            .ToArray();
        var scenario = new AgentEval.Memory.Models.MemoryTestScenario {
            Name = "Customer constraint recall",
            Description = "Did the system recall this customer's stated budget and waterproofing constraints?",
            Steps =
            [
                .. facts.Select(fact => AgentEval.Memory.Models.MemoryStep.Fact($"Customer constraint: {fact.Content}")),
                .. noise.Select(turn => AgentEval.Memory.Models.MemoryStep.Noise(turn.UserMessage))
            ],
            Queries =
            [
                AgentEval.Memory.Models.MemoryQuery.Create(
                    "Which constraints must the recommendations satisfy?",
                    facts)
            ]
        };
        var runner = new AgentEval.Memory.Engine.MemoryTestRunner(
            new IndependentMemoryJudge(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentEval.Memory.Engine.MemoryTestRunner>.Instance);
        var healthy = await runner.RunAsync(new RecommendationMemoryAgent(), scenario, cancellationToken).ConfigureAwait(false);
        var ablated = await runner.RunAsync(new RecommendationMemoryAgent(ablated: true), scenario, cancellationToken).ConfigureAwait(false);
        var healthyInput = RecallProductionEval.Input(new(
            healthy.TotalQueries,
            healthy.QueryResults.Count,
            healthy.OverallScore));
        var ablatedInput = RecallProductionEval.Input(new(
            ablated.TotalQueries,
            ablated.QueryResults.Count,
            ablated.OverallScore));
        var variant = await ProductionVariantBenchmarks.RunRecallAsync(
            healthyInput, ablatedInput, cancellationToken).ConfigureAwait(false);
        var gate = GateFromVariantBenchmark(
            "Memory · stated customer constraints",
            VitrineProductionChecks.Recall,
            variant,
            ProductionVariantBenchmarks.RecallAblatedArmId,
            $"CorpusLoader '{VitrineEvalCriteria.RecallNoiseCorpus}' added {noise.Count} distractor turns; the real ChatClientAgent adapter recalled " +
            $"{healthy.FoundFacts.Count}/{facts.Length}; provider ablation score {ablated.OverallScore:F0}%; distinct runs " +
            $"{string.Join(", ", variant.Arms.Select(static arm => arm.Run.RunId))}.");
        return gate.Outcome == GateMeasurementOutcome.Measured
            ? gate with {
                AgentEval = AgentEvalProvenance.Independent(
                    "recall",
                    "AgentEval.Evals.AtomicCodeEval",
                    "MemoryTestRunner raw query observations → BenchmarkArm/BenchmarkRunner → BenchmarkScore.AgainstReference",
                    "recommendation ChatClientAgent and provider ablation",
                    [
                        new("memory.healthy", "recalled", healthy.OverallScore / 100.0,
                            "conversation", healthy.TotalQueries),
                        new("memory.ablated", "degraded", ablated.OverallScore / 100.0,
                            "conversation", ablated.TotalQueries),
                    ],
                    "run-local immutable memory query projection",
                    "AgentEval.Memory.Engine.MemoryTestRunner.RunAsync",
                    typeof(RecallProductionEval).FullName!),
            }
            : gate;
    }
    internal static bool HasCompleteRecallMeasurements(
        int expectedQueries,
        params (int TotalQueries, int ResultCount)[] arms) =>
        expectedQueries > 0 && arms.Length > 0 &&
        arms.All(arm => arm.TotalQueries == expectedQueries && arm.ResultCount == expectedQueries);
    private static async Task<GateResult> HonestyGateAsync(CancellationToken cancellationToken) {
        var loaded = HonestyEvidenceLoader.Load();
        if (!loaded.Measured || loaded.Evidence is not { } evidence) {
            var failureKind = loaded.FailureKind ?? "UnknownEvidenceFailure";
            var invalidArtifact = failureKind is "EvidenceContainsDisallowedContent" or
                "EvidenceIntegrityMismatch" or "EvidenceSchemaInvalid";
            return await RunProductionEvalAsync(
                "Honesty · committed measurement evidence",
                VitrineProductionChecks.Honesty,
                invalidArtifact
                    ? HonestyProductionEval.Input(new(null, failureKind))
                    : HonestyProductionEval.FailedInput(
                        $"the required committed honesty evidence could not be loaded ({failureKind})."),
                invalidArtifact
                    ? $"Committed honesty evidence failed validation ({failureKind})."
                    : $"Committed honesty evidence was not measured ({failureKind}).",
                "HonestyEvidenceLoader.Load",
                cancellationToken).ConfigureAwait(false);
        }
        var minimumP = AgentEval.Evals.Meta.ExactTests.MinimumAttainableP(
            evidence.NextPurchase.InformativePairs);
        var claims = HonestyInterpretation.Build(evidence);
        var gate = await RunProductionEvalAsync(
            "Honesty · validated committed measurement evidence",
            VitrineProductionChecks.Honesty,
            HonestyProductionEval.Input(new(evidence)),
            $"{claims.NextPurchasePrediction}. {claims.StatedNeedSatisfaction}. Evidence {evidence.MeasurementId}; checksum and schema validated.",
            "HonestyEvidenceLoader.Load",
            cancellationToken).ConfigureAwait(false);
        return gate.Outcome == GateMeasurementOutcome.Measured
            ? gate with {
                HonestInterpretation = claims,
                AgentEval = AgentEvalProvenance.Independent(
                    "honesty",
                    "AgentEval.Evals.AtomicCodeEval",
                    "committed raw measurements → AgentEvalBuilder.AddEval",
                    evidence.MeasurementId,
                    [
                        new("stated-need.live", "shown", evidence.StatedNeed.LiveScore, SampleCount: evidence.StatedNeed.CaseIds.Count),
                        new("stated-need.oracle", "reference", evidence.StatedNeed.OracleScore, SampleCount: evidence.StatedNeed.CaseIds.Count),
                        new("next-purchase.minimum-p", "underpowered", minimumP, SampleCount: evidence.NextPurchase.InformativePairs),
                        new("tag-join.model-calls", "baseline", evidence.Baseline.ModelCalls, SampleCount: evidence.StatedNeed.CaseIds.Count),
                    ],
                    "committed immutable evidence artifact",
                    "HonestyEvidenceLoader.Load",
                    typeof(HonestyProductionEval).FullName!),
            }
            : gate;
    }
    private static async Task<GateResult> RunProductionEvalAsync(
        string name,
        VitrineProductionCheck check,
        AgentEval.Evals.EvalInput input,
        string evidence,
        string observationProducer,
        CancellationToken cancellationToken) {
        var result = await VitrineProductionChecks
            .EvaluateAdmittedAsync(check, input, cancellationToken)
            .ConfigureAwait(false);
        var evaluatorSummary = result.Details.Summary;
        var fullEvidence = string.IsNullOrWhiteSpace(evaluatorSummary)
            ? evidence
            : $"{evidence} Evaluator observation: {evaluatorSummary}";
        var gate = GateResult.FromAdmitted(name, result, check.Floor, fullEvidence);
        var evalType = check.EvalFactory().GetType().FullName ?? "AgentEval.Evals.AtomicCodeEval";
        var measured = global::AgentEval.Evals.EvalScoreExtensions.CountsTowardAggregate(result.Score);
        var state = global::AgentEval.Evals.EvalScoreExtensions.CensusBucket(result.Score);
        return gate with {
            AgentEval = AgentEvalProvenance.Independent(
                check.Key,
                "AgentEval.Evals.AtomicCodeEval",
                $"{evalType} via AgentEvalBuilder.AddEval",
                input.CaseId ?? check.Key,
                [new(check.Key, measured ? result.Score.Passed ? "pass" : "fail" : state.ToString(),
                    measured ? result.Score.Value : null, SampleCount: measured ? 1 : 0)],
                "run-local immutable raw observation",
                observationProducer,
                evalType),
        };
    }
    private static GateResult GateFromVariantBenchmark(
        string name,
        VitrineProductionCheck check,
        ProductionVariantBenchmarkOutcome outcome,
        string alternativeArmId,
        string evidence) {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Arms.Count != 2)
            return GateResult.InstrumentError(name, check.Floor, typeof(InvalidDataException));
        var reference = outcome.Arm(outcome.ReferenceArmId);
        var alternative = outcome.Arm(alternativeArmId);
        var comparison = outcome.ReferenceComparison;
        var alternativeState = global::AgentEval.Evals.EvalScoreExtensions.CensusBucket(
            alternative.Result.Score);
        var diagnosticEvidence =
            $"{evidence} Native reference comparison (diagnostic, not gate authority) " +
            $"W/L/T={comparison.Wins}/{comparison.Losses}/{comparison.Ties}; alternative state " +
            $"{alternativeState}; " +
            $"persisted under {outcome.WorkspaceRoot}.";
        GateResult AttachProvenance(GateResult gate) => gate with {
            AgentEval = AgentEvalProvenance.Independent(
                check.Key,
                "AgentEval.Evals.AtomicCodeEval",
                $"{outcome.Definition.Key}@{outcome.Definition.Version} via BenchmarkArm/BenchmarkRunner and BenchmarkScore.AgainstReference",
                $"reference {outcome.ReferenceArmId}; alternative {alternativeArmId}",
                outcome.Arms.Select(arm => {
                    var counts = global::AgentEval.Evals.EvalScoreExtensions.CountsTowardAggregate(arm.Result.Score);
                    var state = global::AgentEval.Evals.EvalScoreExtensions.CensusBucket(arm.Result.Score);
                    return new AgentEvalObservation(
                        arm.ArmId,
                        counts ? arm.Result.Score.Passed ? "pass" : "fail" : state.ToString(),
                        counts ? arm.Result.Score.Value : null,
                        "BenchmarkRunner arm",
                        counts ? 1 : 0);
                }).ToArray(),
                "run-local immutable per-arm observations",
                "BenchmarkRunner.RunAsync",
                check.EvalFactory().GetType().FullName ?? "AgentEval.Evals.AtomicCodeEval"),
        };
        // The admitted reference-arm result is the product gate. AgainstReference and the
        // deliberately degraded arm are persisted diagnostic facts whose expected direction
        // is enforced by --self-test, never a second unadmitted pass/fail policy here.
        return AttachProvenance(GateResult.FromAdmitted(
            name, reference.Result, check.Floor, diagnosticEvidence));
    }
    private static async Task<GateResult> RunUnavailableJudgedVariantAsync(
        string name,
        AgentEval.Evals.EvalInput unavailableInput,
        string evidence,
        CancellationToken cancellationToken) {
        var variant = await ProductionVariantBenchmarks.RunJudgedQualityAsync(
            unavailableInput, unavailableInput, cancellationToken).ConfigureAwait(false);
        return GateFromVariantBenchmark(
            name,
            VitrineProductionChecks.JudgedQuality,
            variant,
            ProductionVariantBenchmarks.JudgedDemo02ArmId,
            evidence);
    }
    private static AgentEval.Evals.Meta.ChanceFloor? ProductionFloorFor(string id) => id switch {
        "catalogue" => VitrineProductionChecks.Catalogue.Floor,
        "topology" => VitrineProductionChecks.Topology.Floor,
        "judged" => VitrineProductionChecks.JudgedQuality.Floor,
        "injection" => VitrineProductionChecks.Injection.Floor,
        "recall" => VitrineProductionChecks.Recall.Floor,
        "honesty" => VitrineProductionChecks.Honesty.Floor,
        _ => null,
    };
}
