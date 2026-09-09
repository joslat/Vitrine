// SPDX-License-Identifier: MIT

using AgentEval.VitrineDemo.App;
using AgentEval.VitrineDemo.App.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Automation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Galaxus.RecommendationAgent.Catalog;
using System.Reflection;

namespace AgentEval.VitrineDemo.Tests;

public sealed class AppSmokeTests
{
    [AvaloniaFact]
    public void MainWindowLoadsWithCompiledBindingsAndOfflineDefault()
    {
        var window = new MainWindow();

        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
        Assert.Equal(VitrineRunMode.Demo01, viewModel.SelectedMode);
        Assert.Contains("Offline-ready", viewModel.Status, StringComparison.Ordinal);
        Assert.True(viewModel.IsSetupExpanded);
        Assert.False(viewModel.ClearCommand.CanExecute(null));
        Assert.False(viewModel.ExportJsonCommand.CanExecute(null));
        Assert.False(viewModel.ExportHtmlCommand.CanExecute(null));
        Assert.False(viewModel.StartReplayCommand.CanExecute(null));
        Assert.Equal("VITRINE · AgentEval Control Room", window.Title);
    }

    [Fact]
    public void ShellDisplayStateContainsNoEnvironmentConfiguration()
    {
        const string endpoint = "https://sentinel-vitrine-resource.example.invalid/";
        const string key = "SENTINEL-VITRINE-SECRET";
        var originalEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var originalKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", endpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", key);
            var viewModel = new MainWindowViewModel();
            var display = string.Join('\n', viewModel.Title, viewModel.Status, viewModel.EvidenceBoundary);

