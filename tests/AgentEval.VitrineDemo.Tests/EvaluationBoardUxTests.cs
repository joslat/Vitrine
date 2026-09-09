// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.Evals;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AgentEval.VitrineDemo.Tests;

public sealed class EvaluationBoardUxTests
{
    [Fact]
    public void TypedProgressStreamsRowsWithoutRecomputingTheirVerdicts()
    {
        var board = new EvaluationBoardViewModel();
        board.Apply(new(EvaluationProgressKind.SuiteStarted, "suite", "suite", "starting", Total: 6));

        var failedGate = new GateResult("typed failure", false, 0.25, null, "gate evidence");
        board.Apply(new(EvaluationProgressKind.GateCompleted, "gate-1", "typed failure", "ignored copy",
            Passed: true, Gate: failedGate, Completed: 1, Total: 6));

        var control = new ControlResult("NC-X", "causal row", "safety", true, true, "control evidence")
        {
            ScopeClass = ControlScopeClass.ProductionObservation,
            Target = "SubjectBoundary.Run",
            ObservationProducer = "RuntimeObserver.Capture",
            Evaluator = "IndependentPolicy.Evaluate",
            Tranche = "E02A",
            HealthyOutcome = ControlAttemptOutcome.MeasuredPass,
            BrokenOutcome = ControlAttemptOutcome.ExpectedFaultObserved,
            RestoredOutcome = ControlAttemptOutcome.MeasuredPass,
        };
        board.Apply(new(EvaluationProgressKind.ControlHealthyCompleted, control.Id, control.Name, "healthy", Passed: true, Total: 43));
        board.Apply(new(EvaluationProgressKind.ControlBrokenCompleted, control.Id, control.Name, "fault", Passed: true, Total: 43));
        board.Apply(new(EvaluationProgressKind.ControlRestoredCompleted, control.Id, control.Name, "restored", Passed: true, Total: 43));
        Assert.Empty(board.Controls);
        board.Apply(new(EvaluationProgressKind.ControlCompleted, control.Id, control.Name, "complete",
            Passed: false, Control: control, Completed: 1, Total: 43));

        Assert.Equal("FAIL", Assert.Single(board.Gates).Status);
        var row = Assert.Single(board.Controls);
        Assert.Equal("HEALTHY · passed", row.BaselineHealthy);
        Assert.Equal("DETECTED · expected fault", row.DefectInjected);
        Assert.Equal("RECOVERED · passed", row.Recovery);
        Assert.Equal(nameof(ControlScopeClass.ProductionObservation), row.Scope);
        Assert.Equal("SubjectBoundary.Run", row.Target);
        Assert.Equal("RuntimeObserver.Capture", row.Producer);
        Assert.Equal("IndependentPolicy.Evaluate", row.Evaluator);
        Assert.Equal("E02A", row.Tranche);
        Assert.Contains("1/20 ProductionObservation", board.ControlSummary, StringComparison.Ordinal);
        Assert.Contains("0/23 BoundaryCalibrationFixture", board.ControlSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalOfflineResultShowsExactRunDirectoryNativeFloorAndZeroProviderUsageHonestly()
    {
        var check = new VitrineBenchmarkCheckFact(
            "screened-outcome-contract",
            new("uniform_choice", "Derived", 0.5, 0.5, 0.9, 1, 2, "1 / 2 choices"),
            new(1, 0, 0),
            1,
            1,
            1,
            1,
            null,
            true);
        var benchmark = new VitrineOfflineBenchmarkResult(
            "vitrine-offline-use-cases",
            "1.0.0",
            "offline-deterministic",
            "run-123",
            @"C:\repo\.agenteval\Vitrine",
            @"C:\repo\.agenteval\Vitrine\subjects\workflow\runs\run-123",
            [new(VitrineOfflineBenchmark.NadiaCaseId, "Nadia")],
            [check])
        {
            Repetitions = 1,
            Arms = [new("offline-deterministic", "Agent", "fixture", [check])],
            Runs = [new("offline-deterministic", 1, "Agent", "fixture", "run-123",
                @"C:\repo\.agenteval\Vitrine\subjects\workflow\runs\run-123")],
        };
        var live = new EvaluationExecutionProvenance(
            EvaluationExecutionProfile.OfflineDeterministic,
            "Demo01 single agent + Demo02 MAF workflow",
            "scripted and zero-model subjects",
            "deterministic evaluator",
            null,
            0,
            0,
            0,
            null,
            null,
            null,
            0,
            UsesExternalModels: false);
        var board = new EvaluationBoardViewModel();

        board.Load(new SuiteResult([new GateResult("gate", true, 1, null, "ok")], [])
        {
            OfflineBenchmark = benchmark,
            Execution = live,
        });
        board.Apply(new(EvaluationProgressKind.SuiteCompleted, "suite", "suite", "delayed progress", Passed: true));

        Assert.Equal("PASS · exit 0", board.OverallStatus);
        Assert.Contains(benchmark.RunDirectory, board.PersistenceSummary, StringComparison.Ordinal);
        Assert.Contains("run-123", board.PersistenceSummary, StringComparison.Ordinal);
        Assert.Contains("1 measured / 0 not applicable / 0 not measured", board.BenchmarkSummary, StringComparison.Ordinal);
        Assert.Contains("1 / 2 choices", board.BenchmarkFloorDerivation, StringComparison.Ordinal);
        Assert.Contains("1/1 successes", board.BenchmarkFloorDerivation, StringComparison.Ordinal);
        Assert.Contains("minimum attainable p 1.000", board.BenchmarkFloorDerivation, StringComparison.Ordinal);
        Assert.Contains("UNDERPOWERED BY CONSTRUCTION", board.BenchmarkFloorDerivation, StringComparison.Ordinal);
        Assert.Contains("Provider LLM calls: 0", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("provider tokens/cost: 0", board.ModelUsageSummary, StringComparison.Ordinal);
        Assert.Contains("not a pass threshold", board.NullBaselineHelp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogueSelfTestTreatsTheExpectedSuiteFailureAsSuccessfulDetection()
    {
        var board = new EvaluationBoardViewModel();
        var failedCatalogue = new GateResult("catalogue", false, 0, null,
            "Expected products=99; observed products=98; mutation detected.");
        board.Apply(new(EvaluationProgressKind.SuiteStarted, "suite", "suite", "starting", Total: 6));
        board.Apply(new(EvaluationProgressKind.GateCompleted, "catalogue", "catalogue", failedCatalogue.Evidence,
            Passed: false, Gate: failedCatalogue,
            Expectation: EvaluationProgressExpectation.CatalogueDefectDetection));
        Assert.Contains("red gate row", board.ActiveStage, StringComparison.OrdinalIgnoreCase);
        board.Apply(new(EvaluationProgressKind.SuiteCompleted, "suite", "suite",
            "expected 99 to observed 98 was detected; exit 1 retained", Passed: false, Completed: 6, Total: 6,
            Expectation: EvaluationProgressExpectation.CatalogueSelfTestSucceeded));

        Assert.Equal("SELF-TEST SUCCEEDED · expected detection (suite exit 1)", board.OverallStatus);
        Assert.Equal("FAIL", Assert.Single(board.Gates).Status);
        Assert.Contains("underlying exit 1", board.ActiveStage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#63D391", board.OverallStatusColor);

        board.Load(new SuiteResult([failedCatalogue], []),
            expectedCatalogueDetection: true);

        Assert.Equal("SELF-TEST SUCCEEDED · expected detection (suite exit 1)", board.OverallStatus);
        Assert.Equal("#63D391", board.OverallStatusColor);

        board.Load(new SuiteResult([new GateResult("catalogue", true, 1, null, "mutation missed")], []),
            expectedCatalogueDetection: true);
        Assert.Equal("SELF-TEST FAILED · catalogue mutation was missed (suite exit 0)", board.OverallStatus);
        Assert.Equal("#F07076", board.OverallStatusColor);
    }

    [AvaloniaFact]
    public async Task SetupMakesLiveEvaluationExplicitAndSelfTestOfflineOnly()
    {
        await using var viewModel = new MainWindowViewModel();
        viewModel.SelectedMode = VitrineRunMode.Evals;

        Assert.Equal(AgentEval.VitrineDemo.Evals.Live.VitrineEvaluationPlan.OfflineSuite,
            viewModel.Setup.SelectedEvaluationPlan.Plan);
        Assert.Contains("Demo01 + Demo02", viewModel.ModeExplanation, StringComparison.Ordinal);
        Assert.Contains("zero", viewModel.ModeExplanation, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.RunCommand.CanExecute(null));

        viewModel.Setup.SelectedEvaluationPlan = viewModel.Setup.EvaluationPlans.Single(
            item => item.Plan == AgentEval.VitrineDemo.Evals.Live.VitrineEvaluationPlan.LiveEval03AgentVsWorkflow);
        Assert.Contains("explicit", viewModel.ModeExplanation, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.RunCommand.CanExecute(null));
        viewModel.Setup.PaidEvaluationAcknowledged = true;
        Assert.Equal(viewModel.Setup.IsSelectedLivePlanConfigured, viewModel.RunCommand.CanExecute(null));

        viewModel.SelectedMode = VitrineRunMode.Ablation;
        Assert.Equal(AgentEval.VitrineDemo.Evals.Live.VitrineEvaluationPlan.OfflineSuite,
            viewModel.Setup.SelectedEvaluationPlan.Plan);
        Assert.Contains("Catalogue integrity self-test", viewModel.SetupSelectionSummary, StringComparison.Ordinal);
        Assert.Contains("expected exit 1", viewModel.ModeExplanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restored", viewModel.ModeExplanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no provider LLM", viewModel.ModeExplanation, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task EvaluationTabUsesIntentRevealingHeadersAndDelayedHelp()
    {
        var window = new MainWindow { Width = 1480, Height = 900 };
        window.Show();
        var tab = window.GetVisualDescendants().OfType<TabItem>()
            .Single(item => string.Equals(item.Header?.ToString(), "EVALUATION BOARD", StringComparison.Ordinal));
        tab.IsSelected = true;
        Dispatcher.UIThread.RunJobs();

        var visibleCopy = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(static item => item.Text ?? string.Empty)
            .ToArray();
        Assert.Contains("BASELINE HEALTHY", visibleCopy);
        Assert.Contains("DEFECT-INJECTED · EXPECT DETECTION", visibleCopy);
        Assert.Contains("RECOVERY", visibleCopy);
        Assert.Contains("LOCAL AGENTEVAL RUN", visibleCopy);
        Assert.Contains("MODEL CALLS · COST", visibleCopy);
        Assert.Contains("NULL DERIVATION", visibleCopy);
        Assert.DoesNotContain("BROKEN", visibleCopy);
        Assert.DoesNotContain("SCOTARGET", visibleCopy);
        Assert.False(string.IsNullOrWhiteSpace(Avalonia.Controls.ToolTip.GetTip(tab)?.ToString()));

        var profileSelector = window.GetVisualDescendants().OfType<ComboBox>()
            .Single(item => string.Equals(
                AutomationProperties.GetName(item), "Evaluation plan", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(Avalonia.Controls.ToolTip.GetTip(profileSelector)?.ToString()));
        Assert.Equal(650, Avalonia.Controls.ToolTip.GetShowDelay(profileSelector));

        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
        window.Close();
        await viewModel.DisposeAsync();
    }
}
