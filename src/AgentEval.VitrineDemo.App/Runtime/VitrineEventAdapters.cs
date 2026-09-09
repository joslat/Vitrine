// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Workflows;

namespace AgentEval.VitrineDemo.App.Runtime;

public static class VitrineEventAdapters
{
    internal const string LivePersistenceOperationId = "live:persistence";
    internal const string LiveSessionCompletedKind = "Live" + nameof(LiveEvalProgressPhase.SessionCompleted);

    public static VitrineEventDraft FromLiveEvaluation(LiveEvalProgress item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var disposition = item.Phase switch
        {
            LiveEvalProgressPhase.SessionStarting or LiveEvalProgressPhase.TrialStarting
                or LiveEvalProgressPhase.SubjectRunning or LiveEvalProgressPhase.CheckStarting
                or LiveEvalProgressPhase.SafetyTargetRunning
                or LiveEvalProgressPhase.Persisting => VitrineEventDisposition.Active,
            _ when item.Measurement == AgentEval.Evals.Meta.MeasurementState.NotApplicable
                => VitrineEventDisposition.NotApplicable,
            _ when item.Measurement == AgentEval.Evals.Meta.MeasurementState.NotMeasured
                => VitrineEventDisposition.NotMeasured,
            LiveEvalProgressPhase.CheckCompleted or LiveEvalProgressPhase.TrialCompleted
                => item.Passed == true ? VitrineEventDisposition.Succeeded : VitrineEventDisposition.Failed,
            LiveEvalProgressPhase.SafetyFindingsCompleted => item.Passed switch
            {
                true => VitrineEventDisposition.Succeeded,
                false => VitrineEventDisposition.Failed,
                null => VitrineEventDisposition.NotMeasured,
            },
            LiveEvalProgressPhase.SubjectCompleted or LiveEvalProgressPhase.SessionCompleted
                => item.Passed switch
                {
                    true => VitrineEventDisposition.Succeeded,
                    false => VitrineEventDisposition.Failed,
                    null => VitrineEventDisposition.NotMeasured,
                },
            _ => VitrineEventDisposition.Neutral,
        };
        var descriptor = VitrineEvaluationPlans.Require(item.Plan);
        var (source, target, operationId) = LiveEvaluationRoute(item);
        var identity = new[]
        {
            item.ScenarioId is null ? null : $"case {item.ScenarioId}",
            item.ArmId is null ? null : $"arm {item.ArmId}",
            item.Repetition is null ? null : $"rep {item.Repetition.Value.ToString(CultureInfo.InvariantCulture)}",
        }.Where(static value => value is not null);
        var context = string.Join(" · ", identity);
        var title = $"{descriptor.Label} · {LivePhaseLabel(item.Phase)}"
            + (context.Length == 0 ? string.Empty : $" · {context}");
        var payload = new StringBuilder()
            .Append("Evaluation plan: ").AppendLine(descriptor.Label)
            .Append("Phase: ").AppendLine(item.Phase.ToString());
        if (item.ScenarioId is not null) payload.Append("Scenario: ").AppendLine(item.ScenarioId);
        if (item.ArmId is not null) payload.Append("Arm: ").AppendLine(item.ArmId);
        if (item.Repetition is not null)
            payload.Append("Repetition: ").AppendLine(item.Repetition.Value.ToString(CultureInfo.InvariantCulture));
        if (item.CheckKey is not null) payload.Append("Admitted check: ").AppendLine(item.CheckKey);
        if (item.Measurement is not null) payload.Append("Measurement: ").AppendLine(item.Measurement.ToString());
        if (item.Passed is not null) payload.Append("Verdict: ").AppendLine(item.Passed.Value ? "PASS" : "FAIL");
        payload.Append("Evidence: ").AppendLine(item.Detail);
        return new(VitrineEventCategory.Evaluation, $"Live{item.Phase}", disposition,
            source, target, title, item.Detail, operationId, payload.ToString().TrimEnd());
    }

    /// <summary>
    /// Projects only the allow-listed aggregate safety receipt. These observations make the two
    /// real AgentEval attack branches inspectable without copying prompts, responses, canaries,
    /// or judge reasons into the App event stream.
    /// </summary>
    public static IReadOnlyList<VitrineEventDraft> FromLiveSafetyResult(LiveEvalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Plan != VitrineEvaluationPlan.LiveEval06SafetyProbes) return [];

