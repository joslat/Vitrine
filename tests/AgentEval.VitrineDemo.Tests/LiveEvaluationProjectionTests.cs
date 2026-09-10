// SPDX-License-Identifier: MIT

using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.RedTeam.Reporting;
using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Workflows;

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
            trial.Workflow.Contains("bounded degradation events 2", StringComparison.Ordinal)
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
                LiveScenarioId: "nadia-cross-category", PaidExecutionConfirmed: true),
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
                PaidExecutionConfirmed: true),
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
        Assert.Equal(nameof(LiveTerminalAcceptancePolicy.EveryTrialMustPass),
            live.Configuration?.Acceptance?.Policy);
        Assert.NotNull(live.ScenarioAcceptances);
        Assert.Empty(live.ScenarioAcceptances);
        Assert.Equal(result.Trials[0].ResponsePreview, live.Trials[0].ResponsePreview);
        Assert.Equal(result.Trials[0].Criteria[0].Explanation,
            live.Trials[0].Criteria[0].Explanation);
        Assert.Equal(2, live.Trials[1].Workflow!.DegradationCount);
        Assert.Equal(result.Trials[1].Workflow!.DegradationKinds,
            live.Trials[1].Workflow!.DegradationKinds);
        var providerStage = live.Trials[1].Workflow!.ProviderStages.Single(static stage =>
            stage.ExecutorId == DiscoveryExecutorIds.InterestMapper);
        var providerStages = live.Trials[1].Workflow!.ProviderStages;
        Assert.Equal("InterestMapper", providerStage.ExecutorId);
        Assert.Equal(nameof(LiveWorkflowProviderStageStatus.Recovered), providerStage.Status);
        Assert.Equal(1, providerStage.UnusableAttemptCount);
        Assert.Equal(1, providerStage.LastUnusableAttemptNumber);
        Assert.Equal(2, providerStage.LastUsableResponseAttemptNumber);
        Assert.Equal(1, live.Trials[1].Workflow!.ProviderFailedAttemptCount);
        Assert.Equal(1, live.Trials[1].Workflow!.RecoveredProviderFailedAttemptCount);
        Assert.Equal(0, live.Trials[1].Workflow!.TerminalProviderStageCount);
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
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VitrineLiveWorkflowProviderStageSnapshot>)live.Trials[1].Workflow!.ProviderStages).Clear());
        Assert.Contains("\"liveEvaluation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"passThreshold\"", json, StringComparison.Ordinal);
        Assert.Contains("\"acceptance\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scenarioAcceptances\"", json, StringComparison.Ordinal);
        Assert.Contains("\"degradationKinds\"", json, StringComparison.Ordinal);
        Assert.Contains("\"providerStages\"", json, StringComparison.Ordinal);
        Assert.Contains("\"explanation\"", json, StringComparison.Ordinal);
        Assert.Contains("\"minimumAttainableP\"", json, StringComparison.Ordinal);
        Assert.Contains("\"meanRepetitionsPerCase\"", json, StringComparison.Ordinal);
        Assert.Contains("\"repCollapse\"", json, StringComparison.Ordinal);
        Assert.Contains("Paid live use-case evaluation", html, StringComparison.Ordinal);
        Assert.Contains("Quality pass bar (not a chance floor)", html, StringComparison.Ordinal);
        Assert.Contains("Terminal acceptance policy", html, StringComparison.Ordinal);
        Assert.Contains(nameof(LiveTerminalAcceptancePolicy.EveryTrialMustPass), html,
            StringComparison.Ordinal);
        Assert.Contains("Bounded degradation events", html, StringComparison.Ordinal);
        Assert.Contains("InterestMapper:fallback", html, StringComparison.Ordinal);
        Assert.Contains("Model-backed stage outcomes", html, StringComparison.Ordinal);
        Assert.Contains("InterestMapper", html, StringComparison.Ordinal);
        Assert.Contains("Recovered", html, StringComparison.Ordinal);
        Assert.Contains("attempts 2", html, StringComparison.Ordinal);
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
        Assert.Contains("Terminal acceptance: EveryTrialMustPass", inspector,
            StringComparison.Ordinal);
        Assert.Contains("bounded degradation events 2", inspector, StringComparison.Ordinal);
        Assert.Contains("provider failed/recovered/terminal 1/1/0", inspector, StringComparison.Ordinal);
        Assert.Contains("InterestMapper · Recovered · attempts 2", inspector, StringComparison.Ordinal);
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
        Assert.Contains("terminal acceptance every trial must pass",
            replayBoard.LiveConfigurationSummary, StringComparison.Ordinal);
        Assert.Contains("bounded degradation events 2", replayBoard.LiveTrials[1].Workflow,
            StringComparison.Ordinal);
        Assert.Contains("provider attempts failed/recovered/terminal 1/1/0",
            replayBoard.LiveTrials[1].Workflow, StringComparison.Ordinal);
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
        var missingAcceptance = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    Configuration = live.Configuration! with { Acceptance = null },
                },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(missingAcceptance));
        var missingDecisions = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { ScenarioAcceptances = null },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(missingDecisions));

        var inconsistentProviderTrials = live.Trials.ToArray();
        inconsistentProviderTrials[1] = inconsistentProviderTrials[1] with
        {
            Workflow = inconsistentProviderTrials[1].Workflow! with { ProviderFailedAttemptCount = 2 },
        };
        var inconsistentProvider = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = inconsistentProviderTrials },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(inconsistentProvider));

        var impossibleCensusTrials = live.Trials.ToArray();
        impossibleCensusTrials[1] = impossibleCensusTrials[1] with
        {
            Workflow = impossibleCensusTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == providerStage.ExecutorId
                        ? stage with { UnusableAttemptCount = stage.AttemptCount + 1 }
                        : stage).ToArray(),
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = impossibleCensusTrials },
            },
        }));

        var invalidExecutorTrials = live.Trials.ToArray();
        invalidExecutorTrials[1] = invalidExecutorTrials[1] with
        {
            Workflow = invalidExecutorTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == providerStage.ExecutorId
                        ? stage with { ExecutorId = DiscoveryExecutorIds.Discovery }
                        : stage).ToArray(),
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = invalidExecutorTrials },
            },
        }));

        var invalidRecoveryOrderTrials = live.Trials.ToArray();
        invalidRecoveryOrderTrials[1] = invalidRecoveryOrderTrials[1] with
        {
            Workflow = invalidRecoveryOrderTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == providerStage.ExecutorId
                        ? stage with
                        {
                            LastUsableResponseAttemptNumber = stage.LastUnusableAttemptNumber,
                        }
                        : stage).ToArray(),
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = invalidRecoveryOrderTrials },
            },
        }));

        var hiddenCompletedAttemptTrials = live.Trials.ToArray();
        hiddenCompletedAttemptTrials[1] = hiddenCompletedAttemptTrials[1] with
        {
            Workflow = hiddenCompletedAttemptTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == providerStage.ExecutorId
                        ? stage with
                        {
                            ResponseCount = 1,
                            UnusableAttemptCount = 0,
                            FailedAttemptCount = 0,
                            Status = nameof(LiveWorkflowProviderStageStatus.Completed),
                            LastUnusableAttemptNumber = 0,
                            LastUsableResponseAttemptNumber = 2,
                        }
                        : stage).ToArray(),
                ProviderFailedAttemptCount = 0,
                RecoveredProviderFailedAttemptCount = 0,
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = hiddenCompletedAttemptTrials },
            },
        }));

        var hiddenRecoveredAttemptTrials = live.Trials.ToArray();
        hiddenRecoveredAttemptTrials[1] = hiddenRecoveredAttemptTrials[1] with
        {
            Workflow = hiddenRecoveredAttemptTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == providerStage.ExecutorId
                        ? stage with
                        {
                            AttemptCount = 10,
                            ResponseCount = 1,
                            UnusableAttemptCount = 1,
                            LastUnusableAttemptNumber = 5,
                            LastUsableResponseAttemptNumber = 10,
                        }
                        : stage).ToArray(),
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = hiddenRecoveredAttemptTrials },
            },
        }));

        var duplicateAttemptIdentityTrials = live.Trials.ToArray();
        duplicateAttemptIdentityTrials[1] = duplicateAttemptIdentityTrials[1] with
        {
            Workflow = duplicateAttemptIdentityTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == DiscoveryExecutorIds.Ranker
                        ? stage with { LastUsableResponseAttemptNumber = 1 }
                        : stage).ToArray(),
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = duplicateAttemptIdentityTrials },
            },
        }));

        var outOfRangeAttemptIdentityTrials = live.Trials.ToArray();
        outOfRangeAttemptIdentityTrials[1] = outOfRangeAttemptIdentityTrials[1] with
        {
            Workflow = outOfRangeAttemptIdentityTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == DiscoveryExecutorIds.Ranker
                        ? stage with { LastUsableResponseAttemptNumber = 99 }
                        : stage).ToArray(),
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = outOfRangeAttemptIdentityTrials },
            },
        }));

        var undefinedProviderStatusTrials = live.Trials.ToArray();
        undefinedProviderStatusTrials[1] = undefinedProviderStatusTrials[1] with
        {
            Workflow = undefinedProviderStatusTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == providerStage.ExecutorId
                        ? stage with { Status = "future-status" }
                        : stage).ToArray(),
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = undefinedProviderStatusTrials },
            },
        }));

        var terminalProviderTrials = live.Trials.ToArray();
        var recoveredStage = terminalProviderTrials[1].Workflow!.ProviderStages.Single(static stage =>
            stage.Status == nameof(LiveWorkflowProviderStageStatus.Recovered));
        terminalProviderTrials[1] = terminalProviderTrials[1] with
        {
            Workflow = terminalProviderTrials[1].Workflow! with
            {
                ProviderStages = providerStages.Select(stage =>
                    stage.ExecutorId == recoveredStage.ExecutorId
                        ? stage with
                        {
                            UnusableAttemptCount = stage.AttemptCount,
                            Status = nameof(LiveWorkflowProviderStageStatus.FinalFallback),
                            LastUnusableAttemptNumber = stage.LastUsableResponseAttemptNumber,
                            LastUsableResponseAttemptNumber = 0,
                        }
                        : stage).ToArray(),
                RecoveredProviderFailedAttemptCount = 0,
                TerminalProviderStageCount = 1,
            },
        };
        var measuredTerminalProvider = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Trials = terminalProviderTrials },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(measuredTerminalProvider));
    }

    [Fact]
    public void SchemaTenMeasuredWorkflowRequiresItsAlwaysModelBackedProviderStages()
    {
        var result = SyntheticEval02Result();
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            Guid.NewGuid(),
            new(VitrineRunMode.Evals, Personas.NadiaUserId,
                EvaluationPlan: VitrineEvaluationPlan.LiveEval02Workflow,
                LiveScenarioId: "nadia-cross-category",
                EvaluationRepetitions: 1,
                PaidExecutionConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(VitrineEvaluationPlan.LiveEval02Workflow),
            [],
            LiveEvaluation: result));

        var frozen = VitrineArtifactSerializer.Freeze(artifact);
        Assert.True(VitrineArtifactSerializer.Verify(frozen));
        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(frozen.Result.LiveEvaluation);
        Assert.Equal(3, Assert.Single(live.Trials).Workflow!.ProviderStages.Count);

        var trial = live.Trials[0];
        var forged = frozen with
        {
            Result = frozen.Result with
            {
                LiveEvaluation = live with
                {
                    Trials =
                    [
                        trial with
                        {
                            Workflow = trial.Workflow! with
                            {
                                ProviderStages = [],
                                ProviderFailedAttemptCount = 0,
                                RecoveredProviderFailedAttemptCount = 0,
                            },
                        },
                    ],
                },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(forged));
    }

    [Fact]
    public void LiteralSchemaNineWorkflowReceiptVerifiesAndRejectsUnsignedProviderEvidence()
    {
        const string legacyJson = """
{
  "schemaVersion": 9,
  "runId": "11111111-1111-1111-1111-111111111111",
  "createdAtUtc": "2026-09-01T09:59:00\u002B00:00",
  "mode": "evals",
  "personaId": "persona",
  "executionArm": "LiveEval02Workflow",
  "personalizationDisabled": false,
  "graph": {
    "name": "legacy-workflow",
    "source": "evaluationService",
    "nodes": [],
    "edges": [],
    "runtimeSourceId": null
  },
  "events": [],
  "result": {
    "status": "passed",
    "failureKind": null,
    "processEquivalentExitCode": 0,
    "modelCalls": null,
    "toolCalls": null,
    "presented": null,
    "survived": null,
    "workflowLooped": null,
    "workflowSuperSteps": null,
    "workflowStopReason": null,
    "gates": [],
    "controls": [],
    "demo01": null,
    "demo02": null,
    "offlineBenchmark": null,
    "evaluationExecution": null,
    "liveEvaluation": {
      "plan": "LiveEval02Workflow",
      "planLabel": "Eval 02",
      "planDescription": "Legacy workflow fixture",
      "terminalStatus": "Passed",
      "exitCode": 0,
      "sessionId": "legacy-session",
      "startedAtUtc": "2026-09-01T10:00:00\u002B00:00",
      "completedAtUtc": "2026-09-01T10:01:00\u002B00:00",
      "workload": {
        "scenarioCount": 1,
        "armCount": 1,
        "repetitions": 1,
        "plannedSubjectCalls": 1,
        "plannedJudgeEvaluations": 1,
        "safetyAttackCount": 0,
        "plannedSafetyProbes": 0,
        "maximumSafetyModelCalls": 0
      },
      "passThreshold": 0.75,
      "scenarios": [
        {
          "id": "scenario",
          "personaId": "persona",
          "title": "Scenario",
          "description": "Description",
          "query": "Query",
          "expectedBehavior": "Expected",
          "criteria": [],
          "groundTruthFacts": [],
          "agentToolExpectation": null
        }
      ],
      "runs": [],
      "trials": [
        {
          "scenarioId": "scenario",
          "personaId": "persona",
          "armId": "discovery-workflow-live",
          "architecture": "Workflow",
          "repetition": 1,
          "measurement": "Measured",
          "passed": true,
          "subjectStatus": "Completed",
          "responsePreview": "answer",
          "tools": {
            "journalObserved": false,
            "toolNames": [],
            "executed": 0,
            "completed": 0,
            "failed": 0,
            "cancelled": 0,
            "unknownNameCount": 0,
            "calls": [],
            "unreconciledCount": 0
          },
          "workflow": {
            "executors": [
              {
                "executorId": "InterestMapper",
                "executionCount": 1
              }
            ],
            "routes": [
              "map-to-discovery"
            ],
            "discoveryRounds": 1,
            "maximumRounds": 3,
            "superSteps": 2,
            "stopReason": "CoverageSufficient",
            "looped": false,
            "failureCount": 0,
            "degradationCount": 0,
            "degradationKinds": [],
            "unknownExecutorCount": 0,
            "unknownRouteCount": 0
          },
          "checks": [
            {
              "key": "vitrine.live.use-case-quality",
              "name": "quality",
              "measurement": "Measured",
              "score": 1,
              "passed": true
            },
            {
              "key": "vitrine.live.response-observed",
              "name": "response",
              "measurement": "Measured",
              "score": 1,
              "passed": true
            },
            {
              "key": "vitrine.live.workflow-trace",
              "name": "workflow",
              "measurement": "Measured",
              "score": 1,
              "passed": true
            }
          ],
          "criteria": [],
          "subjectUsage": {
            "status": "not-reported",
            "modelCalls": null,
            "inputTokens": null,
            "outputTokens": null,
            "totalTokens": null,
            "estimatedCostUsd": null
          },
          "judgeUsage": {
            "status": "not-reported",
            "modelCalls": null,
            "inputTokens": null,
            "outputTokens": null,
            "totalTokens": null,
            "estimatedCostUsd": null
          },
          "failure": null
        }
      ],
      "arms": [],
      "comparisons": [],
      "persistence": {
        "workspaceRoot": "workspace",
        "sessionDirectory": "session",
        "outcomePath": "outcome.json",
        "indexPath": "index.json"
      },
      "configuration": {
        "definitionKey": "definition",
        "definitionVersion": "version",
        "judgeModelId": "judge",
        "judgePromptId": "prompt",
        "judgeRubricHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "subjectMaxOutputTokens": 4000,
        "judgeMaxOutputTokens": 1200,
        "responsePreviewCharacters": 1000,
        "subjects": [
          {
            "armId": "discovery-workflow-live",
            "architecture": "Workflow",
            "modelId": "subject",
            "judgeSubjectRelation": "differentModel"
          }
        ],
        "safety": null,
        "acceptance": {
          "policy": "EveryTrialMustPass",
          "confidenceLevel": null,
          "minimumLowerBound": null
        }
      },
      "failures": [],
      "safety": null,
      "scenarioAcceptances": []
    }
  },
  "integritySha256": "211595b7b7546846bc8a6fe2693b470cfc3fec7e51405ec968caa64e5ee2abfb"
}
""";

        var restored = VitrineArtifactSerializer.Deserialize(legacyJson);
        var workflow = Assert.IsType<VitrineLiveEvaluationSnapshot>(restored.Result.LiveEvaluation)
            .Trials[0].Workflow!;

        Assert.True(VitrineArtifactSerializer.Verify(restored));
        Assert.Equal(9, restored.SchemaVersion);
        Assert.Empty(workflow.ProviderStages);
        Assert.Equal(0, workflow.ProviderFailedAttemptCount);
        Assert.Equal(0, workflow.RecoveredProviderFailedAttemptCount);
        Assert.Equal(0, workflow.TerminalProviderStageCount);
        Assert.DoesNotContain("providerStages", VitrineArtifactSerializer.Serialize(restored),
            StringComparison.Ordinal);

        var injected = legacyJson.Replace(
            "\"unknownRouteCount\": 0",
            "\"unknownRouteCount\": 0,\n            \"providerStages\": []",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Deserialize(injected));
    }

    [Fact]
    public void StochasticScenarioAcceptanceRoundTripsAndRendersAsTheTerminalDecision()
    {
        var result = SyntheticStochasticResult();
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            Guid.NewGuid(),
            new(
                VitrineRunMode.Evals,
                Personas.NadiaUserId,
                EvaluationPlan: result.Plan,
                LiveScenarioId: "nadia-cross-category",
                EvaluationRepetitions: 4,
                PaidExecutionConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(result.Plan),
            [],
            LiveEvaluation: result));

        var json = VitrineArtifactSerializer.Serialize(artifact);
        var roundTripped = VitrineArtifactSerializer.Deserialize(json);
        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(roundTripped.Result.LiveEvaluation);
        var decision = Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyList<VitrineLiveScenarioAcceptanceSnapshot>>(live.ScenarioAcceptances));
        var html = VitrineHtmlReport.Render(roundTripped);
        var (_, inspector) = VitrineOutcomeInspector.Describe(roundTripped);
        var board = new EvaluationBoardViewModel();
        board.LoadLiveSnapshot(live);

        Assert.Equal("nadia-cross-category", decision.ScenarioId);
        Assert.True(decision.Passed);
        Assert.Equal((4, 4), (decision.Reliability.Successes, decision.Reliability.Total));
        Assert.Contains("\"scenarioAcceptances\"", json, StringComparison.Ordinal);
        Assert.Contains("WilsonLowerBoundPerScenario", json, StringComparison.Ordinal);
        Assert.Contains("Terminal per-scenario Wilson decisions", html, StringComparison.Ordinal);
        Assert.Contains("nadia-cross-category", html, StringComparison.Ordinal);
        Assert.Contains("lower bound &gt;= 0.500", html, StringComparison.Ordinal);
        Assert.Contains("requires all four authored criteria", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("Terminal per-scenario Wilson decisions", StringComparison.Ordinal)
            < html.IndexOf("Per-check census and Wilson reliability", StringComparison.Ordinal));
        Assert.Contains("Terminal acceptance: WilsonLowerBoundPerScenario", inspector,
            StringComparison.Ordinal);
        Assert.Contains("Terminal per-scenario Wilson decisions", inspector,
            StringComparison.Ordinal);
        Assert.Contains("whole-trial success means use-case quality", inspector,
            StringComparison.Ordinal);
        Assert.Contains("minimum lower bound 0.500", inspector, StringComparison.Ordinal);
        var boardDecision = Assert.Single(board.LiveScenarioAcceptances);
        Assert.Equal("PASS", boardDecision.Outcome);
        Assert.Contains("Wilson [0.510", boardDecision.WilsonInterval, StringComparison.Ordinal);
        Assert.Contains("lower bound >= 0.500", boardDecision.Policy, StringComparison.Ordinal);

        var forged = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    ScenarioAcceptances = [decision with { Passed = false }],
                },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(forged));
        var nonCanonicalPolicy = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    Configuration = live.Configuration! with
                    {
                        Acceptance = live.Configuration.Acceptance! with
                        {
                            MinimumLowerBound = 0.49,
                        },
                    },
                },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(nonCanonicalPolicy));
        var forgedWilson = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    ScenarioAcceptances =
                    [
                        decision with
                        {
                            Reliability = decision.Reliability with
                            {
                                Lower = decision.Reliability.Lower!.Value - 0.01,
                            },
                        },
                    ],
                },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(forgedWilson));

        var threeOfFour = WilsonInterval.Compute(3, 4);
        var trialContradiction = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    TerminalStatus = nameof(LiveEvalTerminalStatus.QualityFailed),
                    ExitCode = EvaluationExitCodes.GateFailed,
                    ScenarioAcceptances =
                    [
                        decision with
                        {
                            Reliability = decision.Reliability with
                            {
                                Successes = 3,
                                Estimate = threeOfFour.Estimate,
                                Lower = threeOfFour.Lower,
                                Upper = threeOfFour.Upper,
                            },
                            Passed = false,
                        },
                    ],
                },
            },
        };
        Assert.Throws<InvalidDataException>(() =>
            VitrineArtifactSerializer.Serialize(trialContradiction));

        var forgedWholeTrial = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    TerminalStatus = nameof(LiveEvalTerminalStatus.QualityFailed),
                    ExitCode = EvaluationExitCodes.GateFailed,
                    Trials = [.. live.Trials.Take(3), live.Trials[3] with { Passed = false }],
                    ScenarioAcceptances =
                    [
                        decision with
                        {
                            Reliability = decision.Reliability with
                            {
                                Successes = 3,
                                Estimate = threeOfFour.Estimate,
                                Lower = threeOfFour.Lower,
                                Upper = threeOfFour.Upper,
                            },
                            Passed = false,
                        },
                    ],
                },
            },
        };
        Assert.Throws<InvalidDataException>(() =>
            VitrineArtifactSerializer.Serialize(forgedWholeTrial));

        var duplicateRepetition = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    Trials =
                    [
                        live.Trials[0],
                        live.Trials[1] with { Repetition = 1 },
                        live.Trials[2],
                        live.Trials[3],
                    ],
                },
            },
        };
        Assert.Throws<InvalidDataException>(() =>
            VitrineArtifactSerializer.Serialize(duplicateRepetition));

        var threeOfThree = WilsonInterval.Compute(3, 3);
        var undersampled = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    TerminalStatus = nameof(LiveEvalTerminalStatus.QualityFailed),
                    ExitCode = EvaluationExitCodes.GateFailed,
                    Workload = live.Workload with
                    {
                        Repetitions = 3,
                        PlannedSubjectCalls = 3,
                        PlannedJudgeEvaluations = 3,
                    },
                    Runs = live.Runs.Take(3).ToArray(),
                    Trials = live.Trials.Take(3).ToArray(),
                    ScenarioAcceptances =
                    [
                        decision with
                        {
                            Census = new(3, 0, 0, 3),
                            Reliability = new(
                                nameof(MeasurementState.Measured),
                                3,
                                3,
                                threeOfThree.Estimate,
                                threeOfThree.Lower,
                                threeOfThree.Upper),
                            Passed = false,
                        },
                    ],
                },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(undersampled));
    }

    [Fact]
    public void InterruptedStochasticArtifactRetainsPartialTrialsWithoutInventingAcceptance()
    {
        var complete = SyntheticStochasticResult();
        var partial = complete with
        {
            TerminalStatus = LiveEvalTerminalStatus.Cancelled,
            Runs = complete.Runs.Take(1).ToArray(),
            Trials = complete.Trials.Take(1).ToArray(),
            Arms = [],
            ScenarioAcceptances = [],
            Failures = [new(LiveEvalFailureCode.Cancelled, "cancelled")],
        };
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            Guid.NewGuid(),
            new(
                VitrineRunMode.Evals,
                Personas.NadiaUserId,
                EvaluationPlan: partial.Plan,
                LiveScenarioId: "nadia-cross-category",
                EvaluationRepetitions: 4,
                PaidExecutionConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(partial.Plan),
            [],
            LiveEvaluation: partial));

        var restored = VitrineArtifactSerializer.Deserialize(
            VitrineArtifactSerializer.Serialize(artifact));
        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(restored.Result.LiveEvaluation);
        Assert.Equal(nameof(LiveEvalTerminalStatus.Cancelled), live.TerminalStatus);
        Assert.Single(live.Trials);
        Assert.Empty(Assert.IsAssignableFrom<
            IReadOnlyList<VitrineLiveScenarioAcceptanceSnapshot>>(live.ScenarioAcceptances));
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
                PaidExecutionConfirmed: true),
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
            [
                new(key, name, MeasurementState.Measured, 0.9, true),
                new(LiveUseCaseBenchmark.ResponseObservedCheckKey, "Response observed",
                    MeasurementState.Measured, 1, true),
                new(LiveUseCaseBenchmark.AgentToolJournalCheckKey, "Agent tool journal",
                    MeasurementState.Measured, 1, true),
            ],
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
                ["InterestMapper:fallback", "CoverageReviewer:model-failure"])
            {
                ProviderStages =
                [
                    new("InterestMapper", 2, 1, 1, 1, 0,
                        LiveWorkflowProviderStageStatus.Recovered)
                    {
                        LastUnusableAttemptNumber = 1,
                        LastUsableResponseAttemptNumber = 2,
                    },
                    new(DiscoveryExecutorIds.Ranker, 1, 1, 0, 0, 0,
                        LiveWorkflowProviderStageStatus.Completed)
                    {
                        LastUsableResponseAttemptNumber = 3,
                    },
                    new(DiscoveryExecutorIds.Presenter, 1, 1, 0, 0, 0,
                        LiveWorkflowProviderStageStatus.Completed)
                    {
                        LastUsableResponseAttemptNumber = 4,
                    },
                ],
                ProviderFailedAttemptCount = 1,
                RecoveredProviderFailedAttemptCount = 1,
                TerminalProviderStageCount = 0,
            },
            [
                new(key, name, MeasurementState.Measured, 0.6, false),
                new(LiveUseCaseBenchmark.ResponseObservedCheckKey, "Response observed",
                    MeasurementState.Measured, 1, true),
                new(LiveUseCaseBenchmark.WorkflowTraceCheckKey, "Workflow trace",
                    MeasurementState.Measured, 1, true),
            ],
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
            new LiveEvalConfiguration("vitrine-live-use-cases", "1", "judge", "prompt", "rubric", 512, 256, 640,
            [
                new("robin-agent-live", LiveSubjectArchitecture.Agent, "agent-model", JudgeSubjectRelation.DifferentModel),
                new("discovery-workflow-live", LiveSubjectArchitecture.Workflow, "workflow-model", JudgeSubjectRelation.DifferentModel),
            ])
            {
                Acceptance = new(LiveTerminalAcceptancePolicy.EveryTrialMustPass, null, null),
            },
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

    private static LiveEvalResult SyntheticEval02Result()
    {
        var baseline = SyntheticResult();
        var workflowTrial = baseline.Trials[1];
        var workflow = workflowTrial.Workflow!;
        var interestMapper = workflow.ProviderStages.Single(static stage =>
            stage.ExecutorId == DiscoveryExecutorIds.InterestMapper);
        workflowTrial = workflowTrial with
        {
            Workflow = workflow with
            {
                ProviderStages =
                [
                    interestMapper,
                    new(DiscoveryExecutorIds.Ranker, 1, 1, 0, 0, 0,
                        LiveWorkflowProviderStageStatus.Completed)
                    {
                        LastUsableResponseAttemptNumber = 3,
                    },
                    new(DiscoveryExecutorIds.Presenter, 1, 1, 0, 0, 0,
                        LiveWorkflowProviderStageStatus.Completed)
                    {
                        LastUsableResponseAttemptNumber = 4,
                    },
                ],
            },
        };
        return baseline with
        {
            Plan = VitrineEvaluationPlan.LiveEval02Workflow,
            Workload = new(1, 1, 1, 1, 1),
            Configuration = baseline.Configuration with
            {
                Subjects = [baseline.Configuration.Subjects[1]],
            },
            Runs = [baseline.Runs[1]],
            Trials = [workflowTrial],
            Arms = [baseline.Arms[1]],
            Comparisons = [],
        };
    }

    private static LiveEvalResult SyntheticStochasticResult()
    {
        var baseline = SyntheticResult();
        var trial = baseline.Trials[0];
        var trials = Enumerable.Range(1, 4)
            .Select(repetition => trial with { Repetition = repetition })
            .ToArray();
        var interval = WilsonInterval.Compute(4, 4);
        var reliability = new LiveReliability(
            MeasurementState.Measured, 4, 4,
            interval.Estimate, interval.Lower, interval.Upper);
        var census = new LiveObservationCensus(4, 0, 0);
        return baseline with
        {
            Plan = VitrineEvaluationPlan.LiveEval04StochasticAgent,
            TerminalStatus = LiveEvalTerminalStatus.Passed,
            Workload = new(1, 1, 4, 4, 4),
            Configuration = baseline.Configuration with
            {
                Subjects = [baseline.Configuration.Subjects[0]],
                Acceptance = new(
                    LiveTerminalAcceptancePolicy.WilsonLowerBoundPerScenario,
                    0.95,
                    0.50),
            },
            Runs = Enumerable.Range(1, 4)
                .Select(repetition => new LiveEvalRunReference(
                    $"agent-run-{repetition}",
                    "robin-agent-live",
                    repetition,
                    $"runs/agent-run-{repetition}"))
                .ToArray(),
            Trials = trials,
            Arms =
            [
                new LiveArmSummary(
                    "robin-agent-live",
                    LiveSubjectArchitecture.Agent,
                    4,
                    [new(
                        LiveUseCaseBenchmark.UseCaseQualityCheckKey,
                        "Canonical use-case criteria",
                        census,
                        reliability)]),
            ],
            Comparisons = [],
            ScenarioAcceptances =
            [
                new(
                    "nadia-cross-category",
                    Personas.NadiaUserId,
                    "robin-agent-live",
                    LiveSubjectArchitecture.Agent,
                    census,
                    reliability,
                    0.95,
                    0.50,
                    true),
            ],
        };
    }
}
