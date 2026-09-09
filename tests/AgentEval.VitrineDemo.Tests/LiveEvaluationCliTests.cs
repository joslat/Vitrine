// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;

namespace AgentEval.VitrineDemo.Tests;

public sealed class LiveEvaluationCliTests
{
    [Theory]
    [InlineData("eval01-agent", VitrineEvaluationPlan.LiveEval01Agent)]
    [InlineData("eval02-workflow", VitrineEvaluationPlan.LiveEval02Workflow)]
    [InlineData("eval03-compare", VitrineEvaluationPlan.LiveEval03AgentVsWorkflow)]
    [InlineData("eval04-stochastic-agent", VitrineEvaluationPlan.LiveEval04StochasticAgent)]
    [InlineData("eval05-stochastic-workflow", VitrineEvaluationPlan.LiveEval05StochasticWorkflow)]
    [InlineData("eval06-safety-probes", VitrineEvaluationPlan.LiveEval06SafetyProbes)]
    public void NamedPaidPlansMapToStableRunnerPlans(
        string value,
        VitrineEvaluationPlan expected)
    {
        var parsed = EvaluationCli.Parse(["--eval-plan", value, "--confirm-paid"]);

        Assert.True(parsed.IsValid, parsed.Error);
        Assert.Equal(expected, parsed.Options?.EvalPlan);
        Assert.True(parsed.Options?.ConfirmPaid);
    }

    [Fact]
    public void OfflineRemainsTheUnconfirmedDefault()
    {
        var defaults = EvaluationCli.Parse([]);
        var named = EvaluationCli.Parse(["--eval-plan", "offline"]);

        Assert.True(defaults.IsValid, defaults.Error);
        Assert.True(named.IsValid, named.Error);
        Assert.Equal(VitrineEvaluationPlan.OfflineSuite, defaults.Options?.EvalPlan);
        Assert.Equal(VitrineEvaluationPlan.OfflineSuite, named.Options?.EvalPlan);
        Assert.False(defaults.Options?.ConfirmPaid);
        Assert.DoesNotContain(typeof(EvaluationCliOptions).GetProperties(), property =>
            string.Equals(property.Name, "ExecutionProfile", StringComparison.Ordinal));
    }

    [Fact]
    public void ScenarioAndRepetitionOptionsAreCanonicalAndBounded()
    {
        var one = EvaluationCli.Parse([
            "--eval-plan", "EVAL04-STOCHASTIC-AGENT",
            "--scenario", "MARCO-GIFT-TRAP",
            "--repetitions", "7",
            "--confirm-paid",
        ]);
        var all = EvaluationCli.Parse([
            "--eval-plan", "eval03-compare",
            "--scenario", "ALL",
            "--confirm-paid",
        ]);

        Assert.True(one.IsValid, one.Error);
        Assert.Equal("marco-gift-trap", one.Options?.ScenarioId);
        Assert.Equal(7, one.Options?.Repetitions);
        Assert.True(all.IsValid, all.Error);
        Assert.Null(all.Options?.ScenarioId);
    }

    [Theory]
    [MemberData(nameof(InvalidPaidSelections))]
    public void InvalidOrUnconfirmedPaidSelectionsAreRejected(string[] arguments)
    {
        var parsed = EvaluationCli.Parse(arguments);

        Assert.False(parsed.IsValid);
    }

    public static TheoryData<string[]> InvalidPaidSelections => new()
    {
        { ["--eval-plan", "eval01-agent"] },
        { ["--eval-plan", "unknown", "--confirm-paid"] },
        { ["--eval-plan", "eval01-agent", "--scenario", "unknown", "--confirm-paid"] },
        { ["--eval-plan", "eval04-stochastic-agent", "--repetitions", "0", "--confirm-paid"] },
        { ["--eval-plan", "eval04-stochastic-agent", "--repetitions", "101", "--confirm-paid"] },
        { ["--eval-plan", "eval04-stochastic-agent", "--repetitions", "many", "--confirm-paid"] },
        { ["--eval-plan", "eval01-agent", "--repetitions", "2", "--confirm-paid"] },
        { ["--eval-plan", "eval06-safety-probes", "--scenario", "all", "--confirm-paid"] },
        { ["--eval-plan", "eval06-safety-probes", "--scenario", "nadia-cross-category", "--confirm-paid"] },
        { ["--eval-plan", "eval06-safety-probes", "--repetitions", "1", "--confirm-paid"] },
        { ["--eval-plan", "eval06-safety-probes", "--repetitions", "2", "--confirm-paid"] },
        { ["--scenario", "all"] },
        { ["--repetitions", "2"] },
        { ["--confirm-paid"] },
        { ["--eval-plan", "eval03-compare", "--confirm-paid", "--json", "live.json"] },
        { ["--eval-plan", "eval03-compare", "--confirm-paid", "--html", "live.html"] },
        { ["--live-subjects-and-judge", "--eval-plan", "eval03-compare", "--confirm-paid"] },
        { ["--controls", "--eval-plan", "offline"] },
    };

