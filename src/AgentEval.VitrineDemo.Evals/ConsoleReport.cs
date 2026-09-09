// SPDX-License-Identifier: MIT
using System.Globalization;
using AgentEval.VitrineDemo.Evals.Live;
namespace AgentEval.VitrineDemo.Evals;
public static class ConsoleReport {
    public static void Print(SuiteResult result, bool verboseControls, TextWriter? writer = null) {
        ArgumentNullException.ThrowIfNull(result);
        EvaluationReportBoundary.EnsureSafe(result);
        writer ??= Console.Out;
        writer.WriteLine();
        writer.WriteLine("VITRINE · offline AgentEval evidence chain");
        writer.WriteLine(new string('═', 78));
        if (result.Execution is { } execution) {
            writer.WriteLine($"Execution: {execution.Profile} · {execution.DemoScope}");
            writer.WriteLine($"  subject {execution.SubjectEngine}");
            writer.WriteLine($"  evaluator {execution.EvaluatorEngine}");
            writer.WriteLine($"  deployment {execution.DeploymentName ?? "none · offline deterministic"} · " +
                $"Demo01/Demo02/judge calls " +
                $"{execution.Demo01SubjectModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED"}/" +
                $"{execution.Demo02SubjectModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED"}/" +
                $"{execution.JudgeModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED"}");
            writer.WriteLine($"  Demo01/Demo02/judge tokens " +
                $"{execution.Demo01SubjectTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED"}/" +
                $"{execution.Demo02SubjectTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED"}/" +
                $"{execution.JudgeTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT MEASURED"} · " +
                $"estimated USD {execution.EstimatedCostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT MEASURED"}");
            writer.WriteLine();
        }
        foreach (var gate in result.Gates) {
            var state = gate.Outcome switch {
                GateMeasurementOutcome.InstrumentError => "INSTR ERROR",
                GateMeasurementOutcome.NotApplicable => "N/A",
                GateMeasurementOutcome.NotMeasured => "NOT MEASURED",
                _ => gate.Passed == true ? "PASS" : "FAIL",
            };
            var score = gate.Score is { } value ? value.ToString("0.000", CultureInfo.InvariantCulture) : "—";
            var authority = gate.Authority == GateAuthority.Diagnostic ? "[DIAGNOSTIC] " : string.Empty;
            writer.WriteLine($"{state,-12} {authority}{gate.Name}");
            writer.WriteLine($"             score {score} · null/chance baseline {FormatFloor(gate.ChanceFloor)} (descriptive; not a pass threshold)");
            writer.WriteLine($"             {gate.Evidence}");
        }
        if (result.OfflineBenchmark is { } benchmark) {
            writer.WriteLine();
            writer.WriteLine($"Canonical AgentEval run: {benchmark.RunId}");
            writer.WriteLine($"  {benchmark.DefinitionKey}@{benchmark.DefinitionVersion} · arm {benchmark.ArmId}");
            writer.WriteLine($"  persisted at {benchmark.RunDirectory}");
            foreach (var check in benchmark.Checks)
                writer.WriteLine($"  {check.CheckKey}: census {check.Census.Measured}/{check.Census.Total} measured · " +
                    $"floor {check.Floor.ComparisonBar?.ToString("0.000", CultureInfo.InvariantCulture) ?? "not derivable"} · " +
                    $"p {check.PValue?.ToString("0.000", CultureInfo.InvariantCulture) ?? "n/a"} · " +
                    $"{(check.UnderpoweredByConstruction switch {
                        true => "underpowered by construction",
                        false => "powered at declared alpha",
                        null => "power N/A · native floor comparison not derivable",
                    })}");
            foreach (var arm in benchmark.Arms)
                writer.WriteLine($"  arm {arm.ArmId} · subject {arm.SubjectKind}/{arm.SubjectName} · " +
                    $"{benchmark.Repetitions} separate run(s)");
            foreach (var comparison in benchmark.ReferenceComparisons)
                writer.WriteLine($"  {comparison.ChallengerArmId} vs {comparison.ReferenceArmId} · " +
                    $"{comparison.CheckKey}: W/L/T {comparison.Wins}/{comparison.Losses}/{comparison.Ties} · " +
                    $"case n {comparison.Cases} · reps {comparison.TotalRepObservations} · {comparison.RepCollapse}");
        }
        if (result.Controls.Count > 0 || result.RequireCanonicalControlPanel) {
            writer.WriteLine();
            var canonicalControlScope = NegativeControlCatalog.HasCanonicalRegisteredPanel(result.Controls);
            writer.WriteLine(canonicalControlScope
                ? $"Registered control mutations: {result.CaughtControls}/{result.Controls.Count} caught"
                : $"Controls · scope not established: {result.CaughtControls}/{result.Controls.Count} caught");
            writer.WriteLine(canonicalControlScope
                ? "  Scope: exact attested healthy → perturbed → restored panel; rows include production observations and explicit boundary/calibration fixtures."
                : "  Scope: this is not the exact registered 43-row panel with validated execution provenance.");
            if (verboseControls) {
                foreach (var control in result.Controls) {
                    writer.WriteLine($"  {(control.Caught ? "CAUGHT" : "MISSED"),-7} {control.Id} {control.Name} · diagnostic fixture (no chance floor)");
                    writer.WriteLine($"          {control.Tranche} · {control.ScopeClass} · producer {control.ObservationProducer} · evaluator {control.Evaluator}");
                    writer.WriteLine($"          {control.Evidence}");
                }
            }
        }
        var interpretation = result.Gates
            .Select(static gate => gate.HonestInterpretation)
            .SingleOrDefault(static claims => claims is not null);
        writer.WriteLine();
        writer.WriteLine("Honest interpretation:");
        if (interpretation is null) {
            writer.WriteLine("  NOT MEASURED: no validated honesty evidence is attached to this suite result.");
        }
        else {
            writer.WriteLine($"  {interpretation.StatedNeedSatisfaction}");
            writer.WriteLine($"  {interpretation.NextPurchasePrediction}");
            writer.WriteLine($"  Remedy: {interpretation.NextPurchaseRemedy}");
            writer.WriteLine($"  {interpretation.Baseline}");
            writer.WriteLine($"  Evidence {interpretation.MeasurementId} · {interpretation.Source}");
        }
        writer.WriteLine();
        writer.WriteLine($"Exit code: {result.ExitCode}");
    }
    private static string FormatFloor(AgentEval.Evals.Meta.ChanceFloor? floor) => floor switch {
        null => "not applicable",
        { State: AgentEval.Evals.Meta.FloorState.Derived } =>
            floor.ComparisonBar.ToString("0.000", CultureInfo.InvariantCulture),
        _ => $"not derivable — {floor.Derivation}",
    };
    public static void Print(VitrineAdmittedChecksSelfTestResult result, TextWriter? writer = null) {
        ArgumentNullException.ThrowIfNull(result);
        writer ??= Console.Out;
        writer.WriteLine();
        writer.WriteLine("VITRINE · offline admitted-check self-test");
        writer.WriteLine(new string('═', 78));
        writer.WriteLine($"Checks: {result.BenchmarkChecks} benchmark + {result.ProductionChecks} production");
        writer.WriteLine($"Arms: {string.Join(", ", result.Benchmark.Arms.Select(static arm => arm.ArmId))}");
        writer.WriteLine($"Runs: {result.Benchmark.Runs.Count} separate directories under {result.Benchmark.WorkspaceRoot}");
        foreach (var check in result.Checks) {
            var healthy = check.HealthyPassed switch {
                true => "healthy PASS",
                false => "healthy FAIL",
                null => "healthy NOT VERIFIED",
            };
            var ablated = check.AblationWentRed switch {
                true => "ablated FAIL (expected red)",
                false => "ablated PASS (unexpected green)",
                null => "ablated NOT VERIFIED",
            };
            writer.WriteLine($"  {check.Lane,-10} {check.Key}: {healthy} → {ablated}; {check.Detail}");
        }
        if (result.Passed) {
            writer.WriteLine("PASS · every healthy observation measured green and every declared ablation measured red.");
        }
        else {
            writer.WriteLine($"FAIL · {result.Failures.Count} expectation(s) did not hold:");
            foreach (var failure in result.Failures)
                writer.WriteLine($"  - {failure}");
        }
        writer.WriteLine($"Exit code: {result.ExitCode}");
    }

    public static void Print(LiveEvalProgress progress, TextWriter? writer = null) {
        ArgumentNullException.ThrowIfNull(progress);
        writer ??= Console.Out;
        var coordinates = new[] {
                progress.ScenarioId,
                progress.ArmId,
                progress.Repetition is { } repetition ? $"rep {repetition}" : null,
                progress.CheckKey,
            }
            .Where(static value => !string.IsNullOrWhiteSpace(value));
        var location = string.Join(" · ", coordinates);
        writer.WriteLine($"LIVE {progress.Phase}" +
            (location.Length == 0 ? string.Empty : $" · {location}") +
            $" · {progress.Detail}");
    }

    public static void Print(LiveEvalResult result, TextWriter? writer = null) {
        ArgumentNullException.ThrowIfNull(result);
        writer ??= Console.Out;
        var descriptor = VitrineEvaluationPlans.Require(result.Plan);
        writer.WriteLine();
        writer.WriteLine("VITRINE · named live evaluation");
        writer.WriteLine(new string('═', 78));
        writer.WriteLine($"Plan: {descriptor.Label} · status {result.TerminalStatus}");
        writer.WriteLine($"Session: {result.SessionId}");
        if (result.Plan == VitrineEvaluationPlan.LiveEval06SafetyProbes) {
            PrintSafety(result, writer);
            return;
        }
        writer.WriteLine($"Workload: {result.Workload.ScenarioCount} scenario(s) · " +
            $"{result.Workload.ArmCount} arm(s) · {result.Workload.Repetitions} repetition(s) · " +
            $"{result.Workload.PlannedSubjectCalls} planned subject call(s) · " +
            $"up to {result.Workload.PlannedJudgeEvaluations} judge evaluation(s)");
        writer.WriteLine($"LLM quality pass threshold: {result.PassThreshold.ToString("0.000", CultureInfo.InvariantCulture)}");
        writer.WriteLine(result.Configuration.Acceptance.Policy switch
        {
            LiveTerminalAcceptancePolicy.WilsonLowerBoundPerScenario =>
                $"Terminal acceptance: every arm/scenario must be fully measured; a whole-trial success requires quality, response-observed, and arm-specific tool-journal/workflow-trace checks; its 95% Wilson lower bound must be >= " +
                $"{Optional(result.Configuration.Acceptance.MinimumLowerBound)}",
            LiveTerminalAcceptancePolicy.EveryTrialMustPass =>
                "Terminal acceptance: every fully measured trial must pass",
            _ => "Terminal acceptance: NOT APPLICABLE",
        });
        foreach (var decision in result.ScenarioAcceptances)
        {
            writer.WriteLine($"Scenario acceptance: {decision.ArmId} · {decision.ScenarioId} · " +
                $"census {decision.Census.Measured}/{decision.Census.Total} measured · " +
                $"whole-trial successes {decision.Reliability.Successes}/{decision.Reliability.Total} · " +
                $"Wilson [{Optional(decision.Reliability.Lower)}, {Optional(decision.Reliability.Upper)}] · " +
                $"floor {decision.MinimumLowerBound.ToString("0.000", CultureInfo.InvariantCulture)} · " +
                $"{(decision.Passed is true ? "PASS" : decision.Passed is false ? "FAIL" : "NOT MEASURED")}");
        }
        foreach (var arm in result.Arms) {
            writer.WriteLine($"Arm: {arm.ArmId} · {arm.Architecture} · {arm.Repetitions} repetition(s)");
            foreach (var check in arm.Checks) {
                var reliability = check.Reliability;
                var interval = reliability.Measurement == AgentEval.Evals.Meta.MeasurementState.Measured
                    ? $"{Optional(reliability.Estimate)} [{Optional(reliability.Lower)}, {Optional(reliability.Upper)}]"
                    : "NOT MEASURED";
                writer.WriteLine($"  {check.Key}: census {check.Census.Measured}/{check.Census.Total} measured · " +
                    $"reliability {reliability.Successes}/{reliability.Total} · {interval}");
            }
        }
        foreach (var comparison in result.Comparisons) {
            writer.WriteLine($"Compare: {comparison.ChallengerArm} vs {comparison.ReferenceArm} · " +
                $"{comparison.CheckKey} · W/L/T {comparison.Wins}/{comparison.Losses}/{comparison.Ties} · " +
                $"effective n {comparison.EffectiveN} · p {Optional(comparison.PValue)} · " +
                $"minimum attainable p {Optional(comparison.MinimumAttainableP)} · " +
                $"collapsed pass-rate delta {Optional(comparison.MeanDelta)} · " +
                $"unit {comparison.Cases} case(s)/{comparison.TotalRepObservations} rep observation(s) " +
                $"({Optional(comparison.MeanRepetitionsPerCase)} per case; {comparison.RepCollapse})");
        }
        PrintFailures(result, writer);
        writer.WriteLine($"Runs: {result.Runs.Count} persisted AgentEval run director{(result.Runs.Count == 1 ? "y" : "ies")}");
        writer.WriteLine($"Sanitized outcome: {result.Persistence.OutcomePath}");
        writer.WriteLine($"Session index: {result.Persistence.IndexPath}");
        writer.WriteLine($"Exit code: {result.ExitCode}");
    }

    private static void PrintSafety(LiveEvalResult result, TextWriter writer) {
        var configuration = result.Configuration.Safety;
        writer.WriteLine("Scenarios/repetitions: N/A · fixed Robin-only safety campaign");
        writer.WriteLine($"Campaign: {result.Workload.SafetyAttackCount} attack categor{(result.Workload.SafetyAttackCount == 1 ? "y" : "ies")} · " +
            $"{result.Workload.PlannedSafetyProbes} planned probe(s) · " +
            $"maximum {result.Workload.MaximumSafetyModelCalls} model call(s)");
        if (configuration is { }) {
            writer.WriteLine($"Limits: {configuration.MaxProbesPerAttack} probe(s)/attack · " +
                $"{configuration.TimeoutSeconds}s/probe · {configuration.MaxTargetModelCallsPerProbe} target call(s)/probe · " +
                $"judge {configuration.JudgeMode} · raw evidence persisted {configuration.EvidencePersisted}");
        }
        writer.WriteLine("LLM quality pass threshold: N/A · safety census owns this plan's verdict");
        if (result.Safety is { } safety) {
            var verdict = result.TerminalStatus switch {
                LiveEvalTerminalStatus.Passed => "PASS · RESISTED",
                LiveEvalTerminalStatus.QualityFailed => "FAIL · COMPROMISED",
                LiveEvalTerminalStatus.InfrastructureError => safety.Errored > 0
                    ? $"INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · {safety.Errored}/{safety.Total} PROBES ERRORED"
                    : "INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · INCOMPLETE CENSUS",
                LiveEvalTerminalStatus.Cancelled => "CANCELLED · SAFETY VERDICT NOT MEASURED",
                _ => "NOT MEASURED · INCONCLUSIVE",
            };
            writer.WriteLine($"Safety: {verdict} · measurement {safety.Measurement} · target {safety.Target}");
            writer.WriteLine($"Census: {safety.Resisted} resisted · {safety.Compromised} COMPROMISED · " +
                $"{safety.Inconclusive} inconclusive ({safety.Errored} errored subset; do not add these counts) · " +
                $"truncated {safety.Truncated} · skipped {safety.Skipped} · total {safety.Total}");
            foreach (var attack in safety.Attacks)
                writer.WriteLine($"  Attack {attack.Attack} · {attack.OwaspId} · " +
                    $"{attack.Resisted} resisted/{attack.Compromised} COMPROMISED/" +
                    $"{attack.Inconclusive} inconclusive ({attack.Errored} errored subset) · total {attack.Total}");
            foreach (var probe in safety.Probes) {
                writer.WriteLine($"    Probe {probe.Attack}/{probe.ProbeId}: " +
                    (probe.ErrorKind == LiveSafetyProbeErrorKind.None
                        ? probe.Outcome.ToString()
                        : $"ERROR · {probe.ErrorKind.ToString().ToUpperInvariant()}") +
                    $" · diagnostic {probe.Diagnostic} · severity {probe.Severity} · fidelity {probe.Fidelity} · " +
                    $"technique {probe.Technique}");
                writer.WriteLine(probe.Failure is { } failure
                    ? $"      Typed failure: stage {failure.Stage} · code {failure.Code} · detail {failure.Detail}"
                    : "      Typed failure: stage not-applicable · code None · detail No typed probe execution failure.");
            }
            writer.WriteLine($"Target usage: {FormatUsage(safety.SubjectUsage)}");
            writer.WriteLine($"Fallback-judge usage: {FormatUsage(safety.JudgeUsage)}");
        }
        else {
            writer.WriteLine(result.TerminalStatus == LiveEvalTerminalStatus.InfrastructureError
                ? "Safety: INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · no probe summary was produced"
                : "Safety: NOT MEASURED · no probe summary was produced");
            writer.WriteLine("Target usage: NOT REPORTED");
            writer.WriteLine("Fallback-judge usage: NOT REPORTED");
        }
        PrintFailures(result, writer);
        writer.WriteLine("Runs: 0 BenchmarkRunner run directories · RedTeamRunner session receipt only");
        writer.WriteLine($"Sanitized outcome: {result.Persistence.OutcomePath}");
        writer.WriteLine($"Session index: {result.Persistence.IndexPath}");
        writer.WriteLine($"Exit code: {result.ExitCode}");
    }

    private static void PrintFailures(LiveEvalResult result, TextWriter writer) {
        foreach (var failure in result.Failures) {
            var coordinates = new[] {
                    failure.ScenarioId,
                    failure.ArmId,
                    failure.Repetition is { } repetition ? $"rep {repetition}" : null,
                    failure.CheckKey,
                }
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            var location = string.Join(" · ", coordinates);
            writer.WriteLine($"Failure {failure.Code}" +
                (location.Length == 0 ? string.Empty : $" · {location}") +
                $": {LiveEvidenceText.Bound(failure.Detail, 320)}");
        }
    }

    private static string FormatUsage(LiveUsageEvidence usage) =>
        $"{usage.Status} · calls {usage.ModelCalls?.ToString(CultureInfo.InvariantCulture) ?? "NOT REPORTED"} · " +
        $"input/output/total tokens " +
        $"{usage.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT REPORTED"}/" +
        $"{usage.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT REPORTED"}/" +
        $"{usage.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "NOT REPORTED"} · " +
        $"estimated USD {usage.EstimatedCostUsd?.ToString("0.000000", CultureInfo.InvariantCulture) ?? "NOT REPORTED"}";

    private static string Optional(double? value) =>
        value is { } measured && double.IsFinite(measured)
            ? measured.ToString("0.000", CultureInfo.InvariantCulture)
            : "NOT MEASURED";
}
