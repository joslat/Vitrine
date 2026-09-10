// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals.Live;

namespace AgentEval.VitrineDemo.App.Artifacts;

/// <summary>
/// Projects the same allow-listed result snapshot used by JSON and HTML into the compact control-room inspector.
/// </summary>
internal static class VitrineOutcomeInspector
{
    public static (string Title, string Detail) Describe(VitrineRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact = VitrineArtifactSerializer.Freeze(artifact);
        if (!VitrineArtifactSerializer.Verify(artifact))
            throw new InvalidDataException("Artifact integrity verification failed.");
        var detail = new StringBuilder();
        AppendRunSummary(detail, artifact.Result);

        if (artifact.Result.Demo01 is { } demo01)
        {
            detail.AppendLine().AppendLine("Customer-facing answer:").AppendLine(demo01.CustomerFacingAnswer)
                .AppendLine().Append("Retriever: ").AppendLine(Measured(demo01.Retriever))
                .Append("Budget: ").AppendLine(Measured(demo01.BudgetSummary));
            AppendList(detail, "Registered tools", demo01.RegisteredTools);
            AppendRecommendations(detail, demo01.Recommendations);
            AppendLedger(detail, demo01.Ledger);
            return ($"SCREENED DEMO01 OUTCOME · {demo01.Recommendations.Count.ToString(CultureInfo.InvariantCulture)} shown",
                detail.ToString().Trim());
        }

        if (artifact.Result.Demo02 is { } demo02)
        {
            detail.AppendLine().AppendLine("Customer-facing answer:").AppendLine(demo02.CustomerFacingAnswer)
                .AppendLine().Append("Coverage approved: ").AppendLine(demo02.CoverageApproved.ToString(CultureInfo.InvariantCulture))
                .Append("Partial answer: ").AppendLine(demo02.PartialAnswer.ToString(CultureInfo.InvariantCulture))
                .Append("Discovery rounds: ").Append(demo02.DiscoveryRounds).Append('/').AppendLine(demo02.MaxRounds.ToString(CultureInfo.InvariantCulture))
                .Append("Searches: ").AppendLine(demo02.SearchesRun.ToString(CultureInfo.InvariantCulture))
                .Append("Model calls: ").AppendLine(demo02.ModelCalls.ToString(CultureInfo.InvariantCulture))
                .Append("Selection: ").AppendLine(demo02.SelectionWasDeterministic ? "deterministic" : "model-selected");
            AppendList(detail, "Executors", demo02.ExecutorIds);
            AppendList(detail, "Routes actually taken", demo02.RoutesTaken);
            AppendList(detail, "Interests", demo02.Interests.Select(interest =>
                $"{interest.Id} · {interest.Label} · {interest.Kind} · {interest.Origin} · confidence {D3(interest.Confidence)}"));
            AppendRecommendations(detail, demo02.Recommendations);
            AppendList(detail, "Open coverage gaps", demo02.OpenGaps);
            AppendList(detail, "Dropped SKUs", demo02.DroppedSkus);
            AppendList(detail, "Degradations", demo02.DegradedNotes);
            AppendList(detail, "Executor failures", demo02.ExecutorFailures);
            AppendLedger(detail, demo02.Ledger);
            return ($"SCREENED DEMO02 OUTCOME · {(demo02.PartialAnswer ? "partial" : "coverage approved")}",
                detail.ToString().Trim());
        }

        if (artifact.Result.LiveEvaluation is { } liveEvaluation)
        {
            AppendLiveEvaluation(detail, liveEvaluation);
            if (artifact.Result.Gates.Count > 0)
            {
                AppendGates(detail, artifact.Result.Gates);
                AppendControls(detail, artifact.Result.Controls);
            }
            return ($"LIVE EVALUATION OUTCOME · {liveEvaluation.PlanLabel} · {liveEvaluation.TerminalStatus}",
                detail.ToString().Trim());
        }

        AppendEvaluationExecution(detail, artifact.Result.EvaluationExecution);
        AppendOfflineBenchmark(detail, artifact.Result.OfflineBenchmark);

        if (artifact.Result.Gates.Count > 0)
        {
            AppendGates(detail, artifact.Result.Gates);
            AppendControls(detail, artifact.Result.Controls);
            var title = artifact.Mode == VitrineRunMode.Ablation && artifact.Result.Status == "self-test-detected"
                ? "CATALOGUE INTEGRITY SELF-TEST · expected detection observed"
                : $"EVALUATION OUTCOME · {artifact.Result.Status}";
            return (title, detail.ToString().Trim());
        }

        return ("RUN OUTCOME · unavailable", detail.AppendLine().Append("No screened result artifact was produced.").ToString().Trim());
    }

