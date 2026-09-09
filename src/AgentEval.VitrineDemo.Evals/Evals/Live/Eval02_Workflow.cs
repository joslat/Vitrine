// SPDX-License-Identifier: MIT
namespace AgentEval.VitrineDemo.Evals.Live;
public static class Eval02_Workflow
{
    public static Task<LiveEvalResult> RunAsync(bool paidExecutionConfirmed, LiveEvalOptions? options = null, LiveEvalServices? services = null,
        IProgress<LiveEvalProgress>? progress = null, CancellationToken cancellationToken = default) =>
        LiveEvaluationPlanRunner.RunAsync(VitrineEvaluationPlan.LiveEval02Workflow, paidExecutionConfirmed,
            options, services, progress, cancellationToken);
}
