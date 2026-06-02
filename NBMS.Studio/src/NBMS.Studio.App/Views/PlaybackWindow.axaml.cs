using Avalonia.Controls;
using Avalonia.Interactivity;
using NBMS.Studio.App.ViewModels;

namespace NBMS.Studio.App.Views;

public sealed partial class PlaybackWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;

    public PlaybackWindow()
    {
        InitializeComponent();
    }

    public PlaybackWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.StartPlayback();
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.PausePlayback();
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.ResetPlaybackView();
    }

    private void ToggleLog_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.TogglePlaybackLogVisibility();
        PlaybackLogPanel.IsVisible = _viewModel.IsPlaybackLogVisible;
        ViewerLayout.RowDefinitions[2].Height = _viewModel.IsPlaybackLogVisible
            ? new GridLength(120)
            : new GridLength(0);
    }

    private void HiSpeed1_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SetViewerHiSpeed(1.0);
    }

    private void HiSpeed15_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SetViewerHiSpeed(1.5);
    }

    private void HiSpeed2_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SetViewerHiSpeed(2.0);
    }

    private void HiSpeed3_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SetViewerHiSpeed(3.0);
    }

    private void HiSpeed4_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.SetViewerHiSpeed(4.0);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.StopPlayback();
        base.OnClosed(e);
    }
}