        var summary = result.Safety;
        var overall = SafetyDisposition(result.TerminalStatus, summary?.Passed);
        var terminalDetail = result.TerminalStatus switch
        {
            LiveEvalTerminalStatus.InfrastructureError when summary is { Errored: > 0 } =>
                $"INFRASTRUCTURE ERROR · safety verdict not measured · {summary.Errored}/{summary.Total} probes errored.",
            LiveEvalTerminalStatus.InfrastructureError =>
                "INFRASTRUCTURE ERROR · safety verdict not measured · the campaign census is incomplete.",
            LiveEvalTerminalStatus.NotMeasured =>
                "NOT MEASURED · the campaign executed but AgentEval could not reach a conclusive verdict.",
            LiveEvalTerminalStatus.QualityFailed => "VULNERABLE · at least one attack succeeded.",
            LiveEvalTerminalStatus.Passed => "RESISTED · every planned probe was conclusive.",
            _ => "The safety verdict was not completed.",
        };
        var drafts = new List<VitrineEventDraft>
        {
            SafetyObservation("LiveSafetyTargetObserved", "live-session", "live-safety-target",
                "Robin safety target result", overall,
                summary is null
                    ? "No conclusive target receipt was produced."
                    : terminalDetail + $" Target {summary.Target}; campaign execution returned; measurement {summary.Measurement}; " +
                      $"{summary.Inconclusive} inconclusive ({summary.Errored} errored subset). " +
                      "Raw target text was not retained."),
        };

        var attacks = summary?.Attacks
            ?? result.Configuration.Safety?.Attacks.Select(static attack =>
                new LiveSafetyAttackSummary(attack, SafetyOwaspId(attack), 0, 0, 0, 0, 0)).ToArray()
            ?? [];
        if (summary is { })
        {
            foreach (var probe in summary.Probes)
            {
                var node = SafetyAttackNode(probe.Attack);
                if (node is null) continue;
                var outcome = probe.ErrorKind != LiveSafetyProbeErrorKind.None
                    ? $"ERROR · {probe.ErrorKind.ToString().ToUpperInvariant()}"
                    : probe.Outcome == LiveSafetyProbeOutcome.Compromised
                        ? "VULNERABLE · ATTACK SUCCEEDED"
                        : probe.Outcome == LiveSafetyProbeOutcome.Resisted
                            ? "RESISTED"
                            : "INCONCLUSIVE · NOT MEASURED";
                var failure = probe.Failure;
                var detail = $"{probe.Attack}/{probe.ProbeId}: {outcome}. Safe diagnostic: {probe.Diagnostic} " +
                    (failure is null
                        ? "Typed failure: stage not-applicable; code None; detail No typed probe execution failure."
                        : $"Typed failure: stage {failure.Stage}; code {failure.Code}; detail {failure.Detail}");
                drafts.Add(SafetyObservation("LiveSafetyProbeObserved", node, "live-safety-findings",
                    $"{probe.Attack} · {probe.ProbeId} · {outcome}", SafetyDisposition(probe), detail));
            }
        }

        // Category facts follow their probe receipts so the graph settles on the aggregate
        // category verdict instead of whichever individual probe happened to be emitted last.
        foreach (var attack in attacks)
        {
            var node = SafetyAttackNode(attack.Attack);
            if (node is null) continue;
            var disposition = SafetyDisposition(attack);
            var detail = $"{attack.Attack} ({attack.OwaspId}): {attack.Resisted} resisted, " +
                $"{attack.Compromised} compromised, {attack.Inconclusive} inconclusive " +
                $"({attack.Errored} errored subset); total {attack.Total}.";
            drafts.Add(SafetyObservation("LiveSafetyAttackObserved", "live-safety-target", node,
                $"{SafetyAttackLabel(attack.Attack)} · redacted result", disposition, detail));
            drafts.Add(SafetyObservation("LiveSafetyAttackFindingsObserved", node, "live-safety-findings",
                $"{SafetyAttackLabel(attack.Attack)} · findings admitted", disposition, detail));
        }

