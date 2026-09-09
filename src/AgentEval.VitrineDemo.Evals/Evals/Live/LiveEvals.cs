// SPDX-License-Identifier: MIT

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;
using AgentEval.RedTeam.Reporting;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Guardrails;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.Evals.Live;

/// <summary>Runs a selected paid plan. OfflineSuite remains owned by the existing offline runner.</summary>
public static class LiveEvaluationPlanRunner
{
    public static Task<LiveEvalResult> RunAsync(
        VitrineEvaluationPlan plan,
        bool paidExecutionConfirmed,
        LiveEvalOptions? options = null,
        LiveEvalServices? services = null,
        IProgress<LiveEvalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!paidExecutionConfirmed)
            throw new InvalidOperationException("Paid live evaluation requires explicit confirmation before execution.");
        return LiveEvaluationExecutor.RunAsync(plan, options, services, progress, cancellationToken);
    }
}

internal static class LiveEvaluationExecutor
{
    internal static async Task<LiveEvalResult> RunAsync(
        VitrineEvaluationPlan plan,
        LiveEvalOptions? options,
        LiveEvalServices? services,
        IProgress<LiveEvalProgress>? progress,
        CancellationToken cancellationToken)
    {
        var descriptor = VitrineEvaluationPlans.Require(plan);
        if (!descriptor.IsLive)
            throw new ArgumentException("OfflineSuite is run by the existing offline evaluation suite.", nameof(plan));

        options = ValidateOptions(options ?? new LiveEvalOptions(), descriptor);
        services ??= LiveEvalServices.Default;
        if (plan == VitrineEvaluationPlan.LiveEval06SafetyProbes)
            return await LiveSafetyEvaluationExecutor.RunAsync(
                plan, options, services, progress, cancellationToken).ConfigureAwait(false);

        ValidateSubject(services.Agent, LiveSubjectArchitecture.Agent, "agent");
        ValidateSubject(services.Workflow, LiveSubjectArchitecture.Workflow, "workflow");
        if (string.Equals(services.Agent.ArmId, services.Workflow.ArmId, StringComparison.Ordinal))
            throw new ArgumentException("Agent and workflow arms must have distinct stable ids.", nameof(services));
        var repetitions = options.Repetitions ?? descriptor.DefaultRepetitions;
        var scenarios = SelectScenarios(options.ScenarioIds);
        var subjects = SubjectsFor(plan, services);
        var plannedCalls = checked(scenarios.Count * repetitions * subjects.Count);
        var workload = new LiveEvalWorkload(
            scenarios.Count, subjects.Count, repetitions, plannedCalls, plannedCalls);
        var workspace = ResolveWorkspace(options.WorkspaceRoot);
        var sessionId = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var paths = LiveSessionStore.Paths(workspace, sessionId);
        var started = DateTimeOffset.UtcNow;
        var reporter = new LiveProgressReporter(plan, progress);
        var scenarioDefinitions = Array.AsReadOnly(scenarios.Select(static item => item.ToDefinition()).ToArray());
        var configuration = BuildConfiguration(plan, options, services, subjects, scenarios);
        var runReferences = new List<LiveEvalRunReference>(subjects.Count * repetitions);
        var trials = new List<LiveTrialEvidence>(subjects.Count * repetitions * scenarios.Count);
        reporter.Report(LiveEvalProgressPhase.SessionStarting,
            $"{descriptor.Label} is starting: {plannedCalls} subject call(s) and up to {plannedCalls} judge evaluation(s).");

        var readiness = services.CheckReadiness();
        if (!readiness.IsReady)
        {
            var failure = new LiveEvalFailure(LiveEvalFailureCode.ConfigurationUnavailable,
                LiveEvidenceText.Bound(readiness.Detail, 320));
            var unavailable = new LiveEvalResult(
                plan, LiveEvalTerminalStatus.InfrastructureError, sessionId, started, DateTimeOffset.UtcNow, workload,
                options.PassThreshold, scenarioDefinitions, configuration, [], [], [], [], [failure], paths);
            reporter.Report(LiveEvalProgressPhase.Persisting, "The configuration finding is being persisted.");
            await LiveSessionStore.WriteAsync(unavailable, CancellationToken.None).ConfigureAwait(false);
            reporter.Report(LiveEvalProgressPhase.SessionCompleted,
                "The live evaluation could not start because configuration is unavailable.",
                measurement: MeasurementState.NotMeasured);
            return unavailable;
        }

        LiveEvalResult result;
        try
        {
            Directory.CreateDirectory(workspace);
            var store = new FileSystemOutputStore(workspace);
            await store.InitializeSolutionAsync("Vitrine live use-case evaluations", cancellationToken)
                .ConfigureAwait(false);
            var registry = new LiveObservationRegistry();
            var definition = LiveUseCaseBenchmark.CreateDefinition(
                services.Judge, options.JudgeMaxOutputTokens, options.PassThreshold,
                scenarios, registry, reporter);
            var runsByArm = subjects.ToDictionary(
                static subject => subject.ArmId,
                static _ => new List<BenchmarkRun>(),
                StringComparer.Ordinal);
            var contexts = new Dictionary<(string Arm, int Rep, string Scenario), LiveBenchmarkObservation>();
            var layout = new FileSystemLayout(workspace);

            for (var repetition = 1; repetition <= repetitions; repetition++)
            {
                foreach (var subject in subjects)
                {
                    var subjectIdentity = SubjectFor(subject.Architecture);
                    var benchmarkRunner = new BenchmarkRunner(store, subjectIdentity);
                    var arm = BenchmarkArm.From(subject.ArmId, async (testCase, ct) =>
                    {
                        var scenario = LiveUseCaseScenarios.Require(testCase.Id!);
                        var pending = new LiveBenchmarkObservation(plan, scenario.Id, subject.ArmId,
                            repetition, subject.Architecture,
                            LiveSubjectObservation.NotMeasured(LiveSubjectStatus.Failed, subject.Architecture));
                        reporter.Report(LiveEvalProgressPhase.TrialStarting, "The scenario trial is starting.", pending);
                        reporter.Report(LiveEvalProgressPhase.SubjectRunning, "The paid subject request is in flight.", pending);

                        LiveSubjectObservation observed;
                        try
                        {
                            observed = await subject.RunAsync(
                                new LiveSubjectRequest(
                                    scenario.Id,
                                    scenario.PersonaId,
                                    scenario.Query,
                                    repetition,
                                    options.SubjectMaxOutputTokens), ct)
                                .ConfigureAwait(false)
                                ?? LiveSubjectObservation.NotMeasured(LiveSubjectStatus.Failed, subject.Architecture);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            observed = LiveSubjectObservation.NotMeasured(LiveSubjectStatus.Failed, subject.Architecture) with
                            {
                                FailureCode = LiveEvalFailureCode.SubjectExecutionFailed,
                                FailureDetail = $"The subject threw {LiveEvidenceText.SafeIdentifier(exception.GetType().Name)}.",
                            };
                        }

                        observed = Normalize(observed, subject.Architecture);
                        var context = pending with { Subject = observed };
                        contexts[(subject.ArmId, repetition, scenario.Id)] = context;
                        registry.Record(scenario.Query, context);
                        reporter.Report(LiveEvalProgressPhase.SubjectCompleted,
                            observed.Measurement == MeasurementState.Measured
                                ? "The subject observation completed."
                                : "The subject request did not produce a measurement.",
                            context, measurement: observed.Measurement);

                        return new EvalInput(scenario.Query, observed.Response ?? string.Empty,
                            ToolCalls: null,
                            Metadata: new Dictionary<string, object>
                            { [LiveUseCaseBenchmark.ObservationMetadataKey] = context })
                        {
                            CaseId = scenario.Id,
                            SubjectModel = LiveEvalServices.SafeModelId(subject.ModelId),
                        };
                    });

                    var run = await benchmarkRunner.RunAsync(definition, arm, sessionId, cancellationToken)
                        .ConfigureAwait(false);
                    runsByArm[subject.ArmId].Add(run);
                    var runDirectory = layout.RunDir(subjectIdentity, run.RunId);
                    runReferences.Add(new(run.RunId, subject.ArmId, repetition,
                        Path.GetRelativePath(workspace, runDirectory).Replace('\\', '/')));
                    foreach (var scenario in scenarios)
                    {
                        var context = contexts[(subject.ArmId, repetition, scenario.Id)];
                        var trial = ProjectTrial(run, context, definition, options.ResponsePreviewCharacters);
                        trials.Add(trial);
                        reporter.Report(LiveEvalProgressPhase.TrialCompleted,
                            trial.Measurement == MeasurementState.Measured
                                ? "The scenario trial completed with measured check results."
                                : "The scenario trial was not fully measured.",
                            context, measurement: trial.Measurement, passed: trial.Passed);
                    }
                }
            }

            var armSummaries = BuildArmSummaries(subjects, repetitions, runsByArm, trials, definition);
            var comparisons = subjects.Count == 2
                ? BuildComparisons(runsByArm[subjects[0].ArmId], runsByArm[subjects[1].ArmId],
                    definition, options.PassThreshold)
                : [];
            var scenarioAcceptances = BuildScenarioAcceptances(plan, trials);
            var terminal = Classify(plan, trials, scenarioAcceptances);
            result = new LiveEvalResult(
                plan, terminal, sessionId, started, DateTimeOffset.UtcNow, workload,
                options.PassThreshold, scenarioDefinitions, configuration,
                Array.AsReadOnly(runReferences.ToArray()), Array.AsReadOnly(trials.ToArray()),
                armSummaries, comparisons,
                Array.AsReadOnly(trials.Where(static item => item.Failure is not null)
                    .Select(static item => item.Failure!).ToArray()), paths)
            {
                ScenarioAcceptances = scenarioAcceptances,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = InterruptedResult(LiveEvalTerminalStatus.Cancelled, LiveEvalFailureCode.Cancelled,
                "The live evaluation was cancelled; completed receipts were retained.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = InterruptedResult(LiveEvalTerminalStatus.InfrastructureError,
                LiveEvalFailureCode.BenchmarkExecutionFailed,
                $"The benchmark stopped after {LiveEvidenceText.SafeIdentifier(exception.GetType().Name)}; completed receipts were retained.");
        }

        reporter.Report(LiveEvalProgressPhase.Persisting, "The sanitized session outcome and index are being persisted.");
        await LiveSessionStore.WriteAsync(result, CancellationToken.None).ConfigureAwait(false);
        var terminalMeasurement = result.TerminalStatus is LiveEvalTerminalStatus.Passed or LiveEvalTerminalStatus.QualityFailed
            ? MeasurementState.Measured
            : MeasurementState.NotMeasured;
        var terminalPass = result.TerminalStatus switch
        {
            LiveEvalTerminalStatus.Passed => true,
            LiveEvalTerminalStatus.QualityFailed => false,
            _ => (bool?)null,
        };
        reporter.Report(LiveEvalProgressPhase.SessionCompleted,
            "The selected live evaluation plan completed.",
            measurement: terminalMeasurement,
            passed: terminalPass);
        return result;

        LiveEvalResult InterruptedResult(
            LiveEvalTerminalStatus terminal,
            LiveEvalFailureCode code,
            string detail) => new(
                plan, terminal, sessionId, started, DateTimeOffset.UtcNow, workload,
                options.PassThreshold, scenarioDefinitions, configuration,
                Array.AsReadOnly(runReferences.ToArray()), Array.AsReadOnly(trials.ToArray()),
                [], [], [new(code, LiveEvidenceText.Bound(detail, 320))], paths);
    }

    private static LiveEvalOptions ValidateOptions(
        LiveEvalOptions options,
        VitrineEvaluationPlanDescriptor descriptor)
    {
        if (options.Repetitions is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options.Repetitions), "Repetitions must be between 1 and 100.");
        if (!descriptor.SupportsRepetitions && options.Repetitions is not null and not 1)
            throw new ArgumentException("This plan always runs exactly one repetition.", nameof(options));
        var effectiveRepetitions = options.Repetitions ?? descriptor.DefaultRepetitions;
        if (VitrineEvaluationPlans.IsStochastic(descriptor.Plan) &&
            effectiveRepetitions < VitrineEvaluationPlans.MinimumStochasticRepetitions)
            throw new ArgumentOutOfRangeException(nameof(options),
                $"Stochastic plans require at least {VitrineEvaluationPlans.MinimumStochasticRepetitions} repetitions so an all-success 95% Wilson interval can clear the 0.50 reliability floor.");
        if (!descriptor.SupportsScenarioSelection && options.ScenarioIds is { Count: > 0 })
            throw new ArgumentException("This plan does not select persona scenarios.", nameof(options));
        if (options.SubjectMaxOutputTokens is < 1 or > 128_000)
            throw new ArgumentOutOfRangeException(nameof(options.SubjectMaxOutputTokens));
        if (options.JudgeMaxOutputTokens is < 1 or > 128_000)
            throw new ArgumentOutOfRangeException(nameof(options.JudgeMaxOutputTokens));
        if (!double.IsFinite(options.PassThreshold) || options.PassThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options.PassThreshold));
        if (options.ResponsePreviewCharacters is < 80 or > 6000)
            throw new ArgumentOutOfRangeException(nameof(options.ResponsePreviewCharacters));
        if (options.SafetyMaxProbesPerAttack is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(options.SafetyMaxProbesPerAttack));
        if (options.SafetyTimeoutSeconds is < 5 or > 120)
            throw new ArgumentOutOfRangeException(nameof(options.SafetyTimeoutSeconds));
        return options;
    }

    private static IReadOnlyList<ILiveEvalSubject> SubjectsFor(
        VitrineEvaluationPlan plan,
        LiveEvalServices services) => plan switch
    {
        VitrineEvaluationPlan.LiveEval01Agent or VitrineEvaluationPlan.LiveEval04StochasticAgent =>
            [services.Agent],
        VitrineEvaluationPlan.LiveEval02Workflow or VitrineEvaluationPlan.LiveEval05StochasticWorkflow =>
            [services.Workflow],
        VitrineEvaluationPlan.LiveEval03AgentVsWorkflow => [services.Agent, services.Workflow],
        _ => throw new ArgumentOutOfRangeException(nameof(plan)),
    };

    private static void ValidateSubject(
        ILiveEvalSubject subject,
        LiveSubjectArchitecture expectedArchitecture,
        string slot)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.ArmId.Length is < 1 or > 80 || subject.ArmId.Any(static character =>
            !char.IsLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            throw new ArgumentException("A live subject arm id must be a safe stable identifier.", nameof(subject));
        if (!Enum.IsDefined(subject.Architecture))
            throw new ArgumentException("A live subject architecture is invalid.", nameof(subject));
        if (subject.Architecture != expectedArchitecture)
            throw new ArgumentException($"The {slot} service slot must expose {expectedArchitecture} architecture.", nameof(subject));
    }

    private static LiveEvalConfiguration BuildConfiguration(
        VitrineEvaluationPlan plan,
        LiveEvalOptions options,
        LiveEvalServices services,
        IReadOnlyList<ILiveEvalSubject> subjects,
        IReadOnlyList<LiveUseCaseScenario> scenarios)
    {
        var judge = LiveEvalServices.SafeModelId(services.Judge.ModelId);
        return new(
            LiveUseCaseBenchmark.DefinitionKey,
            LiveUseCaseBenchmark.DefinitionVersionFor(
                scenarios, options.PassThreshold, judge, options.JudgeMaxOutputTokens),
            judge,
            LiveUseCaseBenchmark.JudgePromptId,
            LiveUseCaseBenchmark.RubricHashFor(scenarios),
            options.SubjectMaxOutputTokens,
            options.JudgeMaxOutputTokens,
            options.ResponsePreviewCharacters,
            Array.AsReadOnly(subjects.Select(subject =>
            {
                var model = LiveEvalServices.SafeModelId(subject.ModelId);
                return new LiveSubjectProvenance(subject.ArmId, subject.Architecture, model,
                    JudgeFingerprint.RelationTo(judge, model));
            }).ToArray()))
        {
            Acceptance = VitrineEvaluationPlans.IsStochastic(plan)
                ? new(LiveTerminalAcceptancePolicy.WilsonLowerBoundPerScenario,
                    VitrineEvaluationPlans.StochasticConfidenceLevel,
                    VitrineEvaluationPlans.StochasticMinimumWilsonLowerBound)
                : new(LiveTerminalAcceptancePolicy.EveryTrialMustPass, null, null),
        };
    }

    private static SubjectIdentity SubjectFor(LiveSubjectArchitecture architecture) => architecture switch
    {
        LiveSubjectArchitecture.Agent => new(SubjectKind.Agent, RecommendationAgentFactory.AgentName),
        LiveSubjectArchitecture.Workflow => new(SubjectKind.Workflow, DiscoveryWorkflowFactory.WorkflowName),
        _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
    };

    private static LiveSubjectObservation Normalize(
        LiveSubjectObservation source,
        LiveSubjectArchitecture architecture)
    {
        var measurement = Enum.IsDefined(source.Measurement) ? source.Measurement : MeasurementState.NotMeasured;
        var status = Enum.IsDefined(source.Status) ? source.Status : LiveSubjectStatus.Failed;
        if (status is LiveSubjectStatus.Failed or LiveSubjectStatus.Cancelled)
            measurement = MeasurementState.NotMeasured;
        var sourceTools = source.Tools ?? LiveToolEvidence.NotApplicable;
        var sourceToolNames = sourceTools.ToolNames ?? [];
        var allowedTools = sourceToolNames.Where(ToolSurfaceInvariant.IsReadOnlyToolName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unknownTools = sourceToolNames.Distinct(StringComparer.Ordinal).Count()
            - allowedTools.Length + Math.Max(0, sourceTools.UnknownNameCount);
        var sourceCalls = sourceTools.Calls ?? [];
        var safeCalls = sourceCalls.Where(call => ToolSurfaceInvariant.IsReadOnlyToolName(call.ToolName)
                && LiveEvalServices.IsSafeOperationId(call.OperationId)).ToArray();
        var duplicateOperations = safeCalls.GroupBy(static call => call.OperationId, StringComparer.Ordinal)
            .Count(static group => group.Count() > 1);
        var calls = safeCalls
            .Select(call => new LiveToolCallEvidence(
                call.OperationId,
                call.ToolName,
                Enum.IsDefined(call.Status) ? call.Status : LiveToolCallStatus.Unreconciled,
                Array.AsReadOnly((call.Arguments ?? [])
                    .Where(argument => LiveEvalServices.IsAllowedToolArgument(argument.Name))
                    .Select(argument => new LiveToolArgumentEvidence(
                    LiveEvidenceText.SafeIdentifier(argument.Name),
                    LiveEvidenceText.Bound(argument.Value, 240))).ToArray())))
            .ToArray();
        var tools = architecture == LiveSubjectArchitecture.Agent
            ? new LiveToolEvidence(
                sourceTools.JournalObserved,
                Array.AsReadOnly(allowedTools),
                Math.Max(0, sourceTools.Executed),
                Math.Max(0, sourceTools.Completed),
                Math.Max(0, sourceTools.Failed),
                Math.Max(0, sourceTools.Cancelled),
                unknownTools + sourceCalls.Count(call => !ToolSurfaceInvariant.IsReadOnlyToolName(call.ToolName)),
                Array.AsReadOnly(calls),
                Math.Max(0, sourceTools.UnreconciledCount)
                    + sourceCalls.Count(call => !LiveEvalServices.IsSafeOperationId(call.OperationId))
                    + duplicateOperations)
            : LiveToolEvidence.NotApplicable;
        var workflow = architecture == LiveSubjectArchitecture.Workflow
            ? NormalizeWorkflow(source.Workflow)
            : null;
        LiveEvalFailureCode? failureCode = source.FailureCode is { } code && Enum.IsDefined(code) ? code : null;
        if (failureCode is null && measurement == MeasurementState.NotMeasured &&
            status is LiveSubjectStatus.Failed or LiveSubjectStatus.Cancelled)
            failureCode = status == LiveSubjectStatus.Cancelled
                ? LiveEvalFailureCode.Cancelled : LiveEvalFailureCode.SubjectExecutionFailed;
        var failureDetail = failureCode is null ? null : LiveEvidenceText.Bound(
            source.FailureDetail ?? (failureCode == LiveEvalFailureCode.Cancelled
                ? "The subject observation was cancelled."
                : "The subject observation did not complete."), 320);
        return new(measurement, status, source.Response, tools, workflow, NormalizeUsage(source.Usage),
            failureCode, failureDetail);
    }

    private static LiveWorkflowEvidence NormalizeWorkflow(LiveWorkflowEvidence? source)
    {
        if (source is null) return new([], [], 0, 0, 0, "not-measured", false, 0, 0, []);
        var allowedExecutors = DiscoveryExecutorIds.All.ToHashSet(StringComparer.Ordinal);
        var allowedRoutes = new HashSet<string>(
            [DiscoveryRouteIds.MapToDiscovery, DiscoveryRouteIds.DiscoveryToReview,
             DiscoveryRouteIds.ReviewToMoreDiscovery, DiscoveryRouteIds.ReviewToRanker,
             DiscoveryRouteIds.RankerToPresenter], StringComparer.Ordinal);
        var sourceExecutors = source.Executors ?? [];
        var sourceRoutes = source.Routes ?? [];
        var executors = DiscoveryExecutorIds.All.Select(id => new LiveExecutorEvidence(id,
            Math.Max(0, sourceExecutors.Where(item => string.Equals(item.ExecutorId, id, StringComparison.Ordinal))
                .Sum(static item => item.ExecutionCount)))).ToArray();
        var safeRoutes = sourceRoutes.Where(allowedRoutes.Contains).ToArray();
        var unknownExecutors = sourceExecutors.Select(static item => item.ExecutorId)
            .Distinct(StringComparer.Ordinal).Count(id => !allowedExecutors.Contains(id));
        var stopReason = Enum.TryParse<DiscoveryStopReason>(source.StopReason, out var parsed) && Enum.IsDefined(parsed)
            ? parsed.ToString()
            : source.StopReason == "not-measured" ? "not-measured" : "Unknown";
        var allowedDegradationKinds = DiscoveryExecutorIds.All
            .SelectMany(static id => new[] { $"{id}:fallback", $"{id}:model-failure" })
            .Append("unknown:fallback").Append("unknown:model-failure")
            .ToHashSet(StringComparer.Ordinal);
        var degradationKinds = (source.DegradationKinds ?? [])
            .Where(allowedDegradationKinds.Contains).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        return new(
            Array.AsReadOnly(executors), Array.AsReadOnly(safeRoutes),
            Math.Max(0, source.DiscoveryRounds), Math.Max(0, source.MaximumRounds),
            Math.Max(0, source.SuperSteps), stopReason, source.Looped,
            Math.Max(0, source.FailureCount),
            Math.Max(0, source.DegradationCount), Array.AsReadOnly(degradationKinds),
            Math.Max(0, source.UnknownExecutorCount) + unknownExecutors,
            Math.Max(0, source.UnknownRouteCount) + sourceRoutes.Count(route => !allowedRoutes.Contains(route)));
    }

    internal static LiveUsageEvidence NormalizeUsage(LiveUsageEvidence source)
    {
        if (source is null) return LiveUsageEvidence.NotReported;
        var status = source.Status is "measured" or "measured-zero" or "lower-bound" or "not-reported"
            ? source.Status
            : "not-reported";
        static int? I(int? value) => value is >= 0 ? value : null;
        static long? L(long? value) => value is >= 0 ? value : null;
        double? cost = source.EstimatedCostUsd is { } value && double.IsFinite(value) && value >= 0 ? value : null;
        var normalized = new LiveUsageEvidence(status, I(source.ModelCalls), L(source.InputTokens), L(source.OutputTokens),
            L(source.TotalTokens), cost);
        return normalized.IsConsistent()
            ? normalized
            : new LiveUsageEvidence("not-reported", I(source.ModelCalls), null, null, null, null);
    }

    private static LiveTrialEvidence ProjectTrial(
        BenchmarkRun run,
        LiveBenchmarkObservation context,
        BenchmarkDefinition definition,
        int previewCharacters)
    {
        var rows = run.Observations.Where(item => string.Equals(
            item.Observation.CaseId, context.ScenarioId, StringComparison.Ordinal)).ToArray();
        var checks = definition.Checks.Select(check =>
        {
            var row = rows.Single(item => string.Equals(item.CheckKey, check.Eval.Key, StringComparison.Ordinal));
            var measurement = row.Observation.State;
            return new LiveCheckFact(
                check.Eval.Key,
                check.Eval.Name,
                measurement,
                measurement == MeasurementState.Measured ? row.Result.Score.Value : null,
                measurement == MeasurementState.Measured ? row.Result.Score.Passed : null);
        }).ToArray();

        var scenario = LiveUseCaseScenarios.Require(context.ScenarioId);
        var judgeRow = rows.Single(item => string.Equals(
            item.CheckKey, LiveUseCaseBenchmark.UseCaseQualityCheckKey, StringComparison.Ordinal));
        var dimensions = judgeRow.Result.Details.Dimensions;
        var criterionEvidence = (judgeRow.Result.Details.Evidence ?? [])
            .Where(static item => string.Equals(item.Source, "criterion", StringComparison.Ordinal))
            .GroupBy(static item => item.Reference, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First().Message,
                StringComparer.Ordinal);
        var criteria = scenario.Criteria.Select(criterion =>
        {
            if (judgeRow.Observation.State != MeasurementState.Measured || dimensions is null
                || !dimensions.TryGetValue(criterion.Text, out var value))
                return new LiveCriterionVerdict(criterion.Id, MeasurementState.NotMeasured, null,
                    "No judge explanation is available because this criterion was not measured.");
            var explanation = criterionEvidence.TryGetValue(criterion.Text, out var message)
                ? LiveEvidenceText.Bound(message, 320)
                : "The judge returned no explanation for this measured criterion.";
            return new LiveCriterionVerdict(
                criterion.Id, MeasurementState.Measured, value >= 0.5, explanation);
        }).ToArray();

        var requiredKeys = context.Architecture == LiveSubjectArchitecture.Agent
            ? new[] { LiveUseCaseBenchmark.UseCaseQualityCheckKey, LiveUseCaseBenchmark.ResponseObservedCheckKey,
                LiveUseCaseBenchmark.AgentToolJournalCheckKey }
            : new[] { LiveUseCaseBenchmark.UseCaseQualityCheckKey, LiveUseCaseBenchmark.ResponseObservedCheckKey,
                LiveUseCaseBenchmark.WorkflowTraceCheckKey };
        var required = checks.Where(item => requiredKeys.Contains(item.Key, StringComparer.Ordinal)).ToArray();
        var measurement = required.All(static item => item.Measurement == MeasurementState.Measured)
            ? MeasurementState.Measured
            : MeasurementState.NotMeasured;
        bool? passed = measurement == MeasurementState.Measured
            ? required.All(static item => item.Passed == true)
            : null;
        var preview = LiveEvidenceText.Bound(context.Subject.Response, previewCharacters);
        var judgeUsage = judgeRow.Result.Provenance.TokensUsed is { } tokens
            ? NormalizeUsage(new LiveUsageEvidence(tokens == 0 ? "measured-zero" : "measured",
                null, null, null, tokens, judgeRow.Result.Provenance.EstimatedCost))
            : LiveUsageEvidence.NotReported;
        LiveEvalFailure? failure = context.Subject.FailureCode is { } failureCode
            ? new(failureCode, LiveEvidenceText.Bound(context.Subject.FailureDetail, 320),
                context.ScenarioId, context.ArmId, context.Repetition)
            : judgeRow.Observation.State == MeasurementState.NotMeasured &&
              context.Subject.Measurement == MeasurementState.Measured
                ? new(LiveEvalFailureCode.JudgeExecutionFailed,
                    "The judge did not produce a complete criterion verdict.",
                    context.ScenarioId, context.ArmId, context.Repetition,
                    LiveUseCaseBenchmark.UseCaseQualityCheckKey)
                : null;
        return new(
            context.ScenarioId, scenario.PersonaId, context.ArmId, context.Architecture,
            context.Repetition, measurement, passed, context.Subject.Status, preview,
            context.Subject.Tools, context.Subject.Workflow,
            Array.AsReadOnly(checks), Array.AsReadOnly(criteria), context.Subject.Usage, judgeUsage, failure);
    }

    private static IReadOnlyList<LiveArmSummary> BuildArmSummaries(
        IReadOnlyList<ILiveEvalSubject> subjects,
        int repetitions,
        IReadOnlyDictionary<string, List<BenchmarkRun>> runsByArm,
        IReadOnlyList<LiveTrialEvidence> trials,
        BenchmarkDefinition definition) =>
        Array.AsReadOnly(subjects.Select(subject =>
        {
            var census = BenchmarkScore.Census(runsByArm[subject.ArmId])
                .ToDictionary(static row => row.CheckKey, static row => row.Census, StringComparer.Ordinal);
            var checks = definition.Checks.Select(check =>
            {
                var source = census[check.Eval.Key];
                var facts = trials.Where(trial => string.Equals(trial.ArmId, subject.ArmId, StringComparison.Ordinal))
                    .SelectMany(static trial => trial.Checks)
                    .Where(item => string.Equals(item.Key, check.Eval.Key, StringComparison.Ordinal)
                        && item.Measurement == MeasurementState.Measured).ToArray();
                var successes = facts.Count(static item => item.Passed == true);
                return new LiveCheckSummary(
                    check.Eval.Key,
                    check.Eval.Name,
                    new(source.Measured, source.NotApplicable, source.NotMeasured),
                    Reliability(successes, facts.Length));
            }).ToArray();
            return new LiveArmSummary(subject.ArmId, subject.Architecture, repetitions, Array.AsReadOnly(checks));
        }).ToArray());

    private static IReadOnlyList<LiveCheckComparison> BuildComparisons(
        IReadOnlyList<BenchmarkRun> reference,
        IReadOnlyList<BenchmarkRun> challenger,
        BenchmarkDefinition definition,
        double passThreshold) =>
        Array.AsReadOnly(BenchmarkScore.AgainstReference(
                reference, challenger, RepCollapse.All, passThreshold)
            .Select(row =>
            {
                var comparison = row.Comparison;
                var name = definition.Checks.Single(check => string.Equals(
                    check.Eval.Key, row.CheckKey, StringComparison.Ordinal)).Eval.Name;
                return new LiveCheckComparison(
                    row.CheckKey, name, comparison.Reference, comparison.Challenger,
                    comparison.Wins, comparison.Losses, comparison.Ties, comparison.EffectiveN,
                    double.IsFinite(comparison.PValue) ? comparison.PValue : null,
                    double.IsFinite(comparison.MinimumAttainableP)
                        ? comparison.MinimumAttainableP : null,
                    double.IsFinite(comparison.MeanDelta) ? comparison.MeanDelta : null,
                    comparison.Unit.Cases,
                    comparison.Unit.TotalReps,
                    double.IsFinite(comparison.Unit.MeanRepsPerCase)
                        ? comparison.Unit.MeanRepsPerCase : null,
                    comparison.Unit.Strategy.ToString(),
                    new(comparison.Census.Measured, comparison.Census.NotApplicable, comparison.Census.NotMeasured),
                    comparison.UnderpoweredByConstruction,
                    comparison.Undecidable);
            }).ToArray());

    private static LiveReliability Reliability(int successes, int total)
    {
        if (total == 0)
            return new(MeasurementState.NotMeasured, 0, 0, null, null, null);
        var interval = WilsonInterval.Compute(successes, total);
        return new(MeasurementState.Measured, successes, total,
            interval.Estimate, interval.Lower, interval.Upper);
    }

    private static IReadOnlyList<LiveScenarioAcceptanceDecision> BuildScenarioAcceptances(
        VitrineEvaluationPlan plan,
        IReadOnlyList<LiveTrialEvidence> trials)
    {
        if (!VitrineEvaluationPlans.IsStochastic(plan)) return [];
        return Array.AsReadOnly(trials
            .GroupBy(static trial =>
                (trial.ArmId, trial.Architecture, trial.ScenarioId, trial.PersonaId))
            .Select(group =>
            {
                var measured = group.Where(static trial =>
                    trial.Measurement == MeasurementState.Measured).ToArray();
                var census = new LiveObservationCensus(
                    measured.Length,
                    group.Count(static trial => trial.Measurement == MeasurementState.NotApplicable),
                    group.Count(static trial => trial.Measurement == MeasurementState.NotMeasured));
                var reliability = Reliability(
                    measured.Count(static trial => trial.Passed == true), measured.Length);
                bool? passed = census.Measured == group.Count() && reliability.Lower is { } lower
                    ? lower >= VitrineEvaluationPlans.StochasticMinimumWilsonLowerBound
                    : null;
                return new LiveScenarioAcceptanceDecision(
                    group.Key.ScenarioId,
                    group.Key.PersonaId,
                    group.Key.ArmId,
                    group.Key.Architecture,
                    census,
                    reliability,
                    VitrineEvaluationPlans.StochasticConfidenceLevel,
                    VitrineEvaluationPlans.StochasticMinimumWilsonLowerBound,
                    passed);
            }).ToArray());
    }

    private static LiveEvalTerminalStatus Classify(
        VitrineEvaluationPlan plan,
        IReadOnlyList<LiveTrialEvidence> trials,
        IReadOnlyList<LiveScenarioAcceptanceDecision> scenarioAcceptances)
    {
        if (trials.Count == 0) return LiveEvalTerminalStatus.InfrastructureError;
        if (trials.Any(static trial => trial.SubjectStatus == LiveSubjectStatus.Cancelled))
            return LiveEvalTerminalStatus.Cancelled;
        var measured = trials.Count(static trial => trial.Measurement == MeasurementState.Measured);
        if (measured == 0) return LiveEvalTerminalStatus.NotMeasured;
        if (measured != trials.Count) return LiveEvalTerminalStatus.InfrastructureError;
        if (VitrineEvaluationPlans.IsStochastic(plan))
        {
            if (scenarioAcceptances.Count == 0 || scenarioAcceptances.Any(static item => item.Passed is null))
                return LiveEvalTerminalStatus.InfrastructureError;
            return scenarioAcceptances.All(static item => item.Passed == true)
                ? LiveEvalTerminalStatus.Passed
                : LiveEvalTerminalStatus.QualityFailed;
        }
        return trials.All(static trial => trial.Passed == true)
            ? LiveEvalTerminalStatus.Passed
            : LiveEvalTerminalStatus.QualityFailed;
    }

    private static string ResolveWorkspace(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested);
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AgentEval.VitrineDemo.slnx")))
                return Path.Combine(directory.FullName, ".agenteval", "live");
            directory = directory.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), ".agenteval", "live");
    }

    private static IReadOnlyList<LiveUseCaseScenario> SelectScenarios(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0) return LiveUseCaseScenarios.All;
        if (requested.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Scenario ids cannot be blank.", nameof(requested));
        var ids = requested.ToHashSet(StringComparer.Ordinal);
        if (ids.Count != requested.Count)
            throw new ArgumentException("Scenario ids must be unique.", nameof(requested));
        foreach (var id in ids) LiveUseCaseScenarios.Require(id);
        return Array.AsReadOnly(LiveUseCaseScenarios.All.Where(item => ids.Contains(item.Id)).ToArray());
    }
}
