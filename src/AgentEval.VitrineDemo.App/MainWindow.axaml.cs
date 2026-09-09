// SPDX-License-Identifier: MIT

using System.ComponentModel;
using AgentEval.VitrineDemo.App.ViewModels;
using AgentEval.VitrineDemo.App.Artifacts;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AgentEval.VitrineDemo.App;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private bool _disposeInProgress;
    private bool _closeAfterDispose;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainWindowViewModel(new Runtime.VitrineRunCoordinator(), new AvaloniaArtifactSaveService(this));
        DataContext = _viewModel;
        _viewModel.Timeline.PropertyChanged += OnTimelinePropertyChanged;
    }

    internal MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        _viewModel.Timeline.PropertyChanged += OnTimelinePropertyChanged;
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TimelineViewModel.SelectedEvent)
            || !_viewModel.Timeline.FollowEvents
            || _viewModel.Timeline.SelectedEvent is not { } selected)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => EventTimelineList.ScrollIntoView(selected));
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeAfterDispose)
        {
            return;
        }

        e.Cancel = true;
        if (_disposeInProgress)
        {
            return;
        }

        _disposeInProgress = true;
        _viewModel.Timeline.PropertyChanged -= OnTimelinePropertyChanged;
        try
        {
            await _viewModel.DisposeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Closing must not strand a hidden window if a platform adapter fails to dispose.
        }
        finally
        {
            _closeAfterDispose = true;
            Close();
        }
    }
}
