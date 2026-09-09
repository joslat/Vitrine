// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App.Models;
using AgentEval.VitrineDemo.Evals;
using AgentEval.VitrineDemo.Evals.Live;
using Galaxus.RecommendationAgent.Agents;
using Galaxus.RecommendationAgent.Workflows;
using Microsoft.Agents.AI.Workflows;

namespace AgentEval.VitrineDemo.App.Runtime;

/// <summary>Builds topology from the actual runtime surfaces, never from XAML truth data.</summary>
public static class VitrineGraphFactory
{
    public static VitrineGraphSnapshot FromRegisteredDemo01Functions()
    {
        var graph = FromRegisteredDemo01Functions(RecommendationAgentFactory.PrepareReadOnlyTools());
        return graph with
        {
            Name = "Demo01 · registered function preview",
            Source = VitrineGraphSource.RegisteredFunctionsPreview,
            RuntimeSourceId = null,
        };
    }

    public static VitrineGraphSnapshot FromRegisteredDemo01Functions(RecommendationToolSet toolSet)
    {
        ArgumentNullException.ThrowIfNull(toolSet);
        var tools = toolSet.Tools;
        var nodes = new List<VitrineGraphNode>
        {
            new("customer", "Customer", "actor", IsEntry: true),
            new("model", "Model boundary", "model"),
            new(RecommendationAgentFactory.AgentName, RecommendationAgentFactory.AgentName, "agent"),
            new("guardrails", "Mechanical guardrails", "guardrail", IsExit: true),
        };
        nodes.AddRange(tools.Select(tool => new VitrineGraphNode(
            tool.Name,
            tool.Name,
            "tool",
            tool.Description)));

        var edges = new List<VitrineGraphEdge>
        {
            new("customer-robin", "customer", RecommendationAgentFactory.AgentName, "request"),
            new("robin-model", RecommendationAgentFactory.AgentName, "model", "model turn"),
            new("robin-guards", RecommendationAgentFactory.AgentName, "guardrails", "presented artifact"),
        };
        edges.AddRange(tools.Select(tool => new VitrineGraphEdge(
            $"robin-{tool.Name}",
            RecommendationAgentFactory.AgentName,
            tool.Name,
            "registered function")));
        return new("Demo01 · registered ChatClientAgent surface", VitrineGraphSource.RegisteredFunctions, nodes, edges,
            toolSet.Id);
    }

    public static VitrineGraphSnapshot FromPreparedWorkflow(
        Workflow workflow,
        IReadOnlyList<string> executorIds)
    {
        var adapter = AgentEval.MAF.MAFWorkflowAdapter.FromMAFWorkflow(
            workflow,
            DiscoveryWorkflowFactory.WorkflowName,
            executorIds,
            "bounded-review-loop");
        var graph = adapter.GraphDefinition
            ?? throw new InvalidOperationException("AgentEval did not return a workflow graph snapshot.");
        var topology = DiscoveryTopology.Shipped;
        topology.RequireExact(new(
            executorIds,
            graph.Edges.Select(static edge => (edge.SourceExecutorId, edge.TargetExecutorId)).ToArray()));
        var nodes = graph.Nodes.Select(node => new VitrineGraphNode(
            node.NodeId,
            node.DisplayName ?? node.NodeId,
            node.ExecutorType ?? "executor",
            node.Description,
            node.IsEntryPoint,
            node.IsExitNode)).ToArray();
        var edges = graph.Edges.Select(edge =>
        {
            var route = topology.RequireRoute(edge.SourceExecutorId, edge.TargetExecutorId);
            return new VitrineGraphEdge(
                route.Id,
                edge.SourceExecutorId,
                edge.TargetExecutorId,
                route.Label,
                route.IsConditional,
                route.IsLoopBack);
        }).ToArray();
        return new("Demo02 · actual prepared MAF workflow", VitrineGraphSource.MafWorkflow, nodes, edges);
    }