    private static void AppendRunSummary(StringBuilder text, VitrineResultSnapshot result)
    {
        text.Append("Status: ").AppendLine(result.Status)
            .Append("Failure: ").AppendLine(Measured(result.FailureKind))
            .Append("Process-equivalent exit: ").AppendLine(Measured(result.ProcessEquivalentExitCode))
            .Append("Model calls: ").AppendLine(Measured(result.ModelCalls))
            .Append("Tool calls: ").AppendLine(Measured(result.ToolCalls))
            .Append("Presented: ").AppendLine(Measured(result.Presented))
            .Append("Survived: ").AppendLine(Measured(result.Survived))
            .Append("Workflow looped: ").AppendLine(Measured(result.WorkflowLooped))
            .Append("Workflow super-steps: ").AppendLine(Measured(result.WorkflowSuperSteps))
            .Append("Workflow stop reason: ").Append(Measured(result.WorkflowStopReason));
    }

    private static void AppendEvaluationExecution(
        StringBuilder text,
        VitrineEvaluationExecutionSnapshot? execution)
    {
        if (execution is null) return;
        text.AppendLine().AppendLine("Evaluation execution profile:")
            .Append("  Profile: ").AppendLine(execution.Profile.ToString())
            .Append("  Demo scope: ").AppendLine(execution.DemoScope)
            .Append("  Subject engine: ").AppendLine(execution.SubjectEngine)
            .Append("  Evaluator engine: ").AppendLine(execution.EvaluatorEngine)
            .Append("  External models: ").AppendLine(execution.UsesExternalModels ? "yes" : "no")
            .Append("  Deployment: ").AppendLine(Measured(execution.DeploymentName))
            .Append("  Model calls (Demo01 / Demo02 / judge / total): ")
            .Append(Measured(execution.Demo01SubjectModelCalls)).Append(" / ")
            .Append(Measured(execution.Demo02SubjectModelCalls)).Append(" / ")
            .Append(Measured(execution.JudgeModelCalls)).Append(" / ")
            .AppendLine(Measured(execution.TotalModelCalls))
            .Append("  Tokens (Demo01 / Demo02 / judge): ")
            .Append(Measured(execution.Demo01SubjectTokens)).Append(" / ")
            .Append(Measured(execution.Demo02SubjectTokens)).Append(" / ")
            .AppendLine(Measured(execution.JudgeTokens))
            .Append("  Estimated cost USD: ").AppendLine(Measured(execution.EstimatedCostUsd));
    }

    private static void AppendLiveEvaluation(
        StringBuilder text,
        VitrineLiveEvaluationSnapshot live)
    {
        var isSafety = string.Equals(live.Plan, "LiveEval06SafetyProbes", StringComparison.Ordinal);
        text.AppendLine().AppendLine(isSafety
                ? "Paid live safety evaluation:"
                : "Paid live use-case evaluation:")
            .Append("  Plan: ").Append(live.PlanLabel).Append(" · ").AppendLine(live.Plan)
            .Append("  Description: ").AppendLine(live.PlanDescription)
            .Append("  Terminal status / exit: ").Append(live.TerminalStatus).Append(" / ")
            .AppendLine(live.ExitCode.ToString(CultureInfo.InvariantCulture))
            .Append("  Quality pass bar: ").AppendLine(isSafety
                ? "NOT APPLICABLE · safety uses compromise/resistance census"
                : $"{Measured(live.PassThreshold)} · shipped default 1.000 requires all four authored criteria; NOT a null/chance floor")
            .AppendLine("  Measurement policy: unrecovered provider stages cannot count as measured; recovered attempts and final fallbacks are disclosed.")
            .Append("  Session: ").AppendLine(live.SessionId)
            .Append("  Started / completed: ").Append(live.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture))
            .Append(" / ").AppendLine(live.CompletedAtUtc.ToString("O", CultureInfo.InvariantCulture))
            .Append("  Workload scenarios / arms / repetitions: ")
            .Append(live.Workload.ScenarioCount).Append(" / ").Append(live.Workload.ArmCount).Append(" / ")
            .AppendLine(live.Workload.Repetitions.ToString(CultureInfo.InvariantCulture))
            .Append("  Planned subject / judge calls: ").Append(live.Workload.PlannedSubjectCalls).Append(" / ")
            .AppendLine(live.Workload.PlannedJudgeEvaluations.ToString(CultureInfo.InvariantCulture))
            .Append(isSafety ? $"  Safety attacks / probes / maximum model calls: {live.Workload.SafetyAttackCount} / {live.Workload.PlannedSafetyProbes} / {live.Workload.MaximumSafetyModelCalls}{Environment.NewLine}" : string.Empty)
            .Append("  Outcome path: ").AppendLine(live.Persistence.OutcomePath)
            .Append("  Session directory: ").AppendLine(live.Persistence.SessionDirectory)
            .Append("  Index path: ").AppendLine(live.Persistence.IndexPath);

