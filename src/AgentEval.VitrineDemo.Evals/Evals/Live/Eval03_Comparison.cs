// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals.Live;
public static class Eval03_Comparison
{
    public static Task<LiveEvalResult> RunAsync(LiveEvalOptions? options = null, LiveEvalServices? services = null,
        IProgress<LiveEvalProgress>? progress = null, CancellationToken cancellationToken = default) =>
        LiveEvaluationExecutor.RunAsync(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow, options, services, progress, cancellationToken);
}
