// SPDX-License-Identifier: MIT

using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Catalog;

namespace AgentEval.VitrineDemo.Tests;

public sealed class LiveEvaluationProjectionTests
{
    [Fact]
    public void PrepareLiveShowsExactPaidWorkloadAndAuthoredScenarioBeforeRunning()
    {
        var board = new EvaluationBoardViewModel();
        var scenario = LiveUseCaseScenarios.Require("nadia-cross-category");
        var workload = new LiveEvalWorkload(1, 1, 5, 5, 5);

        board.PrepareLive(
            VitrineEvaluationPlan.LiveEval04StochasticAgent,
            workload,
            [scenario]);

        Assert.True(board.HasLiveEvaluation);
        Assert.Equal("Eval 04 · Stochastic agent", board.LivePlanTitle);
        Assert.Contains("5 rep", board.LiveWorkloadSummary, StringComparison.Ordinal);
        Assert.Contains("5 subject", board.LiveWorkloadSummary, StringComparison.Ordinal);
        Assert.Contains("5 judge", board.LiveWorkloadSummary, StringComparison.Ordinal);
        Assert.Contains("PENDING", board.LiveQualityPassBar, StringComparison.Ordinal);
        Assert.Contains("not a null/chance floor", board.LiveQualityPassBar, StringComparison.Ordinal);
        var projected = Assert.Single(board.LiveScenarios);
        Assert.Equal(scenario.Query, projected.Query);
        Assert.Equal(scenario.Description, projected.Description);
        Assert.Contains("connects-evidence", projected.CriteriaSummary, StringComparison.Ordinal);
        Assert.Contains("NOT MEASURED", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("43-row", board.ControlSummary, StringComparison.Ordinal);
        Assert.Empty(board.Gates);
        Assert.Empty(board.Controls);
    }

    [Fact]
    public void LoadLiveProjectsTrialResponsesWilsonComparisonsUsageAndOutcomePathWithoutRescoring()
    {
        var result = SyntheticResult();
        var board = new EvaluationBoardViewModel();

        board.LoadLive(result);

        Assert.Equal("FAIL · live quality exit 1", board.OverallStatus);
        Assert.Equal("#F07076", board.OverallStatusColor);
        Assert.Equal(result.Persistence.OutcomePath, board.LiveOutcomePath);
        Assert.Contains("QUALITY PASS BAR 0.750", board.LiveQualityPassBar, StringComparison.Ordinal);
        Assert.Contains("not a null/chance floor", board.LiveQualityPassBar, StringComparison.Ordinal);
        Assert.Contains("QUALITY PASS BAR 0.750", board.LiveWorkloadSummary, StringComparison.Ordinal);
        Assert.Equal(2, board.LiveTrials.Count);
        Assert.Contains(board.LiveTrials, trial => trial.ResponsePreview.Contains("agent response", StringComparison.Ordinal));
        Assert.Contains(board.LiveTrials, trial => trial.Workflow.Contains("Discovery×2", StringComparison.Ordinal));
        Assert.Contains(board.LiveTrials, trial =>
            trial.Workflow.Contains("bounded internal fallback degradations 2", StringComparison.Ordinal)
            && trial.Workflow.Contains("InterestMapper:fallback", StringComparison.Ordinal));
        Assert.Contains(board.LiveTrials, trial =>
            trial.Criteria.Contains("judge explanation The response connects", StringComparison.Ordinal));
        Assert.Contains(board.LiveTrials, trial =>
            trial.Criteria.Contains("trace-complete · NOT MEASURED", StringComparison.Ordinal)
            && trial.Criteria.Contains("judge explanation NOT MEASURED · no judge explanation", StringComparison.Ordinal));
        Assert.Equal(2, board.LiveChecks.Count);
        Assert.Contains(board.LiveChecks, check =>
            check.ArmId == "robin-agent-live"
            && check.Reliability.Contains("1/1", StringComparison.Ordinal)
            && check.WilsonInterval.Contains("0.207", StringComparison.Ordinal));
        var comparison = Assert.Single(board.LiveComparisons);
        Assert.Equal("1/0/0", comparison.WinsLossesTies);
        Assert.Equal("1.000", comparison.MinimumAttainableP);
        Assert.Equal(1, comparison.Cases);
        Assert.Equal(2, comparison.TotalRepObservations);
        Assert.Equal("1.000", comparison.MeanRepetitionsPerCase);
        Assert.Equal("All", comparison.RepCollapse);
        Assert.Contains("1 paired case", comparison.ObservationUnit, StringComparison.Ordinal);
        Assert.Contains("2 total raw reps across both arms", comparison.ObservationUnit, StringComparison.Ordinal);
        Assert.Contains("reps/case/arm", comparison.ObservationUnit, StringComparison.Ordinal);
        Assert.Contains("RepCollapse.All", comparison.ObservationUnit, StringComparison.Ordinal);
        Assert.Equal("UNDERPOWERED BY CONSTRUCTION", comparison.Power);
        Assert.Equal(4, board.LiveUsage.Count);
        Assert.Contains("calls ≥6", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("input tokens ≥380", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("output tokens ≥80", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("total tokens ≥460", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("estimated cost USD ≥0.035431", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("not-reported 1", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("No null/chance floor or aggregate", board.BenchmarkFloorDerivation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PartialUsageTelemetryRemainsAKnownLowerBoundForDomainAndReplaySnapshots()
    {
        var result = SyntheticResult();
        var domainBoard = new EvaluationBoardViewModel();
        domainBoard.LoadLive(result);
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            Guid.NewGuid(),
            new(VitrineRunMode.Evals, Personas.NadiaUserId, EvaluationPlan: result.Plan,
                LiveScenarioId: "nadia-cross-category", PaidEvaluationConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(result.Plan),
            [],
            LiveEvaluation: result));
        var snapshot = Assert.IsType<VitrineLiveEvaluationSnapshot>(
            VitrineArtifactSerializer.Deserialize(VitrineArtifactSerializer.Serialize(artifact))
                .Result.LiveEvaluation);
        var replayBoard = new EvaluationBoardViewModel();
        replayBoard.LoadLiveSnapshot(snapshot);

        const string expected = "calls ≥6 · input tokens ≥380 · output tokens ≥80 · " +
            "total tokens ≥460 · estimated cost USD ≥0.035431";
        Assert.Contains(expected, domainBoard.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains(expected, replayBoard.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Equal(domainBoard.ModelUsageSummary, replayBoard.ModelUsageSummary);
        Assert.DoesNotContain("calls NOT MEASURED", domainBoard.ModelUsageSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("total tokens NOT MEASURED", replayBoard.ModelUsageSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyLiveCopiesTypedProgressContextAndTerminalVerdictWithoutCreatingRows()
    {
        var board = new EvaluationBoardViewModel();
        board.ApplyLive(new(
            VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
            LiveEvalProgressPhase.CheckCompleted,
            "nadia-cross-category",
            "robin-agent-live",
            2,
            LiveUseCaseBenchmark.UseCaseQualityCheckKey,
            MeasurementState.Measured,
            false,
            "The registered use-case check completed."));

        Assert.Equal("RUNNING · paid live evaluation", board.OverallStatus);
        Assert.Contains("CHECK COMPLETED", board.ActiveStage, StringComparison.Ordinal);
        Assert.Contains("scenario nadia-cross-category", board.ActiveStage, StringComparison.Ordinal);
        Assert.Contains("arm robin-agent-live", board.ActiveStage, StringComparison.Ordinal);
        Assert.Contains("rep 2", board.ActiveStage, StringComparison.Ordinal);
        Assert.Contains("measurement MEASURED", board.ActiveStage, StringComparison.Ordinal);
        Assert.Contains("verdict FAIL", board.ActiveStage, StringComparison.Ordinal);
        Assert.Empty(board.LiveTrials);
        Assert.Empty(board.LiveChecks);

        board.ApplyLive(new(
            VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
            LiveEvalProgressPhase.SessionCompleted,
            null,
            null,
            null,
            null,
            MeasurementState.Measured,
            false,
            "The selected live evaluation plan completed."));
        Assert.Equal("FAIL · live quality evaluation completed", board.OverallStatus);

        board.ApplyLive(new(
            VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
            LiveEvalProgressPhase.SessionCompleted,
            null,
            null,
            null,
            null,
            MeasurementState.NotMeasured,
            null,
            "The live evaluation was not measured."));
        Assert.Equal("NOT MEASURED · live evaluation completed", board.OverallStatus);
    }

    [Fact]
    public void CurrentSchemaArtifactRoundTripsAndExportsEverySafeLiveResultLayer()
    {
        var result = SyntheticResult("agent response <script data-test='unsafe'>ignored</script>");
        var outcome = new VitrineRunOutcome(
            Guid.NewGuid(),
            new(
                VitrineRunMode.Evals,
                Personas.NadiaUserId,
                EvaluationPlan: result.Plan,
                LiveScenarioId: "nadia-cross-category",
                EvaluationRepetitions: 1,
                PaidEvaluationConfirmed: true),
            VitrineGraphFactory.ForEvaluationSuite(),
            [],
            LiveEvaluation: result);

        var created = VitrineArtifactSerializer.Create(outcome);
        var json = VitrineArtifactSerializer.Serialize(created);
        var artifact = VitrineArtifactSerializer.Deserialize(json);
        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(artifact.Result.LiveEvaluation);
        var html = VitrineHtmlReport.Render(artifact);
        var (title, inspector) = VitrineOutcomeInspector.Describe(artifact);
        var replayBoard = new EvaluationBoardViewModel();
        replayBoard.LoadLiveSnapshot(live);

        Assert.Equal(VitrineRunArtifact.CurrentSchemaVersion, artifact.SchemaVersion);
        Assert.True(VitrineArtifactSerializer.Verify(artifact));
        Assert.Equal(result.Persistence.OutcomePath, live.Persistence.OutcomePath);
        Assert.Equal(result.PassThreshold, live.PassThreshold);
        Assert.Equal(result.Trials[0].ResponsePreview, live.Trials[0].ResponsePreview);
        Assert.Equal(result.Trials[0].Criteria[0].Explanation,
            live.Trials[0].Criteria[0].Explanation);
        Assert.Equal(2, live.Trials[1].Workflow!.DegradationCount);
        Assert.Equal(result.Trials[1].Workflow!.DegradationKinds,
            live.Trials[1].Workflow!.DegradationKinds);
        Assert.Equal(result.Arms[0].Checks[0].Reliability.Lower,
            live.Arms[0].Checks[0].Reliability.Lower);
        Assert.Equal(result.Comparisons[0].PValue, live.Comparisons[0].PValue);
        Assert.Equal(result.Comparisons[0].MinimumAttainableP,
            live.Comparisons[0].MinimumAttainableP);
        Assert.Equal(result.Comparisons[0].Cases, live.Comparisons[0].Cases);
        Assert.Equal(result.Comparisons[0].TotalRepObservations,
            live.Comparisons[0].TotalRepObservations);
        Assert.Equal(result.Comparisons[0].MeanRepetitionsPerCase,
            live.Comparisons[0].MeanRepetitionsPerCase);
        Assert.Equal(result.Comparisons[0].RepCollapse, live.Comparisons[0].RepCollapse);
        Assert.Equal(LiveUseCaseScenarios.Require("nadia-cross-category").Query,
            Assert.Single(live.Scenarios).Query);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VitrineLiveTrialSnapshot>)live.Trials).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VitrineLiveCheckSummarySnapshot>)live.Arms[0].Checks).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)live.Trials[1].Workflow!.DegradationKinds).Clear());
        Assert.Contains("\"liveEvaluation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"passThreshold\"", json, StringComparison.Ordinal);
        Assert.Contains("\"degradationKinds\"", json, StringComparison.Ordinal);
        Assert.Contains("\"explanation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"minimumAttainableP\"", json, StringComparison.Ordinal);
        Assert.Contains("\"meanRepetitionsPerCase\"", json, StringComparison.Ordinal);
        Assert.Contains("\"repCollapse\"", json, StringComparison.Ordinal);
        Assert.Contains("Paid live use-case evaluation", html, StringComparison.Ordinal);
        Assert.Contains("Quality pass bar (not a chance floor)", html, StringComparison.Ordinal);
        Assert.Contains("Bounded fallback degradations", html, StringComparison.Ordinal);
        Assert.Contains("InterestMapper:fallback", html, StringComparison.Ordinal);
        Assert.Contains("Judge explanation", html, StringComparison.Ordinal);
        Assert.Contains("The response connects the authored evidence", html, StringComparison.Ordinal);
        Assert.Contains("NOT MEASURED", html, StringComparison.Ordinal);
        Assert.Contains("Per-check census and Wilson reliability", html, StringComparison.Ordinal);
        Assert.Contains("Minimum attainable p", html, StringComparison.Ordinal);
        Assert.Contains("2 total raw reps across both arms", html, StringComparison.Ordinal);
        Assert.Contains("RepCollapse.All", html, StringComparison.Ordinal);
        Assert.Contains(result.Persistence.OutcomePath, html, StringComparison.Ordinal);
        Assert.Contains("&lt;script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script data-test", html, StringComparison.Ordinal);
        Assert.Contains("LIVE EVALUATION OUTCOME", title, StringComparison.Ordinal);
        Assert.Contains("Quality pass bar: 0.750", inspector, StringComparison.Ordinal);
        Assert.Contains("NOT a null/chance floor", inspector, StringComparison.Ordinal);
        Assert.Contains("bounded fallback degradations 2", inspector, StringComparison.Ordinal);
        Assert.Contains("judge explanation The response connects", inspector, StringComparison.Ordinal);
        Assert.Contains("trace-complete · NotMeasured · met NOT MEASURED · judge explanation NOT MEASURED",
            inspector, StringComparison.Ordinal);
        Assert.Contains("Wilson [0.207", inspector, StringComparison.Ordinal);
        Assert.Contains("minimum attainable p 1.000", inspector, StringComparison.Ordinal);
        Assert.Contains("2 total raw reps across both arms", inspector, StringComparison.Ordinal);
        Assert.Contains("RepCollapse.All", inspector, StringComparison.Ordinal);
        Assert.Contains(result.Persistence.OutcomePath, inspector, StringComparison.Ordinal);
        Assert.Equal(2, replayBoard.LiveTrials.Count);
        Assert.Equal(2, replayBoard.LiveChecks.Count);
        var replayComparison = Assert.Single(replayBoard.LiveComparisons);
        Assert.Equal("1.000", replayComparison.MinimumAttainableP);
        Assert.Contains("2 total raw reps across both arms", replayComparison.ObservationUnit,
            StringComparison.Ordinal);
        Assert.Equal(result.Trials[0].ResponsePreview, replayBoard.LiveTrials[0].ResponsePreview);
        Assert.Contains("QUALITY PASS BAR 0.750", replayBoard.LiveQualityPassBar, StringComparison.Ordinal);
        Assert.Contains("bounded internal fallback degradations 2", replayBoard.LiveTrials[1].Workflow,
            StringComparison.Ordinal);
        Assert.Contains("judge explanation NOT MEASURED", replayBoard.LiveTrials[1].Criteria,
            StringComparison.Ordinal);
        Assert.Contains("no execution occurred", replayBoard.ActiveStage, StringComparison.Ordinal);
        Assert.Contains(result.Persistence.OutcomePath, replayBoard.PersistenceSummary, StringComparison.Ordinal);

        var invalidThreshold = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { PassThreshold = 1.5 },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(invalidThreshold));
    }

    [Fact]
    public void LiveResponseSecretPatternsAreRedactedBeforeIntegrityAndExport()
    {
        const string secret = "SENTINEL-LIVE-EVAL-SECRET";
        var result = SyntheticResult(
            $"safe response api_key={secret}",
            $"criterion explanation api_key={secret}");
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            Guid.NewGuid(),
            new(
                VitrineRunMode.Evals,
                Personas.NadiaUserId,
                EvaluationPlan: result.Plan,
                LiveScenarioId: "nadia-cross-category",
                PaidEvaluationConfirmed: true),
            VitrineGraphFactory.ForEvaluationSuite(),
            [],
            LiveEvaluation: result));

        var json = VitrineArtifactSerializer.Serialize(artifact);
        var html = VitrineHtmlReport.Render(artifact);

        Assert.True(VitrineArtifactSerializer.Verify(artifact));
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, html, StringComparison.Ordinal);
        Assert.Contains("api_key=[REDACTED]", json, StringComparison.Ordinal);

        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(artifact.Result.LiveEvaluation);
        Assert.DoesNotContain(secret, live.Trials[0].Criteria[0].Explanation, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", live.Trials[0].Criteria[0].Explanation, StringComparison.Ordinal);
        var trial = live.Trials[0];
        var forged = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    Trials = [trial with { ResponsePreview = $"api_key={secret}" }, .. live.Trials.Skip(1)],
                },
            },
        };
        Assert.False(VitrineArtifactSerializer.Verify(forged));
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(forged));
    }

    private static LiveEvalResult SyntheticResult(
        string agentResponse = "agent response",
        string agentCriterionExplanation =
            "The response connects the authored evidence to the recommended use case.")
    {
        var started = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        var key = LiveUseCaseBenchmark.UseCaseQualityCheckKey;
        var name = "Canonical use-case criteria";
        var agentUsage = new LiveUsageEvidence("measured", 2, 120, 30, 150, 0.012345);
        var agentJudgeUsage = new LiveUsageEvidence("measured", 1, 80, 10, 90, 0.004321);
        var workflowUsage = new LiveUsageEvidence("lower-bound", 3, 180, 40, 220, 0.018765);
        var agentTrial = new LiveTrialEvidence(
            "nadia-cross-category",
            Personas.NadiaUserId,
            "robin-agent-live",
            LiveSubjectArchitecture.Agent,
            1,
            MeasurementState.Measured,
            true,
            LiveSubjectStatus.Completed,
            agentResponse,
            new(true, ["SearchProducts"], 1, 1, 0, 0, 0, [], 0),
            null,
            [new(key, name, MeasurementState.Measured, 0.9, true)],
            [new("connects-evidence", MeasurementState.Measured, true,
                agentCriterionExplanation)],
            agentUsage,
            agentJudgeUsage,
            null);
        var workflowTrial = new LiveTrialEvidence(
            "nadia-cross-category",
            Personas.NadiaUserId,
            "discovery-workflow-live",
            LiveSubjectArchitecture.Workflow,
            1,
            MeasurementState.Measured,
            false,
            LiveSubjectStatus.Completed,
            "workflow response",
            LiveToolEvidence.NotApplicable,
            new(
                [new("InterestMapper", 1), new("Discovery", 2), new("CoverageReviewer", 2), new("Ranker", 1), new("Presenter", 1)],
                ["map-to-discovery", "discovery-to-review", "review-to-more-discovery", "review-to-ranker", "ranker-to-presenter"],
                2,
                3,
                7,
                "CoverageSufficient",
                true,
                0,
                2,
                ["InterestMapper:fallback", "CoverageReviewer:model-failure"]),
            [new(key, name, MeasurementState.Measured, 0.6, false)],
            [
                new("connects-evidence", MeasurementState.Measured, false,
                    "The response did not connect the authored evidence to the use case."),
                new("trace-complete", MeasurementState.NotMeasured, null, ""),
            ],
            workflowUsage,
            LiveUsageEvidence.NotReported,
            null);
        var census = new LiveObservationCensus(1, 0, 0);
        var agentArm = new LiveArmSummary(
            "robin-agent-live",
            LiveSubjectArchitecture.Agent,
            1,
            [new(key, name, census, new(MeasurementState.Measured, 1, 1, 1, 0.207, 1))]);
        var workflowArm = new LiveArmSummary(
            "discovery-workflow-live",
            LiveSubjectArchitecture.Workflow,
            1,
            [new(key, name, census, new(MeasurementState.Measured, 0, 1, 0, 0, 0.793))]);
        return new(
            VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
            LiveEvalTerminalStatus.QualityFailed,
            "20260908T100000000Z-synthetic",
            started,
            started.AddMinutes(2),
            new(1, 2, 1, 2, 2),
            0.75,
            [LiveUseCaseScenarios.Require("nadia-cross-category").ToDefinition()],
            new("vitrine-live-use-cases", "1", "judge", "prompt", "rubric", 512, 256, 640,
            [
                new("robin-agent-live", LiveSubjectArchitecture.Agent, "agent-model", JudgeSubjectRelation.DifferentModel),
                new("discovery-workflow-live", LiveSubjectArchitecture.Workflow, "workflow-model", JudgeSubjectRelation.DifferentModel),
            ]),
            [
                new("agent-run", "robin-agent-live", 1, "runs/agent-run"),
                new("workflow-run", "discovery-workflow-live", 1, "runs/workflow-run"),
            ],
            [agentTrial, workflowTrial],
            [agentArm, workflowArm],
            [new(key, name, "robin-agent-live", "discovery-workflow-live", 1, 0, 0, 1,
                1, 1, 0.3, 1, 2, 1, "All", census, true, false)],
            [],
            new(
                "C:\\safe\\.agenteval\\live",
                "C:\\safe\\.agenteval\\live\\sessions\\synthetic",
                "C:\\safe\\.agenteval\\live\\sessions\\synthetic\\outcome.json",
                "C:\\safe\\.agenteval\\live\\sessions\\index.json"));
    }
}
