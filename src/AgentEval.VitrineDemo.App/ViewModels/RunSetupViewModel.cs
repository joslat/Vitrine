// SPDX-License-Identifier: MIT

using Galaxus.RecommendationAgent;
using Galaxus.RecommendationAgent.Catalog;
using Galaxus.RecommendationAgent.Demos;
using AgentEval.VitrineDemo.Evals.Live;

namespace AgentEval.VitrineDemo.App.ViewModels;

public sealed record PersonaOption(
    string Id,
    string Label,
    string ScenarioTitle,
    string ScenarioDescription,
    string CanonicalQuery)
{
    public override string ToString() => Label;
}

public sealed record ExecutionArmOption(RecommendationExecutionArm Arm, string Label, string Description)
{
    public override string ToString() => Label;
}

public sealed record LiveScenarioOption(string? Id, string Label, string Description, string Query)
{
    public int CaseCount => Id is null ? LiveUseCaseScenarios.All.Count : 1;
    public override string ToString() => Label;
}

public sealed class RunSetupViewModel : BindableBase
{
    public const int SafetyAttackCategories = 2;
    public const int SafetyMaxProbesPerAttack = 2;
    public const int SafetyMaxTargetModelCallsPerProbe = 25;
    public const int SafetyMaximumModelCalls =
        SafetyAttackCategories * SafetyMaxProbesPerAttack * (SafetyMaxTargetModelCallsPerProbe + 1);
    private PersonaOption _selectedPersona;
    private ExecutionArmOption _selectedArm;
    private VitrineEvaluationPlanDescriptor _selectedEvaluationPlan;
    private LiveScenarioOption _selectedLiveScenario;
    private int _evaluationRepetitions = 5;
    private bool _paidEvaluationAcknowledged;
    private bool _personalizationEnabled = true;
    private int _maxRounds = 3;
    private int _audiencePacingMilliseconds = 90;

    public RunSetupViewModel()
    {
        Personas = UserProfiles.All
            .Select(profile =>
            {
                var scenario = PersonaScenarios.Require(profile.Id);
                return new PersonaOption(
                    profile.Id,
                    $"{profile.User.DisplayName} · {profile.Id}",
                    scenario.Title,
                    scenario.Description,
                    scenario.Query);
            })
            .ToArray();
        _selectedPersona = Personas.First(option => option.Id == GalaxusDemoPrompts.NadiaUserId);
        Arms =
        [
            new(RecommendationExecutionArm.ScriptedAgent, "Mocked model + real runtime", "Deterministic model choices drive the real ChatClientAgent/tools or every model-backed MAF workflow stage."),
            new(RecommendationExecutionArm.ZeroModelBaseline, "Zero-model baseline", "Retrieval and code only; a separately labelled comparison arm."),
            new(RecommendationExecutionArm.LiveAzure, "Live Azure", "Optional paid model execution; deployment name only is disclosed."),
        ];
        _selectedArm = Arms[0];
        EvaluationPlans = VitrineEvaluationPlans.All;
        _selectedEvaluationPlan = EvaluationPlans[0];
        LiveScenarios =
        [
            new(null, "All four live use cases",
                string.Join(" ", LiveUseCaseScenarios.All.Select(scenario =>
                    $"{scenario.Title} ({scenario.PersonaId}): {scenario.ExpectedBehavior}")),
                string.Join(Environment.NewLine + Environment.NewLine,
                    LiveUseCaseScenarios.All.Select(scenario => $"{scenario.Id} · {scenario.Query}"))),
            .. LiveUseCaseScenarios.All.Select(scenario => new LiveScenarioOption(
                scenario.Id,
                $"{scenario.Title} · {scenario.PersonaId}",
                $"{scenario.Description} Expected behavior: {scenario.ExpectedBehavior}",
                scenario.Query)),
        ];
        _selectedLiveScenario = LiveScenarios.First(option =>
            string.Equals(option.Id, "nadia-cross-category", StringComparison.Ordinal));
    }

    public IReadOnlyList<PersonaOption> Personas { get; }

    public IReadOnlyList<ExecutionArmOption> Arms { get; }