    /// <summary>
    /// Honest pre-run projection of the subject-owned topology contract. The executable MAF graph
    /// replaces it as soon as the coordinator prepares the exact workflow instance.
    /// </summary>
    public static VitrineGraphSnapshot FromDiscoveryTopologyContract()
    {
        var topology = DiscoveryTopology.Shipped;
        var nodes = topology.Nodes.OrderBy(static node => node.Order)
            .Select(node => new VitrineGraphNode(
                node.Id,
                node.Id,
                "executor",
                "Subject-owned DiscoveryTopology contract preview; not a runtime observation.",
                node.IsEntry,
                node.IsExit))
            .ToArray();
        var edges = topology.Routes.Select(route => new VitrineGraphEdge(
            route.Id,
            route.SourceExecutorId,
            route.TargetExecutorId,
            route.Label,
            route.IsConditional,
            route.IsLoopBack)).ToArray();
        return new("Demo02 · typed DiscoveryTopology preview",
            VitrineGraphSource.DiscoveryTopologyPreview, nodes, edges);
    }

    public static VitrineGraphSnapshot ForEvaluationSuite() => EvaluationPlan(
        "EvaluationSuite · typed execution-plan preview",
        VitrineGraphSource.EvaluationServicePreview);

    public static VitrineGraphSnapshot ForRunningEvaluationSuite() => EvaluationPlan(
        "EvaluationSuite · runtime typed progress",
        VitrineGraphSource.EvaluationService);

    public static VitrineGraphSnapshot ForLiveEvaluation(VitrineEvaluationPlan plan) =>
        LiveEvaluationPlan(plan, VitrineGraphSource.EvaluationServicePreview);

    public static VitrineGraphSnapshot ForRunningLiveEvaluation(VitrineEvaluationPlan plan) =>
        LiveEvaluationPlan(plan, VitrineGraphSource.EvaluationService);

    private static VitrineGraphSnapshot LiveEvaluationPlan(
        VitrineEvaluationPlan plan,
        VitrineGraphSource source)
    {
        var descriptor = VitrineEvaluationPlans.Require(plan);
        if (!descriptor.IsLive)
            throw new ArgumentException("The offline suite owns its own graph.", nameof(plan));
        if (plan == VitrineEvaluationPlan.LiveEval06SafetyProbes)
            return SafetyEvaluationPlan(descriptor, source);

        var includesAgent = plan is VitrineEvaluationPlan.LiveEval01Agent
            or VitrineEvaluationPlan.LiveEval03AgentVsWorkflow
            or VitrineEvaluationPlan.LiveEval04StochasticAgent;
        var includesWorkflow = plan is VitrineEvaluationPlan.LiveEval02Workflow
            or VitrineEvaluationPlan.LiveEval03AgentVsWorkflow
            or VitrineEvaluationPlan.LiveEval05StochasticWorkflow;
        var nodes = new List<VitrineGraphNode>
        {
            new("live-session", descriptor.Label, "evaluation", descriptor.Description, IsEntry: true),
        };
        var comparison = plan == VitrineEvaluationPlan.LiveEval03AgentVsWorkflow;
        string CheckNodeId(string armNode, string checkKey) => comparison
            ? $"{armNode}:{checkKey}"
            : checkKey;
        void AddCheckNode(string armNode, string checkKey, string label, string description)
        {
            var architecture = armNode == "live-agent" ? "Agent" : "Workflow";
            nodes.Add(new(CheckNodeId(armNode, checkKey),
                comparison ? $"{architecture} · {label}" : label,
                "evaluation-check",
                description));
        }
        if (includesAgent)
            nodes.Add(new("live-agent", "Robin agent + tools", "evaluation-subject",
                "Fresh configured ChatClientAgent execution for each selected case and repetition."));
        if (includesWorkflow)
            nodes.Add(new("live-workflow", "Discovery workflow", "evaluation-subject",
                "Fresh configured MAF workflow execution for each selected case and repetition."));
        if (includesAgent)
        {
            AddCheckNode("live-agent", LiveUseCaseBenchmark.UseCaseQualityCheckKey, "LLM use-case judge",
                "AgentEval AtomicLlmEval applies the scenario-specific authored criteria.");
            AddCheckNode("live-agent", LiveUseCaseBenchmark.ResponseObservedCheckKey, "Response observed",
                "Deterministic response-observation predicate.");
            AddCheckNode("live-agent", LiveUseCaseBenchmark.AgentToolJournalCheckKey, "Agent tool journal",
                "Deterministic tool-call reconciliation for the agent architecture.");
        }
        if (includesWorkflow)
        {
            AddCheckNode("live-workflow", LiveUseCaseBenchmark.UseCaseQualityCheckKey, "LLM use-case judge",
                "AgentEval AtomicLlmEval applies the scenario-specific authored criteria.");
            AddCheckNode("live-workflow", LiveUseCaseBenchmark.ResponseObservedCheckKey, "Response observed",
                "Deterministic response-observation predicate.");
            AddCheckNode("live-workflow", LiveUseCaseBenchmark.WorkflowTraceCheckKey, "Workflow trace",
                "Deterministic executor/route reconciliation for the workflow architecture.");
        }
        nodes.AddRange(
        [
            new("live-persistence", "Local AgentEval evidence", "evaluation-evidence",
                "One run directory per arm and repetition plus a sanitized session index."),
            new("live-complete", "Live evaluation outcome", "evaluation", IsExit: true),
        ]);

        var edges = new List<VitrineGraphEdge>();
        if (includesAgent)
            edges.Add(new("live-session-agent", "live-session", "live-agent", "run selected cases"));
        if (includesWorkflow)
            edges.Add(new("live-session-workflow", "live-session", "live-workflow", "run selected cases"));
        void AddCheckBranch(string subject, string checkKey, string label)
        {
            var nodeId = CheckNodeId(subject, checkKey);
            edges.Add(new($"{subject}-{checkKey}", subject, nodeId, label));
            edges.Add(new($"{nodeId}-persist", nodeId, "live-persistence", "record evidence"));
        }
        if (includesAgent)
        {
            AddCheckBranch("live-agent", LiveUseCaseBenchmark.UseCaseQualityCheckKey, "judge independently");
            AddCheckBranch("live-agent", LiveUseCaseBenchmark.ResponseObservedCheckKey, "check response");
            AddCheckBranch("live-agent", LiveUseCaseBenchmark.AgentToolJournalCheckKey, "reconcile tool calls");
        }
        if (includesWorkflow)
        {
            AddCheckBranch("live-workflow", LiveUseCaseBenchmark.UseCaseQualityCheckKey, "judge independently");
            AddCheckBranch("live-workflow", LiveUseCaseBenchmark.ResponseObservedCheckKey, "check response");
            AddCheckBranch("live-workflow", LiveUseCaseBenchmark.WorkflowTraceCheckKey, "reconcile workflow trace");
        }
        edges.AddRange(
        [
            new("live-persist-complete", "live-persistence", "live-complete", "complete"),
        ]);
        return new($"{descriptor.Label} · {(source == VitrineGraphSource.EvaluationServicePreview ? "plan preview" : "runtime progress")}",
            source, Array.AsReadOnly(nodes.ToArray()), Array.AsReadOnly(edges.ToArray()));
    }

