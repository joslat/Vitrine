// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Artifacts;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using Galaxus.RecommendationAgent.Catalog;

namespace AgentEval.VitrineDemo.Tests;

public sealed class BenchmarkArtifactAndEvaluationGraphTests
{
    [Fact]
    public void EvaluationArtifactCarriesNativeBenchmarkAndExecutionFactsAcrossEveryProjection()
    {
        var benchmark = Benchmark();
        var execution = new EvaluationExecutionProvenance(
            EvaluationExecutionProfile.OfflineDeterministic,
            "Demo01 single agent + Demo02 workflow",
            "scripted and zero-model subjects",
            "deterministic AgentEval evaluator",
            null,
            Demo01SubjectModelCalls: 0,
            Demo02SubjectModelCalls: 0,
            JudgeModelCalls: 0,
            Demo01SubjectTokens: null,
            Demo02SubjectTokens: 321,
            JudgeTokens: null,
            EstimatedCostUsd: 0,
            UsesExternalModels: false);
        var suite = new SuiteResult(
            [new GateResult("Synthetic measured gate", true, 1,
                AgentEval.Evals.Meta.ChanceFloor.UniformChoice(2), "independent evidence")],
            [])
        {
            OfflineBenchmark = benchmark,
            Execution = execution,
        };
        var artifact = VitrineArtifactSerializer.Create(new(
            Guid.NewGuid(),
            new(VitrineRunMode.Evals, Personas.NadiaUserId),
            VitrineGraphFactory.ForRunningEvaluationSuite(),
            [],
            Evaluation: suite));

        var json = VitrineArtifactSerializer.Serialize(artifact);
        var roundTripped = VitrineArtifactSerializer.Deserialize(json);
        var html = VitrineHtmlReport.Render(roundTripped);
        var (_, inspector) = VitrineOutcomeInspector.Describe(roundTripped);
        var snapshot = Assert.IsType<VitrineOfflineBenchmarkSnapshot>(roundTripped.Result.OfflineBenchmark);
        var executionSnapshot = Assert.IsType<VitrineEvaluationExecutionSnapshot>(
            roundTripped.Result.EvaluationExecution);
        var check = Assert.Single(snapshot.Checks);

        Assert.Equal(VitrineRunArtifact.CurrentSchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal("OfflineDeterministic", roundTripped.ExecutionArm);
        Assert.Equal(benchmark.RunId, snapshot.RunId);
        Assert.Equal(benchmark.RunDirectory, snapshot.RunDirectory);
        Assert.Equal(benchmark.DefinitionKey, snapshot.DefinitionKey);
        Assert.Equal(benchmark.DefinitionVersion, snapshot.DefinitionVersion);
        Assert.Equal(benchmark.ArmId, snapshot.ArmId);
        Assert.Equal(2, snapshot.Cases.Count);
        Assert.Equal("Derived", check.Floor.State);
        Assert.Equal(2, check.Census.Measured);
        Assert.Equal(2, check.Census.Total);
        Assert.Equal(0.5, check.PValue);
        Assert.Equal(0.5, check.MinimumAttainableP);
        Assert.True(check.AboveFloor);
        Assert.True(check.UnderpoweredByConstruction);
        Assert.Equal(3, snapshot.Arms.Count);
        Assert.Equal(6, snapshot.Runs.Count);
        Assert.Equal(2, snapshot.Repetitions);
        Assert.Single(snapshot.ReferenceComparisons);
        Assert.Equal(0, executionSnapshot.TotalModelCalls);
        Assert.Equal(0, roundTripped.Result.ModelCalls);
        Assert.Null(executionSnapshot.Demo01SubjectTokens);
        Assert.Null(executionSnapshot.JudgeTokens);
        Assert.Equal(0, executionSnapshot.EstimatedCostUsd);
        Assert.Contains("\"offlineBenchmark\"", json, StringComparison.Ordinal);
        Assert.Contains("\"evaluationExecution\"", json, StringComparison.Ordinal);
        Assert.Contains("Native AgentEval offline benchmark", html, StringComparison.Ordinal);
        Assert.Contains("UNDERPOWERED BY CONSTRUCTION", html, StringComparison.Ordinal);
        Assert.Contains(benchmark.RunId, html, StringComparison.Ordinal);
        Assert.Contains(benchmark.RunDirectory, html, StringComparison.Ordinal);
        Assert.Contains("Evaluation execution profile", html, StringComparison.Ordinal);
        Assert.Contains("Demo01 subject tokens</div><div class=\"metric\">NOT MEASURED", html,
            StringComparison.Ordinal);
        Assert.Contains("Native AgentEval offline benchmark", inspector, StringComparison.Ordinal);
        Assert.Contains(benchmark.RunId, inspector, StringComparison.Ordinal);
        Assert.Contains("minimum attainable p", inspector, StringComparison.Ordinal);
        Assert.Contains("Evaluation execution profile", inspector, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VitrineBenchmarkCaseSnapshot>)snapshot.Cases).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VitrineBenchmarkCheckSnapshot>)snapshot.Checks).Clear());
    }

    [Fact]
    public void NotDerivableFloorPowerRemainsNullAndRendersAsNotApplicable()
    {
        var check = new VitrineBenchmarkCheckFact(
            VitrineOfflineBenchmark.ScreenedDeliverableCheckKey,
            new("free-text", "NotDerivable", null, null, null, 0, 0,
                "Free text has no authored random-draw space."),
            new(2, 0, 0),
            Successes: 2,
            Trials: 2,
            PValue: null,
            MinimumAttainableP: 0.5,
            AboveFloor: null,
            UnderpoweredByConstruction: null);
        var benchmark = new VitrineOfflineBenchmarkResult(
            "fixture", "1.0.0", "fixture-arm", "run-1", "workspace", "directory",
            [new("case-1", "Case 1"), new("case-2", "Case 2")],
            [check])
        {
            Repetitions = 1,
            Arms = [new("fixture-arm", "Agent", "Fixture subject", [check])],
        };
        var suite = new SuiteResult(
            [new GateResult("measured gate", true, 1, null, "measured evidence")],
            [])
        {
            OfflineBenchmark = benchmark,
        };

        var board = new EvaluationBoardViewModel();
        board.Load(suite);
        Assert.Contains("power N/A · native floor comparison not derivable",
            board.BenchmarkFloorDerivation, StringComparison.Ordinal);
        Assert.DoesNotContain("power permits the registered comparison",
            board.BenchmarkFloorDerivation, StringComparison.Ordinal);

        var evaluationJson = EvaluationReportJson.Render(suite);
        var evaluationHtml = EvaluationReportHtml.Render(suite);
        using var console = new StringWriter();
        ConsoleReport.Print(suite, verboseControls: false, console);
        Assert.Contains("\"underpoweredByConstruction\": null", evaluationJson, StringComparison.Ordinal);
        Assert.Contains("POWER N/A · native floor comparison not derivable", evaluationHtml,
            StringComparison.Ordinal);
        Assert.Contains("power N/A · native floor comparison not derivable", console.ToString(),
            StringComparison.Ordinal);

        var artifact = VitrineArtifactSerializer.Create(new(
            Guid.NewGuid(),
            new(VitrineRunMode.Evals, Personas.NadiaUserId),
            VitrineGraphFactory.ForEvaluationSuite(),
            [],
            Evaluation: suite));
        var artifactJson = VitrineArtifactSerializer.Serialize(artifact);
        var artifactHtml = VitrineHtmlReport.Render(artifact);
        var (_, inspector) = VitrineOutcomeInspector.Describe(artifact);
        Assert.Null(Assert.Single(artifact.Result.OfflineBenchmark!.Checks).UnderpoweredByConstruction);
        Assert.Contains("\"underpoweredByConstruction\": null", artifactJson, StringComparison.Ordinal);
        Assert.Contains("POWER N/A · native floor comparison not derivable", artifactHtml,
            StringComparison.Ordinal);
        Assert.Contains("power N/A · native floor comparison not derivable", inspector,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NewEvaluationSnapshotBranchesRemainInsideTheSecretBoundary()
    {
        var benchmark = Benchmark();
        var suite = new SuiteResult([], [])
        {
            OfflineBenchmark = benchmark,
            Execution = new(
                EvaluationExecutionProfile.OfflineDeterministic,
                "scope",
                "subject",
                "judge",
                "safe-deployment",
                1, 1, 1, null, null, null, null, true),
        };
        var safe = VitrineArtifactSerializer.Create(new(
            Guid.NewGuid(),
            new(VitrineRunMode.Evals, Personas.NadiaUserId),
            VitrineGraphFactory.ForEvaluationSuite(),
            [],
            Evaluation: suite));
        var unsafeBenchmark = safe with
        {
            Result = safe.Result with
            {
                OfflineBenchmark = safe.Result.OfflineBenchmark! with
                {
                    RunDirectory = "api_key=SENTINEL-BENCHMARK-SECRET",
                },
            },
        };
        var unsafeExecution = safe with
        {
            Result = safe.Result with
            {
                EvaluationExecution = safe.Result.EvaluationExecution! with
                {
                    DeploymentName = "endpoint=https://secret.example.invalid/private",
                },
            },
        };

        foreach (var artifact in new[] { unsafeBenchmark, unsafeExecution })
        {
            Assert.False(VitrineArtifactSerializer.Verify(artifact));
            Assert.Throws<InvalidDataException>(() => VitrineArtifactSerializer.Serialize(artifact));
            Assert.Throws<InvalidDataException>(() => VitrineHtmlReport.Render(artifact));
        }
    }

    [Fact]
    public void EvaluationGraphExposesTheTypedPlanAndMapsProgressWithoutFabricatingRuns()
    {
        var preview = VitrineGraphFactory.ForEvaluationSuite();
        var runtime = VitrineGraphFactory.ForRunningEvaluationSuite();
        string[] expectedNodes =
        [
            "catalogue", "topology", "judged", "injection", "recall", "honesty",
            .. VitrineOfflineBenchmark.CheckKeys,
            "benchmark", "controls", "suite",
        ];

        Assert.Equal(expectedNodes, preview.Nodes.Select(node => node.Id));
        Assert.Equal(expectedNodes, runtime.Nodes.Select(node => node.Id));
        Assert.Equal(14, preview.Nodes.Count);
        Assert.Equal(5, VitrineOfflineBenchmark.CheckKeys.Count);
        Assert.Equal(13, preview.Edges.Count);
        Assert.Equal(preview.Edges, runtime.Edges);
        Assert.All(preview.Edges, edge =>
        {
            Assert.Contains(edge.SourceId, expectedNodes);
            Assert.Contains(edge.TargetId, expectedNodes);
        });

        var graph = new GraphViewModel();
        var store = new VitrineEventStore();
        graph.Load(runtime);
        graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteStarted, "suite", "suite", "started", Total: 6))));
        Assert.All(graph.Nodes, node => Assert.Equal(GraphNodeState.Idle, node.State));
        Assert.All(graph.Nodes, node => Assert.Equal(0, node.ExecutionCount));

        var gates = new[] { "catalogue", "topology", "judged", "injection", "recall", "honesty" };
        foreach (var id in gates)
        {
            graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
                EvaluationProgressKind.GateStarted, id, id, "started"))));
            graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
                EvaluationProgressKind.GateCompleted, id, id, "complete", Passed: true))));
        }

        foreach (var check in VitrineOfflineBenchmark.CheckKeys)
            graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
                EvaluationProgressKind.BenchmarkCheckCompleted, check, check, "observed"))));
        graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.BenchmarkPersisted, "benchmark", "benchmark", "persisted", Passed: true))));
        graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.GateStarted, "controls", "controls", "started"))));
        foreach (var id in new[] { "NC-ONE", "NC-TWO" })
        {
            graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
                EvaluationProgressKind.ControlStarted, id, id, "started"))));
            graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
                EvaluationProgressKind.ControlHealthyCompleted, id, id, "healthy", Passed: true))));
            graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
                EvaluationProgressKind.ControlBrokenCompleted, id, id, "caught", Passed: true))));
            graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
                EvaluationProgressKind.ControlRestoredCompleted, id, id, "restored", Passed: true))));
            graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
                EvaluationProgressKind.ControlCompleted, id, id, "complete", Passed: true))));
        }
        graph.Apply(store.Append(VitrineEventAdapters.FromEvaluation(new(
            EvaluationProgressKind.SuiteCompleted, "suite", "suite", "complete", Passed: true,
            Completed: 6, Total: 6, IncludesDiagnosticControls: true))));

        Assert.All(gates, id =>
        {
            var node = Assert.Single(graph.Nodes, candidate => candidate.Id == id);
            Assert.Equal(GraphNodeState.Succeeded, node.State);
            Assert.Equal(1, node.ExecutionCount);
        });
        Assert.Equal(1, graph.Nodes.Single(node => node.Id == "benchmark").ExecutionCount);
        Assert.All(VitrineOfflineBenchmark.CheckKeys, id =>
        {
            Assert.Equal(1, graph.Nodes.Single(node => node.Id == id).ExecutionCount);
            Assert.Equal(GraphNodeState.Succeeded, graph.StateOf(id));
        });
        Assert.Equal(GraphNodeState.Succeeded, graph.StateOf("benchmark"));
        var controlsNode = graph.Nodes.Single(node => node.Id == "controls");
        Assert.Equal(2, controlsNode.ExecutionCount);
        Assert.Equal("2/43 controls checked", controlsNode.ExecutionText);
        Assert.Equal("2/43", controlsNode.ExecutionBadgeText);
        Assert.Equal(GraphNodeState.Succeeded, graph.StateOf("controls"));
        Assert.Equal(1, graph.Nodes.Single(node => node.Id == "suite").ExecutionCount);
        Assert.Equal(GraphNodeState.Succeeded, graph.StateOf("suite"));
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == "eval-honesty-screened").TraversalCount);
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == "eval-screened-sku").TraversalCount);
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == "eval-sku-reason").TraversalCount);
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == "eval-reason-purchase").TraversalCount);
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == "eval-purchase-interest").TraversalCount);
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == "eval-interest-persist").TraversalCount);
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == "eval-benchmark-controls").TraversalCount);
        Assert.Equal(1, graph.Edges.Single(edge => edge.Id == "eval-controls-suite").TraversalCount);
    }

    private static VitrineOfflineBenchmarkResult Benchmark()
    {
        var check = new VitrineBenchmarkCheckFact(
            VitrineOfflineBenchmark.ScreenedDeliverableCheckKey,
            new("empirical", "Derived", 0.5, 0.55, 0.6, 1_000, 2,
                "Exact native AgentEval chance-floor derivation."),
            new(2, 0, 0), 2, 2, 0.5, 0.5, true, true);
        var runId = "2026-09-08_10-00-00_test";
        var root = @"C:\safe\.agenteval\Vitrine";
        var result = new VitrineOfflineBenchmarkResult(
            VitrineOfflineBenchmark.DefinitionKey,
            VitrineOfflineBenchmark.DefinitionVersion,
            VitrineOfflineBenchmark.Demo01ArmId,
            runId,
            root,
            $@"{root}\runs\{runId}",
            [new(VitrineOfflineBenchmark.NadiaCaseId, "Nadia"), new(VitrineOfflineBenchmark.SofiaCaseId, "Sofia")],
            [check]);
        return result with
        {
            Repetitions = 2,
            Arms =
            [
                new(VitrineOfflineBenchmark.Demo01ArmId, "Agent", "Demo01", [check]),
                new(VitrineOfflineBenchmark.Demo02ArmId, "Workflow", "Demo02", [check]),
                new(VitrineOfflineBenchmark.DegradedArmId, "Agent", "Degraded", [check]),
            ],
            Runs = Enumerable.Range(1, 2).SelectMany(rep => new[]
            {
                new VitrineBenchmarkRunFact(VitrineOfflineBenchmark.Demo01ArmId, rep, "Agent", "Demo01", $"d1-{rep}", $@"{root}\d1-{rep}"),
                new VitrineBenchmarkRunFact(VitrineOfflineBenchmark.Demo02ArmId, rep, "Workflow", "Demo02", $"d2-{rep}", $@"{root}\d2-{rep}"),
                new VitrineBenchmarkRunFact(VitrineOfflineBenchmark.DegradedArmId, rep, "Agent", "Degraded", $"bad-{rep}", $@"{root}\bad-{rep}"),
            }).ToArray(),
            ReferenceComparisons =
            [
                new(check.CheckKey, VitrineOfflineBenchmark.Demo01ArmId, VitrineOfflineBenchmark.DegradedArmId,
                    0, 2, 0, 2, 0.5, 0.5, -1, new(2, 0, 0), 2, 4, "All", true),
            ],
        };
    }
}
