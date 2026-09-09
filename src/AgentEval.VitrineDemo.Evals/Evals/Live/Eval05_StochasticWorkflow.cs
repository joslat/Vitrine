// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals.Live;
public static class Eval05_StochasticWorkflow
{
    public static Task<LiveEvalResult> RunAsync(LiveEvalOptions? options = null, LiveEvalServices? services = null,
        IProgress<LiveEvalProgress>? progress = null, CancellationToken cancellationToken = default) =>
        LiveEvaluationExecutor.RunAsync(VitrineEvaluationPlan.LiveEval05StochasticWorkflow, options, services, progress, cancellationToken);
}