    private static VitrineGraphSnapshot SafetyEvaluationPlan(
        VitrineEvaluationPlanDescriptor descriptor,
        VitrineGraphSource source) => new(
        $"{descriptor.Label} · {(source == VitrineGraphSource.EvaluationServicePreview ? "plan preview" : "runtime progress")}",
        source,
        [
            new("live-session", descriptor.Label, "evaluation", descriptor.Description, IsEntry: true),
            new("live-safety-target", "Fresh Robin target", "evaluation-subject",
                "Robin-only paid target. The extraction canary is in memory and never enters persisted evidence."),
            new("live-safety-jailbreak", "Jailbreak probes", "evaluation-check",
                "Released AgentEval JailbreakAttack; bounded to two probes by the App plan."),
            new("live-safety-extraction", "Instruction extraction", "evaluation-check",
                "Released AgentEval SystemPromptExtractionAttack; raw canary, prompts, and responses are excluded."),
            new("live-safety-findings", "Redacted safety findings", "evaluation-check",
                "Compromised, resisted, inconclusive, and error census with probe fidelity; no generic success semantic."),
            new("live-persistence", "Local redacted evidence", "evaluation-evidence",
                "Sanitized safety receipt and session index; no raw attack content."),
            new("live-complete", "Safety evaluation outcome", "evaluation", IsExit: true),
        ],
        [
            new("safety-session-target", "live-session", "live-safety-target", "run fresh Robin target"),
            new("safety-target-jailbreak", "live-safety-target", "live-safety-jailbreak", "bounded jailbreak probes"),
            new("safety-target-extraction", "live-safety-target", "live-safety-extraction", "bounded extraction probes"),
            new("safety-jailbreak-findings", "live-safety-jailbreak", "live-safety-findings", "redacted outcomes"),
            new("safety-extraction-findings", "live-safety-extraction", "live-safety-findings", "redacted outcomes"),
            new("safety-findings-persist", "live-safety-findings", "live-persistence", "persist sanitized receipt"),
            new("live-persist-complete", "live-persistence", "live-complete", "complete"),
        ]);