        AppendLiveConfiguration(text, live.Configuration);
        AppendList(text, "Typed live failures (never score zero)", live.Failures.Select(failure =>
            $"{failure.Code} · {failure.Detail} · scenario {Measured(failure.ScenarioId)} · " +
            $"arm {Measured(failure.ArmId)} · rep {Measured(failure.Repetition)} · check {Measured(failure.CheckKey)}"));
        if (isSafety)
        {
            AppendLiveSafety(text, live.Safety, live.TerminalStatus);
            return;
        }

        foreach (var scenario in live.Scenarios)
        {
            text.AppendLine().Append("Scenario ").Append(scenario.Id).Append(" · ")
                .Append(scenario.PersonaId).Append(" · ").AppendLine(scenario.Title)
                .Append("  Description: ").AppendLine(scenario.Description)
                .Append("  Exact query: ").AppendLine(scenario.Query)
                .Append("  Expected behavior: ").AppendLine(scenario.ExpectedBehavior);
            AppendList(text, "  Registered criteria", scenario.Criteria.Select(criterion =>
                $"{criterion.Id} · {criterion.Text}"));
            AppendList(text, "  Ground-truth facts", scenario.GroundTruthFacts);
            if (scenario.AgentToolExpectation is { } tools)
                text.Append("  Agent contract: abstention required ").Append(tools.RequiresAbstention)
                    .Append(" · required tools ").Append(tools.RequiredTools.Count == 0 ? "none" : string.Join(", ", tools.RequiredTools))
                    .Append(" · forbidden tools ").Append(tools.ForbiddenTools.Count == 0 ? "none" : string.Join(", ", tools.ForbiddenTools))
                    .Append(" · forbidden presented SKUs ").AppendLine(tools.ForbiddenPresentedSkus.Count == 0
                        ? "none" : string.Join(", ", tools.ForbiddenPresentedSkus));
        }

        AppendList(text, "Physical AgentEval arm × repetition runs", live.Runs.Select(run =>
            $"{run.ArmId} · rep {run.Repetition} · {run.RunId} · {run.RelativeDirectory}"));

        text.AppendLine().AppendLine("Sanitized live trials:");
        if (live.Trials.Count == 0) text.AppendLine("  none recorded");
        foreach (var trial in live.Trials)
        {
            text.Append("  · ").Append(trial.ScenarioId).Append(" · ").Append(trial.PersonaId)
                .Append(" · ").Append(trial.ArmId).Append(" · ").Append(trial.Architecture)
                .Append(" · rep ").Append(trial.Repetition)
                .Append(" · measurement ").Append(trial.Measurement)
                .Append(" · pass ").Append(Measured(trial.Passed))
                .Append(" · subject ").AppendLine(trial.SubjectStatus)
                .AppendLine("    Response preview:").Append("    ").AppendLine(trial.ResponsePreview)
                .Append("    Tools: journal observed ").Append(trial.Tools.JournalObserved ? "yes" : "no")
                .Append(" · executed/completed/failed/cancelled ")
                .Append(trial.Tools.Executed).Append('/').Append(trial.Tools.Completed).Append('/')
                .Append(trial.Tools.Failed).Append('/').Append(trial.Tools.Cancelled)
                .Append(" · unknown names ").Append(trial.Tools.UnknownNameCount)
                .Append(" · unreconciled ").Append(trial.Tools.UnreconciledCount)
                .Append(" · names ").AppendLine(trial.Tools.ToolNames.Count == 0
                    ? "none"
                    : string.Join(", ", trial.Tools.ToolNames));
            AppendList(text, "    Operation-correlated tool calls", trial.Tools.Calls.Select(call =>
                $"{call.OperationId} · {call.ToolName} · {call.Status} · parameters " +
                (call.Arguments.Count == 0 ? "none" : string.Join(", ", call.Arguments.Select(argument =>
                    $"{argument.Name}={argument.Value}")))));
            if (trial.Failure is { } trialFailure)
                text.Append("    Typed failure: ").Append(trialFailure.Code).Append(" · ")
                    .AppendLine(trialFailure.Detail);
            if (trial.Workflow is { } workflow)
                text.Append("    Workflow: rounds ").Append(workflow.DiscoveryRounds).Append('/')
                    .Append(workflow.MaximumRounds).Append(" · super-steps ").Append(workflow.SuperSteps)
                    .Append(" · looped ").Append(workflow.Looped)
                    .Append(" · stop ").Append(workflow.StopReason)
                    .Append(" · failures ").Append(workflow.FailureCount)
                    .Append(" · bounded degradation events ").Append(workflow.DegradationCount)
                    .Append(" · kinds ").Append(workflow.DegradationKinds.Count == 0
                        ? "none disclosed"
                        : string.Join(", ", workflow.DegradationKinds))
                    .Append(" · provider failed/recovered/terminal ")
                    .Append(workflow.ProviderFailedAttemptCount).Append('/')
                    .Append(workflow.RecoveredProviderFailedAttemptCount).Append('/')
                    .Append(workflow.TerminalProviderStageCount)
                    .Append(" · unknown executors/routes ").Append(workflow.UnknownExecutorCount).Append('/')
                    .AppendLine(workflow.UnknownRouteCount.ToString(CultureInfo.InvariantCulture));
            if (trial.Workflow is { } providerWorkflow)
                AppendList(text, "    Model-backed stage outcomes", providerWorkflow.ProviderStages.Select(stage =>
                    $"{stage.ExecutorId} · {stage.Status} · attempts {stage.AttemptCount} · responses {stage.ResponseCount} · unusable {stage.UnusableAttemptCount} · failed {stage.FailedAttemptCount} · cancelled {stage.CancelledAttemptCount}"));
            AppendLiveUsage(text, "    Subject usage", trial.SubjectUsage);
            AppendLiveUsage(text, "    Judge usage", trial.JudgeUsage);
            AppendList(text, "    Check facts", trial.Checks.Select(check =>
                $"{check.Key} · {check.Name} · {check.Measurement} · score {Measured(check.Score)} · pass {Measured(check.Passed)}"));
            AppendList(text, "    Criterion verdicts", trial.Criteria.Select(criterion =>
                $"{criterion.Id} · {criterion.Measurement} · met {Measured(criterion.Met)} · " +
                $"judge explanation {Measured(criterion.Explanation)}"));
        }

