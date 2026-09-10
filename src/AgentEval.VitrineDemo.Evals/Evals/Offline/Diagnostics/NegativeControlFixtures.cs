// SPDX-License-Identifier: MIT
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using Galaxus.RecommendationAgent.Domain;
using Galaxus.RecommendationAgent.Evaluation;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Retrieval;
using Galaxus.RecommendationAgent.Rendering;
using Galaxus.RecommendationAgent.Tools;
using Galaxus.RecommendationAgent.Workflows;
using AgentEval.Evals.Meta;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
namespace AgentEval.VitrineDemo.Evals;
internal sealed record ControlEnvironment(
    Catalogue Catalogue,
    Product CitedProduct,
    string ResolvableCitation,
    Product FactProduct,
    string FactKey,
    string FactValue,
    int ProductCount,
    int PersonaCount,
    int ToolCount,
    int ConceptVectorCount,
    int ConceptVectorDimensions,
    bool ConceptVectorsFinite) {
    public DenseWipeoutPanelProbe? DenseWipeoutObservation { get; init; }
    public RecommendationRunResult? NadiaAgentRun { get; init; }
    public RecommendationRunResult? NadiaBaselineRun { get; init; }
    public RecommendationRunResult? SofiaBaselineRun { get; init; }
    public DiscoveryRunResult? LoopedWorkflowRun { get; init; }
    public DiscoveryRunResult? BoundedWorkflowRun { get; init; }
    public DiscoveryRunResult? AlwaysApproveWorkflowRun { get; init; }
    public DiscoveryRunResult? SofiaWorkflowRun { get; init; }
    public DiscoveryTopologyObservation? PreparedTopology { get; init; }
    public CommittedVectorContentPanelProbe? CommittedVectorRuns { get; init; }
    public IReadOnlyList<TerminationProbeResult>? TerminationObservations { get; init; }
    public ProductionControlBaselines? Production { get; init; }
    public static ControlEnvironment Capture() {
        var catalogue = Catalogue.Default;
        var cited = catalogue.All.First(product => product.ReviewIds.Count > 0);
        var reviewId = cited.ReviewIds.Order(StringComparer.Ordinal).First();
        var factProduct = catalogue.All.First(product => product.Specs.Count > 0);
        var fact = factProduct.Specs.OrderBy(pair => pair.Key, StringComparer.Ordinal).First();
        var vectors = catalogue.All
            .Select(product => ConceptEmbeddingSource.Instance.Embed($"{product.Name} {product.Description}"))
            .ToArray();
        return new ControlEnvironment(
            catalogue,
            cited,
            $"review:{reviewId}",
            factProduct,
            fact.Key,
            fact.Value,
            catalogue.All.Count,
            Personas.AllPersonaIds.Count,
            RecommendationAgentFactory.BuildReadOnlyTools().Length +
                RecommendationAgentFactory.BuildApprovalGatedCommitTools().Length,
            vectors.Length,
            vectors.Select(vector => vector.Length).Distinct().Single(),
            vectors.SelectMany(vector => vector).All(float.IsFinite));
    }
    public static async Task<ControlEnvironment> CaptureProductionAsync(CancellationToken cancellationToken) {
        var environment = Capture();
        var impossibleRetriever = await HybridRetriever.BuildAsync(
            environment.Catalogue.All,
            ConceptEmbeddingSource.Instance,
            new HybridRetrieverOptions { DenseScoreFloor = 1.1f },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var unreachableRetriever = await HybridRetriever.BuildAsync(
            environment.Catalogue.All,
            ConceptEmbeddingSource.Instance,
            new HybridRetrieverOptions { DenseScoreFloor = -1.1f },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var retrievalQuery = new RetrievalQuery {
            Need = OfflineRecommendationScript.SearchNeed,
            TopK = 6,
        };
        var impossibleDense = (await impossibleRetriever.SearchAsync(retrievalQuery, cancellationToken).ConfigureAwait(false)).Retrieval;
        var unreachableDense = (await unreachableRetriever.SearchAsync(retrievalQuery, cancellationToken).ConfigureAwait(false)).Retrieval;
        var nadia = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(Personas.NadiaUserId, Arm: RecommendationExecutionArm.ScriptedAgent),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var nadiaBaseline = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(Personas.NadiaUserId, Arm: RecommendationExecutionArm.ZeroModelBaseline),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var sofia = await RecommendationRunEngine.RunAsync(
            new RecommendationRunOptions(Personas.SofiaUserId, Arm: RecommendationExecutionArm.ZeroModelBaseline),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        AgentEval.Models.WorkflowGraphSnapshot? preparedGraph = null;
        IReadOnlyList<string>? preparedExecutorIds = null;
        using var workflowClient = new DeterministicDiscoveryChatClient();
        var looped = await GalaxusDiscoveryLoop.RunAsync(
            Personas.NadiaUserId,
            new DiscoveryLoopOptions(
                Offline: false,
                ChatClient: workflowClient,
                MaxRounds: 3,
                WorkflowPrepared: (workflow, ids) => {
                    preparedExecutorIds = ids.ToArray();
                    preparedGraph = AgentEval.MAF.MAFWorkflowAdapter.FromMAFWorkflow(
                        workflow, DiscoveryWorkflowFactory.WorkflowName, ids, "bounded-review-loop").GraphDefinition;
                }),
            cancellationToken).ConfigureAwait(false);
        using var boundedClient = new DeterministicDiscoveryChatClient();
        var bounded = await GalaxusDiscoveryLoop.RunAsync(
            Personas.NadiaUserId,
            new DiscoveryLoopOptions(Offline: false, ChatClient: boundedClient, MaxRounds: 1),
            cancellationToken).ConfigureAwait(false);
        using var alwaysApproveClient = new DeterministicDiscoveryChatClient();
        var alwaysApprove = await GalaxusDiscoveryLoop.RunAsync(
            Personas.NadiaUserId,
            new DiscoveryLoopOptions(
                Offline: false,
                ChatClient: alwaysApproveClient,
                MaxRounds: 3,
                Nodes: new DiscoveryNodeOverrides(Reviewer: new ScriptedReviewer(ScriptedReviewer.Mode.AlwaysApprove))),
            cancellationToken).ConfigureAwait(false);
        using var sofiaWorkflowClient = new DeterministicDiscoveryChatClient();
        var sofiaWorkflow = await GalaxusDiscoveryLoop.RunAsync(
            Personas.SofiaUserId,
            new DiscoveryLoopOptions(Offline: false, ChatClient: sofiaWorkflowClient, MaxRounds: 3),
            cancellationToken).ConfigureAwait(false);
        var termination = await DiscoveryTerminationProbe.RunAllAsync(cancellationToken).ConfigureAwait(false);
        if (preparedGraph is null || preparedExecutorIds is null)
            throw new InvalidDataException("The production workflow did not expose its prepared graph.");
        var preparedTopology = new DiscoveryTopologyObservation(
            preparedExecutorIds,
            preparedGraph.Edges.Select(static edge => (edge.SourceExecutorId, edge.TargetExecutorId)).ToArray());
        var committedSource = PrecomputedEmbeddingSource.Load(environment.Catalogue.All);
        var healthyVectorRun = CommittedVectorContentPolicy.Observe(committedSource, environment.Catalogue.All);
        var productsById = environment.Catalogue.All.ToDictionary(static product => product.Id, StringComparer.Ordinal);
        var committedVectors = CommittedVectorContentPolicy.RequiredAnchors.ToDictionary(
            static anchor => anchor.ProductId,
            anchor => {
                if (!committedSource.TryGetCommitted(EmbeddingDocument.ForProduct(productsById[anchor.ProductId]), out var vector))
                    throw new InvalidDataException($"Committed vector anchor '{anchor.ProductId}' was unavailable.");
                return vector;
            },
            StringComparer.Ordinal);
        var substitutedVectors = committedVectors.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        substitutedVectors["GLX-1001"] = committedVectors["GLX-2001"];
        var brokenVectorRun = CommittedVectorContentPolicy.Observe(
            environment.Catalogue.All,
            substitutedVectors,
            committedSource.Name,
            committedSource.ModelId,
            committedSource.Dimensions);
        var positiveOutput = RecommendationArtifactComposer.Compose(nadia);
        var graderCases = await EvaluationSuite.EvaluateGraderSanityAsync(
            new DeterministicCriteriaEvaluator(), positiveOutput, cancellationToken).ConfigureAwait(false);
        var brokenGraderCases = await EvaluationSuite.EvaluateGraderSanityAsync(
            new AlwaysPassCriteriaEvaluator(), positiveOutput, cancellationToken).ConfigureAwait(false);
        var honesty = HonestyEvidenceLoader.Load().Evidence
            ?? throw new InvalidDataException("The committed honesty evidence was not available to production controls.");
        var honestyClaims = HonestyInterpretation.Build(honesty);
        var forcedChoiceCalibration = ForcedChoiceCalibrationFixture.Capture();
        EvaluationSuite.JudgedQualityDiagnosticDetails? matchedDiagnostics = null;
        var matchedQualityDiagnostic = await EvaluationSuite.JudgedGateAsync(
            cancellationToken,
            diagnostics => matchedDiagnostics = diagnostics).ConfigureAwait(false);
        if (matchedDiagnostics is null)
            throw new InvalidDataException("The matched-quality diagnostic produced no boundary diagnostics.");
        var cleaned = nadia.Outcome?.Cleaned.AllPresented.FirstOrDefault()
            ?? throw new InvalidDataException("The production Nadia run produced no screened recommendation for guardrail controls.");
        var interestMap = nadia.InterestMap
            ?? throw new InvalidDataException("The production Nadia run produced no interest map for guardrail controls.");
        var profile = nadia.Profile
            ?? throw new InvalidDataException("The production Nadia run produced no profile for guardrail controls.");
        var guardrailContext = new GuardrailContext {
            ProductsBySku = environment.Catalogue.BySku,
            User = profile.User,
            InterestMap = interestMap,
            UserPurchaseIds = cleaned.Evidence.UserPurchaseIds.ToHashSet(StringComparer.Ordinal),
            CandidateProductIds = environment.Catalogue.All.Select(static product => product.Id).ToHashSet(StringComparer.Ordinal),
        };
        var guardrailPresentation = new GuardrailPresentationProbe(
            new PresentedRecommendation(
                cleaned.ProductId,
                cleaned.WhyThis,
                cleaned.Evidence.Citation.ToString()),
            guardrailContext);
        IReadOnlyList<ToolCallFact> groundedCommitTrace;
        using (ToolCallBudget.BeginScope()) {
            await GalaxusTools.GetProductDetails(cleaned.ProductId, cancellationToken).ConfigureAwait(false);
            await GalaxusTools.PlaceOrder(cleaned.ProductId, cancellationToken: cancellationToken).ConfigureAwait(false);
            groundedCommitTrace = ToolCallBudget.CallFacts.ToArray();
        }
        IReadOnlyList<ToolCallFact> blindCommitTrace;
        using (ToolCallBudget.BeginScope()) {
            await GalaxusTools.PlaceOrder(cleaned.ProductId, cancellationToken: cancellationToken).ConfigureAwait(false);
            await GalaxusTools.GetProductDetails(cleaned.ProductId, cancellationToken).ConfigureAwait(false);
            blindCommitTrace = ToolCallBudget.CallFacts.ToArray();
        }
        var refusalBoundary = await ToolRefusalBoundary.ObserveAsync(
            ToolResultCodeDetector.ExactDeclaredCode, cancellationToken).ConfigureAwait(false);
        var stringOnlyRefusalBoundary = await ToolRefusalBoundary.ObserveAsync(
            ToolResultCodeDetector.LegacyStringOnly, cancellationToken).ConfigureAwait(false);
        var looseCodeRefusalBoundary = await ToolRefusalBoundary.ObserveAsync(
            ToolResultCodeDetector.LooseSubstring, cancellationToken).ConfigureAwait(false);
        var reportPath = Path.Combine(Path.GetTempPath(), $"vitrine-write-ledger-{Guid.NewGuid():N}.html");
        ReportWriteProbe reportWrite;
        try {
            var notBefore = DateTimeOffset.UtcNow.AddSeconds(-2);
            var receipt = await EvaluationReportWriter.WriteAsync(
                reportPath,
                EvaluationReportHtml.Render(new SuiteResult([], [])),
                cancellationToken).ConfigureAwait(false);
            var file = new FileInfo(reportPath);
            reportWrite = new ReportWriteProbe(
                receipt,
                file.FullName,
                file.Length,
                file.Exists && file.LastWriteTimeUtc >= notBefore.UtcDateTime);
        }
        finally {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
        var ciExecution = await ObserveCiPlanAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var offlineGates = await EvaluationSuite.RunAsync(
            cancellationToken: cancellationToken,
            includeControls: false,
            persistOfflineBenchmark: false).ConfigureAwait(false);
        var admittedChecks = await VitrineAdmittedChecksSelfTest.RunAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var missingGate = await EvaluationSuite.ObserveGateForTestAsync(
            () => Task.FromResult(GateResult.NotMeasured(
                "production missing observation",
                null,
                "NOT MEASURED: no replicate spread was supplied.")),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var instrumentGate = await EvaluationSuite.ObserveGateForTestAsync(
            () => Task.FromException<GateResult>(new InvalidOperationException("withheld control probe")),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var catalogueFailureGate = await EvaluationSuite.CatalogueGateForControlAsync(
            leaveAblated: true,
            cancellationToken).ConfigureAwait(false);
        return environment with {
            DenseWipeoutObservation = new DenseWipeoutPanelProbe(impossibleDense, unreachableDense),
            NadiaAgentRun = nadia,
            NadiaBaselineRun = nadiaBaseline,
            SofiaBaselineRun = sofia,
            LoopedWorkflowRun = looped,
            BoundedWorkflowRun = bounded,
            AlwaysApproveWorkflowRun = alwaysApprove,
            SofiaWorkflowRun = sofiaWorkflow,
            PreparedTopology = preparedTopology,
            CommittedVectorRuns = new CommittedVectorContentPanelProbe(healthyVectorRun, brokenVectorRun, UseBroken: false),
            TerminationObservations = termination,
            Production = new ProductionControlBaselines(
                new GraderRunSelectionProbe(graderCases, brokenGraderCases, UseBroken: false),
                honesty,
                honestyClaims,
                forcedChoiceCalibration,
                matchedQualityDiagnostic,
                matchedDiagnostics,
                guardrailPresentation,
                new CommitOrderingProbe(groundedCommitTrace, blindCommitTrace, UseBlindTrace: false),
                refusalBoundary,
                stringOnlyRefusalBoundary,
                looseCodeRefusalBoundary,
                reportWrite,
                ciExecution,
                offlineGates.Gates.Select(static gate => gate.AgentEval
                    ?? throw new InvalidDataException("A measured offline gate omitted AgentEval provenance.")).ToArray(),
                admittedChecks,
                missingGate,
                EvaluationReportHtml.Render(new SuiteResult([missingGate], [])),
                instrumentGate,
                catalogueFailureGate),
        };
    }
    internal static async Task<CiExecutionPlanProbe> ObserveCiPlanAsync(
        string? path = null,
        CancellationToken cancellationToken = default) {
        var root = FindRepositoryRoot();
        var observation = CiProofPolicy.ObservePlan(path);
        var reportPath = Path.Combine(Path.GetTempPath(), $"vitrine-ci-gates-{Guid.NewGuid():N}.json");
        try {
            var start = new ProcessStartInfo("pwsh") {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(Path.Combine(root, "eng", "test-mocked.ps1"));
            start.ArgumentList.Add("-Configuration");
            start.ArgumentList.Add(CurrentBuildConfiguration());
            start.ArgumentList.Add("-NoRestore");
            start.ArgumentList.Add("-EvalProofOnly");
            start.ArgumentList.Add("-ProofReportPath");
            start.ArgumentList.Add(reportPath);
            start.Environment.Remove("AZURE_OPENAI_API_KEY");
            start.Environment.Remove("AZURE_OPENAI_ENDPOINT");
            start.Environment.Remove("VITRINE_RUN_LIVE_MODEL_TESTS");
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The offline eval proof process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            int? gateCount = null;
            if (File.Exists(reportPath)) {
                using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(reportPath, cancellationToken).ConfigureAwait(false));
                if (report.RootElement.TryGetProperty("gates", out var gates) && gates.ValueKind == JsonValueKind.Array)
                    gateCount = gates.GetArrayLength();
            }
            return observation with { CheckStageExitCode = process.ExitCode, ExecutedCheckStageCount = gateCount };
        }
        finally {
            if (File.Exists(reportPath)) File.Delete(reportPath);
        }
    }
    internal static string FindRepositoryRoot() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AgentEval.VitrineDemo.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not resolve the VITRINE repository root for the committed CI observation.");
    }
    private static string CurrentBuildConfiguration() {
        var parts = typeof(EvaluationSuite).Assembly.Location.Split(Path.DirectorySeparatorChar);
        var bin = Array.FindIndex(parts, static part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase));
        return bin >= 0 && bin + 1 < parts.Length && parts[bin + 1] is "Debug" or "Release"
            ? parts[bin + 1]
            : throw new InvalidDataException("Could not resolve the current eval build configuration.");
    }
}
internal sealed record ProductionControlBaselines(
    GraderRunSelectionProbe GraderCases, HonestyEvidenceArtifact Honesty, HonestyClaims HonestyClaims,
    ForcedChoiceCalibrationObservation ForcedChoiceCalibration, GateResult MatchedQualityDiagnostic,
    EvaluationSuite.JudgedQualityDiagnosticDetails MatchedDiagnostics, GuardrailPresentationProbe GuardrailPresentation,
    CommitOrderingProbe CommitOrdering, ToolRefusalBoundaryObservation RefusalBoundary,
    ToolRefusalBoundaryObservation StringOnlyRefusalBoundary, ToolRefusalBoundaryObservation LooseCodeRefusalBoundary,
    ReportWriteProbe ReportWrite, CiExecutionPlanProbe CiExecution,
    IReadOnlyList<AgentEvalProvenance> AgentEvalManifest, VitrineAdmittedChecksSelfTestResult AdmittedChecks,
    GateResult MissingGate, string MissingGateReport, GateResult InstrumentGate, GateResult CatalogueFailureGate);
internal sealed record ControlAssessment(bool? Satisfied, int SampleCount, string Evidence) {
    public static ControlAssessment Measured(bool satisfied, string evidence, int sampleCount = 1) =>
        new(satisfied, sampleCount, evidence);
    public static ControlAssessment Missing(string evidence) =>
        new(null, 0, evidence);
}
internal sealed record DenseWipeoutPanelProbe(RetrievalDiagnostics ImpossibleFloor, RetrievalDiagnostics UnreachableFloor);
internal sealed record GuardrailPresentationProbe(PresentedRecommendation Presented, GuardrailContext Context);
internal sealed record SubjectBinding(string Slot, string BoundSubject, int SourcePresented, int AppliedK);
internal sealed record CommitOrderingProbe(
    IReadOnlyList<ToolCallFact> GroundedTrace, IReadOnlyList<ToolCallFact> BlindTrace, bool UseBlindTrace) {
    public IReadOnlyList<ToolCallFact> ActiveTrace => UseBlindTrace ? BlindTrace : GroundedTrace;
}
internal sealed record ReviewLoopProbe(
    IReadOnlyList<string> Decisions, int Presented, int Phantom, int Unresolved,
    int Rounds, bool Approved, bool Failed, bool Measured) {
    public int Attempts => Decisions.Count;
    public bool ValidComparator => !Failed && Presented > 0 && Phantom == 0 && Unresolved == 0 && Rounds > 0;
}
internal sealed record ReviewRunSelectionProbe(
    ReviewLoopProbe Healthy, ReviewLoopProbe Ablated, bool UseAblated) {
    public ReviewLoopProbe Active => UseAblated ? Ablated : Healthy;
}
internal sealed record HonestyPolicyProbe(HonestyEvidenceArtifact Evidence, HonestyClaims Claims);
internal static class ForcedChoiceCalibrationFixture {
    internal static ForcedChoiceCalibrationObservation Capture() {
        var cases = ImmutableArray.Create(
            Observation.Measured("calibration-persona-01", ForcedChoiceCalibrationObservation.ArmId, 1),
            Observation.Measured("calibration-persona-02", ForcedChoiceCalibrationObservation.ArmId, 1),
            Observation.Measured("calibration-persona-03", ForcedChoiceCalibrationObservation.ArmId, 0));
        return new(
            cases,
            FloorComparison.Compute(
                cases,
                ForcedChoiceCalibrationObservation.ArmId,
                VitrineEvalCriteria.PersonaForcedChoiceFloor));
    }
}
internal sealed record ForcedChoiceCalibrationObservation(ImmutableArray<Observation> Cases, FloorComparison Comparison) {
    internal const string ArmId = "persona-forced-choice";
}
internal sealed record GoldCase(string Label, bool Expected, bool Predicted);
internal sealed record GraderPanelProbe(IReadOnlyList<GoldCase> Cases);
internal sealed record GraderRunSelectionProbe(
    GraderPanelProbe Healthy, GraderPanelProbe Broken, bool UseBroken) {
    public GraderPanelProbe Active => UseBroken ? Broken : Healthy;
}
internal sealed record RenderedGateProbe(GateResult Gate, bool ForcePassingProjection);
internal sealed record CriterionJoinProbe(IReadOnlyList<AgentEval.Core.CriterionResult> Results);
internal sealed record MatchedKCase(string Subject, int? SourcePresented, int? AppliedK);
internal sealed record MatchedKPanelProbe(IReadOnlyList<MatchedKCase> Cases);
internal sealed record RequestCoverageProbe(
    string CriterionId, string Request, bool Applicable, ApplicabilityEvidenceSource Source);
internal sealed record UnnameableFilterProbe(
    string CustomerId, string Market, string Language, Interest Interest,
    IReadOnlyList<RankedRecommendation> Candidates, bool BypassFilter);
internal sealed record ToolRefusalRunSelectionProbe(
    ToolRefusalBoundaryObservation Healthy, ToolRefusalBoundaryObservation Broken, bool UseBroken) {
    public ToolRefusalBoundaryObservation Active => UseBroken ? Broken : Healthy;
}
internal sealed record ReportWriteProbe(
    ReportWriteReceipt? Receipt, string ObservedFullPath, long ObservedBytes, bool ObservedFresh);
internal sealed record ProvenanceProbe(AgentEvalProvenance Value);
internal sealed record ProvenancePanelProbe(IReadOnlyList<AgentEvalProvenance> Values);
internal sealed record MockedTestPlanDocument(
    string SpdxLicense, int SchemaVersion, string Command, string Solution, string EvalProject,
    IReadOnlyList<string> EvalArguments, IReadOnlyList<string> EvalProofArguments,
    string TestFilter, bool FailOnNonZero);
internal sealed record CiExecutionPlanProbe(
    IReadOnlySet<string> Required, IReadOnlySet<string> Planned, string Solution,
    int? CheckStageExitCode, int? ExecutedCheckStageCount);
internal sealed record CalibrationPolicyProbe(DiscoveryCalibrationPolicy Policy);
internal sealed record RouteOutcome(string CaseId, bool LoopedBack);
internal sealed record RouteCensusProbe(IReadOnlyList<RouteOutcome> Outcomes);
internal sealed record TopologyCaseProbe(DiscoveryTopologyCaseObservation Observation, DiscoveryTopologyCaseClaim Claim);
internal sealed record CatalogueFactProbe(RecommendationDto Recommendation, string CustomerArtifact);
internal sealed record CommittedVectorContentPanelProbe(
    CommittedVectorContentObservation Healthy, CommittedVectorContentObservation Broken, bool UseBroken) {
    public CommittedVectorContentObservation Active => UseBroken ? Broken : Healthy;
}
internal sealed record CohortArmObservation(string? RequestedUserId, string? ReportedUserId) {
    public bool Measured => !string.IsNullOrWhiteSpace(RequestedUserId) && !string.IsNullOrWhiteSpace(ReportedUserId);
    public bool MatchesRequest => Measured && string.Equals(RequestedUserId, ReportedUserId, StringComparison.Ordinal);
}
internal sealed record CohortProbe(
    IReadOnlySet<string> Left, IReadOnlySet<string> Right, bool DifferenceDeclared,
    bool ObservationsMeasured, bool ObservationsMatchRequests);
internal sealed record CohortRunSelectionProbe(
    CohortArmObservation Left, CohortArmObservation HealthyRight,
    CohortArmObservation BrokenRight, bool UseBroken) {
    public CohortProbe Active {
        get {
            var right = UseBroken ? BrokenRight : HealthyRight;
            return new(
                Left.RequestedUserId is null ? [] : new HashSet<string>([Left.RequestedUserId], StringComparer.Ordinal),
                right.RequestedUserId is null ? [] : new HashSet<string>([right.RequestedUserId], StringComparer.Ordinal),
                DifferenceDeclared: false,
                ObservationsMeasured: Left.Measured && right.Measured,
                ObservationsMatchRequests: Left.MatchesRequest && right.MatchesRequest);
        }
    }
}
internal sealed record AssertionReportProbe(GateResult Source, bool UseOrdinaryFailureProjection);
internal sealed record UsageStateContrastProbe(ProviderUsageMeasurement NoModel, ProviderUsageMeasurement ProviderMissing);
internal sealed record MissingGateRenderProbe(GateResult Gate, bool ForceZeroProjection);
internal sealed record JudgeReachabilityProbe(int? Calls, int? Demo01Verdicts, int? Demo02Verdicts);
internal sealed record ApplicableFractionProbe(
    AgentEval.Evals.Meta.ObservationCensus Census, int Successes, int ReportedDenominator);
internal sealed record ContainmentProbe(bool ShouldThrow);
internal sealed record CustomerAnswerProbe(string? Answer, string? AuthoredRequest);
internal static class MatchedBindingPolicy {
    public static bool HasCanonicalMatchedK(MatchedKPanelProbe probe) =>
        probe.Cases.Count == 2 &&
        probe.Cases.Select(static item => item.Subject).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["demo01", "demo02"]) &&
        probe.Cases.All(static item => item.SourcePresented is >= VitrineEvalCriteria.JudgedMatchedK &&
            item.AppliedK == VitrineEvalCriteria.JudgedMatchedK);
}
internal static class JudgedApplicabilityPolicy {
    public static bool ComesFromAuthoredInput(RequestCoverageProbe probe) {
        var criterion = VitrineEvalCriteria.JudgedCriteria.Single(item => item.Id == probe.CriterionId);
        var expected = VitrineEvalCriteria.DecideApplicability(criterion, probe.Request);
        return probe.Applicable == expected.Applicable && probe.Source == ApplicabilityEvidenceSource.AuthoredInput;
    }
}
internal static class JudgedReachabilityPolicy {
    public static bool HasCanonicalVerdicts(JudgeReachabilityProbe probe) =>
        probe.Calls == 2 && probe.Demo01Verdicts == VitrineEvalCriteria.JudgedCriteria.Length &&
        probe.Demo02Verdicts == VitrineEvalCriteria.JudgedCriteria.Length;
}
internal static class CiProofPolicy {
    public static CiExecutionPlanProbe ObservePlan(string? path = null) {
        path ??= Path.Combine(ControlEnvironment.FindRepositoryRoot(), "eng", "test-mocked.plan.json");
        var plan = JsonSerializer.Deserialize<MockedTestPlanDocument>(File.ReadAllBytes(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The mocked-test execution plan is empty.");
        var required = new HashSet<string>(["offline-eval", "dotnet-test", "non-live-filter", "exit-check"], StringComparer.Ordinal);
        var planned = new HashSet<string>(StringComparer.Ordinal);
        if (plan.SpdxLicense == "MIT" && plan.SchemaVersion == 1 && plan.Command == "dotnet-test") planned.Add("dotnet-test");
        if (plan.TestFilter == "Category!=LiveModel") planned.Add("non-live-filter");
        if (plan.FailOnNonZero) planned.Add("exit-check");
        if (plan.EvalProject == "src/AgentEval.VitrineDemo.Evals/AgentEval.VitrineDemo.Evals.csproj" &&
            plan.EvalArguments.SequenceEqual(["--all"], StringComparer.Ordinal) &&
            plan.EvalProofArguments.SequenceEqual(["--gates-only"], StringComparer.Ordinal)) planned.Add("offline-eval");
        return new(required, planned, plan.Solution, null, null);
    }
    public static bool RequiredCiStepsPlanned(CiExecutionPlanProbe probe) =>
        probe.Solution == "AgentEval.VitrineDemo.slnx" && probe.Required.IsSubsetOf(probe.Planned) &&
        probe.CheckStageExitCode == EvaluationExitCodes.Passed && probe.ExecutedCheckStageCount == 6;
}
internal static class CausalControlPolicies {
    public static bool SilentWipeoutDetectorHasBothDirections(DenseWipeoutPanelProbe probe) =>
        IsSilentDenseWipeout(probe.ImpossibleFloor) && !IsSilentDenseWipeout(probe.UnreachableFloor);
    public static bool IsSilentDenseWipeout(RetrievalDiagnostics diagnostics) =>
        !diagnostics.Degraded && diagnostics.DenseCandidates == 0 && diagnostics.DenseBelowFloor > 0;
    public static bool HasRejectThenApprove(ReviewLoopProbe probe) =>
        probe.Attempts > 1 && probe.Decisions.Contains("reject", StringComparer.Ordinal) &&
        probe.Decisions.Contains("approve", StringComparer.Ordinal);
    public static bool CausalLoopContrast(ReviewRunSelectionProbe probe) =>
        probe.Active.ValidComparator && HasRejectThenApprove(probe.Active);
    public static bool MatchesGold(GraderPanelProbe probe) =>
        probe.Cases.Any(static item => item.Expected) && probe.Cases.Any(static item => !item.Expected) &&
        probe.Cases.All(static item => item.Expected == item.Predicted);
    public static bool MatchesGateState(bool passed, string renderedState) =>
        string.Equals(renderedState, passed ? "PASS" : "FAIL", StringComparison.Ordinal);
    public static bool ReportWriteMatchesStore(ReportWriteProbe probe) =>
        probe.Receipt is { } receipt && probe.ObservedFresh && probe.ObservedBytes > 0 &&
        receipt.Bytes == probe.ObservedBytes &&
        string.Equals(receipt.FullPath, probe.ObservedFullPath, StringComparison.OrdinalIgnoreCase);
    public static bool HasBothLoopDirections(RouteCensusProbe probe) =>
        probe.Outcomes.Count == 2 &&
        probe.Outcomes.Single(item => item.CaseId == DiscoveryTerminationProbe.LoopFiresCaseId).LoopedBack &&
        !probe.Outcomes.Single(item => item.CaseId == DiscoveryTerminationProbe.LoopDoesNotFireCaseId).LoopedBack;
    public static bool CohortComparableOrDeclared(CohortProbe probe) =>
        probe.ObservationsMatchRequests && probe.Left.Count > 0 && probe.Right.Count > 0 &&
        (probe.Left.SetEquals(probe.Right) || probe.DifferenceDeclared);
    public static bool CostStatesRemainDistinct(UsageStateContrastProbe probe) =>
        probe.NoModel.IsConsistent() && probe.ProviderMissing.IsConsistent() &&
        probe.NoModel.Status == ProviderUsageStatus.MeasuredZero && probe.NoModel.TotalTokens == 0 &&
        probe.ProviderMissing.Status == ProviderUsageStatus.Missing && probe.ProviderMissing.TotalTokens is null &&
        probe.NoModel.ToDisplayString().Contains("measured", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(probe.ProviderMissing.ToDisplayString(), "NOT MEASURED", StringComparison.Ordinal) &&
        !string.Equals(probe.NoModel.ToDisplayString(), probe.ProviderMissing.ToDisplayString(), StringComparison.Ordinal);
}
