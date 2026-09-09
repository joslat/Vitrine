// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals.Live;
public static class Eval02_Workflow
{
    public static Task<LiveEvalResult> RunAsync(LiveEvalOptions? options = null, LiveEvalServices? services = null,
        IProgress<LiveEvalProgress>? progress = null, CancellationToken cancellationToken = default) =>
        LiveEvaluationExecutor.RunAsync(VitrineEvaluationPlan.LiveEval02Workflow, options, services, progress, cancellationToken);
}
