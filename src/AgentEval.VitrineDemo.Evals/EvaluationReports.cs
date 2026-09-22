// SPDX-License-Identifier: MIT
using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Galaxus.RecommendationAgent.Observability;
using Galaxus.RecommendationAgent.Providers;
namespace AgentEval.VitrineDemo.Evals;
/// <summary>JSON projection of the typed suite result; nullable measurements remain null.</summary>
public static class EvaluationReportJson {
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    public static string Render(SuiteResult result) {
        ArgumentNullException.ThrowIfNull(result);
        EvaluationReportBoundary.EnsureSafe(result);
        var portableBenchmark = EvaluationReportPortableProjection.Project(result.OfflineBenchmark);
        var canonicalControlScope = NegativeControlCatalog.HasCanonicalRegisteredPanel(result.Controls);
        var controlScope = canonicalControlScope
            ? "Exact registered 43-row panel with attested healthy, perturbed, and restored outcomes; row scope includes production observations and explicit boundary/calibration fixtures."
            : "Unverified or partial registered-control panel.";
        var interpretation = result.Gates
            .Select(static gate => gate.HonestInterpretation)
            .SingleOrDefault(static claims => claims is not null);
        return JsonSerializer.Serialize(new {
            schemaVersion = 1,
            processEquivalentExitCode = result.ExitCode,
            caughtControls = result.CaughtControls,
            controlCount = result.Controls.Count,
            controlScopeStatus = canonicalControlScope ? "canonicalRegisteredPanel" : "unverifiedOrPartial",
            controlScope,
            execution = result.Execution,
            offlineBenchmark = portableBenchmark,
            gates = result.Gates.Select(gate => new {
                gate.Name,
                gate.Passed,
                gate.Score,
                chanceFloor = ProjectFloor(gate.ChanceFloor),
                Evidence = EvaluationReportPortableProjection.ProjectText(
                    gate.Evidence, result.OfflineBenchmark?.WorkspaceRoot),
                gate.Outcome,
                gate.Authority,
                gate.AgentEval,
                gate.HonestInterpretation,
                gate.AgentEvalMeasurementState,
            }),
            controls = result.Controls,
            interpretationStatus = interpretation is null ? "notMeasured" : "measured",
            interpretation,
        }, Options);
    }
    private static object? ProjectFloor(AgentEval.Evals.Meta.ChanceFloor? floor) =>
        floor is null ? null : new {
            floor.Kind,
            state = floor.State,
            value = floor.State == AgentEval.Evals.Meta.FloorState.Derived ? floor.Value : (double?)null,
            comparisonBar = floor.State == AgentEval.Evals.Meta.FloorState.Derived ? floor.ComparisonBar : (double?)null,
            intervalHigh = floor.IntervalHigh is { } high && double.IsFinite(high)
                ? high
                : (double?)null,
            floor.Draws,
            floor.PoolSize,
            floor.Derivation,
        };
}
/// <summary>Self-contained, offline HTML projection of the same typed suite result.</summary>
public static class EvaluationReportHtml {
    public static string Render(SuiteResult result) => RenderCore(result, static gate => ProjectGate(gate));
    internal static string RenderWithForcedPassingGateForControl(SuiteResult result, bool forcePassing) =>
        RenderCore(result, gate => ProjectGate(gate, forcePassing: forcePassing));
    internal static string RenderWithForcedMissingScoreForControl(SuiteResult result, bool forceZero) =>
        RenderCore(result, gate => ProjectGate(gate, forceZero: forceZero));
    internal static (string Status, double? Score) ProjectGateForControl(
        GateResult gate,
        bool forcePassing = false,
        bool forceZero = false) {
        ArgumentNullException.ThrowIfNull(gate);
        var projection = ProjectGate(gate, forcePassing, forceZero);
        return (projection.Status, projection.Score);
    }
    internal static (string Status, string Score)? ReadGateRowForControl(string html, string gateName) {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentException.ThrowIfNullOrWhiteSpace(gateName);
        var pattern = $@"<tr><td>{Regex.Escape(H(gateName))}</td><td class=""[^""]+"">(?<status>[^<]*)</td><td class=""score"">(?<score>[^<]*)</td>";
        var matches = Regex.Matches(html, pattern, RegexOptions.CultureInvariant);
        return matches.Count == 1
            ? (WebUtility.HtmlDecode(matches[0].Groups["status"].Value), WebUtility.HtmlDecode(matches[0].Groups["score"].Value))
            : null;
    }
    private static string RenderCore(
        SuiteResult result,
        Func<GateResult, GateRowProjection> projectGate) {
        ArgumentNullException.ThrowIfNull(result);
        EvaluationReportBoundary.EnsureSafe(result);
        var portableBenchmark = EvaluationReportPortableProjection.Project(result.OfflineBenchmark);
        var canonicalControlScope = NegativeControlCatalog.HasCanonicalRegisteredPanel(result.Controls);
        var controlHeading = canonicalControlScope
            ? "Registered control mutations"
            : "Controls · scope not established";
        var interpretation = result.Gates
            .Select(static gate => gate.HonestInterpretation)
            .SingleOrDefault(static claims => claims is not null);
        const string reportHeading = "VITRINE · offline AgentEval evidence chain";
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>VITRINE evaluation report</title><style>")
            .Append("body{background:#08111f;color:#edf3fc;font:14px Segoe UI,sans-serif;margin:0}main{max-width:1200px;margin:auto;padding:32px}.card{background:#111827;border:1px solid #263650;border-radius:10px;padding:15px;margin:12px 0}table{width:100%;border-collapse:collapse}td,th{padding:8px;border-bottom:1px solid #263650;text-align:left;vertical-align:top}th,.muted{color:#91a0b8}.pass{color:#63d391}.fail{color:#f07076}.not{color:#f6c55c}.floor{color:#f6c55c}</style></head><body><main><h1>")
            .Append(H(reportHeading)).Append("</h1>")
            .Append("<p class=\"muted\">Typed SuiteResult projection. Missing measurements are not zero.</p>")
            .Append("<section class=\"card\"><strong>Process-equivalent exit ").Append(result.ExitCode)
            .Append("</strong> · ").Append(result.CaughtControls).Append('/').Append(result.Controls.Count).Append(' ').Append(H(controlHeading.ToLowerInvariant())).Append(" caught")
            .Append(canonicalControlScope
                ? "<p class=\"muted\">The exact registered panel passed baseline-healthy → defect-detected → recovery checks. Rows include both production-observation controls and explicitly classified boundary/calibration fixtures; this proves registered mutation reachability, not natural defect prevalence.</p></section>"
                : "<p class=\"muted\">This result is not the exact registered 43-row panel with validated execution provenance.</p></section>")
            .Append(result.Execution is { } execution
                ? $"<section class=\"card\"><h2>Subject and judge execution</h2><p><strong>{H(execution.Profile.ToString())}</strong> · {H(execution.DemoScope)}</p><p>{H(execution.SubjectEngine)}</p><p>{H(execution.EvaluatorEngine)}</p><p class=\"muted\">Deployment {H(execution.DeploymentName ?? "none · offline deterministic")} · Demo01/Demo02/judge calls {H(execution.Demo01SubjectModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED")}/{H(execution.Demo02SubjectModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED")}/{H(execution.JudgeModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED")} · tokens {H(execution.Demo01SubjectTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED")}/{H(execution.Demo02SubjectTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED")}/{H(execution.JudgeTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED")} · estimated USD {H(execution.EstimatedCostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT MEASURED")}</p></section>"
                : string.Empty)
            .Append(RenderBenchmark(portableBenchmark))
            .Append("<section class=\"card\"><h2>Mandatory gates and diagnostic evaluations</h2><p class=\"muted\">Mandatory gates control the process-equivalent exit. Diagnostic rows remain visible evidence but cannot fail the suite. The null/chance baseline is descriptive comparison evidence, not a pass threshold.</p><table><tr><th>Evaluation</th><th>Status</th><th>Score</th><th>Authority</th><th>Null / chance baseline</th><th>Evidence</th></tr>");
        foreach (var gate in result.Gates) {
            var display = projectGate(gate);
            html.Append("<tr><td>").Append(H(gate.Name)).Append("</td><td class=\"").Append(display.Css).Append("\">").Append(display.Status)
                .Append("</td><td class=\"score\">").Append(display.Score?.ToString("0.000", CultureInfo.InvariantCulture) ?? "—")
                .Append("</td><td>").Append(H(gate.Authority.ToString()))
                .Append("</td><td class=\"floor\">").Append(H(FormatFloor(gate.ChanceFloor)))
                .Append("</td><td>").Append(H(EvaluationReportPortableProjection.ProjectText(
                    gate.Evidence, result.OfflineBenchmark?.WorkspaceRoot))).Append("</td></tr>");
        }
        html.Append("</table></section><section class=\"card\"><h2>").Append(H(controlHeading)).Append("</h2><table><tr><th>ID</th><th>Control</th><th>Scope</th><th>Tranche</th><th>Target</th><th>Producer</th><th>Evaluator</th><th>Baseline healthy</th><th>Defect-injected (detection expected)</th><th>Recovery</th><th>Classification</th><th>Evidence</th></tr>");
        foreach (var control in result.Controls)
            html.Append("<tr><td>").Append(H(control.Id)).Append("</td><td>").Append(H(control.Name))
                .Append("</td><td>").Append(H(control.ScopeClass.ToString()))
                .Append("</td><td>").Append(H(control.Tranche))
                .Append("</td><td>").Append(H(control.Target))
                .Append("</td><td>").Append(H(control.ObservationProducer))
                .Append("</td><td>").Append(H(control.Evaluator))
                .Append("</td><td class=\"").Append(OutcomeCss(control.HealthyOutcome)).Append("\">").Append(H(HealthyOutcomeText(control.HealthyOutcome)))
                .Append("</td><td class=\"").Append(DefectCss(control.BrokenOutcome)).Append("\">").Append(H(OutcomeText(control.BrokenOutcome, broken: true)))
                .Append("</td><td class=\"").Append(OutcomeCss(control.RestoredOutcome)).Append("\">").Append(H(OutcomeText(control.RestoredOutcome, broken: false)))
                .Append("</td><td class=\"floor\">Mutation diagnostic — no chance floor")
                .Append("</td><td>").Append(H(control.Evidence)).Append("</td></tr>");
        html.Append("</table></section><section class=\"card\"><h2>Honest interpretation</h2>");
        if (interpretation is null) {
            html.Append("<p class=\"not\">NOT MEASURED: no validated honesty evidence is attached to this suite result.</p>");
        }
        else {
            html.Append("<p>").Append(H(interpretation.StatedNeedSatisfaction)).Append(".</p><p>")
                .Append(H(interpretation.NextPurchasePrediction)).Append(".</p><p>")
                .Append("Remedy: ").Append(H(interpretation.NextPurchaseRemedy)).Append("</p><p>")
                .Append(H(interpretation.Baseline)).Append(".</p><p class=\"muted\">Evidence ")
                .Append(H(interpretation.MeasurementId)).Append(" · ").Append(H(interpretation.Source)).Append("</p>");
        }
        html.Append("</section></main></body></html>");
        return html.ToString();
    }
    private static GateRowProjection ProjectGate(
        GateResult gate,
        bool forcePassing = false,
        bool forceZero = false) {
        var display = forcePassing
            ? new GateDisplayState("PASS", "pass")
            : gate.Outcome == GateMeasurementOutcome.InstrumentError
                ? new("INSTRUMENT ERROR", "fail")
                : gate.Outcome == GateMeasurementOutcome.NotApplicable
                    ? new("NOT APPLICABLE", "not")
                : gate.Passed switch {
                    true => new("PASS", "pass"),
                    false => new("FAIL", "fail"),
                    null => new("NOT MEASURED", "not"),
                };
        var score = forceZero && gate.Outcome == GateMeasurementOutcome.NotMeasured ? 0 : gate.Score;
        return new(display.Status, display.Css, score);
    }
    private sealed record GateRowProjection(string Status, string Css, double? Score);
    private sealed record GateDisplayState(string Status, string Css);
    private static string RenderBenchmark(VitrineOfflineBenchmarkResult? benchmark) {
        if (benchmark is null) return string.Empty;
        var html = new StringBuilder("<section class=\"card\"><h2>Canonical AgentEval benchmark runs</h2><p><strong>")
            .Append(H(benchmark.DefinitionKey)).Append('@').Append(H(benchmark.DefinitionVersion))
            .Append("</strong> · ").Append(benchmark.Arms.Count).Append(" arms × ")
            .Append(benchmark.Repetitions).Append(" repetitions × ").Append(benchmark.Cases.Count)
            .Append(" cases</p><p class=\"muted\">Workspace ").Append(H(benchmark.WorkspaceRoot))
            .Append("; ").Append(benchmark.Runs.Count).Append(" distinct run directories.</p>");
        foreach (var arm in benchmark.Arms) {
            html.Append("<h3>").Append(H(arm.ArmId)).Append(" · ").Append(H(arm.SubjectKind))
                .Append("</h3><p class=\"muted\">").Append(H(arm.SubjectName)).Append("</p><ul>");
            foreach (var check in arm.Checks)
                html.Append("<li><strong>").Append(H(check.CheckKey)).Append("</strong>: census ")
                    .Append(check.Census.Measured).Append('/').Append(check.Census.Total)
                    .Append(" measured; floor ").Append(H(check.Floor.ComparisonBar?.ToString("0.000", CultureInfo.InvariantCulture) ?? "not derivable"))
                    .Append(" (").Append(H(check.Floor.Derivation)).Append("); ")
                    .Append(check.Successes).Append('/').Append(check.Trials).Append(" successes; p ")
                    .Append(H(check.PValue?.ToString("0.000", CultureInfo.InvariantCulture) ?? "NOT MEASURED"))
                    .Append("; ").Append(check.UnderpoweredByConstruction switch {
                        true => "UNDERPOWERED",
                        false => "power permits comparison",
                        null => "POWER N/A · native floor comparison not derivable",
                    })
                    .Append("</li>");
            html.Append("</ul>");
        }
        html.Append("<h3>Paired reference comparisons</h3><ul>");
        foreach (var row in benchmark.ReferenceComparisons)
            html.Append("<li><strong>").Append(H(row.CheckKey)).Append("</strong> · ")
                .Append(H(row.ReferenceArmId)).Append(" → ").Append(H(row.ChallengerArmId))
                .Append(" · W/L/T ").Append(row.Wins).Append('/').Append(row.Losses).Append('/').Append(row.Ties)
                .Append(" · effective n ").Append(row.EffectiveN).Append(" · p ")
                .Append(H(row.PValue?.ToString("0.000", CultureInfo.InvariantCulture) ?? "NOT MEASURED"))
                .Append(" · ").Append(H(row.RepCollapse)).Append("</li>");
        html.Append("</ul><details><summary>Persisted arm × repetition directories</summary><ul>");
        foreach (var run in benchmark.Runs)
            html.Append("<li>").Append(H($"{run.ArmId} · rep {run.Repetition} · {run.RunId} · {run.RunDirectory}"))
                .Append("</li>");
        return html.Append("</ul></details></section>").ToString();
    }
    private static string FormatFloor(AgentEval.Evals.Meta.ChanceFloor? floor) => floor switch {
        null => "not applicable",
        { State: AgentEval.Evals.Meta.FloorState.Derived } =>
            floor.ComparisonBar.ToString("0.000", CultureInfo.InvariantCulture),
        _ => $"not derivable — {floor.Derivation}",
    };
    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string OutcomeCss(ControlAttemptOutcome outcome) => outcome switch {
        ControlAttemptOutcome.MeasuredPass => "pass",
        ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved => "fail",
        _ => "not",
    };
    private static string DefectCss(ControlAttemptOutcome outcome) => outcome switch {
        ControlAttemptOutcome.MeasuredFail or ControlAttemptOutcome.ExpectedFaultObserved => "pass",
        ControlAttemptOutcome.MeasuredPass or ControlAttemptOutcome.InstrumentError => "fail",
        _ => "not",
    };
    private static string OutcomeText(ControlAttemptOutcome outcome, bool broken) => outcome switch {
        ControlAttemptOutcome.MeasuredPass => broken ? "MISSED · defect still passed" : "RECOVERED · passed",
        ControlAttemptOutcome.MeasuredFail => broken ? "DETECTED · expected failure" : "NOT RECOVERED · failed",
        ControlAttemptOutcome.ExpectedFaultObserved => broken ? "DETECTED · expected fault contained" : "NOT RECOVERED · unexpected fault",
        ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        _ => "UNKNOWN",
    };
    private static string HealthyOutcomeText(ControlAttemptOutcome outcome) => outcome switch {
        ControlAttemptOutcome.MeasuredPass => "PASS · baseline healthy",
        ControlAttemptOutcome.MeasuredFail => "FAIL · invalid baseline",
        ControlAttemptOutcome.NotMeasured => "NOT MEASURED",
        ControlAttemptOutcome.InstrumentError => "INSTRUMENT ERROR",
        ControlAttemptOutcome.ExpectedFaultObserved => "FAIL · unexpected fault",
        _ => "UNKNOWN",
    };
}
/// <summary>
/// Removes machine-specific roots from committed reports while retaining a stable logical path to
/// each AgentEval receipt. Runtime results and the application continue to expose the real local
/// directories to the operator.
/// </summary>
internal static class EvaluationReportPortableProjection {
    private const string PortableRoot = ".agenteval/Vitrine";
    public static VitrineOfflineBenchmarkResult? Project(VitrineOfflineBenchmarkResult? benchmark) {
        if (benchmark is null) return null;
        return benchmark with {
            WorkspaceRoot = PortableRoot,
            RunDirectory = PortableDirectory(benchmark.WorkspaceRoot, benchmark.RunDirectory, benchmark.RunId),
            Runs = benchmark.Runs.Select(run => run with {
                RunDirectory = PortableDirectory(benchmark.WorkspaceRoot, run.RunDirectory, run.RunId),
            }).ToArray(),
        };
    }
    public static string ProjectText(string value, string? workspaceRoot) =>
        string.IsNullOrWhiteSpace(workspaceRoot)
            ? value
            : value.Replace(workspaceRoot, PortableRoot, StringComparison.OrdinalIgnoreCase);
    private static string PortableDirectory(string workspaceRoot, string directory, string runId) {
        try {
            var root = Path.GetFullPath(workspaceRoot);
            var path = Path.GetFullPath(directory);
            var relative = Path.GetRelativePath(root, path);
            if (!Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                return $"{PortableRoot}/{relative.Replace('\\', '/')}";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
            // A report must remain portable even when handed a synthetic or malformed local path.
        }
        var safeRunId = string.Concat(runId.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        return $"{PortableRoot}/runs/{(safeRunId.Length == 0 ? "unknown" : safeRunId)}";
    }
}
/// <summary>Fail-closed boundary shared by every public evaluation report projection.</summary>
internal static class EvaluationReportBoundary {
    private const string UnsafeReportMessage = "Evaluation report contains disallowed secret-bearing content.";
    public static void EnsureSafe(object result) {
        ArgumentNullException.ThrowIfNull(result);
        // Every provider key and endpoint currently configured, from the resolver's single list.
        var configured = InferenceProviderEnvironment.SecretBearingVariables
            .Select(variable => (variable.Comparison, Value: NonBlankEnvironmentValue(variable.Name)))
            .Where(entry => entry.Value is not null)
            .ToList();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (Visit(result)) throw new InvalidDataException(UnsafeReportMessage);
        bool Visit(object? current) {
            if (current is null) return false;
            if (current is string text)
                return configured.Any(entry => text.Contains(entry.Value!, entry.Comparison))
                    || !string.Equals(RecommendationRuntimeEvents.SafePreview(text), text, StringComparison.Ordinal);
            var type = current.GetType();
            if (type.IsValueType) return false;
            if (!visited.Add(current)) return false;
            if (current is IEnumerable items) {
                foreach (var item in items)
                    if (Visit(item)) return true;
                return false;
            }
            if (type.Assembly != typeof(SuiteResult).Assembly) return false;
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
                if (property.GetIndexParameters().Length == 0 && Visit(property.GetValue(current))) return true;
            }
            return false;
        }
    }
    private static string? NonBlankEnvironmentValue(string name) {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