    [Fact]
    public void LegacyLiveAliasSelectsEval03AndStillRequiresPaidConfirmation()
    {
        Assert.False(EvaluationCli.Parse(["--live-subjects-and-judge"]).IsValid);

        var parsed = EvaluationCli.Parse(["--all", "--live-subjects-and-judge", "--confirm-paid"]);

        Assert.True(parsed.IsValid, parsed.Error);
        Assert.Equal(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow, parsed.Options?.EvalPlan);
    }

    [Fact]
    public async Task ConfirmedLegacyAliasDispatchesOnlyToNamedEval03Runner()
    {
        var offlineCalls = 0;
        var receivedPlan = VitrineEvaluationPlan.OfflineSuite;
        var services = Services(
            runOffline: () => offlineCalls++,
            runLive: (plan, _, _, _) =>
            {
                receivedPlan = plan;
                return Task.FromResult(Result(plan));
            });
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await EvaluationCli.RunAsync([
            "--live-subjects-and-judge", "--confirm-paid",
        ], output, error, services);

        Assert.Equal(EvaluationExitCodes.Passed, exit);
        Assert.Equal(0, offlineCalls);
        Assert.Equal(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow, receivedPlan);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task NamedPlanDispatchesOnlyToLiveRunnerWithSelectedOptions()
    {
        var offlineCalls = 0;
        var liveCalls = 0;
        VitrineEvaluationPlan? receivedPlan = null;
        LiveEvalOptions? receivedOptions = null;
        var services = Services(
            runOffline: () => offlineCalls++,
            runLive: (plan, options, progress, _) =>
            {
                liveCalls++;
                receivedPlan = plan;
                receivedOptions = options;
                progress?.Report(new(
                    plan,
                    LiveEvalProgressPhase.SessionStarting,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    "Fake paid workload accepted."));
                return Task.FromResult(Result(plan));
            });
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await EvaluationCli.RunAsync([
            "--eval-plan", "eval04-stochastic-agent",
            "--scenario", "marco-gift-trap",
            "--repetitions", "3",
            "--confirm-paid",
        ], output, error, services);

        Assert.Equal(EvaluationExitCodes.Passed, exit);
        Assert.Equal(0, offlineCalls);
        Assert.Equal(1, liveCalls);
        Assert.Equal(VitrineEvaluationPlan.LiveEval04StochasticAgent, receivedPlan);
        Assert.Equal(3, receivedOptions?.Repetitions);
        Assert.Equal(["marco-gift-trap"], receivedOptions?.ScenarioIds);
        Assert.Contains("Fake paid workload accepted", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Eval 04 · Stochastic agent", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Sanitized outcome: session/outcome.json", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task MissingConfirmationCannotReachEitherRunner()
    {
        var calls = 0;
        var services = Services(
            runOffline: () => calls++,
            runLive: (_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Result(VitrineEvaluationPlan.LiveEval01Agent));
            });
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await EvaluationCli.RunAsync(
            ["--eval-plan", "eval01-agent"], output, error, services);

        Assert.Equal(EvaluationExitCodes.InvalidArguments, exit);
        Assert.Equal(0, calls);
        Assert.Contains("--confirm-paid", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveRunnerFailureNeverFallsBackOfflineAndWithholdsItsMessage()
    {
        const string sensitiveFailure = "endpoint.example.invalid secret-live-value";
        var offlineCalls = 0;
        var services = Services(
            runOffline: () => offlineCalls++,
            runLive: (_, _, _, _) => Task.FromException<LiveEvalResult>(
                new IOException(sensitiveFailure)));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await EvaluationCli.RunAsync([
            "--eval-plan", "eval02-workflow",
            "--confirm-paid",
        ], output, error, services);

        Assert.Equal(EvaluationExitCodes.InfrastructureFailure, exit);
        Assert.Equal(0, offlineCalls);
        Assert.Contains(nameof(IOException), error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveFailure, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LiveEvalTerminalStatus.QualityFailed, EvaluationExitCodes.GateFailed)]
    [InlineData(LiveEvalTerminalStatus.NotMeasured, EvaluationExitCodes.NotMeasured)]
    [InlineData(LiveEvalTerminalStatus.InfrastructureError, EvaluationExitCodes.InfrastructureFailure)]
    public async Task LiveTerminalStatusIsReturnedWithoutOfflineReclassification(
        LiveEvalTerminalStatus status,
        int expectedExit)
    {
        var services = Services(
            runOffline: () => throw new InvalidOperationException("offline fallback must not run"),
            runLive: (plan, _, _, _) => Task.FromResult(Result(plan, status)));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await EvaluationCli.RunAsync([
            "--eval-plan", "eval03-compare",
            "--confirm-paid",
        ], output, error, services);

        Assert.Equal(expectedExit, exit);
        Assert.Contains($"status {status}", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void SafetyPlanConsolePrintsProbeEvidenceInsteadOfUseCasePlaceholders()
    {
        var result = Result(
            VitrineEvaluationPlan.LiveEval06SafetyProbes,
            LiveEvalTerminalStatus.QualityFailed) with
        {
            Workload = new(0, 1, 1, 4, 4, 2, 4, 104),
            Configuration = new(
                "vitrine-live-safety", "1.0.0", "fake-judge", "agenteval-redteam-0.35-fallback",
                "safe-rubric-hash", 4_000, 1_200, 1_000,
                [new("robin-agent-live", LiveSubjectArchitecture.Agent, "fake-target", default)])
            {
                Safety = new(["Jailbreak", "SystemPromptExtraction"], 2, 30, 25, 104, "fallback", false),
            },
            Safety = new(
                "robin-agent-live",
                AgentEval.Evals.Meta.MeasurementState.Measured,
                false,
                4,
                3,
                1,
                0,
                0,
                false,
                0,
                [new("Jailbreak", "LLM01", 2, 1, 1, 0, 0)],
                [new("Jailbreak", "probe-01", LiveSafetyProbeOutcome.Compromised,
                    LiveSafetyProbeErrorKind.None, "High", "Behavioral", "Direct")],
                new("measured", 3, 120, 30, 150, 0.001),
                new("measured", 1, 80, 10, 90, 0.0005)),
        };
        using var output = new StringWriter();

        ConsoleReport.Print(result, output);

        var text = output.ToString();
        Assert.Contains("2 attack categories", text, StringComparison.Ordinal);
        Assert.Contains("maximum 104 model call", text, StringComparison.Ordinal);
        Assert.Contains("LLM quality pass threshold: N/A", text, StringComparison.Ordinal);
        Assert.Contains("1 COMPROMISED", text, StringComparison.Ordinal);
        Assert.Contains("Attack Jailbreak", text, StringComparison.Ordinal);
        Assert.Contains("Probe Jailbreak/probe-01", text, StringComparison.Ordinal);
        Assert.Contains("diagnostic No probe execution error.", text, StringComparison.Ordinal);
        Assert.Contains("Target usage: measured", text, StringComparison.Ordinal);
        Assert.Contains("Fallback-judge usage: measured", text, StringComparison.Ordinal);
        Assert.Contains("0 BenchmarkRunner run directories", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0.750", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0 scenario", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LiveEvalTerminalStatus.InfrastructureError, LiveEvalFailureCode.ConfigurationUnavailable,
        "The configured safety evaluator is unavailable.")]
    [InlineData(LiveEvalTerminalStatus.Cancelled, LiveEvalFailureCode.Cancelled,
        "The safety scan was cancelled.")]
    public void SafetyPlanConsoleExplainsAReadinessOrCancellationTerminal(
        LiveEvalTerminalStatus status,
        LiveEvalFailureCode code,
        string detail)
    {
        var result = Result(VitrineEvaluationPlan.LiveEval06SafetyProbes, status) with
        {
            Workload = new(0, 1, 1, 4, 4, 2, 4, 104),
            Failures = [new(code, detail)],
        };
        using var output = new StringWriter();

        ConsoleReport.Print(result, output);

        var text = output.ToString();
        Assert.Contains(status == LiveEvalTerminalStatus.InfrastructureError
                ? "Safety: INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · no probe summary"
                : "Safety: NOT MEASURED · no probe summary",
            text, StringComparison.Ordinal);
        Assert.Contains($"Failure {code}: {detail}", text, StringComparison.Ordinal);
        Assert.Contains($"Exit code: {(int)status}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SafetyConsoleCallsZeroErrorInfrastructureFailureAnIncompleteCensus()
    {
        var result = Result(VitrineEvaluationPlan.LiveEval06SafetyProbes,
            LiveEvalTerminalStatus.InfrastructureError) with
        {
            Workload = new(0, 1, 1, 4, 4, 2, 4, 104),
            Safety = new("robin-agent-live", AgentEval.Evals.Meta.MeasurementState.NotMeasured, null,
                3, 3, 0, 0, 0, false, 0,
                [new("Jailbreak", "LLM01", 3, 3, 0, 0, 0)],
                [
                    new("Jailbreak", "probe-01", LiveSafetyProbeOutcome.Resisted,
                        LiveSafetyProbeErrorKind.None, "High", "Behavioral", "Direct"),
                    new("Jailbreak", "probe-02", LiveSafetyProbeOutcome.Resisted,
                        LiveSafetyProbeErrorKind.None, "High", "Behavioral", "Direct"),
                    new("Jailbreak", "probe-03", LiveSafetyProbeOutcome.Resisted,
                        LiveSafetyProbeErrorKind.None, "High", "Behavioral", "Direct"),
                ],
                LiveUsageEvidence.NotReported, LiveUsageEvidence.NotReported),
        };
        using var output = new StringWriter();

        ConsoleReport.Print(result, output);

        var text = output.ToString();
        Assert.Contains("INFRASTRUCTURE ERROR · SAFETY VERDICT NOT MEASURED · INCOMPLETE CENSUS",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("0/3 PROBES ERRORED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpNamesEveryPlanAndPaidSafetySwitch()
    {
        foreach (var value in new[]
                 {
                     "offline",
                     "eval01-agent",
                     "eval02-workflow",
                     "eval03-compare",
                     "eval04-stochastic-agent",
                     "eval05-stochastic-workflow",
                     "eval06-safety-probes",
                 })
            Assert.Contains(value, EvaluationCli.HelpText, StringComparison.Ordinal);
        Assert.Contains("--scenario", EvaluationCli.HelpText, StringComparison.Ordinal);
        foreach (var scenario in LiveUseCaseScenarios.All)
            Assert.Contains(scenario.Id, EvaluationCli.HelpText, StringComparison.Ordinal);
        Assert.Contains("--repetitions", EvaluationCli.HelpText, StringComparison.Ordinal);
        Assert.Contains("--confirm-paid", EvaluationCli.HelpText, StringComparison.Ordinal);
        Assert.Contains("never falls back", EvaluationCli.HelpText, StringComparison.Ordinal);
    }

    private static EvaluationCliServices Services(
        Action runOffline,
        Func<VitrineEvaluationPlan, LiveEvalOptions, IProgress<LiveEvalProgress>?, CancellationToken,
            Task<LiveEvalResult>> runLive) =>
        new(
            (_, _) =>
            {
                runOffline();
                return Task.FromResult(new SuiteResult([], []));
            },
            _ => Task.FromResult<IReadOnlyList<ControlResult>>([]),
            (_, _, _) => Task.CompletedTask,
            RunLivePlan: runLive);

    private static LiveEvalResult Result(
        VitrineEvaluationPlan plan,
        LiveEvalTerminalStatus status = LiveEvalTerminalStatus.Passed)
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        return new(
            plan,
            status,
            "cli-fake-session",
            now,
            now,
            new(1, 1, 1, 1, 1),
            0.75,
            [],
            new("cli-fake-definition", "1.0.0", "fake-judge", "fake-prompt", "fake-rubric",
                4_000, 1_200, 1_000, []),
            [],
            [],
            [],
            [],
            [],
            new("workspace", "session", "session/outcome.json", "live-sessions/index.json"));
    }
}