            Assert.DoesNotContain(endpoint, display, StringComparison.Ordinal);
            Assert.DoesNotContain(key, display, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", originalKey);
        }
    }

    [AvaloniaFact]
    public async Task HeadlessWindowRunsDemo01AndProjectsActualGraphAndEvents()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Setup.AudiencePacingMilliseconds = 0;

        await viewModel.RunSelectedModeAsync();
        await viewModel.DrainPresentationAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.IsSetupExpanded);
        Assert.Equal(13, viewModel.Graph.Nodes.Count(node => node.Kind == "tool"));
        Assert.Contains(viewModel.Timeline.Events, item => item.Kind == "ToolExecutionStarted");
        var agent = Assert.IsType<AgentEval.VitrineDemo.App.Artifacts.VitrineDemo01Snapshot>(
            Assert.IsType<AgentEval.VitrineDemo.App.Artifacts.VitrineRunArtifact>(viewModel.CurrentArtifact).Result.Demo01);
        Assert.Contains(agent.CustomerFacingAnswer, viewModel.OutcomeDetails, StringComparison.Ordinal);
        Assert.All(agent.RegisteredTools, tool =>
            Assert.Contains(tool, viewModel.OutcomeDetails, StringComparison.Ordinal));
        Assert.All(agent.Recommendations, recommendation =>
            Assert.Contains(recommendation.Sku, viewModel.OutcomeDetails, StringComparison.Ordinal));
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task HeadlessWindowRunsDemo02AndProjectsExactRoutesAndSkusInOutcomeInspector()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Setup.AudiencePacingMilliseconds = 0;
        viewModel.SelectedMode = VitrineRunMode.Demo02;

        await viewModel.RunSelectedModeAsync();
        await viewModel.DrainPresentationAsync();
        Dispatcher.UIThread.RunJobs();

        var workflow = Assert.IsType<AgentEval.VitrineDemo.App.Artifacts.VitrineDemo02Snapshot>(
            Assert.IsType<AgentEval.VitrineDemo.App.Artifacts.VitrineRunArtifact>(viewModel.CurrentArtifact).Result.Demo02);
        Assert.Contains(workflow.CustomerFacingAnswer, viewModel.OutcomeDetails, StringComparison.Ordinal);
        Assert.All(workflow.RoutesTaken, route =>
            Assert.Contains(route, viewModel.OutcomeDetails, StringComparison.Ordinal));
        Assert.All(workflow.Recommendations, recommendation =>
            Assert.Contains(recommendation.Sku, viewModel.OutcomeDetails, StringComparison.Ordinal));
        Assert.All(workflow.ExecutorIds, executor =>
            Assert.Contains(executor, viewModel.OutcomeDetails, StringComparison.Ordinal));
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task FailedArtifactConstructionCannotLeavePreviousOutcomeExportable()
    {
        var returnInvalidGraph = false;
        var coordinator = new AgentEval.VitrineDemo.App.Runtime.VitrineRunCoordinator((_, tools) =>
            returnInvalidGraph
                ? new AgentEval.VitrineDemo.App.Models.VitrineGraphSnapshot(
                    "invalid test graph",
                    AgentEval.VitrineDemo.App.Models.VitrineGraphSource.RegisteredFunctions,
                    null!,
                    [])
                : AgentEval.VitrineDemo.App.Runtime.VitrineGraphFactory.FromRegisteredDemo01Functions(tools!));
        var viewModel = new MainWindowViewModel(coordinator);
        viewModel.Setup.AudiencePacingMilliseconds = 0;

        await viewModel.RunSelectedModeAsync();
        Assert.NotNull(viewModel.CurrentArtifact);

        returnInvalidGraph = true;
        await viewModel.RunSelectedModeAsync();

        Assert.Null(viewModel.CurrentArtifact);
        Assert.False(viewModel.HasArtifact);
        Assert.Equal("RUN OUTCOME · unavailable", viewModel.OutcomeTitle);
        Assert.Contains("no prior run", viewModel.OutcomeDetails, StringComparison.Ordinal);
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task RunConfigurationExpanderKeepsItsFullWidthWhenCollapsed()
    {
        var window = new MainWindow { Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var viewModel = Assert.IsType<MainWindowViewModel>(window.DataContext);
        var expander = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Expander>()
            .Single(item => AutomationProperties.GetName(item) == "Run configuration and evidence actions");
        var outcomeExpander = window.GetVisualDescendants()
            .OfType<Avalonia.Controls.Expander>()
            .Single(item => AutomationProperties.GetName(item) == "Screened run outcome");
        var expandedWidth = expander.Bounds.Width;

        Assert.False(outcomeExpander.IsExpanded);
        viewModel.IsSetupExpanded = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(expander.IsExpanded);
        Assert.True(expander.Bounds.Width >= window.ClientSize.Width - 1);
        Assert.Equal(expandedWidth, expander.Bounds.Width, precision: 1);
        window.Hide();
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task HeadlessWindowRunsEvaluationAndShowsAllTypedControls()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Setup.AudiencePacingMilliseconds = 0;
        viewModel.SelectedMode = VitrineRunMode.Evals;

        await viewModel.RunSelectedModeAsync();
        await viewModel.DrainPresentationAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("PASS · exit 0", viewModel.Evaluation.OverallStatus);
        Assert.Equal(6, viewModel.Evaluation.Gates.Count);
        Assert.DoesNotContain(viewModel.Evaluation.Gates,
            static gate => gate.Name.Contains("control", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(43, viewModel.Evaluation.Controls.Count);
        Assert.Contains("43/43", viewModel.Evaluation.ControlSummary, StringComparison.Ordinal);
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ControlRoomRendersAtRequiredCompactViewport()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Setup.AudiencePacingMilliseconds = 0;
        viewModel.Timeline.SelectedFilter = TimelineFilter.Debug;
        await viewModel.RunSelectedModeAsync();
        await viewModel.DrainPresentationAsync();
        Dispatcher.UIThread.RunJobs();

        var window = new MainWindow(viewModel) { Width = 1280, Height = 720 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var bitmap = window.CaptureRenderedFrame();

        Assert.NotNull(bitmap);
        Assert.Equal(1280, bitmap.PixelSize.Width);
        Assert.Equal(720, bitmap.PixelSize.Height);

        var capturePath = Environment.GetEnvironmentVariable("VITRINE_CAPTURE_PATH");
        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
            bitmap.Save(capturePath);
        }
        window.Hide();
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task DenseWorkflowAndEvaluationGraphsRenderAtCompactViewport()
    {
        foreach (var (mode, captureVariable) in new[]
                 {
                     (VitrineRunMode.Demo02, "VITRINE_CAPTURE_PATH_DEMO02_1280"),
                     (VitrineRunMode.Evals, "VITRINE_CAPTURE_PATH_EVALS_1280"),
                 })
        {
            var viewModel = new MainWindowViewModel { SelectedMode = mode };
            viewModel.Setup.AudiencePacingMilliseconds = 0;
            await viewModel.RunSelectedModeAsync();
            await viewModel.DrainPresentationAsync();
            var window = new MainWindow(viewModel) { Width = 1280, Height = 720 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            Assert.Equal(new Avalonia.PixelSize(1280, 720), bitmap.PixelSize);
            var path = Environment.GetEnvironmentVariable(captureVariable);
            if (!string.IsNullOrWhiteSpace(path)) bitmap.Save(path);
            window.Hide();
            await viewModel.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ControlRoomRendersAtPresentationViewport()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.Setup.AudiencePacingMilliseconds = 0;
        viewModel.SelectedMode = VitrineRunMode.Evals;
        await viewModel.RunSelectedModeAsync();
        await viewModel.DrainPresentationAsync();

        var window = new MainWindow(viewModel) { Width = 1600, Height = 900 };
        window.Show();
        window.GetVisualDescendants().OfType<Avalonia.Controls.TabControl>().Single().SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var bitmap = window.CaptureRenderedFrame();

        Assert.NotNull(bitmap);
        Assert.Equal(1600, bitmap.PixelSize.Width);
        Assert.Equal(900, bitmap.PixelSize.Height);
        var capturePath = Environment.GetEnvironmentVariable("VITRINE_CAPTURE_PATH_1600");
        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
            bitmap.Save(capturePath);
        }
        window.Hide();
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task ControlRoomRendersAtHighDpiScales()
    {
        foreach (var scale in new[] { 1.5, 2.0 })
        {
            var window = new MainWindow { Width = 1280, Height = 720 };
            window.Show();

            var platformProperty = FindProperty(window.GetType(), "PlatformImpl");
            var platform = platformProperty.GetValue(window)!;
            FindField(platform.GetType(), "<RenderScaling>k__BackingField").SetValue(platform, scale);
            var scalingChanged = (Delegate?)FindField(platform.GetType(), "<ScalingChanged>k__BackingField").GetValue(platform);
            scalingChanged?.DynamicInvoke(scale);
            Dispatcher.UIThread.RunJobs();

            var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            Assert.Equal(scale, window.RenderScaling);
            Assert.Equal((int)(1280 * scale), bitmap.PixelSize.Width);
            Assert.Equal((int)(720 * scale), bitmap.PixelSize.Height);
            window.Hide();
            await ((MainWindowViewModel)window.DataContext!).DisposeAsync();
        }
    }

    private static PropertyInfo FindProperty(Type type, string name)
    {
        var available = new List<string>();
        for (var current = type; current is not null; current = current.BaseType)
        {
            var properties = current.GetProperties(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            available.AddRange(properties.Select(property => $"{current.Name}.{property.Name}"));
            if (properties.FirstOrDefault(property => property.Name == name ||
                property.Name.EndsWith($".{name}", StringComparison.Ordinal)) is { } property)
            {
                return property;
            }
        }

        throw new InvalidOperationException(
            $"Headless test property '{name}' was not found on {type.FullName}. Available: {string.Join(", ", available)}");
    }

    private static FieldInfo FindField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.GetField(name, BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is { } field)
            {
                return field;
            }
        }

        throw new InvalidOperationException($"Headless test field '{name}' was not found on {type.FullName}.");
    }
}