    private static VitrineGraphSnapshot EvaluationPlan(string name, VitrineGraphSource source) => new(
        name,
        source,
        [
            new("catalogue", "1 · Catalogue contract", "evaluation-gate",
                "Atomic catalogue-shape gate; status changes only when typed gate progress arrives.", IsEntry: true),
            new("topology", "2 · Workflow topology", "evaluation-gate",
                "MAF workflow topology gate over the prepared runtime graph."),
            new("judged", "3 · Matched judged quality", "evaluation-gate",
                "Matched Demo01 and Demo02 subject evaluation in the credential-free offline suite. Paid Eval01–Eval06 plans use their own runtime graph."),
            new("injection", "4 · RedTeam injection", "evaluation-gate",
                "AgentEval RedTeam injection-resistance gate."),
            new("recall", "5 · Memory recall", "evaluation-gate",
                "AgentEval memory recall gate."),
            new("honesty", "6 · Honesty claim", "evaluation-gate",
                "Atomic honesty-claim gate with explicit measurement semantics."),
            new(VitrineOfflineBenchmark.ScreenedDeliverableCheckKey, "Check · screened deliverable", "evaluation-check",
                "Admitted AtomicCodeEval applied to both customer cases across the Demo01, Demo02, and degraded arms."),
            new(VitrineOfflineBenchmark.CataloguedSkuCheckKey, "Check · catalogue SKU", "evaluation-check",
                "Requires a cited SKU and resolves every cited SKU against Catalogue.Default."),
            new(VitrineOfflineBenchmark.CustomerReasonCheckKey, "Check · customer reason", "evaluation-check",
                "Requires a customer-facing recommendation reason."),
            new(VitrineOfflineBenchmark.NoPurchaseClaimCheckKey, "Check · no purchase claim", "evaluation-check",
                "Rejects a model answer that claims an order, checkout, or payment completed."),
            new(VitrineOfflineBenchmark.InterestGroundingCheckKey, "Check · interest grounding", "evaluation-check",
                "Requires stated-need or customer-interest grounding."),
            new("benchmark", "Native benchmark persistence", "evaluation-evidence",
                "AgentEval Definition → Arm → BenchmarkRunner → Score evidence persisted under the local workspace."),
            new("controls", "43-control panel", "evaluation-controls",
                "Aggregate node for the exact registered control panel; its ×N count is the number of completed typed control results."),
            new("suite", "Suite completion", "evaluation",
                "Final process-equivalent result emitted by typed SuiteCompleted progress.", IsExit: true),
        ],
        [
            new("eval-catalogue-topology", "catalogue", "topology", "then"),
            new("eval-topology-judged", "topology", "judged", "then"),
            new("eval-judged-injection", "judged", "injection", "then"),
            new("eval-injection-recall", "injection", "recall", "then"),
            new("eval-recall-honesty", "recall", "honesty", "then"),
            new("eval-honesty-screened", "honesty", VitrineOfflineBenchmark.ScreenedDeliverableCheckKey, "run admitted checks"),
            new("eval-screened-sku", VitrineOfflineBenchmark.ScreenedDeliverableCheckKey, VitrineOfflineBenchmark.CataloguedSkuCheckKey, "then"),
            new("eval-sku-reason", VitrineOfflineBenchmark.CataloguedSkuCheckKey, VitrineOfflineBenchmark.CustomerReasonCheckKey, "then"),
            new("eval-reason-purchase", VitrineOfflineBenchmark.CustomerReasonCheckKey, VitrineOfflineBenchmark.NoPurchaseClaimCheckKey, "then"),
            new("eval-purchase-interest", VitrineOfflineBenchmark.NoPurchaseClaimCheckKey, VitrineOfflineBenchmark.InterestGroundingCheckKey, "then"),
            new("eval-interest-persist", VitrineOfflineBenchmark.InterestGroundingCheckKey, "benchmark", "persist + score arms"),
            new("eval-benchmark-controls", "benchmark", "controls", "verify registered panel"),
            new("eval-controls-suite", "controls", "suite", "complete"),
        ]);
}
