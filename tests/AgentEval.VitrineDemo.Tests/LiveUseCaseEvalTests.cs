// SPDX-License-Identifier: MIT

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Tools;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Tests;

public sealed class LiveUseCaseEvalTests
{
    [Fact]
    public void StablePlansAndUseCasesExposeSharedSubjectScenarios()
    {
        Assert.Equal(
            [
                VitrineEvaluationPlan.OfflineSuite,
                VitrineEvaluationPlan.LiveEval01Agent,
                VitrineEvaluationPlan.LiveEval02Workflow,
                VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
                VitrineEvaluationPlan.LiveEval04StochasticAgent,
                VitrineEvaluationPlan.LiveEval05StochasticWorkflow,
                VitrineEvaluationPlan.LiveEval06SafetyProbes,
            ],
            VitrineEvaluationPlans.All.Select(static item => item.Plan));
        Assert.Equal(
            [Personas.NadiaUserId, Personas.SofiaUserId, Personas.MarcoUserId, Personas.LucaUserId],
            LiveUseCaseScenarios.All.Select(static item => item.PersonaId));
        Assert.All(LiveUseCaseScenarios.All, scenario =>
        {
            Assert.Same(PersonaScenarios.Require(scenario.PersonaId), scenario.Persona);
            Assert.Equal(4, scenario.Criteria.Count);
            Assert.Equal($"{scenario.Title} · {scenario.PersonaId}", scenario.ToString());
        });
        Assert.Equal("Eval 01 · Agent", VitrineEvaluationPlans.Require(
            VitrineEvaluationPlan.LiveEval01Agent).ToString());
    }

    [Fact]
    public void DefinitionUsesAtomicJudgeAndThreeDeterministicFlooredChecks()
    {
        var judge = new FakeJudge();
        var definition = LiveUseCaseBenchmark.CreateDefinition(
            judge, 777, 0.8, [LiveUseCaseScenarios.All[0]],
            new LiveObservationRegistry(),
            new LiveProgressReporter(VitrineEvaluationPlan.LiveEval01Agent, null));

        Assert.Single(definition.Cases);
        Assert.Equal(
            [
                LiveUseCaseBenchmark.UseCaseQualityCheckKey,
                LiveUseCaseBenchmark.ResponseObservedCheckKey,
                LiveUseCaseBenchmark.AgentToolJournalCheckKey,
                LiveUseCaseBenchmark.WorkflowTraceCheckKey,
            ],
            definition.Checks.Select(static item => item.Eval.Key));
        Assert.IsType<AtomicLlmEval>(definition.Checks[0].Eval);
        Assert.All(definition.Checks, item =>
        {
            Assert.Equal(FloorState.NotDerivable, item.Floor.State);
            Assert.False(string.IsNullOrWhiteSpace(item.Floor.Derivation));
        });
    }

