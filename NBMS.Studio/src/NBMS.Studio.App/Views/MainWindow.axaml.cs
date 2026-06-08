using System.Globalization;
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
    private const double EditorTimelinePixelsPerTick = 0.125;
    private const double EditorTimelineBottomPadding = 12.0;
    private readonly MainWindowViewModel _viewModel = new();
    private PlaybackWindow? _playbackWindow;
    private bool _pendingScrollEditorTimelineToMeasureZero;
    private int? _pendingEditorTimelineTargetTick;
    private int _editorTimelineScrollRequestId;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        EditorTimelineCanvas.TimelineHit += EditorTimelineCanvas_TimelineHit;
        EditorTimelineCanvas.TimelineDragCompleted += EditorTimelineCanvas_TimelineDragCompleted;
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

    private void EventSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.SelectedEventRow is { } row)
        {
            QueueScrollEditorTimelineToTick(row.Tick);
        }
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

    private async void EditorTimelineCanvas_TimelineHit(object? sender, TimelineHitEventArgs e)
    {
        _viewModel.SetEditorTimelineDraftFromHit(e.Tick, e.Lane);
        if (e.Button == TimelineHitButton.Right)
        {
            _viewModel.DeleteTimelineObjectFromHit(e.Tick, e.Lane);
        }
        else if (e.ClickCount >= 2)
        {
            if (_viewModel.IsTimingLane(e.Lane))
            {
                var input = await ShowTimingEventInputDialogAsync(e.Tick, e.Lane);
                if (input is not null)
                {
                    _viewModel.AddTimelineTimingEventFromHit(e.Tick, e.Lane, input.Value, input.DurationTicks);
                }

                return;
            }

            _viewModel.AddTimelineObjectFromHit(e.Tick, e.Lane);
        }
        else
        {
            var audioId = _viewModel.SelectTimelineObjectFromHit(e.Tick, e.Lane);
            if (!string.IsNullOrWhiteSpace(audioId))
            {
                await _viewModel.PreviewAudioIdAsync(audioId);
            }
        }
    }

    private void EditorTimelineCanvas_TimelineDragCompleted(object? sender, TimelineDragEventArgs e)
    {
        _viewModel.MoveSelectedTimelineObject(e.FromTick, e.FromLane, e.ToTick, e.ToLane);
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

    private void SetLongNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetSelectedNoteLongNote();
    }

    private void ClearLongNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ClearSelectedNoteLongNote();
    }

    private void SetChargeNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetSelectedNoteChargeNote();
    }

    private void SetHellChargeNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetSelectedNoteHellChargeNote();
    }

    private void SetMineNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetSelectedNoteMine();
    }

    private void SetInvisibleNote_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.SetSelectedNoteInvisible();
    }

    private void RepairSelectedMissingAudioReference_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.RepairSelectedMissingAudioReference();
    }

    private async void PreviewSelectedAudio_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.PreviewSelectedAudioAsync();
    }

    private async void AddAudioAsset_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Add Audio Asset",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Audio")
                {
                    Patterns = ["*.wav", "*.flac", "*.ogg", "*.oga", "*.mp3"]
                }
            ]
        });

        var filePath = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var defaultId = Path.GetFileNameWithoutExtension(filePath);
        var audioId = await ShowAudioIdInputDialogAsync("Add Audio Asset", defaultId);
        if (string.IsNullOrWhiteSpace(audioId))
        {
            return;
        }

        await RunUiTaskAsync(() => _viewModel.AddAudioAssetAsync(filePath, audioId));
    }

    private async void RenameAudioAsset_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedAudioRow is null)
        {
            return;
        }

        var audioId = await ShowAudioIdInputDialogAsync("Rename AudioId", _viewModel.SelectedAudioRow.AudioId);
        if (string.IsNullOrWhiteSpace(audioId))
        {
            return;
        }

        _viewModel.RenameSelectedAudioAsset(audioId);
    }

    private async void DeleteAudioAsset_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.DeleteSelectedAudioAssetAsync);
    }

    private async void RemoveUnusedAudioAssets_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.RemoveUnusedAudioAssetsAsync);
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
        var encodingBox = new ComboBox
        {
            ItemsSource = new[] { "utf-8", "shift_jis", "system-default" },
            SelectedIndex = 0,
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
                    StringComparer.OrdinalIgnoreCase),
                encodingBox.SelectedItem?.ToString() ?? "utf-8");
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
                        titleBox,
                        new TextBlock { Text = "BMS text encoding", FontWeight = FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 8, 0, 0) },
                        encodingBox
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

    private async Task<TimingEventInput?> ShowTimingEventInputDialogAsync(int tick, string lane)
    {
        var isStop = string.Equals(lane, "stop", StringComparison.OrdinalIgnoreCase);
        var defaultText = lane switch
        {
            "bpm" => string.IsNullOrWhiteSpace(_viewModel.BpmText) ? "120" : _viewModel.BpmText,
            "stop" => _viewModel.EditorGridTicks.ToString(CultureInfo.InvariantCulture),
            "measure" => "1.0",
            "scroll" => "1.0",
            "speed" => "1.0",
            _ => "1.0"
        };
        var valueBox = new TextBox
        {
            Text = defaultText,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var messageText = new TextBlock
        {
            Text = isStop
                ? "STOP duration ticksを入力してください。"
                : lane.Equals("measure", StringComparison.OrdinalIgnoreCase)
                    ? "小節長倍率を入力してください。通常の1小節は 1.0 です。"
                    : $"{lane.ToUpperInvariant()} valueを入力してください。",
            TextWrapping = TextWrapping.Wrap
        };
        var errorText = new TextBlock
        {
            Foreground = Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap
        };
        TimingEventInput? result = null;
        var okButton = new Button { Content = "Add", MinWidth = 88 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };
        var dialog = new Window
        {
            Title = $"Add {lane.ToUpperInvariant()} Event",
            Width = 420,
            Height = 230,
            MinWidth = 360,
            MinHeight = 210
        };

        okButton.Click += (_, _) =>
        {
            if (isStop)
            {
                if (!int.TryParse(valueBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var durationTicks) ||
                    durationTicks <= 0)
                {
                    errorText.Text = "1以上の整数tickを入力してください。";
                    return;
                }

                result = new TimingEventInput(null, durationTicks);
                dialog.Close();
                return;
            }

            if (!double.TryParse(valueBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                errorText.Text = "数値を入力してください。小数は . を使います。";
                return;
            }

            if (lane.Equals("measure", StringComparison.OrdinalIgnoreCase) && value <= 0)
            {
                errorText.Text = "小節長倍率は0より大きい数値を入力してください。";
                return;
            }

            result = new TimingEventInput(value, null);
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"tick {tick} / lane {lane}",
                    Foreground = Brushes.DimGray
                },
                messageText,
                valueBox,
                errorText,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton }
                }
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<string?> ShowAudioIdInputDialogAsync(string title, string defaultValue)
    {
        var textBox = new TextBox
        {
            Text = defaultValue,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var okButton = new Button { Content = "OK", MinWidth = 88 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };
        string? result = null;
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            MinWidth = 360,
            MinHeight = 140,
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(16),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = "AudioId",
                        FontWeight = FontWeight.SemiBold
                    },
                    BuildDialogRow(1, textBox),
                    BuildDialogRow(2, new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Avalonia.Thickness(0, 12, 0, 0),
                        Children = { cancelButton, okButton }
                    })
                }
            }
        };

        okButton.Click += (_, _) =>
        {
            result = textBox.Text?.Trim();
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

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
        _pendingEditorTimelineTargetTick = null;
        var requestId = ++_editorTimelineScrollRequestId;
        _pendingScrollEditorTimelineToMeasureZero = true;
        Dispatcher.UIThread.Post(() => ScrollEditorTimelineToMeasureZero(requestId), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => ScrollEditorTimelineToMeasureZero(requestId), DispatcherPriority.Render);
        _ = RetryScrollEditorTimelineToMeasureZeroAsync(requestId);
    }

    private void QueueScrollEditorTimelineToTick(int tick)
    {
        _pendingEditorTimelineTargetTick = tick;
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

        var offsetY = _pendingEditorTimelineTargetTick.HasValue
            ? ResolveEditorTimelineOffsetForTick(_pendingEditorTimelineTargetTick.Value, maxOffsetY)
            : maxOffsetY;
        EditorTimelineScrollViewer.Offset = new Avalonia.Vector(EditorTimelineScrollViewer.Offset.X, offsetY);
        if (EditorTimelineScrollViewer.Extent.Height > 0 &&
            EditorTimelineScrollViewer.Viewport.Height > 0 &&
            Math.Abs(EditorTimelineScrollViewer.Offset.Y - offsetY) < 1)
        {
            _pendingScrollEditorTimelineToMeasureZero = false;
            _pendingEditorTimelineTargetTick = null;
        }
    }

    private double ResolveEditorTimelineOffsetForTick(int tick, double maxOffsetY)
    {
        var canvasY = EditorTimelineCanvas.Bounds.Height -
                      EditorTimelineBottomPadding -
                      (tick - _viewModel.EditorTimelineStartTick) * EditorTimelinePixelsPerTick;
        var targetOffset = canvasY - EditorTimelineScrollViewer.Viewport.Height * 0.55;
        return Math.Clamp(targetOffset, 0, maxOffsetY);
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

    private sealed record TimingEventInput(double? Value, int? DurationTicks);
}
