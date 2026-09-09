// SPDX-License-Identifier: MIT

using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.App;
using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.App.Controls;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Demos;
using System.Security.Cryptography;
using System.Text;

namespace AgentEval.VitrineDemo.Tests;

public sealed class AppUiHardeningTests
{
    [Fact]
    public async Task PaidAcknowledgementIsCapturedOnceAndResetAcrossModes()
    {
        await using var viewModel = new MainWindowViewModel();
        viewModel.SelectedMode = VitrineRunMode.Evals;
        viewModel.Setup.SelectedEvaluationPlan = VitrineEvaluationPlans.Require(
            VitrineEvaluationPlan.LiveEval01Agent);
        viewModel.Setup.PaidEvaluationAcknowledged = true;

        var request = viewModel.CaptureRunRequestForExecution();

        Assert.True(request.PaidEvaluationConfirmed);
        Assert.False(viewModel.Setup.PaidEvaluationAcknowledged);
        viewModel.Setup.PaidEvaluationAcknowledged = true;
        viewModel.SelectedMode = VitrineRunMode.Demo01;
        Assert.False(viewModel.Setup.PaidEvaluationAcknowledged);
        viewModel.SelectedMode = VitrineRunMode.Evals;
        Assert.False(viewModel.Setup.PaidEvaluationAcknowledged);
    }

    [Fact]
    public void CorrelatedToolFailureExplainsTheMissingResultOnBothCardsAndFilterDropsHiddenSelection()
    {
        var runId = Guid.NewGuid();
        var timeline = new TimelineViewModel();
        var started = Event(runId, 1, "ToolExecutionStarted", VitrineEventDisposition.Active,
            VitrineEventCategory.Tool, "Robin", "GetProductDetails", "tool:42", "{\"productId\":\"GLX-1003\"}");
        var failed = Event(runId, 2, "ToolFailed", VitrineEventDisposition.Failed,
            VitrineEventCategory.Tool, "GetProductDetails", "Robin", "tool:42", null);

        timeline.Add(started);
        timeline.Add(failed);

        Assert.All(timeline.Events, card =>
        {
            Assert.Equal("FAILED", card.Disposition);
            Assert.True(card.HasPayloadAbsenceExplanation);
            Assert.Contains("tool did not return a result", card.PayloadAbsenceExplanation, StringComparison.Ordinal);
        });
        Assert.Single(timeline.Events[0].PayloadSections);
        timeline.FollowEvents = false;
        timeline.SelectedEvent = timeline.Events[0];
        timeline.SelectedFilter = TimelineFilter.Evals;
        Assert.Null(timeline.SelectedEvent);
    }

    [Fact]
    public void HtmlUsesTheCorrelatedTerminalDispositionInsteadOfLeavingStartActive()
    {
        var runId = Guid.NewGuid();
        var events = new[]
        {
            Event(runId, 1, "ToolExecutionStarted", VitrineEventDisposition.Active,
                VitrineEventCategory.Tool, "Robin", "GetProductDetails", "tool:42", "{\"productId\":\"GLX-1003\"}"),
            Event(runId, 2, "ToolFailed", VitrineEventDisposition.Failed,
                VitrineEventCategory.Tool, "GetProductDetails", "Robin", "tool:42", null),
        };
        var graph = new VitrineGraphSnapshot("fixture", VitrineGraphSource.RegisteredFunctions,
            [new("Robin", "Robin", "agent"), new("GetProductDetails", "GetProductDetails", "tool")],
            [new("tool", "Robin", "GetProductDetails", "call")]);
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(
            runId,
            new(VitrineRunMode.Demo01, "fixture", Demo01Arm: RecommendationExecutionArm.ScriptedAgent),
            graph,
            events));

        var html = VitrineHtmlReport.Render(artifact);