    [Fact]
    public void DefinitionIdentityChangesWithCasesThresholdAndJudgeContract()
    {
        var one = LiveUseCaseBenchmark.DefinitionVersionFor(
            [LiveUseCaseScenarios.All[0]], 0.75, "judge-a", 800);
        var two = LiveUseCaseBenchmark.DefinitionVersionFor(
            [LiveUseCaseScenarios.All[0], LiveUseCaseScenarios.All[1]], 0.75, "judge-a", 800);
        var threshold = LiveUseCaseBenchmark.DefinitionVersionFor(
            [LiveUseCaseScenarios.All[0]], 0.8, "judge-a", 800);
        var judge = LiveUseCaseBenchmark.DefinitionVersionFor(
            [LiveUseCaseScenarios.All[0]], 0.75, "judge-b", 800);
        var budget = LiveUseCaseBenchmark.DefinitionVersionFor(
            [LiveUseCaseScenarios.All[0]], 0.75, "judge-a", 801);

        Assert.Equal(5, new[] { one, two, threshold, judge, budget }.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("nadia-cross-category", one, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkflowProviderFailureOrCancellationCannotMasqueradeAsCleanLiveEvidence()
    {
        Assert.False(LiveEvalServices.HasWorkflowProviderTerminalFailure(
        [
            DiscoveryEvent.ModelResponseReceived(
                DiscoveryExecutorIds.InterestMapper, "mapper", "response", "operation-1"),
        ]));
        Assert.True(LiveEvalServices.HasWorkflowProviderTerminalFailure(
        [
            DiscoveryEvent.ModelRequestFailed(
                DiscoveryExecutorIds.CoverageReviewer, "reviewer", typeof(TimeoutException), "operation-2"),
        ]));
        Assert.True(LiveEvalServices.HasWorkflowProviderTerminalFailure(
        [
            DiscoveryEvent.ModelRequestCancelled(
                DiscoveryExecutorIds.Presenter, "presenter", "operation-3"),
        ]));
    }

    [Fact]
    public async Task ComparisonRunsFreshMatchedArmsAndPersistsOnlySanitizedEvidence()
    {
        var workspace = TemporaryWorkspace();
        try
        {
            var agent = FakeSubject.Agent(MeasuredAgent(
                "A useful answer. api_key=unit-test-secret https://private.example"));
            var workflow = FakeSubject.Workflow(MeasuredWorkflow("A useful workflow answer."));
            var judge = new FakeJudge(summary: "token=unit-test-secret https://judge.example");
            var progress = new RecordingProgress();
            var options = new LiveEvalOptions(
                workspace,
                Repetitions: 2,
                SubjectMaxOutputTokens: 1234,
                JudgeMaxOutputTokens: 321,
                PassThreshold: 0.8,
                ScenarioIds: [LiveUseCaseScenarios.All[0].Id]);

            var result = await Eval03_Comparison.RunAsync(
                options, new(agent, workflow, judge), progress);

            Assert.Equal(LiveEvalTerminalStatus.Passed, result.TerminalStatus);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0.8, result.PassThreshold);
            Assert.Equal(new LiveEvalWorkload(1, 2, 2, 4, 4), result.Workload);
            Assert.Equal(4, result.Runs.Count);
            Assert.Equal(4, result.Runs.Select(static item => item.RunId).Distinct().Count());
            Assert.All(result.Runs, item => Assert.True(Directory.Exists(
                Path.Combine(workspace, item.RelativeDirectory.Replace('/', Path.DirectorySeparatorChar)))));
            Assert.Equal(4, result.Trials.Count);
            Assert.Single(result.Scenarios);
            Assert.Equal(LiveUseCaseBenchmark.JudgePromptId, result.Configuration.JudgePromptId);
            Assert.Equal(LiveUseCaseBenchmark.RubricHashFor([LiveUseCaseScenarios.All[0]]),
                result.Configuration.JudgeRubricHash);
            Assert.Empty(result.Failures);
            Assert.All(result.Trials, trial =>
            {
                Assert.Equal(MeasurementState.Measured, trial.Measurement);
                Assert.True(trial.Passed);
                Assert.Equal(4, trial.Checks.Count);
                Assert.Equal(4, trial.Criteria.Count);
                Assert.All(trial.Criteria, criterion =>
                {
                    Assert.Equal((MeasurementState.Measured, true), (criterion.Measurement, criterion.Met));
                    Assert.False(string.IsNullOrWhiteSpace(criterion.Explanation));
                });
            });
            Assert.All(agent.Requests.Concat(workflow.Requests), request =>
                Assert.Equal(1234, request.MaxOutputTokens));
            Assert.All(judge.Requests, request => Assert.Equal(321, request.MaxOutputTokens));
            Assert.All(judge.Requests, request =>
            {
                Assert.False(string.IsNullOrWhiteSpace(request.ScenarioDescription));
                Assert.False(string.IsNullOrWhiteSpace(request.ExpectedBehavior));
                Assert.NotEmpty(request.GroundTruthFacts);
            });
            Assert.Equal(4, judge.Requests.Count);
            Assert.Equal(4, result.Comparisons.Count);
            Assert.All(result.Comparisons, comparison =>
            {
                Assert.Equal(1, comparison.Cases);
                Assert.Equal(4, comparison.TotalRepObservations);
                Assert.Equal(2, comparison.MeanRepetitionsPerCase);
                Assert.Equal(nameof(RepCollapse.All), comparison.RepCollapse);
                Assert.NotNull(comparison.MinimumAttainableP);
            });
            Assert.Contains(progress.Items, item => item.Phase == LiveEvalProgressPhase.SubjectRunning);
            Assert.Contains(progress.Items, item => item.Phase == LiveEvalProgressPhase.CheckCompleted
                && item.CheckKey == LiveUseCaseBenchmark.UseCaseQualityCheckKey);

            Assert.True(File.Exists(result.Persistence.OutcomePath));
            Assert.True(File.Exists(result.Persistence.IndexPath));
            var persisted = string.Join('\n', Directory.EnumerateFiles(workspace, "*.json", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            Assert.DoesNotContain("unit-test-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("private.example", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("judge.example", persisted, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", persisted, StringComparison.Ordinal);
            Assert.Contains("\"passThreshold\": 0.8", persisted, StringComparison.Ordinal);
            Assert.Contains("criterion met", persisted, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryWorkspace(workspace);
        }
    }

    [Fact]
    public async Task SubjectFailureAndJudgeFailureStayOutOfMeasuredQualityDenominator()
    {
        var subjectWorkspace = TemporaryWorkspace();
        var judgeWorkspace = TemporaryWorkspace();
        try
        {
            var judgeNotCalled = new FakeJudge();
            var subjectFailure = await Eval01_Agent.RunAsync(
                OneScenario(subjectWorkspace),
                new(FakeSubject.Agent(LiveSubjectObservation.NotMeasured()),
                    FakeSubject.Workflow(MeasuredWorkflow("unused")), judgeNotCalled));

            Assert.Equal(LiveEvalTerminalStatus.NotMeasured, subjectFailure.TerminalStatus);
            Assert.Equal(3, subjectFailure.ExitCode);
            Assert.Empty(judgeNotCalled.Requests);
            var subjectTrial = Assert.Single(subjectFailure.Trials);
            Assert.Equal(MeasurementState.NotMeasured, subjectTrial.Measurement);
            Assert.Null(subjectTrial.Passed);
            Assert.Equal(MeasurementState.NotMeasured, subjectTrial.Checks.Single(item =>
                item.Key == LiveUseCaseBenchmark.UseCaseQualityCheckKey).Measurement);

            var failedJudge = new FakeJudge(fail: true);
            var judgeFailure = await Eval01_Agent.RunAsync(
                OneScenario(judgeWorkspace),
                new(FakeSubject.Agent(MeasuredAgent("A measured response.")),
                    FakeSubject.Workflow(MeasuredWorkflow("unused")), failedJudge));

            Assert.Equal(LiveEvalTerminalStatus.NotMeasured, judgeFailure.TerminalStatus);
            Assert.Equal(3, judgeFailure.ExitCode);
            var judgeTrial = Assert.Single(judgeFailure.Trials);
            var quality = judgeTrial.Checks.Single(item =>
                item.Key == LiveUseCaseBenchmark.UseCaseQualityCheckKey);
            Assert.Equal(MeasurementState.NotMeasured, quality.Measurement);
            Assert.Null(quality.Score);
            Assert.Equal(0, judgeFailure.Arms.Single().Checks.Single(item =>
                item.Key == LiveUseCaseBenchmark.UseCaseQualityCheckKey).Census.Measured);
            Assert.Equal(1, judgeFailure.Arms.Single().Checks.Single(item =>
                item.Key == LiveUseCaseBenchmark.UseCaseQualityCheckKey).Census.NotMeasured);
            Assert.All(judgeTrial.Criteria, criterion =>
                Assert.Contains("not measured", criterion.Explanation, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTemporaryWorkspace(subjectWorkspace);
            DeleteTemporaryWorkspace(judgeWorkspace);
        }
    }

    [Theory]
    [InlineData(0, LiveSubjectStatus.Completed, false)]
    [InlineData(3, LiveSubjectStatus.Abstained, true)]
    public async Task WatchedZeroToolCallsPassOnlyForIntentionalAbstention(
        int scenarioIndex,
        LiveSubjectStatus status,
        bool expectedPass)
    {
        var context = new LiveBenchmarkObservation(
            VitrineEvaluationPlan.LiveEval01Agent,
            LiveUseCaseScenarios.All[scenarioIndex].Id,
            "agent",
            1,
            LiveSubjectArchitecture.Agent,
            new(MeasurementState.Measured, status, "answer",
                new LiveToolEvidence(true, [], 0, 0, 0, 0, 0, [], 0), null,
                LiveUsageEvidence.NotReported));
        var input = new EvalInput("query", "answer", Metadata: new Dictionary<string, object>
        {
            [LiveUseCaseBenchmark.ObservationMetadataKey] = context,
        });

        var result = await new AgentToolJournalEval(new LiveProgressReporter(
            VitrineEvaluationPlan.LiveEval01Agent, null)).EvaluateAsync(input);

        Assert.Equal(expectedPass, result.Score.Passed);
        Assert.Equal(MeasurementState.Measured, result.Score.CensusBucket());
    }

    [Fact]
    public async Task SelectedScenarioControlsWorkloadAndUnknownIdFailsBeforePaidSeams()
    {
        var workspace = TemporaryWorkspace();
        try
        {
            var agent = FakeSubject.Agent(request => MeasuredAgent("answer", request.Scenario.PersonaId));
            var workflow = FakeSubject.Workflow(MeasuredWorkflow("unused"));
            var judge = new FakeJudge();
            var result = await Eval04_StochasticAgent.RunAsync(
                new LiveEvalOptions(workspace, Repetitions: 3,
                    ScenarioIds: [LiveUseCaseScenarios.All[1].Id]),
                new(agent, workflow, judge));

            Assert.Equal(new LiveEvalWorkload(1, 1, 3, 3, 3), result.Workload);
            Assert.Equal(3, agent.Requests.Count);
            Assert.All(agent.Requests, request => Assert.Equal(
                LiveUseCaseScenarios.All[1].Id, request.Scenario.Id));
            var quality = result.Arms.Single().Checks.Single(item =>
                item.Key == LiveUseCaseBenchmark.UseCaseQualityCheckKey);
            Assert.Equal(MeasurementState.Measured, quality.Reliability.Measurement);
            Assert.Equal((3, 3), (quality.Reliability.Successes, quality.Reliability.Total));

            await Assert.ThrowsAsync<ArgumentException>(() => Eval01_Agent.RunAsync(
                new LiveEvalOptions(workspace, ScenarioIds: ["unknown-scenario"]),
                new(agent, workflow, judge)));
            Assert.Equal(3, agent.Requests.Count);
        }
        finally
        {
            DeleteTemporaryWorkspace(workspace);
        }
    }

    [Fact]
    public async Task PairedComparisonUsesTheAuthoredPassThresholdInsteadOfOne()
    {
        var workspace = TemporaryWorkspace();
        try
        {
            var agent = FakeSubject.Agent(MeasuredAgent("agent-output"));
            var workflow = FakeSubject.Workflow(MeasuredWorkflow("workflow-output"));
            var judge = new RoutedScoreJudge(request =>
                ResultFor(request, request.Output == "agent-output" ? 80 : 70));

            var result = await Eval03_Comparison.RunAsync(
                new LiveEvalOptions(workspace, PassThreshold: 0.75,
                    ScenarioIds: [LiveUseCaseScenarios.All[0].Id]),
                new(agent, workflow, judge));

            var quality = result.Comparisons.Single(item =>
                item.CheckKey == LiveUseCaseBenchmark.UseCaseQualityCheckKey);
            Assert.Equal((0, 1, 0), (quality.Wins, quality.Losses, quality.Ties));
        }
        finally
        {
            DeleteTemporaryWorkspace(workspace);
        }
    }

    [Fact]
    public async Task SameCountWrongCriterionIdentitiesAreNotMeasured()
    {
        var scenario = LiveUseCaseScenarios.All[0];
        var registry = new LiveObservationRegistry();
        registry.Record(scenario.Query, new(
            VitrineEvaluationPlan.LiveEval01Agent,
            scenario.Id,
            "fake-agent",
            1,
            LiveSubjectArchitecture.Agent,
            MeasuredAgent("answer")));
        var judge = new RoutedScoreJudge(request => new EvaluationResult
        {
            OverallScore = 100,
            CriteriaResults = request.Criteria.Select((_, index) => new CriterionResult
            {
                Criterion = $"invented-criterion-{index}",
                Met = true,
            }).ToArray(),
        });
        var evaluator = new ScenarioRoutingEvaluator(
            judge, 800, 0.75, registry,
            new LiveProgressReporter(VitrineEvaluationPlan.LiveEval01Agent, null));

        var result = await evaluator.EvaluateAsync(
            scenario.Query, "answer", scenario.Criteria.Select(static item => item.Text));

        Assert.True(result.EvaluationFailed);
        Assert.Empty(result.CriteriaResults);
        Assert.Contains("exact authored criterion", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactCriterionCensusOverridesAConfidentButInconsistentOverallScore()
    {
        var scenario = LiveUseCaseScenarios.Require("marco-gift-trap");
        var registry = new LiveObservationRegistry();
        registry.Record(scenario.Query, new(VitrineEvaluationPlan.LiveEval01Agent, scenario.Id,
            "fake-agent", 1, LiveSubjectArchitecture.Agent, MeasuredAgent("Gaming is definitely Marco's interest.")));
        var judge = new RoutedScoreJudge(request =>
        {
            Assert.Contains(request.GroundTruthFacts, fact => fact.Contains("gift", StringComparison.OrdinalIgnoreCase));
            return new EvaluationResult
            {
                OverallScore = 100,
                CriteriaResults = request.Criteria.Select(criterion => new CriterionResult
                { Criterion = criterion, Met = false, Explanation = "Contradicts the evaluator-only oracle." }).ToArray(),
            };
        });
        var evaluator = new ScenarioRoutingEvaluator(judge, 800, 0.75, registry,
            new LiveProgressReporter(VitrineEvaluationPlan.LiveEval01Agent, null));

        var result = await evaluator.EvaluateAsync(scenario.Query, "Gaming is definitely Marco's interest.",
            scenario.Criteria.Select(static item => item.Text));

        Assert.False(result.EvaluationFailed);
        Assert.Equal(0, result.OverallScore);
        Assert.All(result.CriteriaResults, static item => Assert.False(item.Met));
    }

    [Fact]
    public async Task ToolProjectionAndPredicateRejectUnreconciledOrWrongPersonaCalls()
    {
        var projected = LiveEvalServices.ProjectTools(
        [
            new(RecommendationRuntimeEventKind.ToolExecutionStarted, "Robin", nameof(GalaxusTools.GetUserProfile),
                "start", "start", "op-a", "{\"userId\":\"wrong-user\",\"secret\":\"do-not-copy\"}"),
            new(RecommendationRuntimeEventKind.ToolCompleted, nameof(GalaxusTools.GetUserProfile), "Robin",
                "done", "done", "op-b"),
        ]);
        Assert.Equal(2, projected.UnreconciledCount);
        Assert.DoesNotContain(projected.Calls.SelectMany(static call => call.Arguments),
            static argument => argument.Name == "secret");

        var context = new LiveBenchmarkObservation(VitrineEvaluationPlan.LiveEval01Agent,
            LiveUseCaseScenarios.All[0].Id, "agent", 1, LiveSubjectArchitecture.Agent,
            MeasuredAgent("answer", "wrong-user"));
        var input = new EvalInput("query", "answer", Metadata: new Dictionary<string, object>
        { [LiveUseCaseBenchmark.ObservationMetadataKey] = context });
        var result = await new AgentToolJournalEval(new LiveProgressReporter(
            VitrineEvaluationPlan.LiveEval01Agent, null)).EvaluateAsync(input);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task WorkflowTraceRequiresReviewerAndLoopCountsToMatchEveryDiscoveryRound()
    {
        var workflow = MeasuredWorkflow("answer").Workflow! with
        {
            DiscoveryRounds = 2,
            Looped = true,
            Routes =
            [
                DiscoveryRouteIds.MapToDiscovery,
                DiscoveryRouteIds.DiscoveryToReview,
                DiscoveryRouteIds.ReviewToMoreDiscovery,
                DiscoveryRouteIds.DiscoveryToReview,
                DiscoveryRouteIds.ReviewToRanker,
                DiscoveryRouteIds.RankerToPresenter,
            ],
            Executors = DiscoveryExecutorIds.All.Select(id => new LiveExecutorEvidence(id,
                id == DiscoveryExecutorIds.Discovery ? 2 : 1)).ToArray(),
        };
        var context = new LiveBenchmarkObservation(VitrineEvaluationPlan.LiveEval02Workflow,
            LiveUseCaseScenarios.All[0].Id, "workflow", 1, LiveSubjectArchitecture.Workflow,
            MeasuredWorkflow("answer") with { Workflow = workflow });
        var input = new EvalInput("query", "answer", Metadata: new Dictionary<string, object>
        { [LiveUseCaseBenchmark.ObservationMetadataKey] = context });
        var result = await new WorkflowTraceEval(new LiveProgressReporter(
            VitrineEvaluationPlan.LiveEval02Workflow, null)).EvaluateAsync(input);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public void UsageProjectionFailsClosedAndTextBoundsIncludeTheEllipsis()
    {
        var usage = LiveEvaluationExecutor.NormalizeUsage(
            new("measured", 1, 10, 10, 99, 0.01));
        Assert.Equal("not-reported", usage.Status);
        Assert.Null(usage.TotalTokens);
        var bounded = LiveEvidenceText.Bound(new string('x', 100), 10);
        Assert.Equal(10, bounded.Length);
        Assert.EndsWith("…", bounded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationPersistsCompletedRunReceiptsAndSafeFailure()
    {
        var workspace = TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var agent = FakeSubject.Agent(request =>
        {
            if (++calls == 1) return MeasuredAgent("first", request.Scenario.PersonaId);
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        try
        {
            var result = await Eval04_StochasticAgent.RunAsync(
                new(workspace, Repetitions: 2, ScenarioIds: [LiveUseCaseScenarios.All[0].Id]),
                new(agent, FakeSubject.Workflow(MeasuredWorkflow("unused")), new FakeJudge()),
                cancellationToken: cancellation.Token);
            Assert.Equal(LiveEvalTerminalStatus.Cancelled, result.TerminalStatus);
            Assert.Single(result.Runs);
            Assert.Single(result.Trials);
            Assert.Contains(result.Failures, static failure => failure.Code == LiveEvalFailureCode.Cancelled);
            Assert.True(File.Exists(result.Persistence.OutcomePath));
        }
        finally { DeleteTemporaryWorkspace(workspace); }
    }

    [Fact]
    public async Task ArchitectureSlotsAndArmIdsAreValidatedBeforePaidSeams()
    {
        var workspace = TemporaryWorkspace();
        try
        {
            var judge = new FakeJudge();
            await Assert.ThrowsAsync<ArgumentException>(() => Eval01_Agent.RunAsync(OneScenario(workspace),
                new(new FakeSubject("wrong", LiveSubjectArchitecture.Workflow, _ => MeasuredAgent("x")),
                    FakeSubject.Workflow(MeasuredWorkflow("x")), judge)));
            await Assert.ThrowsAsync<ArgumentException>(() => Eval01_Agent.RunAsync(OneScenario(workspace),
                new(new FakeSubject("same", LiveSubjectArchitecture.Agent, _ => MeasuredAgent("x")),
                    new FakeSubject("same", LiveSubjectArchitecture.Workflow, _ => MeasuredWorkflow("x")), judge)));
            Assert.Empty(judge.Requests);
        }
        finally { DeleteTemporaryWorkspace(workspace); }
    }

    [Fact]
    public async Task CorruptIndexIsPreservedInsteadOfSilentlyReplacingHistory()
    {
        var workspace = TemporaryWorkspace();
        var index = Path.Combine(workspace, "live-sessions", "index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);
        File.WriteAllText(index, "{not-json");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Eval01_Agent.RunAsync(
                OneScenario(workspace), new(FakeSubject.Agent(MeasuredAgent("answer")),
                    FakeSubject.Workflow(MeasuredWorkflow("unused")), new FakeJudge())));
            Assert.Equal("{not-json", File.ReadAllText(index));
            Assert.Single(Directory.EnumerateFiles(Path.Combine(workspace, "live-sessions"),
                "outcome.json", SearchOption.AllDirectories));
        }
        finally { DeleteTemporaryWorkspace(workspace); }
    }

    [Fact]
    public async Task Eval06PersistsARealPlanShapedRedactedRobinOnlyReceiptFromFakeSeam()
    {
        var workspace = TemporaryWorkspace();
        var safety = new FakeSafetyEvaluator(new("robin-agent-live", MeasurementState.Measured, true,
            4, 4, 0, 0, 0, false, 0,
            [new("Jailbreak", "LLM01", 2, 2, 0, 0, 0),
             new("SystemPromptExtraction", "LLM07", 2, 2, 0, 0, 0)],
            [SafetyProbe("Jailbreak", "JB-001"), SafetyProbe("Jailbreak", "JB-002"),
             SafetyProbe("SystemPromptExtraction", "SPE-001"),
             SafetyProbe("SystemPromptExtraction", "SPE-002")],
            new("measured", 4, 40, 20, 60, 0.001), LiveUsageEvidence.NotReported));
        try
        {
            var result = await LiveEvaluationPlanRunner.RunAsync(VitrineEvaluationPlan.LiveEval06SafetyProbes,
                new(workspace, SafetyMaxProbesPerAttack: 2, SafetyTimeoutSeconds: 15),
                new(FakeSubject.Agent(MeasuredAgent("unused")),
                    FakeSubject.Workflow(MeasuredWorkflow("unused")), new FakeJudge(), safety: safety));
            Assert.Equal(LiveEvalTerminalStatus.Passed, result.TerminalStatus);
            Assert.Empty(result.Scenarios);
            Assert.Equal((2, 4, 104), (result.Workload.SafetyAttackCount,
                result.Workload.PlannedSafetyProbes, result.Workload.MaximumSafetyModelCalls));
            Assert.Single(safety.Requests);
            Assert.NotNull(result.Safety);
            var json = File.ReadAllText(result.Persistence.OutcomePath);
            Assert.DoesNotContain("CANARY", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("system prompt", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passThreshold", json, StringComparison.Ordinal);
        }
        finally { DeleteTemporaryWorkspace(workspace); }
    }

    [Fact]
    public void SafetyClassificationPreservesKnownCompromiseAndFailsClosedOnIncompleteCoverage()
    {
        var mixed = SafetySummary(4, resisted: 2, compromised: 1, inconclusive: 1, skipped: 0);
        Assert.Equal(LiveEvalTerminalStatus.QualityFailed,
            LiveSafetyEvaluationExecutor.Classify(mixed, 4, 2));
        var errored = SafetySummary(4, resisted: 3, compromised: 0, inconclusive: 1,
            skipped: 0, errored: 1);
        Assert.Equal(LiveEvalTerminalStatus.InfrastructureError,
            LiveSafetyEvaluationExecutor.Classify(errored, 4, 2));
        var ambiguous = SafetySummary(4, resisted: 3, compromised: 0, inconclusive: 1, skipped: 0);
        Assert.Equal(LiveEvalTerminalStatus.NotMeasured,
            LiveSafetyEvaluationExecutor.Classify(ambiguous, 4, 2));
        var skipped = SafetySummary(3, resisted: 3, compromised: 0, inconclusive: 0, skipped: 1);
        Assert.Equal(LiveEvalTerminalStatus.InfrastructureError,
            LiveSafetyEvaluationExecutor.Classify(skipped, 4, 2));
    }

    [Theory]
    [InlineData(LiveSafetyProbeOutcome.Resisted, LiveSafetyProbeErrorKind.None,
        LiveSafetyProbeDiagnostics.NoExecutionError, false)]
    [InlineData(LiveSafetyProbeOutcome.Compromised, LiveSafetyProbeErrorKind.None,
        LiveSafetyProbeDiagnostics.NoExecutionError, false)]
    [InlineData(LiveSafetyProbeOutcome.Inconclusive, LiveSafetyProbeErrorKind.None,
        LiveSafetyProbeDiagnostics.Inconclusive, false)]
    [InlineData(LiveSafetyProbeOutcome.Inconclusive, LiveSafetyProbeErrorKind.Timeout,
        LiveSafetyProbeDiagnostics.Timeout, true)]
    [InlineData(LiveSafetyProbeOutcome.Inconclusive, LiveSafetyProbeErrorKind.Transport,
        LiveSafetyProbeDiagnostics.Transport, true)]
    [InlineData(LiveSafetyProbeOutcome.Inconclusive, LiveSafetyProbeErrorKind.Execution,
        LiveSafetyProbeDiagnostics.Execution, true)]
    public void SafetyProbeDiagnosticIsAllowListedAndTyped(
        LiveSafetyProbeOutcome outcome,
        LiveSafetyProbeErrorKind errorKind,
        string expected,
        bool hasFailure)
    {
        var probe = new LiveSafetyProbeFact("Jailbreak", "P-1", outcome, errorKind,
            "High", "Verbal", "fixture");

        Assert.Equal(expected, probe.Diagnostic);
        Assert.Equal(hasFailure, probe.Failure is not null);
        if (probe.Failure is { } failure)
        {
            Assert.Equal("probe-execution", failure.Stage);
            Assert.Equal(errorKind, failure.Code);
            Assert.Equal(expected, failure.Detail);
        }
    }

    [Fact]
    public async Task ErroredSafetyCampaignIsPersistedAsInfrastructureErrorWithSafeProbeCause()
    {
        var workspace = TemporaryWorkspace();
        var safety = new FakeSafetyEvaluator(SafetySummary(
            4, resisted: 3, compromised: 0, inconclusive: 1, skipped: 0, errored: 1));
        try
        {
            var result = await LiveEvaluationPlanRunner.RunAsync(VitrineEvaluationPlan.LiveEval06SafetyProbes,
                new(workspace, SafetyMaxProbesPerAttack: 2),
                new(FakeSubject.Agent(MeasuredAgent("unused")),
                    FakeSubject.Workflow(MeasuredWorkflow("unused")), new FakeJudge(), safety: safety));

            Assert.Equal(LiveEvalTerminalStatus.InfrastructureError, result.TerminalStatus);
            Assert.Equal(4, result.ExitCode);
            Assert.Equal(MeasurementState.NotMeasured, result.Safety?.Measurement);
            Assert.Null(result.Safety?.Passed);
            var failure = Assert.Single(result.Failures);
            Assert.Equal(LiveEvalFailureCode.SafetyExecutionFailed, failure.Code);
            Assert.Contains("1 probes errored", failure.Detail, StringComparison.Ordinal);
            Assert.Contains("included in the 1 inconclusive", failure.Detail, StringComparison.Ordinal);
            var erroredProbe = Assert.Single(result.Safety!.Probes,
                static probe => probe.ErrorKind == LiveSafetyProbeErrorKind.Execution);
            Assert.Equal(LiveSafetyProbeDiagnostics.Execution, erroredProbe.Diagnostic);
            Assert.NotNull(erroredProbe.Failure);

            var json = File.ReadAllText(result.Persistence.OutcomePath);
            Assert.Contains("\"schemaVersion\": \"1.2\"", json, StringComparison.Ordinal);
            Assert.Contains("\"diagnostic\": \"An unexpected probe execution fault occurred;", json,
                StringComparison.Ordinal);
            Assert.Contains("\"stage\": \"probe-execution\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-provider-sentinel", json, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteTemporaryWorkspace(workspace); }
    }

    [Fact]
    public void SafetyTargetModelCallGateMechanicallyEnforcesItsDeclaredCap()
    {
        var gate = new AgentEvalRedTeamSafetyEvaluator.SafetyModelCallGate(2);
        gate.Admit();
        gate.Admit();
        Assert.Equal(2, gate.Admitted);
        Assert.Throws<InvalidOperationException>(gate.Admit);
        Assert.Equal(2, gate.Admitted);
    }

    [Fact]
    public async Task SafetyReadinessFailureCompletesAsNotMeasuredAndIsPersisted()
    {
        var workspace = TemporaryWorkspace();
        var progress = new RecordingProgress();
        var safety = new FakeSafetyEvaluator(SafetySummary(4, 4, 0, 0, 0));
        try
        {
            var result = await LiveEvaluationPlanRunner.RunAsync(VitrineEvaluationPlan.LiveEval06SafetyProbes,
                new(workspace), new(FakeSubject.Agent(MeasuredAgent("unused")),
                    FakeSubject.Workflow(MeasuredWorkflow("unused")), new FakeJudge(),
                    readiness: () => new(false, "missing"), safety: safety), progress);
            Assert.Equal(LiveEvalTerminalStatus.InfrastructureError, result.TerminalStatus);
            Assert.Empty(safety.Requests);
            var completed = Assert.Single(progress.Items, static item =>
                item.Phase == LiveEvalProgressPhase.SessionCompleted);
            Assert.Equal(MeasurementState.NotMeasured, completed.Measurement);
            Assert.Null(completed.Passed);
            Assert.True(File.Exists(result.Persistence.OutcomePath));
        }
        finally { DeleteTemporaryWorkspace(workspace); }
    }

    private static LiveSafetyProbeFact SafetyProbe(string attack, string id) =>
        new(attack, id, LiveSafetyProbeOutcome.Resisted, LiveSafetyProbeErrorKind.None,
            "High", "Verbal", "fixture");

    private static LiveSafetySummary SafetySummary(
        int total, int resisted, int compromised, int inconclusive, int skipped, int errored = 0) => new(
            "robin-agent-live", MeasurementState.NotMeasured, null, total, resisted, compromised,
            inconclusive, errored, skipped > 0, skipped,
            [new("Jailbreak", "LLM01", total, resisted, compromised, inconclusive, errored),
             new("SystemPromptExtraction", "LLM07", 0, 0, 0, 0, 0)],
            Enumerable.Range(0, total).Select(index => new LiveSafetyProbeFact("Jailbreak", $"P-{index}",
                index < compromised ? LiveSafetyProbeOutcome.Compromised
                    : index < compromised + inconclusive ? LiveSafetyProbeOutcome.Inconclusive
                    : LiveSafetyProbeOutcome.Resisted,
                index >= compromised && index < compromised + errored
                    ? LiveSafetyProbeErrorKind.Execution
                    : LiveSafetyProbeErrorKind.None,
                "High", "Verbal", "fixture")).ToArray(),
            LiveUsageEvidence.NotReported, LiveUsageEvidence.NotReported);

    private static LiveEvalOptions OneScenario(string workspace) => new(
        workspace, ScenarioIds: [LiveUseCaseScenarios.All[0].Id]);

    private static LiveSubjectObservation MeasuredAgent(
        string response,
        string personaId = Personas.NadiaUserId) => new(
        MeasurementState.Measured,
        LiveSubjectStatus.Completed,
        response,
        ValidAgentTools(personaId),
        null,
        new LiveUsageEvidence("measured", 1, 100, 20, 120, 0.001));

    private static LiveToolEvidence ValidAgentTools(string personaId)
    {
        var calls = new[]
        {
            Call("op-1", nameof(GalaxusTools.GetUserProfile), new LiveToolArgumentEvidence("userId", personaId)),
            Call("op-2", nameof(GalaxusTools.GetInterestMap), new LiveToolArgumentEvidence("userId", personaId)),
            Call("op-3", nameof(GalaxusTools.SearchProductsByMeaning), new LiveToolArgumentEvidence("need", "relevant products")),
            Call("op-4", nameof(GalaxusTools.GetProductDetails), new LiveToolArgumentEvidence("productId", "GLX-1003")),
            Call("op-5", nameof(GalaxusTools.PresentRecommendation), new LiveToolArgumentEvidence("sku", "GLX-1003")),
        };
        return new(true, calls.Select(static item => item.ToolName).ToArray(), calls.Length,
            calls.Length, 0, 0, 0, calls, 0);
    }

    private static LiveToolCallEvidence Call(
        string operationId,
        string tool,
        params LiveToolArgumentEvidence[] arguments) =>
        new(operationId, tool, LiveToolCallStatus.Completed, arguments);

    private static LiveSubjectObservation MeasuredWorkflow(string response) => new(
        MeasurementState.Measured,
        LiveSubjectStatus.Completed,
        response,
        LiveToolEvidence.NotApplicable,
        new LiveWorkflowEvidence(
            DiscoveryExecutorIds.All.Select(static id => new LiveExecutorEvidence(id, 1)).ToArray(),
            [
                DiscoveryRouteIds.MapToDiscovery,
                DiscoveryRouteIds.DiscoveryToReview,
                DiscoveryRouteIds.ReviewToRanker,
                DiscoveryRouteIds.RankerToPresenter,
            ],
            1, 3, 5, DiscoveryStopReason.CoverageSufficient.ToString(), false, 0, 0, []),
        new LiveUsageEvidence("measured", 4, 200, 40, 240, 0.002));

    private static string TemporaryWorkspace() => Path.Combine(
        Path.GetTempPath(), "vitrine-live-eval-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTemporaryWorkspace(string path)
    {
        var full = Path.GetFullPath(path);
        var expectedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vitrine-live-eval-tests"));
        if (Directory.Exists(full) && full.StartsWith(expectedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            Directory.Delete(full, recursive: true);
    }

    private sealed class FakeSubject(
        string armId,
        LiveSubjectArchitecture architecture,
        Func<LiveSubjectRequest, LiveSubjectObservation> observe) : ILiveEvalSubject
    {
        public List<LiveSubjectRequest> Requests { get; } = [];
        public string ArmId => armId;
        public string ModelId => "fake-subject";
        public LiveSubjectArchitecture Architecture => architecture;

        public Task<LiveSubjectObservation> RunAsync(
            LiveSubjectRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(observe(request));
        }

        public static FakeSubject Agent(LiveSubjectObservation result) =>
            new("fake-agent", LiveSubjectArchitecture.Agent, _ => result);

        public static FakeSubject Agent(Func<LiveSubjectRequest, LiveSubjectObservation> observe) =>
            new("fake-agent", LiveSubjectArchitecture.Agent, observe);

        public static FakeSubject Workflow(LiveSubjectObservation result) =>
            new("fake-workflow", LiveSubjectArchitecture.Workflow, _ => result);
    }

    private sealed class FakeJudge(bool fail = false, string summary = "fixture verdict") : ILiveEvalJudge
    {
        public List<LiveJudgeRequest> Requests { get; } = [];
        public string ModelId => "fake-judge";

        public Task<EvaluationResult> EvaluateAsync(
            LiveJudgeRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = fail ? 0 : 100,
                EvaluationFailed = fail,
                Summary = summary,
                CriteriaResults = fail
                    ? []
                    : request.Criteria.Select(criterion => new CriterionResult
                    {
                        Criterion = criterion,
                        Met = true,
                        Explanation = "criterion met; token=unit-test-secret",
                    }).ToArray(),
                InputTokenCount = 10,
                OutputTokenCount = 5,
            });
        }
    }

    private sealed class RoutedScoreJudge(
        Func<LiveJudgeRequest, EvaluationResult> evaluate) : ILiveEvalJudge
    {
        public string ModelId => "routed-score-judge";

        public Task<EvaluationResult> EvaluateAsync(
            LiveJudgeRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(evaluate(request));
        }
    }

    private sealed class FakeSafetyEvaluator(LiveSafetySummary result) : ILiveSafetyEvaluator
    {
        public List<LiveSafetyRequest> Requests { get; } = [];
        public string TargetId => "robin-agent-live";
        public string ModelId => "fake-subject";

        public Task<LiveSafetySummary> RunAsync(
            LiveSafetyRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private static EvaluationResult ResultFor(LiveJudgeRequest request, int overallScore) => new()
    {
        OverallScore = overallScore,
        Summary = "fixture verdict",
        CriteriaResults = request.Criteria.Select(criterion => new CriterionResult
        {
            Criterion = criterion,
            Met = overallScore / 100d >= 0.75,
            Explanation = "fixture criterion",
        }).ToArray(),
    };

    private sealed class RecordingProgress : IProgress<LiveEvalProgress>
    {
        public List<LiveEvalProgress> Items { get; } = [];
        public void Report(LiveEvalProgress value) => Items.Add(value);
    }
}
