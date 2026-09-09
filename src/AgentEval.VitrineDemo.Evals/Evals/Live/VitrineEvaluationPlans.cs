// SPDX-License-Identifier: MIT

namespace AgentEval.VitrineDemo.Evals.Live;

/// <summary>The stable plans exposed by the Vitrine evaluation picker.</summary>
public enum VitrineEvaluationPlan
{
    OfflineSuite,
    LiveEval01Agent,
    LiveEval02Workflow,
    LiveEval03AgentVsWorkflow,
    LiveEval04StochasticAgent,
    LiveEval05StochasticWorkflow,
    LiveEval06SafetyProbes,
}

/// <summary>UI-safe facts about one evaluation plan.</summary>
public sealed record VitrineEvaluationPlanDescriptor(
    VitrineEvaluationPlan Plan,
    string Label,
    string Description,
    bool IsLive,
    bool IsPaid,
    bool SupportsRepetitions,
    bool SupportsScenarioSelection,
    bool IsComparison,
    int DefaultRepetitions)
{
    public override string ToString() => Label;
}

/// <summary>The ordered, stable evaluation-plan catalogue.</summary>
public static class VitrineEvaluationPlans
{
    public static IReadOnlyList<VitrineEvaluationPlanDescriptor> All { get; } =
    [
        new(VitrineEvaluationPlan.OfflineSuite, "Offline suite",
            "Run the deterministic, no-model evaluation suite.", false, false, false, false, false, 1),
        new(VitrineEvaluationPlan.LiveEval01Agent, "Eval 01 · Agent",
            "Judge one fresh Robin agent run for each selected use case.", true, true, false, true, false, 1),
        new(VitrineEvaluationPlan.LiveEval02Workflow, "Eval 02 · Workflow",
            "Judge one fresh discovery-workflow run for each selected use case.", true, true, false, true, false, 1),
        new(VitrineEvaluationPlan.LiveEval03AgentVsWorkflow, "Eval 03 · Agent vs workflow",
            "Run matched fresh agent and workflow arms and compare them case by case.", true, true, true, true, true, 1),
        new(VitrineEvaluationPlan.LiveEval04StochasticAgent, "Eval 04 · Stochastic agent",
            "Repeat the agent arm and report reliability with a Wilson interval.", true, true, true, true, false, 5),
        new(VitrineEvaluationPlan.LiveEval05StochasticWorkflow, "Eval 05 · Stochastic workflow",
            "Repeat the workflow arm and report reliability with a Wilson interval.", true, true, true, true, false, 5),
        new(VitrineEvaluationPlan.LiveEval06SafetyProbes, "Eval 06 · Safety probes",
            "Run a small real AgentEval jailbreak and hidden-instruction extraction scan against fresh Robin targets only.",
            true, true, false, false, false, 1),
    ];

    public static VitrineEvaluationPlanDescriptor Require(VitrineEvaluationPlan plan) =>
        All.FirstOrDefault(item => item.Plan == plan)
        ?? throw new ArgumentOutOfRangeException(nameof(plan), plan, "Unknown evaluation plan.");
}
