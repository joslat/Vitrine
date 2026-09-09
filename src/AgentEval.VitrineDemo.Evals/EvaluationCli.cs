// SPDX-License-Identifier: MIT

using System.Globalization;
using AgentEval.VitrineDemo.Evals.Live;

namespace AgentEval.VitrineDemo.Evals;
public enum EvaluationCliMode { All, Controls, Gates, SelfTest }
public sealed record EvaluationCliOptions(EvaluationCliMode Mode, bool AblateCatalogue, string? JsonPath, string? HtmlPath,
    VitrineEvaluationPlan EvalPlan = VitrineEvaluationPlan.OfflineSuite,
    string? ScenarioId = null,
    int? Repetitions = null,
    bool ConfirmPaid = false);
public sealed record EvaluationCliParseResult(EvaluationCliOptions? Options, bool ShowHelp, string? Error) {
    public bool IsValid => Error is null;
}
/// <summary>Strict command parsing and stable process-exit classification for the eval CLI.</summary>
public static class EvaluationCli {
    public const string HelpText = """
        VITRINE evaluation CLI

          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --all
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --controls
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --gates-only
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --self-test
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --ablate-catalogue
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --all --json report.json --html report.html
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --eval-plan eval01-agent --scenario all --confirm-paid
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --eval-plan eval03-compare --scenario nadia-cross-category --repetitions 3 --confirm-paid
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --eval-plan eval06-safety-probes --confirm-paid
          dotnet run --project src/AgentEval.VitrineDemo.Evals -- --live-subjects-and-judge --confirm-paid

        --all is the default. The offline suite is credential-free and makes zero external model calls.
        --eval-plan selects exactly one plan:
          offline | eval01-agent | eval02-workflow | eval03-compare |
          eval04-stochastic-agent | eval05-stochastic-workflow | eval06-safety-probes
        --scenario selects one canonical live scenario id, or all (the live default):
          nadia-cross-category | sofia-capability-gap | marco-gift-trap |
          luca-safe-abstention | all
        --repetitions N overrides a live plan's repetition count. Eval 01 and Eval 02 always run
          once; Eval 03 accepts 1..100; stochastic Eval 04 and Eval 05 accept 4..100.
        Eval 06 runs a fixed AgentEval safety-probe campaign against Robin. It does not accept
          scenario or repetition options.
        --confirm-paid is mandatory for every live plan. Live plans require configured model services,
          can incur cost, persist a sanitized outcome.json plus session index under .agenteval/live,
          and a live plan never falls back to the offline suite.
        --live-subjects-and-judge is a backward-compatible alias for --eval-plan eval03-compare;
          it also requires --confirm-paid.
        --gates-only executes five mandatory evaluation gates plus the matched-quality diagnostic;
          the CI control uses this six-stage lane without recursion.
        --self-test runs all eleven admitted checks offline, proves each ablation goes red, and
          returns exit 1 if any execution, census, tool-journal, healthy, or degraded expectation fails.
        --ablate-catalogue removes one product from an isolated snapshot and must exit 1.
        Exit codes: 0 pass; 1 mandatory-gate or registered-control failure, admitted-check
                    self-test failure, or expected catalogue-ablation detection; 2 invalid
                    arguments; 3 not measured; 4 evaluation or report infrastructure failure.
                    A matched-quality diagnostic finding alone cannot set exit 1.
        """;
    public static EvaluationCliParseResult Parse(IReadOnlyList<string> args) {
        ArgumentNullException.ThrowIfNull(args);
        var all = false;
        var controls = false;
        var gates = false;
        var selfTest = false;
        var ablate = false;
        var legacyLive = false;
        var evalPlanSupplied = false;
        var scenarioSupplied = false;
        var repetitionsSupplied = false;
        var confirmPaid = false;
        var help = false;
        string? jsonPath = null;
        string? htmlPath = null;
        string? scenarioId = null;
        int? repetitions = null;
        VitrineEvaluationPlan? selectedPlan = null;
        for (var index = 0; index < args.Count; index++) {
            var argument = args[index];
            switch (argument.ToLowerInvariant()) {
                case "--help" or "-h":
                    if (help) return Invalid("The help option may be supplied only once.");
                    help = true;
                    break;
                case "--all":
                    if (all) return Invalid("The all mode may be supplied only once.");
                    all = true;
                    break;
                case "--controls":
                    if (controls) return Invalid("The controls mode may be supplied only once.");
                    controls = true;
                    break;
                case "--gates-only":
                    if (gates) return Invalid("The gates-only mode may be supplied only once.");
                    gates = true;
                    break;
                case "--self-test":
                    if (selfTest) return Invalid("The self-test mode may be supplied only once.");
                    selfTest = true;
                    break;
                case "--ablate-catalogue":
                    if (ablate) return Invalid("The catalogue ablation may be supplied only once.");
                    ablate = true;
                    break;
                case "--live-subjects-and-judge":
                    if (legacyLive) return Invalid("The live execution alias may be supplied only once.");
                    legacyLive = true;
                    break;
                case "--eval-plan":
                    if (evalPlanSupplied) return Invalid("The evaluation plan may be supplied only once.");
                    if (!TryTakeValue(args, ref index, out var planValue))
                        return Invalid("The evaluation plan option requires a value.");
                    if (!TryParseEvalPlan(planValue!, out var parsedPlan))
                        return Invalid("The evaluation plan value is not recognized.");
                    selectedPlan = parsedPlan;
                    evalPlanSupplied = true;
                    break;
                case "--scenario":
                    if (scenarioSupplied) return Invalid("The scenario option may be supplied only once.");
                    if (!TryTakeValue(args, ref index, out var scenarioValue))
                        return Invalid("The scenario option requires an id or 'all'.");
                    if (!string.Equals(scenarioValue, "all", StringComparison.OrdinalIgnoreCase)) {
                        scenarioId = LiveUseCaseScenarios.All
                            .FirstOrDefault(item => string.Equals(item.Id, scenarioValue, StringComparison.OrdinalIgnoreCase))
                            ?.Id;
                        if (scenarioId is null)
                            return Invalid("The scenario id is not recognized.");
                    }
                    scenarioSupplied = true;
                    break;
                case "--repetitions":
                    if (repetitionsSupplied) return Invalid("The repetitions option may be supplied only once.");
                    if (!TryTakeValue(args, ref index, out var repetitionsValue))
                        return Invalid("The repetitions option requires an integer value.");
                    if (!int.TryParse(repetitionsValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRepetitions) ||
                        parsedRepetitions is < 1 or > 100)
                        return Invalid("Repetitions must be an integer between 1 and 100.");
                    repetitions = parsedRepetitions;
                    repetitionsSupplied = true;
                    break;
                case "--confirm-paid":
                    if (confirmPaid) return Invalid("The paid-execution confirmation may be supplied only once.");
                    confirmPaid = true;
                    break;
                case "--json":
                    if (jsonPath is not null) return Invalid("The JSON report path may be supplied only once.");
                    if (!TryTakeValue(args, ref index, out jsonPath))
                        return Invalid("The JSON report option requires a path value.");
                    break;
                case "--html":
                    if (htmlPath is not null) return Invalid("The HTML report path may be supplied only once.");
                    if (!TryTakeValue(args, ref index, out htmlPath))
                        return Invalid("The HTML report option requires a path value.");
                    break;
                default:
                    return Invalid($"Unknown argument at position {index + 1}.");
            }
        }
        if (help && args.Count != 1)
            return Invalid("The help option cannot be combined with execution options.");
        if ((all ? 1 : 0) + (controls ? 1 : 0) + (gates ? 1 : 0) + (selfTest ? 1 : 0) > 1)
            return Invalid("Choose exactly one of all, controls-only, gates-only, or self-test mode.");
        if ((controls || gates) && ablate)
            return Invalid("Catalogue ablation cannot be combined with controls-only or gates-only mode.");
        if (legacyLive && evalPlanSupplied)
            return Invalid("Choose either the live execution alias or an evaluation plan, not both.");

        var evalPlan = legacyLive
            ? VitrineEvaluationPlan.LiveEval03AgentVsWorkflow
            : selectedPlan ?? VitrineEvaluationPlan.OfflineSuite;
        var live = evalPlan != VitrineEvaluationPlan.OfflineSuite;

        if (evalPlanSupplied && (controls || gates || selfTest))
            return Invalid("Evaluation plans cannot be combined with controls-only, gates-only, or self-test mode.");
        if (live && (controls || gates || selfTest || ablate))
            return Invalid("A live evaluation plan requires all mode without catalogue ablation.");
        if (live && (jsonPath is not null || htmlPath is not null))
            return Invalid("Live plans persist their own sanitized outcome and cannot use offline JSON or HTML report paths.");
        if (live && !confirmPaid)
            return Invalid("Paid live evaluation requires the explicit --confirm-paid option.");
        if (!live && (scenarioSupplied || repetitionsSupplied || confirmPaid))
            return Invalid("Scenario, repetitions, and paid confirmation apply only to a live evaluation plan.");
        if (live) {
            var descriptor = VitrineEvaluationPlans.Require(evalPlan);
            if (scenarioSupplied && !descriptor.SupportsScenarioSelection)
                return Invalid("The selected evaluation plan does not accept a scenario option.");
            if (evalPlan == VitrineEvaluationPlan.LiveEval06SafetyProbes && repetitionsSupplied)
                return Invalid("The selected evaluation plan does not accept a repetitions option.");
            if (repetitions is not null && !descriptor.SupportsRepetitions && repetitions != 1)
                return Invalid("The selected evaluation plan always runs exactly one repetition.");
            if (repetitions is < 4 && evalPlan is
                VitrineEvaluationPlan.LiveEval04StochasticAgent or
                VitrineEvaluationPlan.LiveEval05StochasticWorkflow)
                return Invalid("Stochastic Eval 04 and Eval 05 require between 4 and 100 repetitions.");
        }
        if (selfTest && (ablate || legacyLive || jsonPath is not null || htmlPath is not null))
            return Invalid("Self-test is offline-only and cannot be combined with ablation, live execution, or report paths.");
        if (!TryNormalizeReportPath(jsonPath, out var normalizedJsonPath) ||
            !TryNormalizeReportPath(htmlPath, out var normalizedHtmlPath))
            return Invalid("A report path is not valid.");
        if (normalizedJsonPath is not null && normalizedHtmlPath is not null &&
            string.Equals(
                normalizedJsonPath,
                normalizedHtmlPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return Invalid("JSON and HTML reports require different output paths.");
        return new EvaluationCliParseResult(
            help ? null : new EvaluationCliOptions(
                controls ? EvaluationCliMode.Controls :
                gates ? EvaluationCliMode.Gates :
                selfTest ? EvaluationCliMode.SelfTest : EvaluationCliMode.All,
                ablate, normalizedJsonPath, normalizedHtmlPath,
                EvalPlan: evalPlan,
                ScenarioId: scenarioId,
                Repetitions: repetitions,
                ConfirmPaid: confirmPaid),
            help,
            null);
    }
    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default) =>
        RunAsync(args, output, error, EvaluationCliServices.Default, cancellationToken);
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        EvaluationCliServices services,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(services);
        var parsed = Parse(args);
        if (!parsed.IsValid) {
            await error.WriteLineAsync($"ERROR: {parsed.Error}").ConfigureAwait(false);
            return EvaluationExitCodes.InvalidArguments;
        }
        if (parsed.ShowHelp) {
            await output.WriteLineAsync(HelpText).ConfigureAwait(false);
            return EvaluationExitCodes.Passed;
        }
        try {
            var options = parsed.Options!;
            if (options.Mode == EvaluationCliMode.SelfTest) {
                var selfTest = await (services.RunSelfTest ??
                        (ct => VitrineAdmittedChecksSelfTest.RunAsync(cancellationToken: ct)))
                    (cancellationToken)
                    .ConfigureAwait(false);
                ConsoleReport.Print(selfTest, output);
                return selfTest.ExitCode;
            }
            if (options.EvalPlan != VitrineEvaluationPlan.OfflineSuite) {
                var runLivePlan = services.RunLivePlan ??
                    throw new InvalidOperationException("The selected live evaluation plan has no configured runner.");
                var liveOptions = new LiveEvalOptions(
                    Repetitions: options.Repetitions,
                    ScenarioIds: options.ScenarioId is null ? null : [options.ScenarioId]);
                var progress = new TextWriterLiveEvalProgress(output);
                var liveResult = await runLivePlan(
                        options.EvalPlan, liveOptions, progress, cancellationToken)
                    .ConfigureAwait(false);
                ConsoleReport.Print(liveResult, output);
                return liveResult.ExitCode;
            }
            SuiteResult result;
            if (options.Mode == EvaluationCliMode.Controls) {
                var controls = await services.RunControls(cancellationToken).ConfigureAwait(false);
                result = new SuiteResult([], controls) { RequireCanonicalControlPanel = true };
            }
            else if (options.Mode == EvaluationCliMode.Gates) {
                result = await (services.RunGates ??
                    (ct => services.RunSuite(false, ct)))(cancellationToken).ConfigureAwait(false);
            }
            else {
                result = await services.RunSuite(options.AblateCatalogue, cancellationToken).ConfigureAwait(false);
            }
            if (options.Mode == EvaluationCliMode.Gates &&
                ReferenceEquals(services, EvaluationCliServices.Default) &&
                !CiProofPolicy.RequiredCiStepsPlanned(CiProofPolicy.ObservePlan() with {
                    CheckStageExitCode = result.ExitCode,
                    ExecutedCheckStageCount = result.Gates.Count,
                }))
                throw new InvalidDataException("The committed non-recursive CI proof contract was not satisfied.");
            ConsoleReport.Print(result, verboseControls: true, output);
            await WriteReportAsync("JSON", options.JsonPath, EvaluationReportJson.Render, result, services, output, cancellationToken)
                .ConfigureAwait(false);
            await WriteReportAsync("HTML", options.HtmlPath, EvaluationReportHtml.Render, result, services, output, cancellationToken)
                .ConfigureAwait(false);
            return result.ExitCode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) {
            await error.WriteLineAsync(
                $"ERROR: evaluation infrastructure failed ({exception.GetType().Name}); message withheld.")
                .ConfigureAwait(false);
            return EvaluationExitCodes.InfrastructureFailure;
        }
    }
    private static async Task WriteReportAsync(
        string kind,
        string? path,
        Func<SuiteResult, string> render,
        SuiteResult result,
        EvaluationCliServices services,
        TextWriter output,
        CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(path)) return;
        await services.WriteAllText(path, render(result), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"{kind} report written.").ConfigureAwait(false);
    }
    private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, out string? value) {
        var valueIndex = index + 1;
        if (valueIndex >= args.Count || string.IsNullOrWhiteSpace(args[valueIndex]) || args[valueIndex].StartsWith('-')) {
            value = null;
            return false;
        }
        value = args[valueIndex];
        index = valueIndex;
        return true;
    }
    private static bool TryParseEvalPlan(string value, out VitrineEvaluationPlan plan) {
        plan = value.ToLowerInvariant() switch {
            "offline" => VitrineEvaluationPlan.OfflineSuite,
            "eval01-agent" => VitrineEvaluationPlan.LiveEval01Agent,
            "eval02-workflow" => VitrineEvaluationPlan.LiveEval02Workflow,
            "eval03-compare" => VitrineEvaluationPlan.LiveEval03AgentVsWorkflow,
            "eval04-stochastic-agent" => VitrineEvaluationPlan.LiveEval04StochasticAgent,
            "eval05-stochastic-workflow" => VitrineEvaluationPlan.LiveEval05StochasticWorkflow,
            "eval06-safety-probes" => VitrineEvaluationPlan.LiveEval06SafetyProbes,
            _ => (VitrineEvaluationPlan)(-1),
        };
        return Enum.IsDefined(plan);
    }
    private static bool TryNormalizeReportPath(string? path, out string? normalized) {
        if (path is null) {
            normalized = null;
            return true;
        }
        try {
            normalized = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) {
            normalized = null;
            return false;
        }
    }
    private static EvaluationCliParseResult Invalid(string error) => new(null, false, error);

    private sealed class TextWriterLiveEvalProgress(TextWriter writer) : IProgress<LiveEvalProgress> {
        public void Report(LiveEvalProgress value) => ConsoleReport.Print(value, writer);
    }
}
internal sealed record EvaluationCliServices(Func<bool, CancellationToken, Task<SuiteResult>> RunSuite,
    Func<CancellationToken, Task<IReadOnlyList<ControlResult>>> RunControls,
    Func<string, string, CancellationToken, Task> WriteAllText,
    Func<CancellationToken, Task<SuiteResult>>? RunGates = null,
    Func<CancellationToken, Task<VitrineAdmittedChecksSelfTestResult>>? RunSelfTest = null,
    Func<VitrineEvaluationPlan, LiveEvalOptions, IProgress<LiveEvalProgress>?, CancellationToken, Task<LiveEvalResult>>? RunLivePlan = null) {
    public static EvaluationCliServices Default { get; } = new(
        (ablated, cancellationToken) => EvaluationSuite.RunAsync(ablated, cancellationToken),
        cancellationToken => NegativeControlRunner.RunAsync(cancellationToken),
        async (path, contents, cancellationToken) => {
            _ = await EvaluationReportWriter.WriteAsync(path, contents, cancellationToken).ConfigureAwait(false);
        },
        cancellationToken => EvaluationSuite.RunAsync(
            cancellationToken: cancellationToken,
            includeControls: false),
        cancellationToken => VitrineAdmittedChecksSelfTest.RunAsync(cancellationToken: cancellationToken),
        (plan, options, progress, cancellationToken) => LiveEvaluationPlanRunner.RunAsync(
            plan, paidExecutionConfirmed: true, options, progress: progress,
            cancellationToken: cancellationToken));
}
internal sealed record ReportWriteReceipt(string FullPath, long Bytes, DateTimeOffset WrittenAtUtc);
internal static class EvaluationReportWriter {
    public static async Task<ReportWriteReceipt> WriteAsync(string path, string contents,
        CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ??
            throw new IOException("The report output path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try {
            await File.WriteAllTextAsync(temporaryPath, contents, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally {
            File.Delete(temporaryPath);
        }
        var file = new FileInfo(fullPath);
        return new ReportWriteReceipt(fullPath, file.Length, file.LastWriteTimeUtc);
    }
}
