// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals.Live;
public static class Eval01_Agent
{
    public static Task<LiveEvalResult> RunAsync(bool paidExecutionConfirmed, LiveEvalOptions? options = null, LiveEvalServices? services = null,
        IProgress<LiveEvalProgress>? progress = null, CancellationToken cancellationToken = default) =>
        LiveEvaluationPlanRunner.RunAsync(VitrineEvaluationPlan.LiveEval01Agent, paidExecutionConfirmed,
            options, services, progress, cancellationToken);
}
