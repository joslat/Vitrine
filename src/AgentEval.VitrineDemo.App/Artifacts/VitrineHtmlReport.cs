// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using System.Text;
using AgentEval.VitrineDemo.App.Controls;
using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals.Live;

namespace AgentEval.VitrineDemo.App.Artifacts;

public static class VitrineHtmlReport
{
    public static string Render(VitrineRunArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        artifact = VitrineArtifactSerializer.Freeze(artifact);
        if (!VitrineArtifactSerializer.Verify(artifact)) throw new InvalidDataException("Artifact integrity verification failed.");
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
            .Append("<title>VITRINE evidence report</title><style>")
            .Append("body{margin:0;background:#08111f;color:#eaf1fb;font:14px Inter,Segoe UI,sans-serif}main{max-width:1280px;margin:auto;padding:32px}h1{margin:.2rem 0;font-size:30px}.muted{color:#91a0b8}.card{background:#111827;border:1px solid #25324a;border-radius:12px;padding:16px;margin:14px 0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(250px,1fr));gap:12px}.pill{display:inline-block;padding:5px 8px;border:1px solid #327d73;border-radius:6px;color:#5ae4d2}.metric{font-size:22px;font-weight:700}.floor{color:#f6c55c}table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:8px;border-bottom:1px solid #263650;vertical-align:top}th{color:#7f91ae;font-size:11px}code{color:#9fc5ff}pre{white-space:pre-wrap;overflow-wrap:anywhere}details{margin-top:8px}summary{cursor:pointer;color:#9fc5ff}.payload{max-height:360px;overflow:auto;padding:10px;background:#08111f;border:1px solid #263650;border-radius:7px}svg{width:100%;min-height:210px;background:#091321;border-radius:9px}.node{fill:#101d30;stroke:#5ae4d2;stroke-width:1.5}.edge{stroke:#405675;stroke-width:1.5}.loop{stroke:#b77cff}.label{fill:#eef4fd;font-size:11px}.trace{fill:#91a0b8;font-size:9px}.event{border-left:3px solid #47709e;padding:8px 12px;margin:6px 0;background:#0d1828}.not{color:#f6c55c}.error{color:#f07076}</style></head><body><main>");
        var executionLabel = artifact.Result.LiveEvaluation is not null
            ? "PAID LIVE EVAL"
            : artifact.ExecutionArm switch
        {
            "LiveAzure" => "LIVE AZURE",
            _ => "OFFLINE",
        };
        html.Append("<span class=\"pill\">SANITIZED · ").Append(executionLabel).Append(" ARTIFACT</span><h1>VITRINE evidence report</h1>")
            .Append("<p class=\"muted\">No hidden chain-of-thought. No API key or endpoint URL. Missing measurements remain missing.</p>")
            .Append("<section class=\"card grid\">")
            .Append(Metric("Mode", ModeLabel(artifact.Mode)))
            .Append(Metric("Persona scope", PersonaScopeLabel(artifact)))
            .Append(Metric("Personalization", PersonalizationLabel(artifact)))
            .Append(Metric("Arm", artifact.ExecutionArm))
            .Append(Metric("Status", artifact.Result.Status))
            .Append(Metric("Failure", Missing(artifact.Result.FailureKind)))
            .Append(Metric("Exit", Number(artifact.Result.ProcessEquivalentExitCode)))
            .Append(Metric("Model calls", Number(artifact.Result.ModelCalls)))
            .Append(Metric("Tool calls", Number(artifact.Result.ToolCalls)))
            .Append(Metric("Presented", Number(artifact.Result.Presented)))
            .Append(Metric("Survived", Number(artifact.Result.Survived)))
            .Append(Metric("Workflow looped", artifact.Result.WorkflowLooped.HasValue
                ? (artifact.Result.WorkflowLooped.Value ? "yes" : "no")
                : "NOT MEASURED"))
            .Append(Metric("Workflow super-steps", Number(artifact.Result.WorkflowSuperSteps)))
            .Append(Metric("Workflow stop", Measurement(artifact.Result.WorkflowStopReason)))
            .Append(Metric("Events", artifact.Events.Count.ToString(CultureInfo.InvariantCulture)))
            .Append("</section>");
        html.Append("<section class=\"card\"><h2>").Append(H(artifact.Graph.Name)).Append("</h2>")
            .Append("<p class=\"muted\">Topology source: ").Append(H(artifact.Graph.Source.ToString())).Append("</p>")
            .Append(RenderGraph(artifact)).Append("</section>");

        RenderRunOutcome(html, artifact.Result);
        RenderEvaluationExecution(html, artifact.Result.EvaluationExecution);
        RenderOfflineBenchmark(html, artifact.Result.OfflineBenchmark);
        RenderLiveEvaluation(html, artifact.Result.LiveEvaluation);

        if (artifact.Result.Gates.Count > 0)
        {
            html.Append("<section class=\"card\"><h2>Mandatory gates and diagnostic evaluations</h2><p class=\"muted\">Mandatory gates control the process-equivalent exit. Diagnostic rows are visible evidence but cannot fail the suite. The null/chance baseline is descriptive comparison evidence, not a pass threshold.</p><table><thead><tr><th>Evaluation</th><th>Status</th><th>Authority</th><th>Score</th><th>Null / chance baseline</th><th>Evidence</th><th>AgentEval provenance</th></tr></thead><tbody>");
            foreach (var gate in artifact.Result.Gates)
                html.Append("<tr><td>").Append(H(gate.Name)).Append("</td><td>").Append(H(GateStatus(gate)))
                    .Append("</td><td>").Append(H(gate.EffectiveAuthority.ToString()))
                    .Append("</td><td>").Append(H(Number(gate.Score))).Append("</td><td class=\"floor\">")
                    .Append(H(Floor(gate.ChanceFloor))).Append("</td><td>")
                    .Append(H(gate.Evidence)).Append("</td><td>")
                    .Append(RenderAgentEval(gate.AgentEval)).Append("</td></tr>");
            html.Append("</tbody></table></section>");
        }

        if (artifact.Result.Controls.Count > 0)
        {
            html.Append("<section class=\"card\"><h2>Registered diagnostic controls</h2><p class=\"muted\">These mutation diagnostics are not admitted evals and carry no chance floor. Each row names its scope, target, producer, evaluator, and tranche; the defect-injected arm is expected to fail.</p><table><thead><tr><th>ID</th><th>Control</th><th>Scope</th><th>Target</th><th>Observation producer</th><th>Evaluator</th><th>Tranche</th><th>Baseline healthy</th><th>Defect-injected (detection expected)</th><th>Recovery</th><th>Evidence</th></tr></thead><tbody>");
            foreach (var control in artifact.Result.Controls)
                html.Append("<tr><td>").Append(H(control.Id)).Append("</td><td>").Append(H(control.Name))
                    .Append("</td><td>").Append(H(control.ScopeClass.ToString()))
                    .Append("</td><td>").Append(H(control.Target))
                    .Append("</td><td>").Append(H(control.ObservationProducer))
                    .Append("</td><td>").Append(H(control.Evaluator))
                    .Append("</td><td>").Append(H(control.Tranche))
                    .Append("</td><td>").Append(H(HealthyOutcome(control.HealthyOutcome)))
                    .Append("</td><td>").Append(H(ControlOutcome(control.BrokenOutcome, broken: true)))
                    .Append("</td><td>").Append(H(ControlOutcome(control.RestoredOutcome, broken: false)))
                    .Append("</td><td>").Append(H(control.Evidence)).Append("</td></tr>");
            html.Append("</tbody></table></section>");
        }

        var timeline = new TimelineViewModel();
        foreach (var item in artifact.Events) timeline.Add(item);
        html.Append("<section class=\"card\"><h2>Event timeline</h2>");
        foreach (var card in timeline.Events)
        {
            var item = card.Event;
            html.Append("<article class=\"event\"><code>#").Append(item.Sequence).Append(" · ").Append(H(item.Category.ToString()))
                .Append(" · ").Append(H(item.Kind)).Append(" · ").Append(H(card.Disposition))
                .Append(" · +").Append(item.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)).Append("s</code><h3>")
                .Append(H(item.Title)).Append("</h3><p>").Append(H(item.Detail)).Append("</p><p class=\"muted\">")
                .Append(H(Route(item))).Append(string.IsNullOrWhiteSpace(item.OperationId)
                    ? string.Empty
                    : $" · operation {H(item.OperationId)}")
                .Append("</p>")
                .Append(string.IsNullOrWhiteSpace(item.SanitizedPayload)
                    ? $"<p class=\"muted\">{H(card.PayloadAbsenceExplanation)}</p>"
                    : $"<details><summary>{H(PayloadLabel(item))}</summary><pre class=\"payload\">{H(item.SanitizedPayload)}</pre></details>")
                .Append(card.HasPayloadAbsenceExplanation && !string.IsNullOrWhiteSpace(item.SanitizedPayload)
                    ? $"<p class=\"muted\">{H(card.PayloadAbsenceExplanation)}</p>"
                    : string.Empty)
                .Append("</article>");
        }
        html.Append("</section><p class=\"muted\">Integrity SHA-256 over sanitized fields: <code>")
            .Append(H(artifact.IntegritySha256)).Append("</code></p></main></body></html>");
        return html.ToString();
    }

    private static string Route(VitrineEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.SourceId) && string.IsNullOrWhiteSpace(item.TargetId))
            return "run-level event";
        if (string.IsNullOrWhiteSpace(item.TargetId)) return item.SourceId;
        if (string.IsNullOrWhiteSpace(item.SourceId)) return item.TargetId;
        return $"{item.SourceId} → {item.TargetId}";
    }

    private static string PayloadLabel(VitrineEvent item) => item.Kind switch
    {
        "ModelRequestStarted" => "Show sanitized model input",
        "ModelResponseReceived" => "Show sanitized model output",
        "ToolExecutionStarted" => "Show sanitized tool parameters",
        "ToolCompleted" => "Show sanitized tool response",
        _ when item.Category == VitrineEventCategory.Evaluation => "Show evaluation evidence",
        _ => "Show sanitized event payload",
    };

    private static string ModeLabel(VitrineRunMode mode) => mode == VitrineRunMode.Ablation
        ? "Catalogue integrity self-test"
        : mode.ToString();

    private static string PersonaScopeLabel(VitrineRunArtifact artifact) => artifact.PersonaId switch
    {
        VitrineRunRequest.MultiplePersonasScope => "MULTIPLE PERSONAS",
        VitrineRunRequest.NotApplicablePersonaScope => "NOT APPLICABLE",
        _ => artifact.PersonaId,
    };

    private static string PersonalizationLabel(VitrineRunArtifact artifact) =>
        artifact.Mode is VitrineRunMode.Evals or VitrineRunMode.Ablation
            ? "NOT APPLICABLE · evaluation-defined"
            : artifact.PersonalizationDisabled switch
            {
                true => "disabled",
                false => "enabled",
                null => "NOT APPLICABLE",
            };

    private static void RenderRunOutcome(StringBuilder html, VitrineResultSnapshot result)
    {
        if (result.Demo01 is { } demo01)
        {
            html.Append("<section class=\"card\"><h2>Screened Demo01 outcome</h2><pre>")
                .Append(H(demo01.CustomerFacingAnswer)).Append("</pre>")
                .Append("<p class=\"muted\">Retriever: ").Append(H(Measurement(demo01.Retriever)))
                .Append(" · Budget: ").Append(H(Measurement(demo01.BudgetSummary))).Append("</p>");
            RenderStringList(html, $"Registered tools ({demo01.RegisteredTools.Count.ToString(CultureInfo.InvariantCulture)})",
                demo01.RegisteredTools);
            RenderRecommendations(html, demo01.Recommendations);
            RenderLedger(html, demo01.Ledger);
            html.Append("</section>");
        }

        if (result.Demo02 is { } demo02)
        {
            html.Append("<section class=\"card\"><h2>Screened Demo02 workflow outcome</h2><pre>")
                .Append(H(demo02.CustomerFacingAnswer)).Append("</pre><div class=\"grid\">")
                .Append(Metric("Coverage", demo02.CoverageApproved ? "approved" : "not approved"))
                .Append(Metric("Partial answer", demo02.PartialAnswer ? "yes" : "no"))
                .Append(Metric("Discovery rounds", $"{demo02.DiscoveryRounds}/{demo02.MaxRounds}"))
                .Append(Metric("Searches", demo02.SearchesRun.ToString(CultureInfo.InvariantCulture)))
                .Append(Metric("Model calls", demo02.ModelCalls.ToString(CultureInfo.InvariantCulture)))
                .Append(Metric("Selection", demo02.SelectionWasDeterministic ? "deterministic" : "model-selected"))
                .Append("</div><h3>Routes actually taken</h3><p>")
                .Append(H(demo02.RoutesTaken.Count == 0 ? "—" : string.Join(" → ", demo02.RoutesTaken))).Append("</p>");
            RenderStringList(html, $"Executors ({demo02.ExecutorIds.Count.ToString(CultureInfo.InvariantCulture)})",
                demo02.ExecutorIds);
            RenderRecommendations(html, demo02.Recommendations);
            RenderStringList(html, "Interests", demo02.Interests.Select(i =>
                $"{i.Id} · {i.Label} · {i.Kind} · {i.Origin} · {i.Confidence.ToString("0.000", CultureInfo.InvariantCulture)}"));
            RenderStringList(html, "Open coverage gaps", demo02.OpenGaps);
            RenderStringList(html, "Dropped SKUs", demo02.DroppedSkus);
            RenderStringList(html, "Degradations", demo02.DegradedNotes);
            RenderStringList(html, "Executor failures", demo02.ExecutorFailures);
            RenderLedger(html, demo02.Ledger);
            html.Append("</section>");
        }
    }

    private static void RenderEvaluationExecution(
        StringBuilder html,
        VitrineEvaluationExecutionSnapshot? execution)
    {
        if (execution is null) return;
        html.Append("<section class=\"card\"><h2>Evaluation execution profile</h2>")
            .Append("<p class=\"muted\">Typed SuiteResult provenance. Calls, tokens, and cost stay NOT MEASURED when their provider does not report them.</p>")
            .Append("<div class=\"grid\">")
            .Append(Metric("Profile", execution.Profile.ToString()))
            .Append(Metric("Demo scope", execution.DemoScope))
            .Append(Metric("Subject engine", execution.SubjectEngine))
            .Append(Metric("Evaluator engine", execution.EvaluatorEngine))
            .Append(Metric("External models", execution.UsesExternalModels ? "yes" : "no"))
            .Append(Metric("Deployment", Measurement(execution.DeploymentName)))
            .Append(Metric("Demo01 subject calls", Number(execution.Demo01SubjectModelCalls)))
            .Append(Metric("Demo02 subject calls", Number(execution.Demo02SubjectModelCalls)))
            .Append(Metric("Judge calls", Number(execution.JudgeModelCalls)))
            .Append(Metric("Total model calls", Number(execution.TotalModelCalls)))
            .Append(Metric("Demo01 subject tokens", Number(execution.Demo01SubjectTokens)))
            .Append(Metric("Demo02 subject tokens", Number(execution.Demo02SubjectTokens)))
            .Append(Metric("Judge tokens", Number(execution.JudgeTokens)))
            .Append(Metric("Estimated cost USD", Number(execution.EstimatedCostUsd)))
            .Append("</div></section>");
    }

    private static void RenderLiveEvaluation(
        StringBuilder html,
        VitrineLiveEvaluationSnapshot? live)
    {
        if (live is null) return;
        var isSafety = string.Equals(live.Plan, "LiveEval06SafetyProbes", StringComparison.Ordinal);
        html.Append(isSafety
                ? "<section class=\"card\"><h2>Paid live safety evaluation</h2>"
                : "<section class=\"card\"><h2>Paid live use-case evaluation</h2>")
            .Append("<p class=\"muted\">These are evaluator-owned LiveEvalResult facts. This report does not average checks, re-score trials, or derive a winner. Provider failure cannot count as measured; bounded internal fallbacks are disclosed.</p>")
            .Append("<div class=\"grid\">")
            .Append(Metric("Plan", $"{live.PlanLabel} · {live.Plan}"))
            .Append(Metric("Terminal status", $"{live.TerminalStatus} · exit {live.ExitCode}"))
            .Append(Metric(isSafety ? "Quality pass bar" : "Quality pass bar (not a chance floor)",
                isSafety
                    ? "NOT APPLICABLE · safety uses compromise/resistance census"
                    : Number(live.PassThreshold)))
            .Append(Metric("Session", live.SessionId))
            .Append(Metric(isSafety ? "Safety attack categories" : "Scenarios",
                (isSafety ? live.Workload.SafetyAttackCount : live.Workload.ScenarioCount)
                    .ToString(CultureInfo.InvariantCulture)))
            .Append(Metric("Arms", live.Workload.ArmCount.ToString(CultureInfo.InvariantCulture)))
            .Append(Metric("Repetitions", live.Workload.Repetitions.ToString(CultureInfo.InvariantCulture)))
            .Append(Metric("Planned subject calls", live.Workload.PlannedSubjectCalls.ToString(CultureInfo.InvariantCulture)))
            .Append(Metric("Planned judge evaluations", live.Workload.PlannedJudgeEvaluations.ToString(CultureInfo.InvariantCulture)))
            .Append(isSafety ? Metric("Bounded probes / maximum safety model calls",
                $"{live.Workload.PlannedSafetyProbes} / {live.Workload.MaximumSafetyModelCalls}") : string.Empty)
            .Append(Metric("Recorded trials", live.Trials.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Metric("Physical AgentEval runs", live.Runs.Count.ToString(CultureInfo.InvariantCulture)))
            .Append("</div><p>").Append(H(live.PlanDescription)).Append("</p>")
            .Append("<p><span class=\"muted\">Started:</span> ").Append(H(live.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)))
            .Append("<br><span class=\"muted\">Completed:</span> ").Append(H(live.CompletedAtUtc.ToString("O", CultureInfo.InvariantCulture)))
            .Append("<br><span class=\"muted\">Workspace:</span> <code>").Append(H(live.Persistence.WorkspaceRoot))
            .Append("</code><br><span class=\"muted\">Session directory:</span> <code>").Append(H(live.Persistence.SessionDirectory))
            .Append("</code><br><span class=\"muted\">Outcome:</span> <code>").Append(H(live.Persistence.OutcomePath))
            .Append("</code><br><span class=\"muted\">Index:</span> <code>").Append(H(live.Persistence.IndexPath))
            .Append("</code></p>");

        RenderLiveConfiguration(html, live.Configuration);
        if (live.Failures.Count > 0)
            RenderStringList(html, "Typed failures (never converted to score zero)", live.Failures.Select(failure =>
                $"{failure.Code} · {failure.Detail} · scenario {Measurement(failure.ScenarioId)} · " +
                $"arm {Measurement(failure.ArmId)} · rep {Number(failure.Repetition)} · check {Measurement(failure.CheckKey)}"));

        if (isSafety)
        {
            RenderLiveSafety(html, live.Safety, live.TerminalStatus);
            html.Append("</section>");
            return;
        }

        if (live.Scenarios.Count > 0)
        {
            html.Append("<h3>Authored scenarios and exact queries</h3>");
            foreach (var scenario in live.Scenarios)
            {
                html.Append("<details><summary><code>").Append(H(scenario.Id)).Append("</code> · ")
                    .Append(H(scenario.PersonaId)).Append(" · ").Append(H(scenario.Title))
                    .Append("</summary><p>").Append(H(scenario.Description))
                    .Append("</p><p><strong>Exact query</strong></p><pre class=\"payload\">")
                    .Append(H(scenario.Query)).Append("</pre><p><strong>Expected behavior</strong><br>")
                    .Append(H(scenario.ExpectedBehavior)).Append("</p>");
                RenderStringList(html, "Registered criteria", scenario.Criteria.Select(criterion =>
                    $"{criterion.Id} · {criterion.Text}"));
                RenderStringList(html, "Ground-truth facts", scenario.GroundTruthFacts);
                if (scenario.AgentToolExpectation is { } tools)
                {
                    html.Append("<h4>Agent tool and presented-SKU contract</h4><p>")
                        .Append(H($"Abstention required: {tools.RequiresAbstention}; required tools: " +
                            $"{(tools.RequiredTools.Count == 0 ? "none" : string.Join(", ", tools.RequiredTools))}; " +
                            $"forbidden tools: {(tools.ForbiddenTools.Count == 0 ? "none" : string.Join(", ", tools.ForbiddenTools))}; " +
                            $"forbidden presented SKUs: {(tools.ForbiddenPresentedSkus.Count == 0 ? "none" : string.Join(", ", tools.ForbiddenPresentedSkus))}."))
                        .Append("</p>");
                }
                html.Append("</details>");
            }
        }

        if (live.Runs.Count > 0)
            RenderStringList(html, "Physical AgentEval arm × repetition runs", live.Runs.Select(run =>
                $"{run.ArmId} · rep {run.Repetition} · {run.RunId} · {run.RelativeDirectory}"));

        html.Append("<h3>Sanitized subject trials</h3>");
        if (live.Trials.Count == 0)
            html.Append("<p class=\"not\">No trial was measured. Inspect terminal status and persistence paths.</p>");
        foreach (var trial in live.Trials)
        {
            html.Append("<details><summary><code>").Append(H(trial.ScenarioId)).Append("</code> · ")
                .Append(H(trial.ArmId)).Append(" · rep ").Append(trial.Repetition)
                .Append(" · ").Append(H(trial.Measurement)).Append(" · pass ").Append(H(Bool(trial.Passed)))
                .Append("</summary><div class=\"grid\">")
                .Append(Metric("Persona", trial.PersonaId))
                .Append(Metric("Architecture", trial.Architecture))
                .Append(Metric("Subject status", trial.SubjectStatus))
                .Append(Metric("Tool journal", trial.Tools.JournalObserved ? "observed" : "not observed"))
                .Append(Metric("Tool executions", $"{trial.Tools.Executed} started · {trial.Tools.Completed} completed · {trial.Tools.Failed} failed · {trial.Tools.Cancelled} cancelled"))
                .Append(Metric("Unknown tool names", trial.Tools.UnknownNameCount.ToString(CultureInfo.InvariantCulture)))
                .Append(Metric("Unreconciled tool calls", trial.Tools.UnreconciledCount.ToString(CultureInfo.InvariantCulture)))
                .Append("</div><h4>Sanitized model response preview</h4><pre class=\"payload\">")
                .Append(H(Measurement(trial.ResponsePreview))).Append("</pre>");
            RenderStringList(html, "Observed tool names", trial.Tools.ToolNames);
            if (trial.Tools.Calls.Count > 0)
                RenderStringList(html, "Operation-correlated tool calls", trial.Tools.Calls.Select(call =>
                    $"{call.OperationId} · {call.ToolName} · {call.Status} · parameters " +
                    (call.Arguments.Count == 0 ? "none" : string.Join(", ", call.Arguments.Select(argument =>
                        $"{argument.Name}={argument.Value}")))));
            if (trial.Failure is { } trialFailure)
                html.Append("<h4>Typed trial failure</h4><p class=\"not\">")
                    .Append(H($"{trialFailure.Code} · {trialFailure.Detail}")).Append("</p>");
            if (trial.Workflow is { } workflow)
            {
                html.Append("<h4>Workflow evidence</h4><div class=\"grid\">")
                    .Append(Metric("Rounds", $"{workflow.DiscoveryRounds}/{workflow.MaximumRounds}"))
                    .Append(Metric("Super-steps", workflow.SuperSteps.ToString(CultureInfo.InvariantCulture)))
                    .Append(Metric("Looped", workflow.Looped ? "yes" : "no"))
                    .Append(Metric("Stop reason", workflow.StopReason))
                    .Append(Metric("Failures", workflow.FailureCount.ToString(CultureInfo.InvariantCulture)))
                    .Append(Metric("Bounded fallback degradations", workflow.DegradationCount.ToString(CultureInfo.InvariantCulture)))
                    .Append(Metric("Fallback kinds", workflow.DegradationKinds.Count == 0
                        ? "none disclosed"
                        : string.Join(", ", workflow.DegradationKinds)))
                    .Append(Metric("Unknown executors/routes", $"{workflow.UnknownExecutorCount}/{workflow.UnknownRouteCount}"))
                    .Append("</div>");
                RenderStringList(html, "Executor counts", workflow.Executors.Select(executor =>
                    $"{executor.ExecutorId} × {executor.ExecutionCount}"));
                RenderStringList(html, "Routes observed", workflow.Routes);
            }
            RenderLiveUsage(html, "Subject usage", trial.SubjectUsage);
            RenderLiveUsage(html, "Judge usage", trial.JudgeUsage);
            if (trial.Checks.Count > 0)
            {
                html.Append("<h4>Admitted check facts</h4><table><thead><tr><th>Check</th><th>Measurement</th><th>Score</th><th>Evaluator verdict</th></tr></thead><tbody>");
                foreach (var check in trial.Checks)
                    html.Append("<tr><td><code>").Append(H(check.Key)).Append("</code><br>").Append(H(check.Name))
                        .Append("</td><td>").Append(H(check.Measurement))
                        .Append("</td><td>").Append(H(Number(check.Score)))
                        .Append("</td><td>").Append(H(Bool(check.Passed))).Append("</td></tr>");
                html.Append("</tbody></table>");
            }
            if (trial.Criteria.Count > 0)
            {
                html.Append("<h4>Use-case criterion verdicts</h4><table><thead><tr><th>Criterion</th><th>Measurement</th><th>Met</th><th>Judge explanation</th></tr></thead><tbody>");
                foreach (var criterion in trial.Criteria)
                    html.Append("<tr><td><code>").Append(H(criterion.Id)).Append("</code></td><td>")
                        .Append(H(criterion.Measurement)).Append("</td><td>").Append(H(Bool(criterion.Met)))
                        .Append("</td><td>").Append(H(Measurement(criterion.Explanation))).Append("</td></tr>");
                html.Append("</tbody></table>");
            }
            html.Append("</details>");
        }

        if (live.ScenarioAcceptances is { Count: > 0 } acceptances)
        {
            html.Append("<h3>Terminal per-scenario Wilson decisions</h3>")
                .Append("<p class=\"muted\">These per-arm, per-scenario decisions are the exact stochastic acceptance facts used for the terminal verdict. Every planned trial must be fully measured. A whole-trial success means its three required checks passed: use-case quality, response observed, and the arm-specific agent tool journal or workflow trace. Each scenario independently requires its 95% Wilson lower bound to clear 0.500. Pooled per-check summaries below are diagnostic and do not replace this policy.</p>")
                .Append("<table><thead><tr><th>Scenario · arm</th><th>Architecture</th><th>Measurement census</th><th>Whole-trial successes</th><th>Wilson interval</th><th>Policy</th><th>Decision</th></tr></thead><tbody>");
            foreach (var decision in acceptances)
                html.Append("<tr><td><code>").Append(H(decision.ScenarioId)).Append("</code><br>")
                    .Append(H($"{decision.PersonaId} · {decision.ArmId}"))
                    .Append("</td><td>").Append(H(decision.Architecture))
                    .Append("</td><td>").Append(H($"measured {decision.Census.Measured}; N/A {decision.Census.NotApplicable}; not measured {decision.Census.NotMeasured}; total {decision.Census.Total}"))
                    .Append("</td><td>").Append(H($"{decision.Reliability.Successes}/{decision.Reliability.Total} · {decision.Reliability.Measurement}"))
                    .Append("</td><td>").Append(H(decision.Reliability.Lower.HasValue && decision.Reliability.Upper.HasValue
                        ? $"[{Number(decision.Reliability.Lower)}, {Number(decision.Reliability.Upper)}]"
                        : "NOT MEASURED"))
                    .Append("</td><td>").Append(H($"{decision.ConfidenceLevel:P0} confidence · lower bound >= {decision.MinimumLowerBound:0.000}"))
                    .Append("</td><td>").Append(H(Bool(decision.Passed))).Append("</td></tr>");
            html.Append("</tbody></table>");
        }

        if (live.Arms.Count > 0)
        {
            html.Append("<h3>Per-check census and Wilson reliability · diagnostic rollups</h3><p class=\"muted\">Every denominator and interval below is copied from the evaluator-owned arm summary. For stochastic plans these pooled check rows explain behaviour, but only the fully measured whole-trial scenario decisions above control acceptance.</p>");
            foreach (var arm in live.Arms)
            {
                html.Append("<h4>").Append(H(arm.ArmId)).Append(" · ").Append(H(arm.Architecture))
                    .Append(" · ").Append(arm.Repetitions).Append(" repetition(s)</h4>")
                    .Append("<table><thead><tr><th>Check</th><th>Measurement census</th><th>Successes</th><th>Estimate</th><th>Wilson interval</th></tr></thead><tbody>");
                foreach (var check in arm.Checks)
                    html.Append("<tr><td><code>").Append(H(check.Key)).Append("</code><br>").Append(H(check.Name))
                        .Append("</td><td>").Append(H($"measured {check.Census.Measured}; N/A {check.Census.NotApplicable}; not measured {check.Census.NotMeasured}; total {check.Census.Total}"))
                        .Append("</td><td>").Append(check.Reliability.Successes).Append('/').Append(check.Reliability.Total)
                        .Append(" · ").Append(H(check.Reliability.Measurement))
                        .Append("</td><td>").Append(H(Number(check.Reliability.Estimate)))
                        .Append("</td><td>").Append(H(check.Reliability.Lower.HasValue && check.Reliability.Upper.HasValue
                            ? $"[{Number(check.Reliability.Lower)}, {Number(check.Reliability.Upper)}]"
                            : "NOT MEASURED"))
                        .Append("</td></tr>");
                html.Append("</tbody></table>");
            }
        }

        if (live.Comparisons.Count > 0)
        {
            html.Append("<h3>AgentEval case-paired comparisons</h3><p class=\"muted\">No UI winner is derived. Statistical power and the case-level observation unit are copied from AgentEval.</p>")
                .Append("<table><thead><tr><th>Check</th><th>Reference → challenger</th><th>W/L/T</th><th>Effective n</th><th>Census</th><th>p-value</th><th>Minimum attainable p</th><th>Mean delta</th><th>Observation unit</th><th>Power</th></tr></thead><tbody>");
            foreach (var comparison in live.Comparisons)
                html.Append("<tr><td><code>").Append(H(comparison.CheckKey)).Append("</code><br>").Append(H(comparison.CheckName))
                    .Append("</td><td>").Append(H($"{comparison.ReferenceArm} → {comparison.ChallengerArm}"))
                    .Append("</td><td>").Append(H($"{comparison.Wins}/{comparison.Losses}/{comparison.Ties}"))
                    .Append("</td><td>").Append(comparison.EffectiveN)
                    .Append("</td><td>").Append(H($"measured {comparison.Census.Measured}; N/A {comparison.Census.NotApplicable}; not measured {comparison.Census.NotMeasured}; total {comparison.Census.Total}"))
                    .Append("</td><td>").Append(H(Number(comparison.PValue)))
                    .Append("</td><td>").Append(H(Number(comparison.MinimumAttainableP)))
                    .Append("</td><td>").Append(H(Number(comparison.MeanDelta)))
                    .Append("</td><td>").Append(H($"{comparison.Cases} paired cases; {comparison.TotalRepObservations} total raw reps across both arms; mean {Number(comparison.MeanRepetitionsPerCase)} reps/case/arm; RepCollapse.{comparison.RepCollapse}"))
                    .Append("</td><td>").Append(comparison.Undecidable
                        ? "UNDECIDABLE · evaluator did not admit a directional comparison"
                        : comparison.UnderpoweredByConstruction
                            ? "UNDERPOWERED BY CONSTRUCTION"
                            : "AgentEval reports power permits comparison")
                    .Append("</td></tr>");
            html.Append("</tbody></table>");
        }
        html.Append("</section>");
    }

    private static void RenderLiveConfiguration(
        StringBuilder html,
        VitrineLiveConfigurationSnapshot? configuration)
    {
        if (configuration is null) return;
        html.Append("<h3>Evaluator configuration and provenance</h3><div class=\"grid\">")
            .Append(Metric("Definition", $"{configuration.DefinitionKey}@{configuration.DefinitionVersion}"))
            .Append(Metric("Judge model", configuration.JudgeModelId))
            .Append(Metric("Judge prompt / rubric", $"{configuration.JudgePromptId} / {configuration.JudgeRubricHash}"))
            .Append(Metric("Subject / judge output limits", $"{configuration.SubjectMaxOutputTokens} / {configuration.JudgeMaxOutputTokens}"))
            .Append(Metric("Response preview characters", configuration.ResponsePreviewCharacters.ToString(CultureInfo.InvariantCulture)))
            .Append("</div>");
        if (configuration.Acceptance is { } acceptance)
            html.Append("<h4>Terminal acceptance policy</h4><div class=\"grid\">")
                .Append(Metric("Policy", acceptance.Policy))
                .Append(Metric("Confidence level", Number(acceptance.ConfidenceLevel)))
                .Append(Metric("Minimum Wilson lower bound", Number(acceptance.MinimumLowerBound)))
                .Append("</div>");
        RenderStringList(html, "Subject provenance", configuration.Subjects.Select(subject =>
            $"{subject.ArmId} · {subject.Architecture} · model {subject.ModelId} · judge relation {subject.JudgeSubjectRelation}"));
        if (configuration.Safety is { } safety)
            html.Append("<h4>Bounded safety configuration</h4><div class=\"grid\">")
                .Append(Metric("Attack categories", string.Join(", ", safety.Attacks)))
                .Append(Metric("Maximum probes / category", safety.MaxProbesPerAttack.ToString(CultureInfo.InvariantCulture)))
                .Append(Metric("Timeout / probe", $"{safety.TimeoutSeconds} seconds"))
                .Append(Metric("Maximum target calls / probe", safety.MaxTargetModelCallsPerProbe.ToString(CultureInfo.InvariantCulture)))
                .Append(Metric("Maximum safety model calls", safety.MaximumModelCalls.ToString(CultureInfo.InvariantCulture)))
                .Append(Metric("Judge mode", safety.JudgeMode))
                .Append(Metric("Raw evidence persisted", safety.EvidencePersisted ? "yes" : "no · redacted receipts only"))
                .Append("</div>");
    }

    private static void RenderLiveSafety(
        StringBuilder html,
        VitrineLiveSafetySnapshot? safety,
        string terminalStatus)
    {
        html.Append("<h3>Redacted AgentEval safety census</h3>")
            .Append("<p class=\"muted\">Campaign execution and measurement completeness are separate. COMPROMISED means an attack succeeded; RESISTED means conclusive defence; clean ambiguity is NOT MEASURED; execution faults or incomplete coverage are INFRASTRUCTURE ERROR. Errored is a subset of Inconclusive—do not add those counts. Raw probes, responses, extraction canary, system instructions, endpoints, and exceptions are excluded.</p>");
        if (safety is null)
        {
            html.Append(string.Equals(terminalStatus, nameof(LiveEvalTerminalStatus.InfrastructureError), StringComparison.Ordinal)
                ? "<p class=\"error\">INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · no sanitized safety summary was produced. Inspect typed failures above.</p>"
                : "<p class=\"not\">NOT MEASURED · no sanitized safety summary was produced. Inspect typed failures above.</p>");
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
        html.Append("<div class=\"grid\">")
            .Append(Metric("Safety outcome", status))
            .Append(Metric("Campaign execution", "completed · a receipt was returned"))
            .Append(Metric("Measurement completeness",
                string.Equals(terminalStatus, nameof(LiveEvalTerminalStatus.InfrastructureError), StringComparison.Ordinal)
                    ? "INCOMPLETE · infrastructure error"
                    : safety.Passed is null ? "INCOMPLETE · inconclusive" : "complete"))
            .Append(Metric("Target", safety.Target))
            .Append(Metric("Measurement", safety.Measurement))
            .Append(Metric("Census", $"total {safety.Total}; resisted {safety.Resisted}; compromised {safety.Compromised}; inconclusive {safety.Inconclusive}; errored subset {safety.Errored}"))
            .Append(Metric("Truncated / skipped", $"{safety.Truncated} / {safety.Skipped}"))
            .Append("</div>");
        RenderLiveUsage(html, "Target usage", safety.SubjectUsage);
        RenderLiveUsage(html, "Fallback-judge usage", safety.JudgeUsage);
        if (safety.Attacks.Count > 0)
        {
            html.Append("<h4>Attack-category outcomes</h4><table><thead><tr><th>Attack / OWASP</th><th>Semantic outcome</th><th>Resisted</th><th>Compromised</th><th>Inconclusive</th><th>Errored subset</th><th>Total</th></tr></thead><tbody>");
            foreach (var attack in safety.Attacks)
            {
                var outcome = attack.Compromised > 0 ? "VULNERABLE · ATTACK SUCCEEDED"
                    : attack.Errored > 0 ? "INCOMPLETE · PROBE ERROR"
                    : attack.Inconclusive > 0 ? "NOT MEASURED · INCONCLUSIVE" : "RESISTED";
                html.Append("<tr><td>").Append(H($"{attack.Attack} · {attack.OwaspId}"))
                    .Append("</td><td>").Append(H(outcome))
                    .Append("</td><td>").Append(attack.Resisted)
                    .Append("</td><td>").Append(attack.Compromised)
                    .Append("</td><td>").Append(attack.Inconclusive)
                    .Append("</td><td>").Append(attack.Errored)
                    .Append("</td><td>").Append(attack.Total).Append("</td></tr>");
            }
            html.Append("</tbody></table>");
        }
        if (safety.Probes.Count > 0)
        {
            html.Append("<h4>Allow-listed probe receipts</h4><table><thead><tr><th>Attack / probe</th><th>Outcome</th><th>Safe diagnostic</th><th>Failure stage</th><th>Failure code</th><th>Failure detail</th><th>Severity</th><th>Fidelity</th><th>Technique</th></tr></thead><tbody>");
            foreach (var probe in safety.Probes)
            {
                var hasFailure = probe.ErrorKind != nameof(LiveSafetyProbeErrorKind.None);
                var outcome = hasFailure
                    ? $"ERROR · {probe.ErrorKind.ToUpperInvariant()}"
                    : probe.Outcome == "Compromised" ? "VULNERABLE · ATTACK SUCCEEDED"
                    : probe.Outcome == "Resisted" ? "RESISTED" : "INCONCLUSIVE · NOT MEASURED";
                var diagnostic = LiveSafetyProbeDiagnostics.Describe(probe.Outcome, probe.ErrorKind);
                html.Append("<tr><td>").Append(H($"{probe.Attack} · {probe.ProbeId}"))
                    .Append("</td><td>").Append(H(outcome))
                    .Append("</td><td>").Append(H(diagnostic))
                    .Append("</td><td>").Append(H(probe.Failure?.Stage ?? (hasFailure ? "probe-execution" : "not-applicable")))
                    .Append("</td><td>").Append(H(probe.Failure?.Code ?? (hasFailure ? probe.ErrorKind : nameof(LiveSafetyProbeErrorKind.None))))
                    .Append("</td><td>").Append(H(probe.Failure?.Detail ?? (hasFailure ? diagnostic : "No typed probe execution failure.")))
                    .Append("</td><td>").Append(H(probe.Severity))
                    .Append("</td><td>").Append(H(probe.Fidelity))
                    .Append("</td><td>").Append(H(probe.Technique)).Append("</td></tr>");
            }
            html.Append("</tbody></table>");
        }
    }

    private static void RenderLiveUsage(StringBuilder html, string title, VitrineLiveUsageSnapshot usage)
    {
        html.Append("<h4>").Append(H(title)).Append("</h4><div class=\"grid\">")
            .Append(Metric("Status", usage.Status))
            .Append(Metric("Model calls", Number(usage.ModelCalls)))
            .Append(Metric("Input tokens", Number(usage.InputTokens)))
            .Append(Metric("Output tokens", Number(usage.OutputTokens)))
            .Append(Metric("Total tokens", Number(usage.TotalTokens)))
            .Append(Metric("Estimated cost USD", Number(usage.EstimatedCostUsd)))
            .Append("</div>");
    }

    private static void RenderOfflineBenchmark(
        StringBuilder html,
        VitrineOfflineBenchmarkSnapshot? benchmark)
    {
        if (benchmark is null) return;
        html.Append("<section class=\"card\"><h2>Native AgentEval offline benchmark</h2>")
            .Append("<p class=\"muted\">Definition → arm → BenchmarkRunner → score facts. Floor comparisons are descriptive evidence, not a second gate verdict.</p>")
            .Append("<div class=\"grid\">")
            .Append(Metric("Definition", $"{benchmark.DefinitionKey}@{benchmark.DefinitionVersion}"))
            .Append(Metric("Arms", benchmark.Arms.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Metric("Repetitions per arm", benchmark.Repetitions.ToString(CultureInfo.InvariantCulture)))
            .Append(Metric("Persisted runs", benchmark.Runs.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Metric("Cases", benchmark.Cases.Count.ToString(CultureInfo.InvariantCulture)))
            .Append("</div><p><span class=\"muted\">Workspace:</span> <code>")
            .Append(H(benchmark.WorkspaceRoot)).Append("</code><br><span class=\"muted\">Run directory:</span> <code>")
            .Append(H(benchmark.RunDirectory)).Append("</code></p>");
        RenderStringList(html, "Benchmark cases", benchmark.Cases.Select(item => $"{item.Id} · {item.Name}"));
        if (benchmark.Checks.Count == 0)
        {
            html.Append("<p class=\"not\">No benchmark check facts were recorded.</p></section>");
            return;
        }

        foreach (var arm in benchmark.Arms)
        {
            html.Append("<h3>Arm · ").Append(H(arm.ArmId)).Append("</h3><p class=\"muted\">")
                .Append(H($"{arm.SubjectKind} · {arm.SubjectName}")).Append("</p>");
            RenderBenchmarkChecks(html, arm.Checks);
        }
        if (benchmark.Arms.Count == 0)
            RenderBenchmarkChecks(html, benchmark.Checks);

        if (benchmark.Runs.Count > 0)
            RenderStringList(html, "Persisted arm × repetition runs", benchmark.Runs.Select(run =>
                $"{run.ArmId} · rep {run.Repetition} · {run.SubjectKind} · {run.RunId} · {run.RunDirectory}"));

        if (benchmark.ReferenceComparisons.Count > 0)
        {
            html.Append("<h3>Paired arm comparisons</h3><table><thead><tr><th>Check</th><th>Reference → challenger</th><th>W/L/T</th><th>Effective n</th><th>Census</th><th>p-value</th><th>Minimum p</th><th>Mean delta</th><th>Rep collapse</th><th>Power</th></tr></thead><tbody>");
            foreach (var row in benchmark.ReferenceComparisons)
                html.Append("<tr><td><code>").Append(H(row.CheckKey)).Append("</code></td><td>")
                    .Append(H($"{row.ReferenceArmId} → {row.ChallengerArmId}"))
                    .Append("</td><td>").Append(H($"{row.Wins}/{row.Losses}/{row.Ties}"))
                    .Append("</td><td>").Append(row.EffectiveN)
                    .Append("</td><td>").Append(H($"measured {row.Census.Measured}; N/A {row.Census.NotApplicable}; not measured {row.Census.NotMeasured}"))
                    .Append("</td><td>").Append(H(Number(row.PValue)))
                    .Append("</td><td>").Append(H(Number(row.MinimumAttainableP)))
                    .Append("</td><td>").Append(H(Number(row.MeanDelta)))
                    .Append("</td><td>").Append(H($"{row.RepCollapse}; {row.Cases} cases/{row.TotalRepObservations} rep observations"))
                    .Append("</td><td>").Append(row.UnderpoweredByConstruction ? "UNDERPOWERED BY CONSTRUCTION" : "power permits comparison")
                    .Append("</td></tr>");
            html.Append("</tbody></table>");
        }
        html.Append("</section>");
    }

    private static void RenderBenchmarkChecks(
        StringBuilder html,
        IReadOnlyList<VitrineBenchmarkCheckSnapshot> checks)
    {
        html.Append("<table><thead><tr><th>Check</th><th>Native floor</th><th>Derivation</th><th>Census</th><th>Observed</th><th>p-value</th><th>Minimum attainable p</th><th>Above floor</th><th>Power</th></tr></thead><tbody>");
        foreach (var check in checks)
            html.Append("<tr><td><code>").Append(H(check.CheckKey)).Append("</code></td><td>")
                .Append(H($"{check.Floor.Kind} · {check.Floor.State} · value {Number(check.Floor.Value)} · bar {Number(check.Floor.ComparisonBar)} · interval high {Number(check.Floor.IntervalHigh)} · draws {check.Floor.Draws.ToString(CultureInfo.InvariantCulture)} · pool {check.Floor.PoolSize.ToString(CultureInfo.InvariantCulture)}"))
                .Append("</td><td>").Append(H(check.Floor.Derivation)).Append("</td><td>")
                .Append(H($"measured {check.Census.Measured}; N/A {check.Census.NotApplicable}; not measured {check.Census.NotMeasured}; total {check.Census.Total}"))
                .Append("</td><td>").Append(check.Successes).Append('/').Append(check.Trials)
                .Append("</td><td>").Append(H(Number(check.PValue)))
                .Append("</td><td>").Append(H(Number(check.MinimumAttainableP)))
                .Append("</td><td>").Append(H(check.AboveFloor.HasValue ? (check.AboveFloor.Value ? "yes" : "no") : "NOT MEASURED"))
                .Append("</td><td>").Append(check.UnderpoweredByConstruction switch
                {
                    true => "UNDERPOWERED BY CONSTRUCTION",
                    false => "sufficient for configured comparison",
                    null => "POWER N/A · native floor comparison not derivable",
                })
                .Append("</td></tr>");
        html.Append("</tbody></table>");
    }

    private static void RenderRecommendations(StringBuilder html, IReadOnlyList<VitrineRecommendationSnapshot> items)
    {
        html.Append("<h3>Recommendations actually shown</h3>");
        if (items.Count == 0) { html.Append("<p class=\"not\">None.</p>"); return; }
        html.Append("<table><thead><tr><th>Tray</th><th>SKU</th><th>Product</th><th>Why</th><th>Evidence</th><th>Confidence</th><th>Verified price</th><th>Stock</th><th>Delivery</th></tr></thead><tbody>");
        foreach (var item in items)
            html.Append("<tr><td>").Append(H(item.Tray)).Append("</td><td><code>").Append(H(item.Sku))
                .Append("</code></td><td>").Append(H(item.Name)).Append("</td><td>").Append(H(item.Reason))
                .Append("</td><td>").Append(H(item.Evidence)).Append("</td><td>")
                .Append(item.Confidence.ToString("0.000", CultureInfo.InvariantCulture)).Append("</td>")
                .Append(MeasurementCell(item.VerifiedPriceChf, value =>
                    $"CHF {value.ToString("0.00", CultureInfo.InvariantCulture)}"))
                .Append(MeasurementCell(item.StockUnits, value =>
                    value.ToString(CultureInfo.InvariantCulture)))
                .Append(MeasurementCell(item.DeliveryEstimateDays, value =>
                    $"{value.ToString(CultureInfo.InvariantCulture)} working days"))
                .Append("</tr>");
        html.Append("</tbody></table>");
    }

    private static void RenderLedger(StringBuilder html, VitrineLedgerSnapshot? ledger)
    {
        if (ledger is null) { html.Append("<h3>Guardrail ledger</h3><p class=\"not\">NOT MEASURED</p>"); return; }
        html.Append("<h3>Guardrail ledger</h3><p>")
            .Append(ledger.InputCount).Append(" in → ").Append(ledger.OutputCount).Append(" out · ")
            .Append(ledger.DroppedCount).Append(" dropped · ").Append(ledger.DemotedCount).Append(" demoted · ")
            .Append(ledger.NotedCount).Append(" noted · gift exclusions ").Append(H(NumberOrNotMeasured(ledger.GiftExcluded)))
            .Append(" · price/stock ").Append(ledger.PriceStockVerified).Append('/').Append(ledger.PriceStockRequested)
            .Append(" verified · tool calls ").Append(H(ToolCalls(ledger))).Append("</p>");
        if (ledger.Entries.Count == 0) return;
        html.Append("<table><thead><tr><th>Stage</th><th>Action</th><th>Reason</th><th>Subject</th><th>Detail</th></tr></thead><tbody>");
        foreach (var entry in ledger.Entries)
            html.Append("<tr><td>").Append(H(entry.Stage)).Append("</td><td>").Append(H(entry.Action))
                .Append("</td><td>").Append(H(entry.Reason)).Append("</td><td>").Append(H(entry.Subject))
                .Append("</td><td>").Append(H(entry.Detail)).Append("</td></tr>");
        html.Append("</tbody></table>");
    }

    private static void RenderStringList(StringBuilder html, string title, IEnumerable<string> values)
    {
        var items = values.ToArray();
        if (items.Length == 0)
        {
            html.Append("<h3>").Append(H(title)).Append("</h3><p class=\"muted\">None recorded.</p>");
            return;
        }
        html.Append("<h3>").Append(H(title)).Append("</h3><ul>");
        foreach (var item in items) html.Append("<li>").Append(H(item)).Append("</li>");
        html.Append("</ul>");
    }

    private static string RenderAgentEval(VitrineAgentEvalProvenanceSnapshot? provenance)
    {
        if (provenance is null) return "<span class=\"not\">NOT MEASURED</span>";
        var html = new StringBuilder("<code>").Append(H(provenance.IntegrationId)).Append(" · ")
            .Append(H(provenance.LibraryType)).Append("</code><br>")
            .Append(H(provenance.Mechanism)).Append("<br><span class=\"muted\">Subject: ")
            .Append(H(provenance.Subject)).Append(" · Snapshot: ").Append(H(provenance.SnapshotPolicy))
            .Append("<br>Observation producer: ").Append(H(provenance.ObservationProducer))
            .Append(" · Acceptance evaluator: ").Append(H(provenance.AcceptanceEvaluator))
            .Append(" · Subject supplied pass/fail: ")
            .Append(provenance.SubjectSuppliedPassFail ? "yes" : "no")
            .Append("</span>");
        if (provenance.Observations.Count > 0)
        {
            html.Append("<ul>");
            foreach (var observation in provenance.Observations)
                html.Append("<li><code>").Append(H(observation.Id)).Append("</code> · ")
                    .Append(H(observation.Outcome)).Append(" · score ").Append(H(NumberOrNotMeasured(observation.Score)))
                    .Append(" · surface ").Append(H(Measurement(observation.Surface)))
                    .Append(" · n ").Append(H(NumberOrNotMeasured(observation.SampleCount))).Append("</li>");
            html.Append("</ul>");
        }
        return html.ToString();
    }

    private static string MeasurementCell<T>(T? value, Func<T, string> format) where T : struct =>
        value.HasValue
            ? $"<td>{H(format(value.Value))}</td>"
            : "<td><span class=\"not\">NOT MEASURED</span></td>";

    private static string ToolCalls(VitrineLedgerSnapshot ledger) =>
        ledger.ToolCallsUsed.HasValue && ledger.ToolCallCap.HasValue
            ? $"{ledger.ToolCallsUsed.Value.ToString(CultureInfo.InvariantCulture)}/{ledger.ToolCallCap.Value.ToString(CultureInfo.InvariantCulture)}"
            : "NOT MEASURED";

    private static string RenderGraph(VitrineRunArtifact artifact)
    {
        var nodes = artifact.Graph.Nodes;
        if (nodes.Count == 0) return "<p class=\"not\">Topology not captured.</p>";
        var replay = new GraphViewModel();
        replay.Load(artifact.Graph);
        foreach (var item in artifact.Events) replay.Apply(item);
        const double width = 1100;
        var height = RuntimeGraphControl.RequiredHeightForLayout(replay, width);
        var positions = RuntimeGraphControl.Layout(replay, width, height);
        var svg = new StringBuilder("<svg viewBox=\"0 0 1100 ").Append(D(height))
            .Append("\" role=\"img\" aria-label=\"Runtime graph\"><defs><marker id=\"arrow\" viewBox=\"0 0 10 10\" refX=\"8\" refY=\"5\" markerWidth=\"5\" markerHeight=\"5\" orient=\"auto-start-reverse\"><path d=\"M 0 0 L 10 5 L 0 10 z\" fill=\"#405675\"/></marker></defs>");
        foreach (var edge in artifact.Graph.Edges)
            if (positions.TryGetValue(edge.SourceId, out var source) && positions.TryGetValue(edge.TargetId, out var target))
            {
                if (edge.IsLoopBack)
                {
                    var controlY = Math.Max(20, Math.Min(source.Y, target.Y) - 65);
                    svg.Append("<path class=\"edge loop\" fill=\"none\" marker-end=\"url(#arrow)\" d=\"M ")
                        .Append(D(source.X)).Append(' ').Append(D(source.Y - 30)).Append(" L ")
                        .Append(D(source.X)).Append(' ').Append(D(controlY)).Append(" L ")
                        .Append(D(target.X)).Append(' ').Append(D(controlY)).Append(" L ")
                        .Append(D(target.X)).Append(' ').Append(D(target.Y - 30)).Append("\"/>");
                }
                else if (RuntimeGraphControl.IsDemo01Surface(replay)
                         && replay.Nodes.Single(node => node.Id == edge.SourceId).Kind == "agent"
                         && replay.Nodes.Single(node => node.Id == edge.TargetId).Kind == "tool")
                {
                    var route = RuntimeGraphControl.Demo01ToolRoute(source, target);
                    svg.Append("<path class=\"edge demo-tool-route\" fill=\"none\" marker-end=\"url(#arrow)\" d=\"M ")
                        .Append(D(route[0].Start.X)).Append(' ').Append(D(route[0].Start.Y));
                    foreach (var leg in route)
                        svg.Append(" L ").Append(D(leg.End.X)).Append(' ').Append(D(leg.End.Y));
                    svg.Append("\"/>");
                }
                else
                {
                    var segment = RuntimeGraphControl.TrimmedSegment(source, target, 110, 48, 110, 48);
                    svg.Append("<line class=\"edge\" marker-end=\"url(#arrow)\" x1=\"").Append(D(segment.Start.X)).Append("\" y1=\"").Append(D(segment.Start.Y))
                        .Append("\" x2=\"").Append(D(segment.End.X)).Append("\" y2=\"").Append(D(segment.End.Y)).Append("\"/>");
                }
                var observed = replay.Edges.Single(item => item.Id == edge.Id).TraversalCount;
                svg.Append("<text class=\"trace\" x=\"").Append(D((source.X + target.X) / 2)).Append("\" y=\"")
                    .Append(D((source.Y + target.Y) / 2 - 9)).Append("\" text-anchor=\"middle\">")
                    .Append(H(edge.IsLoopBack ? $"BACK · {edge.Label} · ×{observed}" : $"×{observed}"))
                    .Append("</text>");
            }
        foreach (var pair in positions)
        {
            var projected = replay.Nodes.Single(node => node.Id == pair.Key);
            svg.Append("<rect class=\"node\" data-node-id=\"").Append(H(pair.Key))
                .Append("\" data-center-x=\"").Append(D(pair.Value.X)).Append("\" data-center-y=\"").Append(D(pair.Value.Y))
                .Append("\" x=\"").Append(D(pair.Value.X - 55)).Append("\" y=\"").Append(D(pair.Value.Y - 24)).Append("\" width=\"110\" height=\"48\" rx=\"8\"/>")
                .Append("<text class=\"label\" x=\"").Append(D(pair.Value.X)).Append("\" y=\"").Append(D(pair.Value.Y - 2)).Append("\" text-anchor=\"middle\">")
                .Append(H(Clip(pair.Key, 16))).Append("</text><text class=\"trace\" x=\"").Append(D(pair.Value.X)).Append("\" y=\"").Append(D(pair.Value.Y + 15)).Append("\" text-anchor=\"middle\">")
                .Append(H($"{projected.CanvasStateText} · {projected.ExecutionBadgeText}")).Append("</text>");
        }
        svg.Append("</svg><h3>Executed graph facts</h3><table><thead><tr><th>Node</th><th>State</th><th>Execution count</th></tr></thead><tbody>");
        foreach (var node in replay.Nodes)
            svg.Append("<tr><td>").Append(H(node.Label)).Append("</td><td>").Append(H(node.StateText)).Append("</td><td>").Append(H(node.ExecutionText)).Append("</td></tr>");
        svg.Append("</tbody></table><table><thead><tr><th>Directed connector</th><th>Traversal count</th></tr></thead><tbody>");
        foreach (var edge in replay.Edges)
            svg.Append("<tr><td>").Append(H($"{edge.SourceId} → {edge.TargetId} · {edge.Label}")).Append("</td><td>").Append(edge.TraversalCount).Append("</td></tr>");
        return svg.Append("</tbody></table>").ToString();
    }

    private static string Metric(string label, string value) => $"<div><div class=\"muted\">{H(label)}</div><div class=\"metric\">{H(value)}</div></div>";
    private static string GateStatus(VitrineGateSnapshot gate) => gate.Outcome switch
    {
        AgentEval.VitrineDemo.Evals.GateMeasurementOutcome.InstrumentError => "INSTRUMENT ERROR",
        AgentEval.VitrineDemo.Evals.GateMeasurementOutcome.NotApplicable => "NOT APPLICABLE",
        AgentEval.VitrineDemo.Evals.GateMeasurementOutcome.NotMeasured => "NOT MEASURED",
        _ => gate.Passed switch { true => "PASS", false => "FAIL", null => "NOT MEASURED" },
    };
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Number(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Number(decimal? value) => value?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string Bool(bool? value) => value switch
    {
        true => "PASS",
        false => "FAIL",
        null => "NOT MEASURED",
    };
    private static string Floor(VitrineChanceFloorSnapshot? floor) => floor switch
    {
        null => "NO CHANCE COMPARISON · diagnostic/meta evaluation",
        { State: "NotDerivable" } => $"NOT DERIVABLE · {floor.Derivation}",
        _ => $"{floor.Kind} · bar {Number(floor.ComparisonBar)} · {floor.Derivation}",
    };
    private static string Missing(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string Measurement(string? value) => string.IsNullOrWhiteSpace(value) ? "NOT MEASURED" : value;
    private static string NumberOrNotMeasured(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string NumberOrNotMeasured(double? value) => value?.ToString("0.000", CultureInfo.InvariantCulture) ?? "NOT MEASURED";
    private static string D(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string ControlOutcome(AgentEval.VitrineDemo.Evals.ControlAttemptOutcome outcome, bool broken) => outcome switch
    {
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.MeasuredPass when broken => "MISSED · defect still passed",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.MeasuredPass => "RECOVERED · passed",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.MeasuredFail when broken => "DETECTED · expected failure",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.ExpectedFaultObserved when broken => "DETECTED · expected fault contained",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.MeasuredFail or AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.ExpectedFaultObserved => "NOT RECOVERED · failed",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        _ => "UNKNOWN",
    };
    private static string HealthyOutcome(AgentEval.VitrineDemo.Evals.ControlAttemptOutcome outcome) => outcome switch
    {
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.MeasuredPass => "PASS · baseline healthy",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.MeasuredFail => "FAIL · invalid baseline",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.ExpectedFaultObserved => "FAIL · unexpected fault",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        AgentEval.VitrineDemo.Evals.ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        _ => "UNKNOWN",
    };
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
