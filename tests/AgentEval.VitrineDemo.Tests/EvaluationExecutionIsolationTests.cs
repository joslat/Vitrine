// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Runtime;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Catalog;

namespace AgentEval.VitrineDemo.Tests;

public sealed class EvaluationExecutionIsolationTests
{
    [Fact]
    public void OfflineSuiteHasNoLegacyLiveExecutionInput()
    {
        Assert.Equal([EvaluationExecutionProfile.OfflineDeterministic],
            Enum.GetValues<EvaluationExecutionProfile>());

        var run = typeof(EvaluationSuite).GetMethods()
            .Single(method => method.Name == nameof(EvaluationSuite.RunAsync));
        Assert.DoesNotContain(run.GetParameters(), parameter =>
            parameter.ParameterType == typeof(EvaluationExecutionProfile));

        Assert.DoesNotContain(typeof(VitrineRunRequest).GetProperties(), property =>
            string.Equals(property.Name, "EvaluationProfile", StringComparison.Ordinal));
        Assert.Contains(typeof(RunSetupViewModel).GetProperties(), property =>
            string.Equals(property.Name, nameof(RunSetupViewModel.EvaluationPlans), StringComparison.Ordinal));
    }

    [Fact]
    public void PaidCompatibilityAliasMapsOnlyToConfirmedNamedEval03()
    {
        var offline = EvaluationCli.Parse([]);
        var unconfirmed = EvaluationCli.Parse(["--all", "--live-subjects-and-judge"]);
        var confirmed = EvaluationCli.Parse([
            "--all", "--live-subjects-and-judge", "--confirm-paid",
        ]);

        Assert.Equal(VitrineEvaluationPlan.OfflineSuite, offline.Options?.EvalPlan);
        Assert.False(unconfirmed.IsValid);
        Assert.True(confirmed.IsValid, confirmed.Error);
        Assert.Equal(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow, confirmed.Options?.EvalPlan);
        Assert.True(confirmed.Options?.ConfirmPaid);
        Assert.False(EvaluationCli.Parse([
            "--controls", "--live-subjects-and-judge", "--confirm-paid",
        ]).IsValid);
        Assert.False(EvaluationCli.Parse([
            "--gates-only", "--live-subjects-and-judge", "--confirm-paid",
        ]).IsValid);
        Assert.False(EvaluationCli.Parse([
            "--ablate-catalogue", "--live-subjects-and-judge", "--confirm-paid",
        ]).IsValid);
    }

    [Fact]
    public void AppRequestDefaultsToOfflinePlanWithoutAConfirmation()
    {
        var request = new VitrineRunRequest(VitrineRunMode.Evals, Personas.NadiaUserId);

        Assert.Equal(VitrineEvaluationPlan.OfflineSuite, request.EvaluationPlan);
        Assert.False(request.PaidExecutionConfirmed);
    }

    [Fact]
    public async Task OfflineJudgedGateReportsZeroProviderUsage()
    {
        EvaluationExecutionProvenance? execution = null;

        var gate = await EvaluationSuite.JudgedGateAsync(
            default,
            captureExecution: value => execution = value);

        Assert.Equal(GateMeasurementOutcome.Measured, gate.Outcome);
        Assert.NotNull(execution);
        Assert.Equal(EvaluationExecutionProfile.OfflineDeterministic, execution!.Profile);
        Assert.False(execution.UsesExternalModels);
        Assert.Equal(0, execution.TotalModelCalls);
        Assert.Equal(0, execution.JudgeModelCalls);
        Assert.Null(execution.DeploymentName);
        Assert.Null(execution.Demo01SubjectTokens);
        Assert.Null(execution.Demo02SubjectTokens);
        Assert.Null(execution.JudgeTokens);
        Assert.Null(execution.EstimatedCostUsd);
    }
}