        Assert.Contains("ToolExecutionStarted · FAILED", html, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolExecutionStarted · STARTED", html, StringComparison.Ordinal);
        Assert.Contains("tool did not return a result", html, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineSnapshotRestoresTheEvaluationBoardWithoutExecutingAnything()
    {
        var floor = new VitrineChanceFloorSnapshot(
            "Permutation", "Derived", 0.5, 0.5, null, 2, 2, "fixture null model");
        var census = new VitrineBenchmarkCensusSnapshot(1, 0, 0, 1);
        var check = new VitrineBenchmarkCheckSnapshot(
            "fixture-check", floor, census, 1, 1, 0.5, 0.5, true, false);
        var benchmark = new VitrineOfflineBenchmarkSnapshot(
            "fixture-definition", "1", "fixture-arm", "fixture-run", "workspace",
            "workspace/fixture-run", [new("case", "Fixture")], [check])
        {
            Repetitions = 1,
            Arms = [new("fixture-arm", "Agent", "Fixture", [check])],
            Runs = [new("fixture-arm", 1, "Agent", "Fixture", "fixture-run", "workspace/fixture-run")],
        };
        var gate = new VitrineGateSnapshot(
            "Honesty claim", true, 1, null, "validated honesty evidence", GateMeasurementOutcome.Measured);
        var control = new VitrineControlSnapshot(
            "NC-01", "Fixture control", "fixture", "fixture target", "fixture producer", "fixture evaluator",
            ControlScopeClass.ProductionObservation, "production", ControlAttemptOutcome.MeasuredPass,
            ControlAttemptOutcome.MeasuredFail, ControlAttemptOutcome.MeasuredPass, true, true, "caught and restored");
        var result = new VitrineResultSnapshot(
            "passed", null, EvaluationExitCodes.Passed, 0, 0, null, null, null, null, null,
            [gate], [control], OfflineBenchmark: benchmark,
            EvaluationExecution: new(EvaluationExecutionProfile.OfflineDeterministic, "Demo01 + Demo02",
                "deterministic subjects", "AgentEval", null, 1, 0, 1, 2, null, null, null, null, false));
        var board = new EvaluationBoardViewModel();

        board.LoadOfflineSnapshot(result);

        Assert.Equal("PASS · exit 0", board.OverallStatus);
        Assert.Single(board.Gates);
        Assert.Single(board.Controls);
        Assert.True(board.HasBenchmark);
        Assert.Contains("fixture-definition@1", board.BenchmarkSummary, StringComparison.Ordinal);
        Assert.Contains("replay", board.ActiveStage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("replayed honesty evidence", board.HonestClaims, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaV7RejectsNullItemsInsideNestedLiveCollectionsBeforeFreezing()
    {
        var scenario = new VitrineLiveScenarioSnapshot(
            "scenario", "persona", "title", "description", "query", "expected",
            new VitrineLiveCriterionDefinitionSnapshot[] { null! });
        var live = new VitrineLiveEvaluationSnapshot(
            Plan: nameof(VitrineEvaluationPlan.LiveEval01Agent),
            PlanLabel: "Eval01",
            PlanDescription: "fixture",
            TerminalStatus: "Passed",
            ExitCode: 0,
            SessionId: "fixture-session",
            StartedAtUtc: new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
            CompletedAtUtc: new DateTimeOffset(2026, 9, 8, 12, 1, 0, TimeSpan.Zero),
            Workload: new(1, 1, 1, 1, 1),
            PassThreshold: 0.75,
            Scenarios: [scenario],
            Runs: [],
            Trials: [],
            Arms: [],
            Comparisons: [],
            Persistence: new("workspace", "session", "outcome.json", "index.json"));
        var artifact = new VitrineRunArtifact(
            VitrineRunArtifact.CurrentSchemaVersion,
            Guid.NewGuid(),
            new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
            VitrineRunMode.Evals,
            "persona",
            nameof(VitrineEvaluationPlan.LiveEval01Agent),
            false,
            VitrineGraphSnapshot.Empty("fixture"),
            [],
            new("passed", null, 0, null, null, null, null, null, null, null, [], [],
                LiveEvaluation: live),
            string.Empty);

        var error = Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Freeze(artifact));
        Assert.Contains("required collection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogueSelfTestBannerSemanticIsModeSpecific()
    {
        await using var viewModel = new MainWindowViewModel(
            new VitrineRunCoordinator(static _ => VitrineGraphSnapshot.Empty("fixture")));
        viewModel.SelectedMode = VitrineRunMode.Evals;
        Assert.False(viewModel.IsCatalogueSelfTestMode);
        viewModel.SelectedMode = VitrineRunMode.Ablation;
        Assert.True(viewModel.IsCatalogueSelfTestMode);
        viewModel.SelectedMode = VitrineRunMode.Demo01;
        Assert.False(viewModel.IsCatalogueSelfTestMode);
    }

    [Fact]
    public void MinimumWidthDemo01LayoutKeepsModelBelowAndOutsideRobinToolFanAndArrowTipsOutsideCards()
    {
        var graph = new GraphViewModel();
        graph.Load(VitrineGraphFactory.FromRegisteredDemo01Functions());

        var requiredHeight = RuntimeGraphControl.RequiredHeightForLayout(graph, 620);
        var positions = RuntimeGraphControl.Layout(graph, 620, requiredHeight);
        var agent = graph.Nodes.Single(static node => node.Kind == "agent");

        Assert.True(requiredHeight >= 500);
        Assert.Equal(positions["customer"].Y, positions[agent.Id].Y);
        Assert.Equal(positions[agent.Id].Y, positions["guardrails"].Y);
        Assert.True(positions["model"].Y > positions[agent.Id].Y);
        Assert.True(positions["model"].Y > graph.Nodes.Where(static node => node.Kind == "tool")
            .Max(tool => positions[tool.Id].Y));
        Assert.All(graph.Nodes.Where(static node => node.Kind == "tool"),
            tool => Assert.True(positions[tool.Id].Y >= 230));

        foreach (var toolId in new[] { "ListDepartments", "GetProductDetails" })
        {
            var target = positions[toolId];
            var route = RuntimeGraphControl.Demo01ToolRoute(positions[agent.Id], target);
            Assert.Equal(target.X, route[^1].End.X, 6);
            Assert.Equal(target.Y - 29, route[^1].End.Y, 6);
            var nonTargets = graph.Nodes.Where(node => node.Kind == "tool" && node.Id != toolId)
                .Select(node => new Rect(positions[node.Id].X - 75, positions[node.Id].Y - 25, 150, 50))
                .Append(new Rect(positions["model"].X - 83, positions["model"].Y - 25, 166, 50));
            Assert.All(nonTargets, rectangle => Assert.DoesNotContain(route,
                leg => SegmentIntersectsInterior(leg.Start, leg.End, rectangle)));
        }
    }

    [Fact]
    public void RectangleTrimmingUsesCardHeightForVerticalEdges()
    {
        var segment = RuntimeGraphControl.TrimmedSegment(
            new Point(0, 0), new Point(0, 100), 166, 50, 150, 50);

        Assert.Equal(29, segment.Start.Y, 6);
        Assert.Equal(71, segment.End.Y, 6);
        Assert.True(segment.End.Y < 75); // arrow tip remains outside the target rectangle
    }

    [Theory]
    [InlineData(VitrineEvaluationPlan.LiveEval01Agent, true)]
    [InlineData(VitrineEvaluationPlan.LiveEval04StochasticAgent, true)]
    [InlineData(VitrineEvaluationPlan.LiveEval02Workflow, false)]
    [InlineData(VitrineEvaluationPlan.LiveEval05StochasticWorkflow, false)]
    public void SingleArchitectureLiveGraphsContainOnlyApplicableIndependentChecks(
        VitrineEvaluationPlan plan,
        bool agent)
    {
        var graph = VitrineGraphFactory.ForLiveEvaluation(plan);
        var subject = agent ? "live-agent" : "live-workflow";
        var applicable = agent
            ? LiveUseCaseBenchmark.AgentToolJournalCheckKey
            : LiveUseCaseBenchmark.WorkflowTraceCheckKey;
        var inapplicable = agent
            ? LiveUseCaseBenchmark.WorkflowTraceCheckKey
            : LiveUseCaseBenchmark.AgentToolJournalCheckKey;

        Assert.Contains(graph.Nodes, node => node.Id == applicable);
        Assert.DoesNotContain(graph.Nodes, node => node.Id == inapplicable);
        Assert.All(graph.Nodes.Where(static node => node.Kind == "evaluation-check"), check =>
        {
            Assert.Contains(graph.Edges, edge => edge.SourceId == subject && edge.TargetId == check.Id);
            Assert.Contains(graph.Edges, edge => edge.SourceId == check.Id && edge.TargetId == "live-persistence");
        });
        Assert.DoesNotContain(graph.Edges, edge =>
            graph.Nodes.Any(node => node.Id == edge.SourceId && node.Kind == "evaluation-check")
            && graph.Nodes.Any(node => node.Id == edge.TargetId && node.Kind == "evaluation-check"));
    }

    [Fact]
    public void ComparisonLiveGraphKeepsAgentAndWorkflowChecksOnSeparateBranches()
    {
        var graph = VitrineGraphFactory.ForLiveEvaluation(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow);
        var agentChecks = graph.Nodes.Where(static node =>
            node.Id.StartsWith("live-agent:", StringComparison.Ordinal)).ToArray();
        var workflowChecks = graph.Nodes.Where(static node =>
            node.Id.StartsWith("live-workflow:", StringComparison.Ordinal)).ToArray();

        Assert.Equal(3, agentChecks.Length);
        Assert.Equal(3, workflowChecks.Length);
        Assert.DoesNotContain(agentChecks, node => node.Id.EndsWith(
            LiveUseCaseBenchmark.WorkflowTraceCheckKey, StringComparison.Ordinal));
        Assert.DoesNotContain(workflowChecks, node => node.Id.EndsWith(
            LiveUseCaseBenchmark.AgentToolJournalCheckKey, StringComparison.Ordinal));
        Assert.All(agentChecks, check => Assert.Contains(graph.Edges,
            edge => edge.SourceId == "live-agent" && edge.TargetId == check.Id));
        Assert.All(workflowChecks, check => Assert.Contains(graph.Edges,
            edge => edge.SourceId == "live-workflow" && edge.TargetId == check.Id));
        Assert.DoesNotContain(graph.Edges, edge =>
            edge.SourceId.StartsWith("live-agent:", StringComparison.Ordinal)
            && edge.TargetId.StartsWith("live-workflow:", StringComparison.Ordinal)
            || edge.SourceId.StartsWith("live-workflow:", StringComparison.Ordinal)
            && edge.TargetId.StartsWith("live-agent:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(LiveEvalTerminalStatus.Passed, GraphNodeState.Succeeded)]
    [InlineData(LiveEvalTerminalStatus.QualityFailed, GraphNodeState.Failed)]
    [InlineData(LiveEvalTerminalStatus.InfrastructureError, GraphNodeState.Failed)]
    [InlineData(LiveEvalTerminalStatus.Cancelled, GraphNodeState.NotMeasured)]
    public void SafetyGraphProjectsEveryCoreNodeFromTypedProgressAndRedactedResult(
        LiveEvalTerminalStatus terminal,
        GraphNodeState expected)
    {
        var result = SafetyResult(terminal);
        var graph = new GraphViewModel();
        var store = new VitrineEventStore();
        graph.Load(VitrineGraphFactory.ForRunningLiveEvaluation(VitrineEvaluationPlan.LiveEval06SafetyProbes));
        var progress = new List<LiveEvalProgress>
        {
            new(result.Plan, LiveEvalProgressPhase.SessionStarting, null, null, null, null, null, null, "start"),
        };
        if (result.Safety is { } safety)
        {
            progress.Add(new(result.Plan, LiveEvalProgressPhase.SafetyTargetRunning,
                null, null, null, null, null, null, "bounded target"));
            progress.Add(new(result.Plan, LiveEvalProgressPhase.SafetyFindingsCompleted,
                null, null, null, null, safety.Measurement, safety.Passed, "redacted findings"));
        }
        progress.Add(new(result.Plan, LiveEvalProgressPhase.Persisting,
            null, null, null, null, null, null, "persist"));
        progress.Add(new(result.Plan, LiveEvalProgressPhase.SessionCompleted,
            null, null, null, null, result.Safety?.Measurement, result.Safety?.Passed, "complete"));
        foreach (var item in progress)
            graph.Apply(store.Append(VitrineEventAdapters.FromLiveEvaluation(item)));
        foreach (var draft in VitrineEventAdapters.FromLiveSafetyResult(result))
            graph.Apply(store.Append(draft));

        foreach (var nodeId in new[] { "live-safety-target", "live-safety-jailbreak",
                     "live-safety-extraction", "live-safety-findings", "live-persistence", "live-complete" })
        {
            Assert.NotEqual(GraphNodeState.Idle, graph.StateOf(nodeId));
            Assert.NotEqual(GraphNodeState.Active, graph.StateOf(nodeId));
        }
        Assert.Equal(expected, graph.StateOf("live-safety-target"));
        Assert.Equal(expected, graph.StateOf("live-safety-findings"));
        Assert.Equal(expected, graph.StateOf("live-complete"));
        if (terminal == LiveEvalTerminalStatus.QualityFailed)
        {
            Assert.Equal(GraphNodeState.Failed, graph.StateOf("live-safety-jailbreak"));
            Assert.Equal(GraphNodeState.Succeeded, graph.StateOf("live-safety-extraction"));
        }
        else Assert.Equal(expected, graph.StateOf("live-safety-jailbreak"));

        var targetProgress = progress.FirstOrDefault(item => item.Phase == LiveEvalProgressPhase.SafetyTargetRunning);
        if (targetProgress is not null)
            Assert.Equal(VitrineEventDisposition.Active,
                VitrineEventAdapters.FromLiveEvaluation(targetProgress).Disposition);
    }

    [Fact]
    public void SafetyArtifactUsesNullableQualityBarAndReplaysRedactedCensusEverywhere()
    {
        var result = SafetyResult(LiveEvalTerminalStatus.QualityFailed);
        var outcome = new VitrineRunOutcome(Guid.NewGuid(),
            new(VitrineRunMode.Evals, "persona", EvaluationPlan: result.Plan,
                PaidEvaluationConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(result.Plan), [], LiveEvaluation: result);

        var artifact = VitrineArtifactSerializer.Create(outcome);
        var json = VitrineArtifactSerializer.Serialize(artifact);
        var roundTrip = VitrineArtifactSerializer.Deserialize(json);
        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(roundTrip.Result.LiveEvaluation);
        var html = VitrineHtmlReport.Render(roundTrip);
        var inspector = VitrineOutcomeInspector.Describe(roundTrip).Detail;
        var board = new EvaluationBoardViewModel();
        board.LoadLiveSnapshot(live);

        Assert.Null(live.PassThreshold);
        Assert.Contains("\"passThreshold\": null", json, StringComparison.Ordinal);
        Assert.Contains("\"compromised\": 1", json, StringComparison.Ordinal);
        Assert.Contains("Paid live safety evaluation", html, StringComparison.Ordinal);
        Assert.Contains("VULNERABLE", html, StringComparison.Ordinal);
        Assert.Contains("ATTACK SUCCEEDED", html, StringComparison.Ordinal);
        Assert.Contains("Quality pass bar", html, StringComparison.Ordinal);
        Assert.Contains("NOT APPLICABLE", html, StringComparison.Ordinal);
        Assert.Contains("Paid live safety evaluation", inspector, StringComparison.Ordinal);
        Assert.Contains("maximum model calls 104", inspector, StringComparison.Ordinal);
        Assert.Contains("Raw probes", inspector, StringComparison.Ordinal);
        Assert.True(board.HasLiveSafety);
        Assert.Contains("VULNERABLE", board.LiveSafetyStatus, StringComparison.Ordinal);
        Assert.Contains("NOT APPLICABLE", board.LiveQualityPassBar, StringComparison.Ordinal);
        Assert.Equal(2, board.LiveSafetyAttacks.Count);
        Assert.Equal(4, board.LiveSafetyProbes.Count);
    }

    [Fact]
    public void ErroredSafetyReceiptExplainsInfrastructureFailureAcrossEveryReplaySurface()
    {
        const string forbiddenRawDetail = "RAW-PROVIDER-EXCEPTION-SENTINEL";
        var result = SafetyResult(LiveEvalTerminalStatus.InfrastructureError);
        var outcome = new VitrineRunOutcome(Guid.NewGuid(),
            new(VitrineRunMode.Evals, "persona", EvaluationPlan: result.Plan,
                PaidEvaluationConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(result.Plan), [], LiveEvaluation: result);
        var artifact = VitrineArtifactSerializer.Create(outcome);
        var json = VitrineArtifactSerializer.Serialize(artifact);
        var roundTrip = VitrineArtifactSerializer.Deserialize(json);
        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(roundTrip.Result.LiveEvaluation);
        var html = VitrineHtmlReport.Render(roundTrip);
        var inspector = VitrineOutcomeInspector.Describe(roundTrip).Detail;
        var board = new EvaluationBoardViewModel();
        board.LoadLiveSnapshot(live);
        using var console = new StringWriter();
        ConsoleReport.Print(result, console);
        var events = VitrineEventAdapters.FromLiveSafetyResult(result);

        Assert.Equal(8, roundTrip.SchemaVersion);
        Assert.Contains("\"diagnostic\": \"An unexpected probe execution fault occurred;", json,
            StringComparison.Ordinal);
        Assert.Contains("\"stage\": \"probe-execution\"", json, StringComparison.Ordinal);
        Assert.Contains("\"code\": \"Execution\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(forbiddenRawDetail, json, StringComparison.Ordinal);
        Assert.Contains("INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · 2/4 PROBES ERRORED",
            board.LiveSafetyStatus, StringComparison.Ordinal);
        Assert.Equal("#F07076", board.LiveSafetyStatusColor);
        Assert.Contains("Campaign execution returned", board.LiveSafetySummary, StringComparison.Ordinal);
        Assert.Contains("2 errored subset; do not add", board.LiveSafetySummary, StringComparison.Ordinal);
        Assert.Contains(board.LiveSafetyProbes, probe =>
            probe.OutcomeLabel == "ERROR · EXECUTION"
            && probe.Diagnostic == LiveSafetyProbeDiagnostics.Execution
            && probe.HasFailure
            && probe.FailureStage == "probe-execution"
            && probe.FailureCode == nameof(LiveSafetyProbeErrorKind.Execution)
            && probe.FailureDetail == LiveSafetyProbeDiagnostics.Execution);
        Assert.Contains("INFRASTRUCTURE ERROR", html, StringComparison.Ordinal);
        Assert.Contains("SAFETY VERDICT NOT MEASURED", html, StringComparison.Ordinal);
        Assert.Contains("2/4 PROBES ERRORED", html, StringComparison.Ordinal);
        Assert.Contains("Errored is a subset of Inconclusive", html, StringComparison.Ordinal);
        Assert.Contains(LiveSafetyProbeDiagnostics.Execution, html, StringComparison.Ordinal);
        Assert.Contains("Failure stage", html, StringComparison.Ordinal);
        Assert.Contains("Failure code", html, StringComparison.Ordinal);
        Assert.Contains("Failure detail", html, StringComparison.Ordinal);
        Assert.Contains("probe-execution", html, StringComparison.Ordinal);
        Assert.Contains("INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · 2/4 PROBES ERRORED",
            inspector, StringComparison.Ordinal);
        Assert.Contains("Errored is a subset of Inconclusive", inspector, StringComparison.Ordinal);
        Assert.Contains(LiveSafetyProbeDiagnostics.Timeout, inspector, StringComparison.Ordinal);
        Assert.Contains("typed failure stage probe-execution", inspector, StringComparison.Ordinal);
        Assert.Contains("code Execution", inspector, StringComparison.Ordinal);
        Assert.Contains($"detail {LiveSafetyProbeDiagnostics.Execution}", inspector, StringComparison.Ordinal);
        Assert.Contains("INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · 2/4 PROBES ERRORED",
            console.ToString(), StringComparison.Ordinal);
        Assert.Contains("2 inconclusive (2 errored subset; do not add these counts)",
            console.ToString(), StringComparison.Ordinal);
        Assert.Contains("Typed failure: stage probe-execution · code Execution · detail " +
            LiveSafetyProbeDiagnostics.Execution, console.ToString(), StringComparison.Ordinal);
        Assert.Contains(events, item => item.Kind == "LiveSafetyProbeObserved"
            && item.Disposition == VitrineEventDisposition.Failed
            && item.Detail.Contains(LiveSafetyProbeDiagnostics.Execution, StringComparison.Ordinal)
            && item.Detail.Contains("stage probe-execution", StringComparison.Ordinal)
            && item.Detail.Contains("code Execution", StringComparison.Ordinal)
            && item.Detail.Contains("detail " + LiveSafetyProbeDiagnostics.Execution, StringComparison.Ordinal));
        Assert.Contains(events, item => item.Kind == "LiveSafetyTerminalObserved"
            && item.Disposition == VitrineEventDisposition.Failed
            && item.Detail.Contains("INFRASTRUCTURE ERROR", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item =>
            item.Detail.Contains(forbiddenRawDetail, StringComparison.Ordinal)
            || item.Payload?.Contains(forbiddenRawDetail, StringComparison.Ordinal) == true);

        var safety = Assert.IsType<VitrineLiveSafetySnapshot>(live.Safety);
        var forged = artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    Safety = safety with
                    {
                        Probes = [safety.Probes[0] with { Diagnostic = forbiddenRawDetail }, .. safety.Probes.Skip(1)],
                    },
                },
            },
        };
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(forged));
    }

    [Fact]
    public void SchemaSevenSafetyArtifactWithoutDiagnosticStillVerifiesAndReplaysSafeFallback()
    {
        var result = SafetyResult(LiveEvalTerminalStatus.InfrastructureError);
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(Guid.NewGuid(),
            new(VitrineRunMode.Evals, "persona", EvaluationPlan: result.Plan,
                PaidEvaluationConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(result.Plan), [], LiveEvaluation: result));
        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(artifact.Result.LiveEvaluation);
        var safety = Assert.IsType<VitrineLiveSafetySnapshot>(live.Safety);
        var unsigned = artifact with
        {
            SchemaVersion = 7,
            Result = artifact.Result with
            {
                LiveEvaluation = live with
                {
                    Safety = safety with
                    {
                        Probes = safety.Probes.Select(static probe => probe with
                        {
                            Diagnostic = null,
                            Failure = null,
                        }).ToArray(),
                    },
                },
            },
            IntegritySha256 = string.Empty,
        };
        var payload = VitrineArtifactSerializer.SerializeIntegrityPayload(unsigned);
        var signed = unsigned with
        {
            IntegritySha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
                .ToLowerInvariant(),
        };

        var json = VitrineArtifactSerializer.Serialize(signed);
        var restored = VitrineArtifactSerializer.Deserialize(json);
        var restoredLive = Assert.IsType<VitrineLiveEvaluationSnapshot>(restored.Result.LiveEvaluation);
        var board = new EvaluationBoardViewModel();
        board.LoadLiveSnapshot(restoredLive);

        Assert.Equal(7, restored.SchemaVersion);
        Assert.DoesNotContain("\"diagnostic\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("probe-execution", json, StringComparison.Ordinal);
        Assert.Contains(board.LiveSafetyProbes, probe =>
            probe.OutcomeLabel == "ERROR · EXECUTION"
            && probe.Diagnostic == LiveSafetyProbeDiagnostics.Execution
            && probe.FailureStage == "probe-execution"
            && probe.FailureCode == nameof(LiveSafetyProbeErrorKind.Execution)
            && probe.FailureDetail == LiveSafetyProbeDiagnostics.Execution);
    }

    [Fact]
    public void SchemaEightRejectsMissingOrMismatchedProbeReceiptsAndContradictorySafetyCensus()
    {
        var result = SafetyResult(LiveEvalTerminalStatus.InfrastructureError);
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(Guid.NewGuid(),
            new(VitrineRunMode.Evals, "persona", EvaluationPlan: result.Plan,
                PaidEvaluationConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(result.Plan), [], LiveEvaluation: result));
        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(artifact.Result.LiveEvaluation);
        var safety = Assert.IsType<VitrineLiveSafetySnapshot>(live.Safety);
        var errorProbe = safety.Probes[0];

        VitrineRunArtifact WithSafety(VitrineLiveSafetySnapshot replacement) => artifact with
        {
            Result = artifact.Result with
            {
                LiveEvaluation = live with { Safety = replacement },
            },
        };
        VitrineLiveSafetySnapshot WithFirstProbe(VitrineLiveSafetyProbeSnapshot replacement) => safety with
        {
            Probes = [replacement, .. safety.Probes.Skip(1)],
        };

        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(
            WithSafety(WithFirstProbe(errorProbe with { Diagnostic = null }))));
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(
            WithSafety(WithFirstProbe(errorProbe with { Failure = null }))));
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(
            WithSafety(WithFirstProbe(errorProbe with
            {
                Diagnostic = LiveSafetyProbeDiagnostics.NoExecutionError,
            }))));
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(
            WithSafety(WithFirstProbe(errorProbe with
            {
                Failure = new("probe-execution", nameof(LiveSafetyProbeErrorKind.Transport),
                    LiveSafetyProbeDiagnostics.Transport),
            }))));
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(
            WithSafety(WithFirstProbe(errorProbe with
            {
                Outcome = nameof(LiveSafetyProbeOutcome.Resisted),
                Diagnostic = LiveSafetyProbeDiagnostics.Execution,
            }))));
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(
            WithSafety(safety with { Resisted = safety.Resisted + 1 })));
        Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(
            WithSafety(safety with
            {
                Attacks = [safety.Attacks[0] with { Errored = 0 }, .. safety.Attacks.Skip(1)],
            })));
    }

    [Fact]
    public void PartialAllScenarioSessionPreservesEverySelectedDefinitionInCurrentSchema()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var result = new LiveEvalResult(VitrineEvaluationPlan.LiveEval01Agent,
            LiveEvalTerminalStatus.Cancelled, "partial", now, now, new(4, 1, 1, 4, 4), 0.75,
            LiveUseCaseScenarios.All.Select(static scenario => scenario.ToDefinition()).ToArray(),
            new("definition", "1", "judge", "prompt", "rubric", 256, 256, 320, []),
            [], [], [], [], [new(LiveEvalFailureCode.Cancelled, "cancelled")],
            new("workspace", "session", "outcome.json", "index.json"));
        var artifact = VitrineArtifactSerializer.Create(new VitrineRunOutcome(Guid.NewGuid(),
            new(VitrineRunMode.Evals, "persona", EvaluationPlan: result.Plan,
                LiveScenarioId: null, PaidEvaluationConfirmed: true),
            VitrineGraphFactory.ForRunningLiveEvaluation(result.Plan), [], LiveEvaluation: result));

        var live = Assert.IsType<VitrineLiveEvaluationSnapshot>(
            VitrineArtifactSerializer.Deserialize(VitrineArtifactSerializer.Serialize(artifact))
                .Result.LiveEvaluation);
        Assert.Equal(LiveUseCaseScenarios.All.Select(static scenario => scenario.Id),
            live.Scenarios.Select(static scenario => scenario.Id));
    }

    [AvaloniaFact]
    public async Task DemoAndLivePreviewGraphsRenderAtCompactViewportWithoutExecutingPaidPlans()
    {
        var cases = new (VitrineRunMode Mode, VitrineEvaluationPlan? Plan, string CaptureVariable)[]
        {
            (VitrineRunMode.Demo01, null, "VITRINE_CAPTURE_PATH_DEMO01_PREVIEW_1280"),
            (VitrineRunMode.Evals, VitrineEvaluationPlan.LiveEval01Agent, "VITRINE_CAPTURE_PATH_EVAL01_PREVIEW_1280"),
            (VitrineRunMode.Evals, VitrineEvaluationPlan.LiveEval02Workflow, "VITRINE_CAPTURE_PATH_EVAL02_PREVIEW_1280"),
            (VitrineRunMode.Evals, VitrineEvaluationPlan.LiveEval03AgentVsWorkflow, "VITRINE_CAPTURE_PATH_EVAL03_PREVIEW_1280"),
            (VitrineRunMode.Evals, VitrineEvaluationPlan.LiveEval06SafetyProbes, "VITRINE_CAPTURE_PATH_EVAL06_PREVIEW_1280"),
        };
        foreach (var testCase in cases)
        {
            var viewModel = new MainWindowViewModel { SelectedMode = testCase.Mode, IsSetupExpanded = false };
            if (testCase.Plan is { } plan)
                viewModel.Setup.SelectedEvaluationPlan = VitrineEvaluationPlans.Require(plan);
            var window = new MainWindow(viewModel) { Width = 1280, Height = 720 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var graphControl = window.GetVisualDescendants().OfType<RuntimeGraphControl>().Single();
            var bitmap = window.CaptureRenderedFrame();

            Assert.True(graphControl.IsEffectivelyVisible);
            Assert.True(graphControl.Bounds.Width >= 600);
            Assert.NotNull(bitmap);
            Assert.Equal(new PixelSize(1280, 720), bitmap.PixelSize);
            var path = Environment.GetEnvironmentVariable(testCase.CaptureVariable);
            if (!string.IsNullOrWhiteSpace(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                bitmap.Save(path);
            }
            window.Close();
            await viewModel.DisposeAsync();
        }
    }

    private static bool SegmentIntersectsInterior(Point start, Point end, Rect rectangle)
    {
        const double margin = 0.5;
        var inner = new Rect(rectangle.X + margin, rectangle.Y + margin,
            rectangle.Width - margin * 2, rectangle.Height - margin * 2);
        if (inner.Contains(start) || inner.Contains(end)) return true;
        return SegmentsIntersect(start, end, inner.TopLeft, inner.TopRight)
            || SegmentsIntersect(start, end, inner.TopRight, inner.BottomRight)
            || SegmentsIntersect(start, end, inner.BottomRight, inner.BottomLeft)
            || SegmentsIntersect(start, end, inner.BottomLeft, inner.TopLeft);
    }

    private static bool SegmentsIntersect(Point a, Point b, Point c, Point d)
    {
        static double Cross(Point p, Point q, Point r) =>
            (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        return abC * abD <= 0 && cdA * cdB <= 0;
    }

    private static LiveEvalResult SafetyResult(LiveEvalTerminalStatus terminal)
    {
        var compromised = terminal == LiveEvalTerminalStatus.QualityFailed ? 1 : 0;
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var config = new LiveEvalConfiguration("vitrine-live-safety", "1", "judge", "prompt", "rubric",
            256, 256, 0, [])
        {
            Safety = new(["Jailbreak", "SystemPromptExtraction"], 2, 45, 25, 104, "fallback", false),
        };
        LiveSafetySummary? safety = terminal switch
        {
            LiveEvalTerminalStatus.Passed or LiveEvalTerminalStatus.QualityFailed =>
                new("robin-agent-live", MeasurementState.Measured, compromised == 0,
                4, 4 - compromised, compromised, 0, 0, false, 0,
                [
                    new("Jailbreak", "LLM01", 2, 2 - compromised, compromised, 0, 0),
                    new("SystemPromptExtraction", "LLM07", 2, 2, 0, 0, 0),
                ],
                [
                    new("Jailbreak", "JB-1", compromised == 1 ? LiveSafetyProbeOutcome.Compromised : LiveSafetyProbeOutcome.Resisted, LiveSafetyProbeErrorKind.None, "high", "high", "jailbreak"),
                    new("Jailbreak", "JB-2", LiveSafetyProbeOutcome.Resisted, LiveSafetyProbeErrorKind.None, "high", "high", "jailbreak"),
                    new("SystemPromptExtraction", "SP-1", LiveSafetyProbeOutcome.Resisted, LiveSafetyProbeErrorKind.None, "high", "high", "instruction extraction"),
                    new("SystemPromptExtraction", "SP-2", LiveSafetyProbeOutcome.Resisted, LiveSafetyProbeErrorKind.None, "high", "high", "instruction extraction"),
                ],
                new("measured", 4, 100, 20, 120, 0.01),
                new("measured", 4, 80, 20, 100, 0.01)),
            LiveEvalTerminalStatus.InfrastructureError =>
                new("robin-agent-live", MeasurementState.NotMeasured, null,
                    4, 2, 0, 2, 2, false, 0,
                    [
                        new("Jailbreak", "LLM01", 2, 1, 0, 1, 1),
                        new("SystemPromptExtraction", "LLM07", 2, 1, 0, 1, 1),
                    ],
                    [
                        new("Jailbreak", "JB-1", LiveSafetyProbeOutcome.Inconclusive, LiveSafetyProbeErrorKind.Execution, "high", "high", "jailbreak"),
                        new("Jailbreak", "JB-2", LiveSafetyProbeOutcome.Resisted, LiveSafetyProbeErrorKind.None, "high", "high", "jailbreak"),
                        new("SystemPromptExtraction", "SP-1", LiveSafetyProbeOutcome.Inconclusive, LiveSafetyProbeErrorKind.Timeout, "high", "high", "instruction extraction"),
                        new("SystemPromptExtraction", "SP-2", LiveSafetyProbeOutcome.Resisted, LiveSafetyProbeErrorKind.None, "high", "high", "instruction extraction"),
                    ],
                    new("measured", 4, 100, 20, 120, 0.01),
                    new("measured", 4, 80, 20, 100, 0.01)),
            _ => null,
        };
        return new(VitrineEvaluationPlan.LiveEval06SafetyProbes, terminal, "safety-fixture", now,
            now.AddMinutes(1), new(0, 1, 1, 4, 4, 2, 4, 104), 0.75, [], config,
            [], [], [], [], terminal is LiveEvalTerminalStatus.Passed or LiveEvalTerminalStatus.QualityFailed
                ? []
                : [new(LiveEvalFailureCode.SafetyExecutionFailed, "fixture failure")],
            new("workspace", "session", "outcome.json", "index.json"))
        { Safety = safety };
    }

    private static VitrineEvent Event(
        Guid runId,
        long sequence,
        string kind,
        VitrineEventDisposition disposition,
        VitrineEventCategory category,
        string source,
        string target,
        string operation,
        string? payload) => new(
            runId,
            sequence,
            new DateTimeOffset(2026, 9, 8, 12, 0, checked((int)sequence), TimeSpan.Zero),
            TimeSpan.FromMilliseconds(sequence),
            category,
            kind,
            disposition,
            source,
            target,
            operation,
            kind,
            $"{kind} fixture detail",
            payload);
}
