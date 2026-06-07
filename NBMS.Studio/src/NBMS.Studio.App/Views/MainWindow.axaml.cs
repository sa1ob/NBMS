using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NBMS.Studio.App.Controls;
using NBMS.Studio.App.Import;
using NBMS.Studio.App.ViewModels;

namespace NBMS.Studio.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private PlaybackWindow? _playbackWindow;
    private bool _pendingScrollEditorTimelineToMeasureZero;
    private int _editorTimelineScrollRequestId;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        EditorTimelineCanvas.TimelineHit += EditorTimelineCanvas_TimelineHit;
        KeyDown += MainWindow_KeyDown;
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
            QueueScrollEditorTimelineToMeasureZero();
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

        BmsFolderConversionOptions? options;
        try
        {
            var preview = await _viewModel.PreviewBmsFolderConversionAsync(sourcePath);
            options = await ShowBmsConversionPreviewDialogAsync(preview);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
            return;
        }

        if (options is null)
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
            await RunUiTaskAsync(() => _viewModel.ConvertBmsFolderAsync(sourcePath, outputPath, options));
            QueueScrollEditorTimelineToMeasureZero();
        }
    }

    private void ChartSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        QueueScrollEditorTimelineToMeasureZero();
    }

    private void EditorTimelineScrollViewer_Loaded(object? sender, RoutedEventArgs e)
    {
        QueueScrollEditorTimelineToMeasureZero();
    }

    private void EditorTimelineCanvas_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_pendingScrollEditorTimelineToMeasureZero ||
            Math.Abs(e.PreviousSize.Height - e.NewSize.Height) > 0.5)
        {
            QueueScrollEditorTimelineToMeasureZero();
        }
    }

    private void EditorTimelineCanvas_TimelineHit(object? sender, TimelineHitEventArgs e)
    {
        _viewModel.SetEditorTimelineDraftFromHit(e.Tick, e.Lane);
        if (e.Button == TimelineHitButton.Right)
        {
            _viewModel.DeleteTimelineObjectFromHit(e.Tick, e.Lane);
        }
        else if (e.ClickCount >= 2)
        {
            _viewModel.AddTimelineObjectFromHit(e.Tick, e.Lane);
        }
        else
        {
            _viewModel.SelectTimelineObjectFromHit(e.Tick, e.Lane);
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

    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.UndoEditorCommand();
    }

    private void Redo_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.RedoEditorCommand();
    }

    private void Copy_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.CopyEditorObject();
    }

    private void Paste_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.PasteEditorObject();
    }

    private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
        {
            return;
        }

        if (e.Key == Key.Z)
        {
            _viewModel.UndoEditorCommand();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            _viewModel.RedoEditorCommand();
            e.Handled = true;
        }
        else if (e.Key == Key.C)
        {
            _viewModel.CopyEditorObject();
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            _viewModel.PasteEditorObject();
            e.Handled = true;
        }
    }

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        ShowPlaybackWindow();
    }

    private void MonoGameViewer_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.LaunchMonoGameViewer();
    }

    private async void SetMonoGameViewerPath_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select MonoGame Viewer",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("MonoGame Viewer")
                {
                    Patterns = ["NBMS.Studio.MonoGameViewer.exe", "*.exe"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            _viewModel.SetMonoGameViewerPath(path);
        }
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

    private async Task<BmsFolderConversionOptions?> ShowBmsConversionPreviewDialogAsync(BmsFolderConversionPreview preview)
    {
        var titleBox = new TextBox
        {
            Text = preview.SuggestedTitle,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var levelNameBoxes = new List<(string BmsPath, TextBox TextBox)>();
        var chartPanel = new StackPanel { Spacing = 8 };

        foreach (var chart in preview.Charts)
        {
            var levelNameBox = new TextBox
            {
                Text = chart.SuggestedLevelName,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            levelNameBoxes.Add((chart.BmsPath, levelNameBox));

            var eventSummary = $"BPM:{chart.BpmEventCount} STOP:{chart.StopEventCount} LN:{chart.HoldNoteCount}";
            if (!string.IsNullOrWhiteSpace(chart.LnObj))
            {
                eventSummary += $" LNOBJ:{chart.LnObj}";
            }

            chartPanel.Children.Add(new Border
            {
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Avalonia.Thickness(1),
                Padding = new Avalonia.Thickness(8),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{Path.GetFileName(chart.BmsPath)} / {chart.Mode} / Lv.{chart.Difficulty}",
                            FontWeight = FontWeight.SemiBold
                        },
                        new TextBlock
                        {
                            Text = $"Original title: {chart.OriginalTitle}",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = eventSummary,
                            Foreground = Brushes.DimGray
                        },
                        levelNameBox
                    }
                }
            });
        }

        BmsFolderConversionOptions? result = null;
        var okButton = new Button { Content = "Convert", MinWidth = 96 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };
        var dialog = new Window
        {
            Title = "BMS Conversion Preview",
            Width = 760,
            Height = 560,
            MinWidth = 640,
            MinHeight = 420
        };

        okButton.Click += (_, _) =>
        {
            result = new BmsFolderConversionOptions(
                titleBox.Text?.Trim() ?? preview.SuggestedTitle,
                levelNameBoxes.ToDictionary(
                    item => item.BmsPath,
                    item => item.TextBox.Text?.Trim() ?? "",
                    StringComparer.OrdinalIgnoreCase));
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new Grid
        {
            Margin = new Avalonia.Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Children =
            {
                new TextBlock
                {
                    Text = "Common title and chart level names were inferred from BMS titles. Edit them before conversion.",
                    TextWrapping = TextWrapping.Wrap
                },
                BuildDialogRow(1, new StackPanel
                {
                    Spacing = 4,
                    Margin = new Avalonia.Thickness(0, 12, 0, 12),
                    Children =
                    {
                        new TextBlock { Text = "Song title", FontWeight = FontWeight.SemiBold },
                        titleBox
                    }
                }),
                BuildDialogRow(2, new ScrollViewer
                {
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = chartPanel
                }),
                BuildDialogRow(3, new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Avalonia.Thickness(0, 12, 0, 0),
                    Children = { cancelButton, okButton }
                })
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private static Control BuildDialogRow(int row, Control control)
    {
        Grid.SetRow(control, row);
        return control;
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

    private void QueueScrollEditorTimelineToMeasureZero()
    {
        var requestId = ++_editorTimelineScrollRequestId;
        _pendingScrollEditorTimelineToMeasureZero = true;
        Dispatcher.UIThread.Post(() => ScrollEditorTimelineToMeasureZero(requestId), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => ScrollEditorTimelineToMeasureZero(requestId), DispatcherPriority.Render);
        _ = RetryScrollEditorTimelineToMeasureZeroAsync(requestId);
    }

    private async Task RetryScrollEditorTimelineToMeasureZeroAsync(int requestId)
    {
        var delays = new[] { 50, 150, 300, 600 };
        foreach (var delay in delays)
        {
            await Task.Delay(delay);
            await Dispatcher.UIThread.InvokeAsync(() => ScrollEditorTimelineToMeasureZero(requestId), DispatcherPriority.Background);
        }
    }

    private void ScrollEditorTimelineToMeasureZero(int requestId)
    {
        if (requestId != _editorTimelineScrollRequestId)
        {
            return;
        }

        EditorTimelineScrollViewer.UpdateLayout();
        var maxOffsetY = Math.Max(
            0,
            EditorTimelineScrollViewer.Extent.Height - EditorTimelineScrollViewer.Viewport.Height);

        EditorTimelineScrollViewer.Offset = new Avalonia.Vector(EditorTimelineScrollViewer.Offset.X, maxOffsetY);
        if (EditorTimelineScrollViewer.Extent.Height > 0 &&
            EditorTimelineScrollViewer.Viewport.Height > 0 &&
            Math.Abs(EditorTimelineScrollViewer.Offset.Y - maxOffsetY) < 1)
        {
            _pendingScrollEditorTimelineToMeasureZero = false;
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
