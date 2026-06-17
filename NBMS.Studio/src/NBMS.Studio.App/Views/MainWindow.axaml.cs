using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private bool _monoGameContextMenuExtended;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        EditorTimelineCanvas.TimelineHit += EditorTimelineCanvas_TimelineHit;
        EditorTimelineCanvas.TimelineDragCompleted += EditorTimelineCanvas_TimelineDragCompleted;
        EditorTimelineCanvas.TimelineRangeSelected += EditorTimelineCanvas_TimelineRangeSelected;
        KeyDown += MainWindow_KeyDown;
        Loaded += (_, _) => ExtendMonoGameViewerContextMenu();
    }

    private void ExtendMonoGameViewerContextMenu()
    {
        if (_monoGameContextMenuExtended)
        {
            return;
        }

        var monoGameButton = this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "MonoGame(Viewer)", StringComparison.Ordinal));
        if (monoGameButton is null)
        {
            return;
        }

        monoGameButton.ContextMenu ??= new ContextMenu();
        var ffmpegItem = new MenuItem
        {
            Header = "Show ffmpeg status"
        };
        var videoLeadItem = new MenuItem
        {
            Header = "Set video lead ms..."
        };
        var audioItem = new MenuItem
        {
            Header = "Set audio options..."
        };

        ffmpegItem.Click += ShowFfmpegStatus_Click;
        videoLeadItem.Click += SetVideoLeadMs_Click;
        audioItem.Click += SetMonoGameAudioOptions_Click;

        monoGameButton.ContextMenu.Items.Add(ffmpegItem);
        monoGameButton.ContextMenu.Items.Add(videoLeadItem);
        monoGameButton.ContextMenu.Items.Add(audioItem);

        _monoGameContextMenuExtended = true;
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

    private async void EditSelectedEvent_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.IsSelectedEventTiming)
        {
            var draft = _viewModel.CreateSelectedTimingEventEditDraft();
            if (draft is null)
            {
                return;
            }

            var input = await ShowTimingEventEditDialogAsync(draft);
            if (input is not null)
            {
                _viewModel.ApplySelectedTimingEventEdit(input.Value, input.DurationTicks);
            }

            return;
        }

        if (_viewModel.IsSelectedEventMedia)
        {
            var draft = _viewModel.CreateSelectedMediaEventEditDraft();
            if (draft is null)
            {
                return;
            }

            var input = await ShowMediaEventEditDialogAsync(draft);
            if (input is not null)
            {
                _viewModel.ApplySelectedMediaEventEdit(input.MediaId, input.Type, input.Layer);
            }
        }
    }

    private void DeleteSelectedEvent_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.DeleteSelectedEvent();
    }

    private async void GridSettings_Click(object? sender, RoutedEventArgs e)
    {
        var settings = await ShowGridSettingsDialogAsync();
        if (settings is not null)
        {
            _viewModel.ApplyEditorGridSettings(settings.Division, settings.SnapEnabled);
        }
    }

    private void ApplyInspector_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ApplyInspectorEdits();
    }

    private void DeleteInspector_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.DeleteInspectorTarget();
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

    private void EditorTimelineCanvas_TimelineRangeSelected(object? sender, TimelineRangeSelectionEventArgs e)
    {
        _viewModel.SelectTimelineObjectsInRange(e.StartTick, e.EndTick, e.Lanes);
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

    private async void RepairSelectedMissingAudioReference_Click(object? sender, RoutedEventArgs e)
    {
        var candidates = _viewModel.GetSelectedMissingAudioRepairCandidates();
        if (candidates.Count == 0)
        {
            _viewModel.RepairSelectedMissingAudioReference();
            return;
        }

        var selected = await ShowAudioRepairCandidateDialogAsync(candidates);
        if (selected is null)
        {
            return;
        }

        _viewModel.RepairSelectedMissingAudioReference(selected.AudioId);
    }

    private void JumpToIssue_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.JumpToSelectedIssue();
        if (_viewModel.EditorSelectedTick >= 0)
        {
            QueueScrollEditorTimelineToTick(_viewModel.EditorSelectedTick);
        }
    }

    private async void AutoFixIssue_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.AutoFixSelectedIssueAsync);
    }

    private async void ValidatePackage_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.ValidatePackageAsync);
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

        await RunUiTaskAsync(() => _viewModel.RenameSelectedAudioAssetAsync(audioId));
    }

    private async void DeleteAudioAsset_Click(object? sender, RoutedEventArgs e)
    {
        var selectedAudioIds = AudioAssetListBox.SelectedItems?
            .OfType<AudioRow>()
            .Select(row => row.AudioId)
            .ToList() ?? [];

        if (selectedAudioIds.Count > 1)
        {
            await RunUiTaskAsync(() => _viewModel.DeleteAudioAssetsAsync(selectedAudioIds));
            return;
        }

        await RunUiTaskAsync(_viewModel.DeleteSelectedAudioAssetAsync);
    }

    private async void RemoveUnusedAudioAssets_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.RemoveUnusedAudioAssetsAsync);
    }

    private async void CheckAudioArchive_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.ValidateAudioArchiveAsync);
    }

    private async void AddMediaAsset_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Add Media Asset",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("Media")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.mp4", "*.webm", "*.avi", "*.mov", "*.mkv"]
                }
            ]
        });

        var filePath = files.FirstOrDefault()?.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var defaultId = Path.GetFileNameWithoutExtension(filePath);
        var mediaId = await ShowAudioIdInputDialogAsync("Add Media Asset", defaultId);
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        await RunUiTaskAsync(() => _viewModel.AddMediaAssetAsync(filePath, mediaId, ""));
    }

    private async void RenameMediaAsset_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedMediaAssetRow is null)
        {
            return;
        }

        var mediaId = await ShowAudioIdInputDialogAsync("Rename MediaId", _viewModel.SelectedMediaAssetRow.MediaId);
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        await RunUiTaskAsync(() => _viewModel.RenameSelectedMediaAssetAsync(mediaId));
    }

    private async void DeleteMediaAsset_Click(object? sender, RoutedEventArgs e)
    {
        var selectedMediaIds = MediaAssetListBox.SelectedItems?
            .OfType<MediaAssetRow>()
            .Select(row => row.MediaId)
            .ToList() ?? [];

        if (selectedMediaIds.Count > 1)
        {
            await RunUiTaskAsync(() => _viewModel.DeleteMediaAssetsAsync(selectedMediaIds));
            return;
        }

        await RunUiTaskAsync(_viewModel.DeleteSelectedMediaAssetAsync);
    }

    private async void RemoveUnusedMediaAssets_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.RemoveUnusedMediaAssetsAsync);
    }

    private async void CheckMediaArchive_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.ValidateMediaArchiveAsync);
    }

    private async void PreviewSelectedMedia_Click(object? sender, RoutedEventArgs e)
    {
        var preview = await _viewModel.LoadSelectedMediaPreviewAsync();
        if (preview is null)
        {
            return;
        }

        await ShowMediaPreviewDialogAsync(preview);
    }

    private void Undo_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.UndoEditorCommand();
    }

    private void Redo_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.RedoEditorCommand();
    }

    private void InsertMeasure_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.InsertMeasureAtSelection();
    }

    private void DeleteMeasure_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.DeleteMeasureAtSelection();
    }

    private void Copy_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.CopyEditorObject();
    }

    private void Paste_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.PasteEditorObject();
    }

    private void ReplaceSelectedObjectsAudioId_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ReplaceSelectedObjectsAudioId();
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

    private async void PlaybackFromEditorPosition_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.StartPlaybackFromEditorPositionAsync);
    }

    private async void PlaybackSelectedRange_Click(object? sender, RoutedEventArgs e)
    {
        await RunUiTaskAsync(_viewModel.StartSelectedRangePlaybackAsync);
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

    private async void SetFfmpegPath_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Select ffmpeg.exe",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new Avalonia.Platform.Storage.FilePickerFileType("ffmpeg")
                {
                    Patterns = ["ffmpeg.exe", "*.exe"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            _viewModel.SetFfmpegPath(path);
        }
    }

    private void ToggleMonoGameViewerBga_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ToggleMonoGameViewerBga();
    }

    private void ShowFfmpegStatus_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.ShowFfmpegDetectionStatus();
    }

    private async void SetVideoLeadMs_Click(object? sender, RoutedEventArgs e)
    {
        var value = await ShowVideoLeadMsDialogAsync(_viewModel.GetMonoGameViewerVideoLeadMs());
        if (value is { } videoLeadMs)
        {
            _viewModel.SetMonoGameViewerVideoLeadMs(videoLeadMs);
        }
    }

    private async void SetMonoGameAudioOptions_Click(object? sender, RoutedEventArgs e)
    {
        var value = await ShowMonoGameAudioOptionsDialogAsync(_viewModel.GetMonoGameViewerAudioSettings());
        if (value is { } audioSettings)
        {
            _viewModel.SetMonoGameViewerAudioSettings(
                audioSettings.AudioVolume,
                audioSettings.MasterGain,
                audioSettings.LimiterThreshold);
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
        var readableScoreJsonBox = new CheckBox
        {
            Content = "Readable score JSON (indent compact-json)",
            IsChecked = false,
            Margin = new Avalonia.Thickness(0, 8, 0, 0)
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
                encodingBox.SelectedItem?.ToString() ?? "utf-8",
                readableScoreJsonBox.IsChecked == true);
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
                        encodingBox,
                        readableScoreJsonBox
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

    private async Task<TimingEventInput?> ShowTimingEventEditDialogAsync(MainWindowViewModel.TimingEventEditDraft draft)
    {
        var isStop = draft.Type.Equals("stop", StringComparison.OrdinalIgnoreCase);
        var valueBox = new TextBox
        {
            Text = isStop
                ? (draft.DurationTicks ?? _viewModel.EditorGridTicks).ToString(CultureInfo.InvariantCulture)
                : (draft.Value ?? 1.0).ToString("0.###", CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var errorText = new TextBlock
        {
            Foreground = Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap
        };
        TimingEventInput? result = null;
        var okButton = new Button { Content = "Apply", MinWidth = 88 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };
        var dialog = new Window
        {
            Title = $"Edit {draft.Type.ToUpperInvariant()} Event",
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

            if (draft.Lane.Equals("measure", StringComparison.OrdinalIgnoreCase) && value <= 0)
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
                    Text = $"tick {draft.Tick} / lane {draft.Lane}",
                    Foreground = Brushes.DimGray
                },
                new TextBlock
                {
                    Text = isStop ? "STOP duration ticks" : $"{draft.Type} value",
                    TextWrapping = TextWrapping.Wrap
                },
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

    private async Task<MediaEventInput?> ShowMediaEventEditDialogAsync(MainWindowViewModel.MediaEventEditDraft draft)
    {
        var mediaIdBox = new TextBox { Text = draft.MediaId, HorizontalAlignment = HorizontalAlignment.Stretch };
        var typeBox = new TextBox { Text = draft.Type, HorizontalAlignment = HorizontalAlignment.Stretch };
        var layerBox = new TextBox
        {
            Text = draft.Layer.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var errorText = new TextBlock
        {
            Foreground = Brushes.Firebrick,
            TextWrapping = TextWrapping.Wrap
        };
        MediaEventInput? result = null;
        var okButton = new Button { Content = "Apply", MinWidth = 88 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };
        var dialog = new Window
        {
            Title = "Edit Media Event",
            Width = 460,
            Height = 330,
            MinWidth = 380,
            MinHeight = 280
        };

        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(mediaIdBox.Text))
            {
                errorText.Text = "MediaIdを入力してください。";
                return;
            }

            if (!int.TryParse(layerBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layer) ||
                layer < 0)
            {
                errorText.Text = "Layerは0以上の整数で入力してください。";
                return;
            }

            result = new MediaEventInput(mediaIdBox.Text.Trim(), typeBox.Text?.Trim() ?? "", layer);
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = $"tick {draft.Tick} / lane {draft.Lane}",
                    Foreground = Brushes.DimGray
                },
                new TextBlock { Text = "MediaId", FontWeight = FontWeight.SemiBold },
                mediaIdBox,
                new TextBlock { Text = "Type", FontWeight = FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 6, 0, 0) },
                typeBox,
                new TextBlock { Text = "Layer", FontWeight = FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 6, 0, 0) },
                layerBox,
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

    private async Task<GridSettingsInput?> ShowGridSettingsDialogAsync()
    {
        var divisionBox = new ComboBox
        {
            ItemsSource = _viewModel.EditorGridDivisions,
            SelectedItem = _viewModel.EditorGridDivision,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var snapBox = new CheckBox
        {
            Content = "Snap to grid",
            IsChecked = _viewModel.IsEditorSnapEnabled
        };
        var okButton = new Button { Content = "OK", MinWidth = 88 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };
        GridSettingsInput? result = null;
        var dialog = new Window
        {
            Title = "Grid settings",
            Width = 380,
            Height = 220,
            MinWidth = 340,
            MinHeight = 200,
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(16),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = "Grid division",
                        FontWeight = FontWeight.SemiBold
                    },
                    BuildDialogRow(1, divisionBox),
                    BuildDialogRow(2, snapBox),
                    BuildDialogRow(3, new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Avalonia.Thickness(0, 16, 0, 0),
                        Children = { cancelButton, okButton }
                    })
                }
            }
        };

        okButton.Click += (_, _) =>
        {
            var division = divisionBox.SelectedItem is int selected ? selected : _viewModel.EditorGridDivision;
            result = new GridSettingsInput(division, snapBox.IsChecked == true);
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<int?> ShowVideoLeadMsDialogAsync(int currentValue)
    {
        var valueBox = new TextBox
        {
            Text = currentValue.ToString(CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        int? result = null;
        var cancelButton = new Button { Content = "Cancel" };
        var okButton = new Button { Content = "OK" };
        var dialog = new Window
        {
            Title = "MonoGame Viewer video lead",
            Width = 360,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                Margin = new Thickness(16),
                Children =
                {
                    BuildDialogRow(0, new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            new TextBlock { Text = "Video lead milliseconds (0-1500)" },
                            valueBox
                        }
                    }),
                    BuildDialogRow(1, new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            cancelButton,
                            okButton
                        }
                    })
                }
            }
        };

        cancelButton.Click += (_, _) => dialog.Close();
        okButton.Click += (_, _) =>
        {
            if (int.TryParse(valueBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                result = Math.Clamp(parsed, 0, 1500);
                dialog.Close();
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<MainWindowViewModel.MonoGameViewerAudioSettings?> ShowMonoGameAudioOptionsDialogAsync(
        MainWindowViewModel.MonoGameViewerAudioSettings currentValue)
    {
        var volumeBox = new TextBox
        {
            Text = currentValue.AudioVolume.ToString("0.00", CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var gainBox = new TextBox
        {
            Text = currentValue.MasterGain.ToString("0.00", CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var limiterBox = new TextBox
        {
            Text = currentValue.LimiterThreshold.ToString("0.00", CultureInfo.InvariantCulture),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        MainWindowViewModel.MonoGameViewerAudioSettings? result = null;
        var cancelButton = new Button { Content = "Cancel" };
        var okButton = new Button { Content = "OK" };
        var dialog = new Window
        {
            Title = "MonoGame Viewer audio options",
            Width = 420,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto)
                },
                Margin = new Thickness(16),
                Children =
                {
                    BuildDialogRow(0, new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = "Audio volume (0.0-2.0)" },
                            volumeBox,
                            new TextBlock { Text = "Master gain (0.0-2.0)" },
                            gainBox,
                            new TextBlock { Text = "Limiter threshold (0.1-1.0)" },
                            limiterBox
                        }
                    }),
                    BuildDialogRow(1, new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children =
                        {
                            cancelButton,
                            okButton
                        }
                    })
                }
            }
        };

        cancelButton.Click += (_, _) => dialog.Close();
        okButton.Click += (_, _) =>
        {
            if (float.TryParse(volumeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var volume) &&
                float.TryParse(gainBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) &&
                float.TryParse(limiterBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var limiter))
            {
                result = new MainWindowViewModel.MonoGameViewerAudioSettings(
                    Math.Clamp(volume, 0f, 2f),
                    Math.Clamp(gain, 0f, 2f),
                    Math.Clamp(limiter, 0.1f, 1f));
                dialog.Close();
            }
        };

        await dialog.ShowDialog(this);
        return result;
    }

    private async Task<MainWindowViewModel.AudioRepairCandidate?> ShowAudioRepairCandidateDialogAsync(
        IReadOnlyList<MainWindowViewModel.AudioRepairCandidate> candidates)
    {
        var listBox = new ListBox
        {
            ItemsSource = candidates,
            SelectedIndex = 0,
            Background = Brushes.White,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var okButton = new Button { Content = "Fix", MinWidth = 88 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 88 };
        MainWindowViewModel.AudioRepairCandidate? result = null;
        var dialog = new Window
        {
            Title = "Audio reference repair candidates",
            Width = 760,
            Height = 360,
            MinWidth = 520,
            MinHeight = 260,
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(16),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = "参照切れの置換候補を選択してください",
                        FontWeight = FontWeight.SemiBold
                    },
                    BuildDialogRow(1, listBox),
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
            result = listBox.SelectedItem as MainWindowViewModel.AudioRepairCandidate;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();
        listBox.DoubleTapped += (_, _) =>
        {
            result = listBox.SelectedItem as MainWindowViewModel.AudioRepairCandidate;
            dialog.Close();
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

    private async Task ShowMediaPreviewDialogAsync(MediaPreviewData preview)
    {
        Control body;
        if (IsImagePreview(preview))
        {
            try
            {
                using var stream = new MemoryStream(preview.Bytes);
                var bitmap = new Bitmap(stream);
                body = new ScrollViewer
                {
                    Content = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform,
                        MaxWidth = 720,
                        MaxHeight = 520
                    }
                };
            }
            catch (Exception ex)
            {
                body = new TextBlock
                {
                    Text = $"画像プレビューを開けませんでした。\n{ex.Message}",
                    TextWrapping = TextWrapping.Wrap
                };
            }
        }
        else
        {
            body = new TextBlock
            {
                Text = $"動画/非画像メディアです。\nMediaId: {preview.MediaId}\nType: {preview.Type}\nPath: {preview.Path}\nSize: {preview.Bytes.Length:N0} bytes\n\n動画再生プレビューは環境依存が大きいため別途検討対象です。",
                TextWrapping = TextWrapping.Wrap
            };
        }

        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var dialog = new Window
        {
            Title = $"Media Preview - {preview.MediaId}",
            Width = 780,
            Height = 640,
            MinWidth = 420,
            MinHeight = 260,
            Content = new Grid
            {
                Margin = new Avalonia.Thickness(16),
                RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{preview.MediaId} / {preview.Type} / {preview.Path}",
                        FontWeight = FontWeight.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    BuildDialogRow(1, body),
                    BuildDialogRow(2, closeButton)
                }
            }
        };
        closeButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private static bool IsImagePreview(MediaPreviewData preview)
    {
        if (preview.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.GetExtension(preview.Path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
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

    private sealed record MediaEventInput(string MediaId, string Type, int Layer);

    private sealed record GridSettingsInput(int Division, bool SnapEnabled);
}