    public IReadOnlyList<VitrineEvaluationPlanDescriptor> EvaluationPlans { get; }

    public IReadOnlyList<LiveScenarioOption> LiveScenarios { get; }

    public PersonaOption SelectedPersona
    {
        get => _selectedPersona;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedPersona, value)) return;
            RaisePropertyChanged(nameof(ScenarioTitle));
            RaisePropertyChanged(nameof(ScenarioDescription));
            RaisePropertyChanged(nameof(ScenarioQuery));
        }
    }

    public ExecutionArmOption SelectedArm
    {
        get => _selectedArm;
        set
        {
            if (SetProperty(ref _selectedArm, value)) RaisePropertyChanged(nameof(ArmDescription));
        }
    }

    public VitrineEvaluationPlanDescriptor SelectedEvaluationPlan
    {
        get => _selectedEvaluationPlan;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedEvaluationPlan, value)) return;
            EvaluationRepetitions = value.DefaultRepetitions;
            PaidEvaluationAcknowledged = false;
            RaiseEvaluationPlanProperties();
        }
    }

    public LiveScenarioOption SelectedLiveScenario
    {
        get => _selectedLiveScenario;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedLiveScenario, value)) return;
            PaidEvaluationAcknowledged = false;
            RaiseEvaluationPlanProperties();
        }
    }

    public int EvaluationRepetitions
    {
        get => _evaluationRepetitions;
        set
        {
            var minimum = VitrineEvaluationPlans.IsStochastic(SelectedEvaluationPlan.Plan)
                ? VitrineEvaluationPlans.MinimumStochasticRepetitions
                : 1;
            if (!SetProperty(ref _evaluationRepetitions, Math.Clamp(value, minimum, 30))) return;
            PaidEvaluationAcknowledged = false;
            RaiseEvaluationPlanProperties();
        }
    }

    public bool PaidEvaluationAcknowledged
    {
        get => _paidEvaluationAcknowledged;
        set
        {
            if (!SetProperty(ref _paidEvaluationAcknowledged, value)) return;
            RaisePropertyChanged(nameof(CanRunSelectedEvaluationPlan));
        }
    }

    /// <summary>Positive UI semantic: checked means profile signals are allowed.</summary>
    public bool PersonalizationEnabled
    {
        get => _personalizationEnabled;
        set => SetProperty(ref _personalizationEnabled, value);
    }

    public int MaxRounds
    {
        get => _maxRounds;
        set => SetProperty(ref _maxRounds, Math.Clamp(value, 1, 6));
    }

    public int AudiencePacingMilliseconds
    {
        get => _audiencePacingMilliseconds;
        set => SetProperty(ref _audiencePacingMilliseconds, Math.Clamp(value, 0, 1000));
    }

    public string ArmDescription => SelectedArm.Description;

    public string ScenarioTitle => SelectedPersona.ScenarioTitle;

    public string ScenarioDescription => SelectedPersona.ScenarioDescription;

    public string ScenarioQuery => SelectedPersona.CanonicalQuery;

    public bool IsLiveEvaluationPlan => SelectedEvaluationPlan.IsLive;

    public bool SupportsEvaluationRepetitions => SelectedEvaluationPlan.SupportsRepetitions;

    public int MinimumEvaluationRepetitions =>
        VitrineEvaluationPlans.IsStochastic(SelectedEvaluationPlan.Plan)
            ? VitrineEvaluationPlans.MinimumStochasticRepetitions
            : 1;

    public bool SupportsLiveScenarioSelection => SelectedEvaluationPlan.SupportsScenarioSelection;

    public bool IsSafetyEvaluationPlan =>
        SelectedEvaluationPlan.Plan == VitrineEvaluationPlan.LiveEval06SafetyProbes;

    public int PlannedLiveSubjectRuns => IsLiveEvaluationPlan
        ? IsSafetyEvaluationPlan
            ? SafetyAttackCategories * SafetyMaxProbesPerAttack
            : SelectedLiveScenario.CaseCount * EffectiveEvaluationRepetitions *
          (SelectedEvaluationPlan.IsComparison ? 2 : 1)
        : 0;

    public int PlannedLiveJudgeCalls => PlannedLiveSubjectRuns;

    public int EffectiveEvaluationRepetitions => SelectedEvaluationPlan.SupportsRepetitions
        ? EvaluationRepetitions
        : 1;

    public bool IsSelectedLivePlanConfigured => !IsLiveEvaluationPlan
        || LiveEvalServices.Default.CheckReadiness().IsReady;

    public bool CanRunSelectedEvaluationPlan => !IsLiveEvaluationPlan ||
        IsSelectedLivePlanConfigured && PaidEvaluationAcknowledged;

    public string EvaluationPlanDescription => IsLiveEvaluationPlan
        ? IsSafetyEvaluationPlan
            ? $"{SelectedEvaluationPlan.Description} Robin-only target; {SafetyAttackCategories} attack categories × up to {SafetyMaxProbesPerAttack} probes = {PlannedLiveSubjectRuns} target probe invocations. Each probe is capped at {SafetyMaxTargetModelCallsPerProbe} target model turns plus at most one fallback-judge call ({SafetyMaximumModelCalls} maximum safety model calls). An in-memory canary instruments extraction detection; no raw canary, probe prompt, model response, or system instruction is stored. " + ReadinessCopy()
            : $"{SelectedEvaluationPlan.Description} Selected cases: {SelectedLiveScenario.CaseCount}; planned subject executions: {PlannedLiveSubjectRuns}; planned judge evaluations: {PlannedLiveJudgeCalls}. " + ReadinessCopy()
        : "Credential-free deterministic benchmark, five mandatory evaluation gates, one matched-quality diagnostic, and 43 registered mutation controls. Zero provider model calls. The separate Catalogue integrity self-test lives in Ablation mode; the CLI's full admitted-check self-test is a different verification lane.";

    private string ReadinessCopy() =>
          (IsSelectedLivePlanConfigured
              ? $"{LiveEvalServices.Default.CheckReadiness().Detail} Confirm the paid run below; low-level model requests can exceed subject-run count when tools or retries are used."
              : $"NOT READY: {LiveEvalServices.Default.CheckReadiness().Detail} No offline fallback will run.");

    public string EvaluationScenarioTitle => SelectedLiveScenario.Label;
    public string EvaluationScenarioDescription => SelectedLiveScenario.Description;
    public string EvaluationScenarioQuery => SelectedLiveScenario.Query;

    public void SelectOfflineEvaluationPlan() => SelectedEvaluationPlan = EvaluationPlans[0];

    public void SelectWorkflowDemonstrationPersona() =>
        SelectedPersona = Personas.First(option => option.Id == GalaxusDemoPrompts.MarcoUserId);

    public void SelectRecommendationDemonstrationPersona() =>
        SelectedPersona = Personas.First(option => option.Id == GalaxusDemoPrompts.NadiaUserId);

    private void RaiseEvaluationPlanProperties()
    {
        RaisePropertyChanged(nameof(IsLiveEvaluationPlan));
        RaisePropertyChanged(nameof(SupportsEvaluationRepetitions));
        RaisePropertyChanged(nameof(MinimumEvaluationRepetitions));
        RaisePropertyChanged(nameof(SupportsLiveScenarioSelection));
        RaisePropertyChanged(nameof(IsSafetyEvaluationPlan));
        RaisePropertyChanged(nameof(EffectiveEvaluationRepetitions));
        RaisePropertyChanged(nameof(PlannedLiveSubjectRuns));
        RaisePropertyChanged(nameof(PlannedLiveJudgeCalls));
        RaisePropertyChanged(nameof(IsSelectedLivePlanConfigured));
        RaisePropertyChanged(nameof(CanRunSelectedEvaluationPlan));
        RaisePropertyChanged(nameof(EvaluationPlanDescription));
        RaisePropertyChanged(nameof(EvaluationScenarioTitle));
        RaisePropertyChanged(nameof(EvaluationScenarioDescription));
        RaisePropertyChanged(nameof(EvaluationScenarioQuery));
    }
}
