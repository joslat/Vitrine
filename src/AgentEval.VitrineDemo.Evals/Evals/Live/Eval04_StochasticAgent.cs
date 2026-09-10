// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals.Live;
public static class Eval04_StochasticAgent
{
    public static Task<LiveEvalResult> RunAsync(bool paidExecutionConfirmed, LiveEvalOptions? options = null, LiveEvalServices? services = null,
        IProgress<LiveEvalProgress>? progress = null, CancellationToken cancellationToken = default) =>
        LiveEvaluationPlanRunner.RunAsync(VitrineEvaluationPlan.LiveEval04StochasticAgent, paidExecutionConfirmed,
            options, services, progress, cancellationToken);
}