        drafts.Add(SafetyObservation("LiveSafetyFindingsObserved", string.Empty, "live-safety-findings",
            "Redacted safety census", overall,
            summary is null
                ? "NOT MEASURED · no sanitized safety census was produced."
                : terminalDetail + $" {summary.Resisted} resisted, {summary.Compromised} compromised, " +
                  $"{summary.Inconclusive} inconclusive ({summary.Errored} errored subset); " +
                  $"truncated {summary.Truncated}; skipped {summary.Skipped}."));
        drafts.Add(SafetyObservation("LiveSafetyTerminalObserved", "live-complete", string.Empty,
            "Safety campaign terminal status", overall,
            $"{terminalDetail} Terminal {result.TerminalStatus}; exit {result.ExitCode}."));
        return drafts;
    }

    private static VitrineEventDraft SafetyObservation(
        string kind,
        string source,
        string target,
        string title,
        VitrineEventDisposition disposition,
        string detail) => new(
            VitrineEventCategory.Evaluation,
            kind,
            disposition,
            source,
            target,
            title,
            detail,
            Payload: "Sanitized allow-listed receipt only. Raw probes, model responses, extraction canary, system instructions, endpoints, and exceptions were not retained.\n" + detail);

    private static VitrineEventDisposition SafetyDisposition(
        LiveEvalTerminalStatus terminal,
        bool? passed) => terminal switch
    {
        LiveEvalTerminalStatus.Passed => VitrineEventDisposition.Succeeded,
        LiveEvalTerminalStatus.QualityFailed or LiveEvalTerminalStatus.InfrastructureError =>
            VitrineEventDisposition.Failed,
        _ => passed switch
        {
            true => VitrineEventDisposition.Succeeded,
            false => VitrineEventDisposition.Failed,
            null => VitrineEventDisposition.NotMeasured,
        },
    };

    private static VitrineEventDisposition SafetyDisposition(LiveSafetyAttackSummary attack) =>
        attack.Compromised > 0
            ? VitrineEventDisposition.Failed
            : attack.Errored > 0
                ? VitrineEventDisposition.Failed
                : attack.Total > 0 && attack.Resisted == attack.Total && attack.Inconclusive == 0
                ? VitrineEventDisposition.Succeeded
                : VitrineEventDisposition.NotMeasured;

    private static VitrineEventDisposition SafetyDisposition(LiveSafetyProbeFact probe) =>
        probe.ErrorKind != LiveSafetyProbeErrorKind.None
            ? VitrineEventDisposition.Failed
            : probe.Outcome switch
            {
                LiveSafetyProbeOutcome.Compromised => VitrineEventDisposition.Failed,
                LiveSafetyProbeOutcome.Resisted => VitrineEventDisposition.Succeeded,
                _ => VitrineEventDisposition.NotMeasured,
            };

    private static string? SafetyAttackNode(string attack) => attack switch
    {
        "Jailbreak" => "live-safety-jailbreak",
        "SystemPromptExtraction" => "live-safety-extraction",
        _ => null,
    };

    private static string SafetyAttackLabel(string attack) => attack switch
    {
        "Jailbreak" => "Jailbreak probes",
        "SystemPromptExtraction" => "Instruction-extraction probes",
        _ => "Safety probes",
    };

    private static string SafetyOwaspId(string attack) => attack switch
    {
        "Jailbreak" => "LLM01",
        "SystemPromptExtraction" => "LLM07",
        _ => "unknown",
    };

    public static VitrineEventDraft FromRecommendation(RecommendationRuntimeEvent item)
    {
        var category = item.Kind switch
        {
            RecommendationRuntimeEventKind.ModelRequestStarted or RecommendationRuntimeEventKind.ModelResponseReceived
                or RecommendationRuntimeEventKind.ModelRequestCancelled or RecommendationRuntimeEventKind.ModelRequestFailed => VitrineEventCategory.Model,
            RecommendationRuntimeEventKind.ToolExecutionStarted or RecommendationRuntimeEventKind.ToolCompleted
                or RecommendationRuntimeEventKind.ToolCancelled or RecommendationRuntimeEventKind.ToolFailed => VitrineEventCategory.Tool,
            RecommendationRuntimeEventKind.GuardDecision => VitrineEventCategory.Guardrail,
            _ => VitrineEventCategory.Agent,
        };
        var disposition = item.Kind switch
        {
            RecommendationRuntimeEventKind.ModelRequestStarted or RecommendationRuntimeEventKind.ToolExecutionStarted or RecommendationRuntimeEventKind.RunStarted => VitrineEventDisposition.Active,
            RecommendationRuntimeEventKind.ModelResponseReceived or RecommendationRuntimeEventKind.ToolCompleted or RecommendationRuntimeEventKind.RunCompleted or RecommendationRuntimeEventKind.GuardDecision => VitrineEventDisposition.Succeeded,
            RecommendationRuntimeEventKind.ModelRequestCancelled or RecommendationRuntimeEventKind.ToolCancelled
                or RecommendationRuntimeEventKind.RunCancelled => VitrineEventDisposition.Warning,
            RecommendationRuntimeEventKind.ModelRequestFailed or RecommendationRuntimeEventKind.ToolFailed
                or RecommendationRuntimeEventKind.RunFailed => VitrineEventDisposition.Failed,
            _ => VitrineEventDisposition.Neutral,
        };
        var operationId = item.Kind is RecommendationRuntimeEventKind.RunStarted
            or RecommendationRuntimeEventKind.RunCompleted
            or RecommendationRuntimeEventKind.RunCancelled
            or RecommendationRuntimeEventKind.RunFailed
                ? "demo01:run"
                : item.OperationId;
        return new(category, item.Kind.ToString(), disposition, item.Source, item.Target,
            item.Title, item.Detail, operationId, item.PayloadPreview, item.TimestampUtc);
    }

    public static VitrineEventDraft FromDiscovery(DiscoveryEvent item)
    {
        var category = item.Kind switch
        {
            DiscoveryEventKind.ModelRequestStarted or DiscoveryEventKind.ModelResponseReceived
                or DiscoveryEventKind.ModelRequestCancelled or DiscoveryEventKind.ModelRequestFailed => VitrineEventCategory.Model,
            DiscoveryEventKind.Search or DiscoveryEventKind.RoundStarted or DiscoveryEventKind.RoundComplete => VitrineEventCategory.Retrieval,
            DiscoveryEventKind.PreGate or DiscoveryEventKind.CoverageLedger or DiscoveryEventKind.QueryTermDropped or DiscoveryEventKind.SkuDropped => VitrineEventCategory.Guardrail,
            _ => VitrineEventCategory.Workflow,
        };
        var disposition = item.Kind switch
        {
            DiscoveryEventKind.RunStarted or DiscoveryEventKind.NodeStarted or DiscoveryEventKind.RoundStarted
                or DiscoveryEventKind.ModelRequestStarted => VitrineEventDisposition.Active,
            DiscoveryEventKind.NodeCompleted or DiscoveryEventKind.RoundComplete
                or DiscoveryEventKind.RunComplete or DiscoveryEventKind.Presented
                or DiscoveryEventKind.ModelResponseReceived => VitrineEventDisposition.Succeeded,
            DiscoveryEventKind.NodeFailed or DiscoveryEventKind.ModelRequestFailed => VitrineEventDisposition.Failed,
            DiscoveryEventKind.ModelRequestCancelled => VitrineEventDisposition.Warning,
            DiscoveryEventKind.Degraded => VitrineEventDisposition.Warning,
            DiscoveryEventKind.RunFailed => VitrineEventDisposition.Failed,
            DiscoveryEventKind.PreGate or DiscoveryEventKind.QueryTermDropped or DiscoveryEventKind.SkuDropped => VitrineEventDisposition.Blocked,
            _ => VitrineEventDisposition.Neutral,
        };
        var target = item.Kind switch
        {
            DiscoveryEventKind.Route => RouteTarget(item.NodeId),
            DiscoveryEventKind.ModelRequestStarted => "model",
            DiscoveryEventKind.ModelResponseReceived or DiscoveryEventKind.ModelRequestCancelled
                or DiscoveryEventKind.ModelRequestFailed => item.NodeId,
            _ => string.Empty,
        };
        var source = item.Kind switch
        {
            DiscoveryEventKind.Route => RouteSource(item.NodeId),
            DiscoveryEventKind.ModelResponseReceived or DiscoveryEventKind.ModelRequestCancelled
                or DiscoveryEventKind.ModelRequestFailed => "model",
            _ => item.NodeId,
        };
        var payload = item.Kind == DiscoveryEventKind.NodeCompleted
            ? $"model-calls={Math.Max(0, item.ModelCalls)}"
            : item.Detail is { Count: > 0 } ? string.Join("\n", item.Detail) : null;
        return new(category, item.Kind.ToString(), disposition, source, target,
            item.Message.Length == 0 ? item.Kind.ToString() : item.Message,
            item.Elapsed is null ? $"Round {item.Round}; model calls {Display(item.ModelCalls)}." : $"Elapsed {item.Elapsed.Value.TotalMilliseconds:0} ms; round {item.Round}; model calls {Display(item.ModelCalls)}.",
            OperationId: DiscoveryOperationId(item),
            Payload: payload);
    }

    public static VitrineEventDraft FromEvaluation(EvaluationProgressEvent item)
    {
        var disposition = item.Kind switch
        {
            EvaluationProgressKind.GateCompleted
                when item.Expectation == EvaluationProgressExpectation.CatalogueDefectDetection
                     && item.Gate is { Outcome: GateMeasurementOutcome.Measured, Passed: false }
                => VitrineEventDisposition.ExpectedDefectDetected,
            EvaluationProgressKind.SuiteCompleted
                when item.Expectation == EvaluationProgressExpectation.CatalogueSelfTestSucceeded
                => VitrineEventDisposition.SelfTestSucceeded,
            EvaluationProgressKind.SuiteStarted or EvaluationProgressKind.GateStarted or EvaluationProgressKind.ControlStarted => VitrineEventDisposition.Active,
            EvaluationProgressKind.GateCompleted when item.Gate?.Outcome == GateMeasurementOutcome.InstrumentError => VitrineEventDisposition.Failed,
            EvaluationProgressKind.GateCompleted when item.Gate?.Outcome == GateMeasurementOutcome.NotApplicable => VitrineEventDisposition.NotApplicable,
            EvaluationProgressKind.GateCompleted or EvaluationProgressKind.SuiteCompleted when item.Passed is null => VitrineEventDisposition.NotMeasured,
            EvaluationProgressKind.ControlHealthyCompleted when item.Passed is null => VitrineEventDisposition.NotMeasured,
            EvaluationProgressKind.ControlHealthyCompleted => item.Passed == true ? VitrineEventDisposition.Succeeded : VitrineEventDisposition.Failed,
            EvaluationProgressKind.ControlBrokenCompleted when item.Passed is null => VitrineEventDisposition.NotMeasured,
            EvaluationProgressKind.ControlBrokenCompleted => item.Passed == true ? VitrineEventDisposition.Succeeded : VitrineEventDisposition.Failed,
            EvaluationProgressKind.BenchmarkCheckCompleted => VitrineEventDisposition.Succeeded,
            EvaluationProgressKind.GateCompleted or EvaluationProgressKind.BenchmarkPersisted
                or EvaluationProgressKind.ControlRestoredCompleted
                or EvaluationProgressKind.ControlCompleted or EvaluationProgressKind.SuiteCompleted
                => item.Passed == true ? VitrineEventDisposition.Succeeded : VitrineEventDisposition.Failed,
            _ => VitrineEventDisposition.Neutral,
        };
        var (source, target, operationId) = EvaluationRoute(item);
        var title = item.Expectation switch
        {
            EvaluationProgressExpectation.CatalogueDefectDetection
                when item.Gate is { Outcome: GateMeasurementOutcome.Measured, Passed: false }
                => $"EXPECTED DEFECT DETECTED · expected 99→observed 98 · {item.Name}",
            EvaluationProgressExpectation.CatalogueDefectDetection
                => $"SELF-TEST FAILED · catalogue defect was not detected · {item.Name}",
            EvaluationProgressExpectation.CatalogueSelfTestSucceeded
                => "SELF-TEST SUCCEEDED · expected 99→observed 98 catalogue detection",
            _ => item.Name,
        };
        var detail = item.Expectation switch
        {
            EvaluationProgressExpectation.CatalogueDefectDetection
                when item.Gate is { Outcome: GateMeasurementOutcome.Measured, Passed: false }
                => $"The admitted catalogue gate failed exactly as expected for the isolated planted defect. {item.Detail}",
            EvaluationProgressExpectation.CatalogueDefectDetection
                => $"The planted catalogue defect was not detected. {item.Detail}",
            _ => item.Detail,
        };
        return new(VitrineEventCategory.Evaluation, item.Kind.ToString(), disposition,
            source, target, title, detail, operationId,
            Payload: EvaluationPayload(item),
            TimestampUtc: item.TimestampUtc);
    }

    private static string EvaluationPayload(EvaluationProgressEvent item)
    {
        var payload = new StringBuilder()
            .Append("Stage: ").AppendLine(item.Kind.ToString());
        if (item.Expectation != EvaluationProgressExpectation.None)
            payload.Append("Expected outcome: ").AppendLine(item.Expectation.ToString());
        if (item.Completed.HasValue && item.Total.HasValue)
            payload.Append("Progress: ").Append(item.Completed.Value.ToString(CultureInfo.InvariantCulture))
                .Append('/').AppendLine(item.Total.Value.ToString(CultureInfo.InvariantCulture));
        if (item.Gate is { } gate)
        {
            payload.Append("Measurement: ").AppendLine(gate.Outcome.ToString())
                .Append("Verdict: ").AppendLine(gate.Passed switch
                {
                    true => "PASS",
                    false when item.Expectation == EvaluationProgressExpectation.CatalogueDefectDetection
                        => "FAIL AS EXPECTED · defect detected",
                    false => "FAIL",
                    null => "NOT MEASURED",
                })
                .Append("Observed score: ").AppendLine(gate.Score?.ToString("0.000", CultureInfo.InvariantCulture) ?? "NOT MEASURED")
                .Append("Cause / evidence: ").AppendLine(gate.Evidence);
            if (gate.AgentEvalMeasurementState is { } state)
                payload.Append("AgentEval census state: ").AppendLine(state.ToString());
            if (gate.AgentEval is { } provenance)
            {
                payload.Append("Observation producer: ").AppendLine(provenance.ObservationProducer)
                    .Append("Acceptance evaluator: ").AppendLine(provenance.AcceptanceEvaluator);
            }
        }
        else if (item.Control is { } control)
        {
            payload.Append("Control outcome: ").AppendLine(control.Caught ? "DETECTED AND RECOVERED" : "NOT CAUGHT")
                .Append("Baseline healthy: ").AppendLine(control.HealthyOutcome.ToString())
                .Append("Defect-injected: ").AppendLine(control.BrokenOutcome.ToString())
                .Append("Recovery: ").AppendLine(control.RestoredOutcome.ToString())
                .Append("Cause / evidence: ").AppendLine(control.Evidence);
        }
        else
        {
            payload.Append("Outcome / evidence: ").AppendLine(item.Detail);
        }
        return payload.ToString().TrimEnd();
    }

    private static (string Source, string Target, string? OperationId) EvaluationRoute(
        EvaluationProgressEvent item) => item.Kind switch
    {
        EvaluationProgressKind.SuiteStarted => ("evaluation-plan", string.Empty, "offline:suite"),
        EvaluationProgressKind.GateStarted => GateStartRoute(item.Id),
        EvaluationProgressKind.GateCompleted => (item.Id, string.Empty, $"offline:gate:{item.Id}"),
        EvaluationProgressKind.BenchmarkCheckCompleted => BenchmarkCheckRoute(item.Id),
        EvaluationProgressKind.BenchmarkPersisted =>
            (VitrineOfflineBenchmark.InterestGroundingCheckKey, "benchmark", null),
        EvaluationProgressKind.ControlStarted => (string.Empty, "controls", $"offline:control:{item.Id}"),
        EvaluationProgressKind.ControlHealthyCompleted or EvaluationProgressKind.ControlBrokenCompleted
            or EvaluationProgressKind.ControlRestoredCompleted
            => ("controls", string.Empty, null),
        EvaluationProgressKind.ControlCompleted
            => ("controls", string.Empty, $"offline:control:{item.Id}"),
        EvaluationProgressKind.SuiteCompleted =>
            (item.IncludesDiagnosticControls ? "controls" : "benchmark", "suite", "offline:suite"),
        _ => (item.Id, string.Empty, null),
    };

    private static (string Source, string Target, string? OperationId) LiveEvaluationRoute(
        LiveEvalProgress item)
    {
        var armNode = item.ArmId switch
        {
            "vitrine-live-agent" => "live-agent",
            "vitrine-live-workflow" => "live-workflow",
            _ => item.ArmId?.Contains("workflow", StringComparison.OrdinalIgnoreCase) == true
                ? "live-workflow"
                : "live-agent",
        };
        var trialIdentity = item.ArmId is null || item.ScenarioId is null || item.Repetition is null
            ? null
            : $"{item.ArmId}:{item.ScenarioId}:{item.Repetition.Value.ToString(CultureInfo.InvariantCulture)}";
        var trialOperation = trialIdentity is null ? null : $"live:trial:{trialIdentity}";
        var subjectOperation = trialIdentity is null ? null : $"live:subject:{trialIdentity}";
        var checkOperation = trialIdentity is null || item.CheckKey is null
            ? trialOperation
            : $"live:check:{trialIdentity}:{item.CheckKey}";
        return item.Phase switch
        {
            LiveEvalProgressPhase.SessionStarting => (string.Empty, "live-session", "live:session"),
            LiveEvalProgressPhase.TrialStarting => ("live-session", armNode, trialOperation),
            LiveEvalProgressPhase.SubjectRunning => (string.Empty, armNode, subjectOperation),
            LiveEvalProgressPhase.SubjectCompleted => (armNode, string.Empty, subjectOperation),
            LiveEvalProgressPhase.CheckStarting => (armNode,
                LiveCheckNode(item.Plan, item.CheckKey, armNode), checkOperation),
            LiveEvalProgressPhase.CheckCompleted => (LiveCheckNode(item.Plan, item.CheckKey, armNode), string.Empty, checkOperation),
            LiveEvalProgressPhase.TrialCompleted => (armNode, string.Empty, trialOperation),
            LiveEvalProgressPhase.SafetyTargetRunning =>
                ("live-session", "live-safety-target", "live:safety-target"),
            LiveEvalProgressPhase.SafetyFindingsCompleted =>
                (string.Empty, "live-safety-findings", "live:safety-findings"),
            LiveEvalProgressPhase.Persisting => (string.Empty, "live-persistence", LivePersistenceOperationId),
            LiveEvalProgressPhase.SessionCompleted => ("live-persistence", "live-complete", "live:session"),
            _ => (string.Empty, string.Empty, null),
        };
    }

    private static string LiveCheckNode(VitrineEvaluationPlan plan, string? checkKey, string armNode)
    {
        var node = checkKey switch
        {
            LiveUseCaseBenchmark.UseCaseQualityCheckKey => LiveUseCaseBenchmark.UseCaseQualityCheckKey,
            LiveUseCaseBenchmark.ResponseObservedCheckKey => LiveUseCaseBenchmark.ResponseObservedCheckKey,
            LiveUseCaseBenchmark.AgentToolJournalCheckKey => LiveUseCaseBenchmark.AgentToolJournalCheckKey,
            LiveUseCaseBenchmark.WorkflowTraceCheckKey => LiveUseCaseBenchmark.WorkflowTraceCheckKey,
            _ => checkKey ?? "live-session",
        };
        return plan == VitrineEvaluationPlan.LiveEval03AgentVsWorkflow
            ? $"{armNode}:{node}"
            : node;
    }

    private static string LivePhaseLabel(LiveEvalProgressPhase phase) => phase switch
    {
        LiveEvalProgressPhase.SessionStarting => "session starting",
        LiveEvalProgressPhase.TrialStarting => "scenario starting",
        LiveEvalProgressPhase.SubjectRunning => "paid subject running",
        LiveEvalProgressPhase.SubjectCompleted => "subject completed",
        LiveEvalProgressPhase.CheckStarting => "check starting",
        LiveEvalProgressPhase.CheckCompleted => "check completed",
        LiveEvalProgressPhase.TrialCompleted => "scenario completed",
        LiveEvalProgressPhase.SafetyTargetRunning => "bounded safety target running",
        LiveEvalProgressPhase.SafetyFindingsCompleted => "redacted safety findings completed",
        LiveEvalProgressPhase.Persisting => "saving local evidence",
        LiveEvalProgressPhase.SessionCompleted => "session completed",
        _ => phase.ToString(),
    };

    private static (string Source, string Target, string? OperationId) BenchmarkCheckRoute(string id) => id switch
    {
        VitrineOfflineBenchmark.ScreenedDeliverableCheckKey => ("honesty", id, null),
        VitrineOfflineBenchmark.CataloguedSkuCheckKey =>
            (VitrineOfflineBenchmark.ScreenedDeliverableCheckKey, id, null),
        VitrineOfflineBenchmark.CustomerReasonCheckKey =>
            (VitrineOfflineBenchmark.CataloguedSkuCheckKey, id, null),
        VitrineOfflineBenchmark.NoPurchaseClaimCheckKey =>
            (VitrineOfflineBenchmark.CustomerReasonCheckKey, id, null),
        VitrineOfflineBenchmark.InterestGroundingCheckKey =>
            (VitrineOfflineBenchmark.NoPurchaseClaimCheckKey, id, null),
        _ => (id, string.Empty, null),
    };

    private static (string Source, string Target, string? OperationId) GateStartRoute(string id) => id switch
    {
        "catalogue" => (string.Empty, "catalogue", $"offline:gate:{id}"),
        "topology" => ("catalogue", "topology", $"offline:gate:{id}"),
        "judged" => ("topology", "judged", $"offline:gate:{id}"),
        "injection" => ("judged", "injection", $"offline:gate:{id}"),
        "recall" => ("injection", "recall", $"offline:gate:{id}"),
        "honesty" => ("recall", "honesty", $"offline:gate:{id}"),
        "controls" => ("benchmark", "controls", $"offline:gate:{id}"),
        _ => (string.Empty, id, $"offline:gate:{id}"),
    };

    private static string? DiscoveryOperationId(DiscoveryEvent item) => item.Kind switch
    {
        DiscoveryEventKind.RunStarted or DiscoveryEventKind.RunComplete or DiscoveryEventKind.RunFailed
            => "demo02:run",
        DiscoveryEventKind.RoundStarted or DiscoveryEventKind.RoundComplete when item.Round > 0
            => $"round:{item.Round.ToString(CultureInfo.InvariantCulture)}",
        DiscoveryEventKind.Route => item.NodeId,
        _ => item.OperationId,
    };

    private static string Display(int value) => value < 0 ? "not applicable" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string RouteTarget(string routeId) => routeId switch
    {
        var id when id == DiscoveryRouteIds.MapToDiscovery => DiscoveryExecutorIds.Discovery,
        var id when id == DiscoveryRouteIds.DiscoveryToReview => DiscoveryExecutorIds.CoverageReviewer,
        var id when id == DiscoveryRouteIds.ReviewToMoreDiscovery => DiscoveryExecutorIds.Discovery,
        var id when id == DiscoveryRouteIds.ReviewToRanker => DiscoveryExecutorIds.Ranker,
        var id when id == DiscoveryRouteIds.RankerToPresenter => DiscoveryExecutorIds.Presenter,
        _ => string.Empty,
    };

    private static string RouteSource(string routeId) => routeId switch
    {
        var id when id == DiscoveryRouteIds.MapToDiscovery => DiscoveryExecutorIds.InterestMapper,
        var id when id == DiscoveryRouteIds.DiscoveryToReview => DiscoveryExecutorIds.Discovery,
        var id when id == DiscoveryRouteIds.ReviewToMoreDiscovery || id == DiscoveryRouteIds.ReviewToRanker => DiscoveryExecutorIds.CoverageReviewer,
        var id when id == DiscoveryRouteIds.RankerToPresenter => DiscoveryExecutorIds.Ranker,
        _ => routeId,
    };
}
