using Avalonia.Controls;
using Avalonia.Interactivity;
using NBMS.Studio.App.ViewModels;

namespace NBMS.Studio.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private PlaybackWindow? _playbackWindow;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void OpenNbms_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open NBMS Header",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("NBMS Header")
                {
                    Patterns = ["*.nbmh", "*.json"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            await RunUiTaskAsync(() => _viewModel.OpenProjectAsync(path));
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.SaveProjectAsync);
    }

    private async void ExportPackage_Click(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Create NBMS Package",
            SuggestedFileName = "song.nbmp",
            FileTypeChoices =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("NBMS Package")
                {
                    Patterns = ["*.nbmp"]
                }
            ]
        });

        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            await RunUiTaskAsync(() => _viewModel.CreatePackageAsync(path));
        }
    }

    private async void ConvertBms_Click(object? sender, RoutedEventArgs e)
    {
        var sourceFolders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Select BMS Folder",
            AllowMultiple = false
        });

        var sourceFolder = sourceFolders.FirstOrDefault();
        if (sourceFolder?.Path.LocalPath is not { Length: > 0 } sourcePath)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Select NBMS Output Folder",
            AllowMultiple = false
        });

        var outputFolder = folders.FirstOrDefault();
        if (outputFolder?.Path.LocalPath is { Length: > 0 } outputPath)
        {
            await RunUiTaskAsync(() => _viewModel.ConvertBmsFolderAsync(sourcePath, outputPath));
        }
    }

    private void AddNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddDraftNote();
    }

    private void ApplyNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ApplyDraftToSelectedNote();
    }

    private void DeleteNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.DeleteSelectedNote();
    }

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        ShowPlaybackWindow();
    }

    private void Stop_Click(object? sender, RoutedEventArgs e)
    {
        ShowPlaybackWindow();
    }

    private void ResetPlaybackView_Click(object? sender, RoutedEventArgs e)
    {
        ShowPlaybackWindow();
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void ShowStatus_Click(object? sender, RoutedEventArgs e)
    {
        await ShowMessageAsync(
            "実装状況",
            "BMSEを参考にしたメニュー、ツールバー、タイムライン、Direct Input、ノーツ表、音源一覧、参照切れチェックを統合しています。Undo/Redo、外部Viewer連携、詳細なグリッド設定は今後の実装対象です。");
    }

    private async void About_Click(object? sender, RoutedEventArgs e)
    {
        await ShowMessageAsync(
            "NBMS Studioについて",
            "NBMS Studio は NBMS Editor/Viewer 一体型の検証実装です。C# / .NET 8 / Avalonia により、Windowsで単一exe配布しやすい構成を目指します。");
    }

    protected override void OnClosed(EventArgs e)
    {
        _playbackWindow?.Close();
        _playbackWindow = null;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void ShowPlaybackWindow()
    {
        if (_playbackWindow is null)
        {
            _playbackWindow = new PlaybackWindow(_viewModel);
            _playbackWindow.Closed += (_, _) =>
            {
                _playbackWindow = null;
            };
            _playbackWindow.Show(this);
        }
        else
        {
            _playbackWindow.Activate();
        }
    }

    private async Task RunUiTaskAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        await ShowMessageAsync("NBMS Studio Error", message);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 220,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };

        if (dialog.Content is StackPanel panel && panel.Children.OfType<Button>().FirstOrDefault() is { } button)
        {
            button.Click += (_, _) => dialog.Close();
        }

        await dialog.ShowDialog(this);
    }
}