        text.AppendLine().AppendLine("Evaluator-owned per-check census and Wilson reliability:");
        if (live.Arms.Count == 0) text.AppendLine("  none recorded");
        foreach (var arm in live.Arms)
        {
            text.Append("  Arm ").Append(arm.ArmId).Append(" · ").Append(arm.Architecture)
                .Append(" · repetitions ").AppendLine(arm.Repetitions.ToString(CultureInfo.InvariantCulture));
            foreach (var check in arm.Checks)
                text.Append("    · ").Append(check.Key).Append(" · ").Append(check.Name)
                    .Append(" · census M/N-A/N-M/total ").Append(check.Census.Measured).Append('/')
                    .Append(check.Census.NotApplicable).Append('/').Append(check.Census.NotMeasured).Append('/')
                    .Append(check.Census.Total)
                    .Append(" · reliability ").Append(check.Reliability.Measurement)
                    .Append(" · successes/total ").Append(check.Reliability.Successes).Append('/')
                    .Append(check.Reliability.Total)
                    .Append(" · estimate ").Append(Measured(check.Reliability.Estimate))
                    .Append(" · Wilson [").Append(Measured(check.Reliability.Lower)).Append(", ")
                    .Append(Measured(check.Reliability.Upper)).AppendLine("]");
        }

        text.AppendLine().AppendLine("Terminal per-scenario Wilson decisions:")
            .AppendLine("  These are the exact stochastic terminal-acceptance facts: every planned trial must be fully measured; a whole-trial success means use-case quality, response observed, and the arm-specific agent tool journal or workflow trace all passed; every arm/scenario must clear its 95% Wilson lower-bound floor of 0.500. Pooled per-check summaries are diagnostic and cannot replace them.");
        if (live.ScenarioAcceptances is not { Count: > 0 })
        {
            text.AppendLine("  none recorded · not applicable to non-stochastic plans and absent from compatible schema 7/8 artifacts");
        }
        else
        {
            foreach (var decision in live.ScenarioAcceptances)
                text.Append("  · ").Append(decision.ScenarioId).Append(" · ").Append(decision.PersonaId)
                    .Append(" · ").Append(decision.ArmId).Append(" · ").Append(decision.Architecture)
                    .Append(" · census M/N-A/N-M/total ").Append(decision.Census.Measured).Append('/')
                    .Append(decision.Census.NotApplicable).Append('/').Append(decision.Census.NotMeasured)
                    .Append('/').Append(decision.Census.Total)
                    .Append(" · whole-trial successes/total ").Append(decision.Reliability.Successes).Append('/')
                    .Append(decision.Reliability.Total)
                    .Append(" · Wilson [").Append(Measured(decision.Reliability.Lower)).Append(", ")
                    .Append(Measured(decision.Reliability.Upper)).Append(']')
                    .Append(" · confidence ").Append(Measured(decision.ConfidenceLevel))
                    .Append(" · minimum lower bound ").Append(Measured(decision.MinimumLowerBound))
                    .Append(" · decision ").AppendLine(Measured(decision.Passed));
        }

        text.AppendLine("Evaluator-owned paired comparisons (no UI winner):");
        if (live.Comparisons.Count == 0) text.AppendLine("  none recorded");
        foreach (var comparison in live.Comparisons)
            text.Append("  · ").Append(comparison.CheckKey).Append(" · ")
                .Append(comparison.ReferenceArm).Append(" → ").Append(comparison.ChallengerArm)
                .Append(" · W/L/T ").Append(comparison.Wins).Append('/').Append(comparison.Losses).Append('/')
                .Append(comparison.Ties).Append(" · effective n ").Append(comparison.EffectiveN)
                .Append(" · p ").Append(Measured(comparison.PValue))
                .Append(" · minimum attainable p ").Append(Measured(comparison.MinimumAttainableP))
                .Append(" · delta ").Append(Measured(comparison.MeanDelta))
                .Append(" · census M/N-A/N-M/total ").Append(comparison.Census.Measured).Append('/')
                .Append(comparison.Census.NotApplicable).Append('/').Append(comparison.Census.NotMeasured).Append('/')
                .Append(comparison.Census.Total)
                .Append(" · observation unit ").Append(comparison.Cases).Append(" paired cases / ")
                .Append(comparison.TotalRepObservations).Append(" total raw reps across both arms / mean ")
                .Append(Measured(comparison.MeanRepetitionsPerCase)).Append(" reps per case per arm / RepCollapse.")
                .Append(comparison.RepCollapse).Append(" · ")
                .AppendLine(comparison.Undecidable
                    ? "UNDECIDABLE · evaluator did not admit a directional comparison"
                    : comparison.UnderpoweredByConstruction
                        ? "UNDERPOWERED BY CONSTRUCTION"
                        : "AgentEval reports power permits comparison");
    }

    private static void AppendLiveConfiguration(
        StringBuilder text,
        VitrineLiveConfigurationSnapshot? configuration)
    {
        if (configuration is null) return;
        text.AppendLine("Evaluator configuration and provenance:")
            .Append("  Definition: ").Append(configuration.DefinitionKey).Append('@').AppendLine(configuration.DefinitionVersion)
            .Append("  Judge model / prompt / rubric: ").Append(configuration.JudgeModelId).Append(" / ")
            .Append(configuration.JudgePromptId).Append(" / ").AppendLine(configuration.JudgeRubricHash)
            .Append("  Subject / judge output limits; response preview characters: ")
            .Append(configuration.SubjectMaxOutputTokens).Append(" / ").Append(configuration.JudgeMaxOutputTokens)
            .Append("; ").AppendLine(configuration.ResponsePreviewCharacters.ToString(CultureInfo.InvariantCulture));
        if (configuration.Acceptance is { } acceptance)
            text.Append("  Terminal acceptance: ").Append(acceptance.Policy)
                .Append(" · confidence ").Append(Measured(acceptance.ConfidenceLevel))
                .Append(" · minimum Wilson lower bound ").AppendLine(Measured(acceptance.MinimumLowerBound));
        AppendList(text, "  Subject provenance", configuration.Subjects.Select(subject =>
            $"{subject.ArmId} · {subject.Architecture} · model {subject.ModelId} · judge relation {subject.JudgeSubjectRelation}"));
        if (configuration.Safety is { } safety)
            text.Append("  Safety limits: attacks ").Append(string.Join(", ", safety.Attacks))
                .Append(" · probes/category ").Append(safety.MaxProbesPerAttack)
                .Append(" · timeout/probe ").Append(safety.TimeoutSeconds).Append("s")
                .Append(" · target calls/probe ").Append(safety.MaxTargetModelCallsPerProbe)
                .Append(" · maximum model calls ").Append(safety.MaximumModelCalls)
                .Append(" · judge mode ").Append(safety.JudgeMode)
                .Append(" · raw evidence persisted ").AppendLine(safety.EvidencePersisted ? "yes" : "no");
    }

    private static void AppendLiveSafety(
        StringBuilder text,
        VitrineLiveSafetySnapshot? safety,
        string terminalStatus)
    {
        text.AppendLine().AppendLine("Redacted AgentEval safety census:")
            .AppendLine("  Campaign execution and measurement completeness are separate. Clean ambiguity is NOT MEASURED; execution faults or incomplete coverage are INFRASTRUCTURE ERROR.")
            .AppendLine("  Errored is a subset of Inconclusive; do not add those counts.")
            .AppendLine("  Raw probes, model responses, extraction canary, system instructions, endpoints, and exceptions were not retained.");
        if (safety is null)
        {
            text.AppendLine(string.Equals(terminalStatus, nameof(LiveEvalTerminalStatus.InfrastructureError), StringComparison.Ordinal)
                ? "  INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · no sanitized safety summary was produced."
                : "  NOT MEASURED · no sanitized safety summary was produced.");
            return;
        }
        var status = string.Equals(terminalStatus, nameof(LiveEvalTerminalStatus.QualityFailed), StringComparison.Ordinal)
            ? $"VULNERABLE · {safety.Compromised} attack(s) succeeded"
            : string.Equals(terminalStatus, nameof(LiveEvalTerminalStatus.Passed), StringComparison.Ordinal)
                ? $"RESISTED · {safety.Resisted}/{safety.Total}"
                : string.Equals(terminalStatus, nameof(LiveEvalTerminalStatus.InfrastructureError), StringComparison.Ordinal)
                    ? safety.Errored > 0
                        ? $"INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · {safety.Errored}/{safety.Total} PROBES ERRORED"
                        : "INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · INCOMPLETE CENSUS"
                    : "NOT MEASURED · INCONCLUSIVE";
        text.Append("  Outcome: ").AppendLine(status)
            .AppendLine("  Campaign execution: completed · a redacted receipt was returned")
            .Append("  Measurement completeness: ").AppendLine(
                string.Equals(terminalStatus, nameof(LiveEvalTerminalStatus.InfrastructureError), StringComparison.Ordinal)
                    ? "INCOMPLETE · infrastructure error"
                    : safety.Passed is null ? "INCOMPLETE · inconclusive" : "complete")
            .Append("  Target / measurement: ").Append(safety.Target).Append(" / ").AppendLine(safety.Measurement)
            .Append("  Census total/resisted/compromised/inconclusive/errored-subset: ")
            .Append(safety.Total).Append('/').Append(safety.Resisted).Append('/').Append(safety.Compromised)
            .Append('/').Append(safety.Inconclusive).Append('/').AppendLine(safety.Errored.ToString(CultureInfo.InvariantCulture))
            .Append("  Truncated / skipped: ").Append(safety.Truncated).Append(" / ")
            .AppendLine(safety.Skipped.ToString(CultureInfo.InvariantCulture));
        AppendLiveUsage(text, "  Target usage", safety.SubjectUsage);
        AppendLiveUsage(text, "  Fallback-judge usage", safety.JudgeUsage);
        AppendList(text, "  Attack categories", safety.Attacks.Select(attack =>
            $"{attack.Attack} · {attack.OwaspId} · " +
            (attack.Compromised > 0 ? "VULNERABLE · ATTACK SUCCEEDED"
                : attack.Errored > 0 ? "INCOMPLETE · PROBE ERROR"
                : attack.Inconclusive > 0 ? "NOT MEASURED · INCONCLUSIVE" : "RESISTED") +
            $" · total/resisted/compromised/inconclusive/errored-subset {attack.Total}/{attack.Resisted}/{attack.Compromised}/{attack.Inconclusive}/{attack.Errored}"));
        AppendList(text, "  Allow-listed probe receipts", safety.Probes.Select(SafetyProbeReceipt));
    }

    private static string SafetyProbeReceipt(VitrineLiveSafetyProbeSnapshot probe)
    {
        var hasFailure = probe.ErrorKind != nameof(LiveSafetyProbeErrorKind.None);
        var diagnostic = LiveSafetyProbeDiagnostics.Describe(probe.Outcome, probe.ErrorKind);
        var outcome = hasFailure
            ? $"ERROR · {probe.ErrorKind.ToUpperInvariant()}"
            : probe.Outcome == "Compromised" ? "VULNERABLE · ATTACK SUCCEEDED"
            : probe.Outcome == "Resisted" ? "RESISTED" : "INCONCLUSIVE · NOT MEASURED";
        return $"{probe.Attack} · {probe.ProbeId} · {outcome} · safe diagnostic {diagnostic}" +
               $" · typed failure stage {probe.Failure?.Stage ?? (hasFailure ? "probe-execution" : "not-applicable")}" +
               $" · code {probe.Failure?.Code ?? (hasFailure ? probe.ErrorKind : nameof(LiveSafetyProbeErrorKind.None))}" +
               $" · detail {probe.Failure?.Detail ?? (hasFailure ? diagnostic : "No typed probe execution failure.")}" +
               $" · severity {probe.Severity} · fidelity {probe.Fidelity} · technique {probe.Technique}";
    }

    private static void AppendLiveUsage(StringBuilder text, string label, VitrineLiveUsageSnapshot usage) =>
        text.Append(label).Append(": status ").Append(usage.Status)
            .Append(" · calls ").Append(Measured(usage.ModelCalls))
            .Append(" · input/output/total tokens ").Append(Measured(usage.InputTokens)).Append('/')
            .Append(Measured(usage.OutputTokens)).Append('/').Append(Measured(usage.TotalTokens))
            .Append(" · estimated USD ").AppendLine(Measured(usage.EstimatedCostUsd));

    private static void AppendOfflineBenchmark(
        StringBuilder text,
        VitrineOfflineBenchmarkSnapshot? benchmark)
    {
        if (benchmark is null) return;
        text.AppendLine().AppendLine("Native AgentEval offline benchmark:")
            .Append("  Definition: ").Append(benchmark.DefinitionKey).Append('@').AppendLine(benchmark.DefinitionVersion)
            .Append("  Arms / repetitions / persisted runs: ").Append(benchmark.Arms.Count).Append(" / ")
            .Append(benchmark.Repetitions).Append(" / ").AppendLine(benchmark.Runs.Count.ToString(CultureInfo.InvariantCulture))
            .Append("  Workspace: ").AppendLine(benchmark.WorkspaceRoot)
            .Append("  Reference run directory: ").AppendLine(benchmark.RunDirectory);
        AppendList(text, "  Cases", benchmark.Cases.Select(item => $"{item.Id} · {item.Name}"));
        text.AppendLine("  Native score facts:");
        if (benchmark.Checks.Count == 0)
        {
            text.AppendLine("    none recorded");
            return;
        }

        foreach (var arm in benchmark.Arms)
        {
            text.Append("  Arm ").Append(arm.ArmId).Append(" · ").Append(arm.SubjectKind)
                .Append(" · ").AppendLine(arm.SubjectName);
            AppendBenchmarkChecks(text, arm.Checks);
        }
        if (benchmark.Arms.Count == 0)
            AppendBenchmarkChecks(text, benchmark.Checks);
        AppendList(text, "  Persisted arm × repetition runs", benchmark.Runs.Select(run =>
            $"{run.ArmId} · rep {run.Repetition} · {run.SubjectKind} · {run.RunId} · {run.RunDirectory}"));
        text.AppendLine("  Paired reference comparisons:");
        foreach (var row in benchmark.ReferenceComparisons)
            text.Append("    · ").Append(row.CheckKey).Append(" · ").Append(row.ReferenceArmId)
                .Append(" → ").Append(row.ChallengerArmId).Append(" · W/L/T ")
                .Append(row.Wins).Append('/').Append(row.Losses).Append('/').Append(row.Ties)
                .Append(" · effective n ").Append(row.EffectiveN)
                .Append(" · p ").Append(Measured(row.PValue))
                .Append(" · minimum p ").Append(Measured(row.MinimumAttainableP))
                .Append(" · delta ").Append(Measured(row.MeanDelta))
                .Append(" · ").Append(row.RepCollapse).Append(" over ").Append(row.Cases)
                .Append(" cases / ").Append(row.TotalRepObservations).Append(" rep observations · ")
                .AppendLine(row.UnderpoweredByConstruction ? "UNDERPOWERED" : "power permits comparison");
    }

    private static void AppendBenchmarkChecks(
        StringBuilder text,
        IReadOnlyList<VitrineBenchmarkCheckSnapshot> checks)
    {
        foreach (var check in checks)
            text.Append("    · ").Append(check.CheckKey)
                .Append(" · floor ").Append(check.Floor.Kind).Append('/').Append(check.Floor.State)
                .Append(" value ").Append(Measured(check.Floor.Value))
                .Append(" bar ").Append(Measured(check.Floor.ComparisonBar))
                .Append(" interval-high ").Append(Measured(check.Floor.IntervalHigh))
                .Append(" · draws ").Append(check.Floor.Draws).Append(" / pool ").Append(check.Floor.PoolSize)
                .Append(" · census measured/N-A/not-measured/total ")
                .Append(check.Census.Measured).Append('/').Append(check.Census.NotApplicable).Append('/')
                .Append(check.Census.NotMeasured).Append('/').Append(check.Census.Total)
                .Append(" · observed ").Append(check.Successes).Append('/').Append(check.Trials)
                .Append(" · p ").Append(Measured(check.PValue))
                .Append(" · minimum attainable p ").Append(Measured(check.MinimumAttainableP))
                .Append(" · above floor ").Append(Measured(check.AboveFloor))
                .Append(" · power ").AppendLine(check.UnderpoweredByConstruction switch
                {
                    true => "UNDERPOWERED BY CONSTRUCTION",
                    false => "sufficient for configured comparison",
                    null => "N/A · native floor comparison not derivable",
                })
                .Append("      Derivation: ").AppendLine(check.Floor.Derivation);
    }

    private static void AppendRecommendations(StringBuilder text, IReadOnlyList<VitrineRecommendationSnapshot> items)
    {
        text.AppendLine().AppendLine("Recommendations actually shown:");
        if (items.Count == 0)
        {
            text.AppendLine("  none recorded");
            return;
        }

        foreach (var item in items)
            text.Append("  · ").Append(item.Tray).Append(" · ").Append(item.Sku).Append(" · ").Append(item.Name)
                .Append(" · confidence ").Append(D3(item.Confidence))
                .Append(" · price ").Append(Measured(item.VerifiedPriceChf, "CHF 0.00"))
                .Append(" · stock ").Append(Measured(item.StockUnits))
                .Append(" · delivery ").Append(Measured(item.DeliveryEstimateDays, "0 working days"))
                .AppendLine().Append("    Why: ").AppendLine(item.Reason)
                .Append("    Evidence: ").AppendLine(item.Evidence);
    }

    private static void AppendLedger(StringBuilder text, VitrineLedgerSnapshot? ledger)
    {
        text.AppendLine().AppendLine("Guardrail ledger:");
        if (ledger is null)
        {
            text.AppendLine("  NOT MEASURED");
            return;
        }

        text.Append("  ").Append(ledger.InputCount).Append(" in → ").Append(ledger.OutputCount).Append(" out · ")
            .Append(ledger.DroppedCount).Append(" dropped · ").Append(ledger.DemotedCount).Append(" demoted · ")
            .Append(ledger.NotedCount).Append(" noted · gift exclusions ").Append(Measured(ledger.GiftExcluded))
            .Append(" · price/stock ").Append(ledger.PriceStockVerified).Append('/').Append(ledger.PriceStockRequested)
            .Append(" verified · tool calls ").Append(Measured(ledger.ToolCallsUsed)).Append('/')
            .AppendLine(Measured(ledger.ToolCallCap));
        AppendList(text, "Ledger entries", ledger.Entries.Select(entry =>
            $"{entry.Stage} · {entry.Action} · {entry.Reason} · {entry.Subject} · {entry.Detail}"));
    }

    private static void AppendGates(StringBuilder text, IReadOnlyList<VitrineGateSnapshot> gates)
    {
        text.AppendLine().AppendLine("Mandatory gates and diagnostic evaluations:")
            .AppendLine("  Mandatory gates control the process-equivalent exit; diagnostic rows remain visible evidence but cannot fail the suite.");
        foreach (var gate in gates)
        {
            text.Append("  · ").Append(gate.Name).Append(" · ").Append(gate.Outcome)
                .Append(" · authority ").Append(gate.EffectiveAuthority)
                .Append(" · pass ").Append(Measured(gate.Passed))
                .Append(" · score ").Append(Measured(gate.Score))
                .Append(" · chance floor ").Append(Floor(gate.ChanceFloor))
                .AppendLine().Append("    Evidence: ").AppendLine(gate.Evidence);
            AppendAgentEval(text, gate.AgentEval);
        }
    }

    private static void AppendAgentEval(StringBuilder text, VitrineAgentEvalProvenanceSnapshot? provenance)
    {
        if (provenance is null)
        {
            text.AppendLine("    AgentEval provenance: NOT MEASURED");
            return;
        }

        text.Append("    AgentEval: ").Append(provenance.IntegrationId).Append(" · ")
            .Append(provenance.LibraryType).Append(" · ").Append(provenance.Mechanism)
            .Append(" · subject ").Append(provenance.Subject).Append(" · snapshot ").AppendLine(provenance.SnapshotPolicy)
            .Append("      Observation producer: ").AppendLine(provenance.ObservationProducer)
            .Append("      Acceptance evaluator: ").AppendLine(provenance.AcceptanceEvaluator)
            .Append("      Subject supplied pass/fail: ").AppendLine(provenance.SubjectSuppliedPassFail ? "yes" : "no");
        foreach (var observation in provenance.Observations)
            text.Append("      · ").Append(observation.Id).Append(" · ").Append(observation.Outcome)
                .Append(" · score ").Append(Measured(observation.Score))
                .Append(" · surface ").Append(Measured(observation.Surface))
                .Append(" · n ").AppendLine(Measured(observation.SampleCount));
    }

    private static void AppendControls(StringBuilder text, IReadOnlyList<VitrineControlSnapshot> controls)
    {
        text.AppendLine().AppendLine("Registered diagnostic controls (not admitted evals; no chance floor):");
        if (controls.Count == 0)
        {
            text.AppendLine("  none recorded");
            return;
        }

        foreach (var control in controls)
            text.Append("  · ").Append(control.Id).Append(" · ").Append(control.Name).Append(" · ").Append(control.Category)
                .Append(" · scope ").Append(control.ScopeClass).Append(" · target ").Append(control.Target).Append(" · producer ").Append(control.ObservationProducer)
                .Append(" · evaluator ").Append(control.Evaluator).Append(" · tranche ").Append(control.Tranche)
                .Append(" · healthy ").Append(control.HealthyOutcome)
                .Append(" · broken ").Append(control.BrokenOutcome)
                .Append(" · restored ").Append(control.RestoredOutcome)
                .AppendLine().Append("    Evidence: ").AppendLine(control.Evidence);
    }

    private static void AppendList(StringBuilder text, string title, IEnumerable<string> values)
    {
        var items = values.ToArray();
        text.AppendLine().Append(title).AppendLine(":");
        if (items.Length == 0)
        {
            text.AppendLine("  none recorded");
            return;
        }

        foreach (var item in items) text.Append("  · ").AppendLine(item);
    }

    private static string D3(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    private static string Floor(VitrineChanceFloorSnapshot? floor) => floor switch
    {
        null => "NO CHANCE COMPARISON",
        { State: "NotDerivable" } => $"NOT DERIVABLE · {floor.Derivation}",
        _ => $"{floor.Kind} · bar {Measured(floor.ComparisonBar)} · {floor.Derivation}",
    };
    private static string Measured(string? value) => string.IsNullOrWhiteSpace(value) ? "NOT MEASURED" : value;
    private static string Measured(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Measured(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Measured(bool? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Measured(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Measured(decimal? value) => value?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Measured(decimal? value, string format) =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Measured(int? value, string format) =>
        value?.ToString(format, CultureInfo.InvariantCulture) ?? "NOT MEASURED";
}
