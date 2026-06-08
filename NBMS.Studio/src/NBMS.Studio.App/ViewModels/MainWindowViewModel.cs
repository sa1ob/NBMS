using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia.Threading;
using NBMS.Core.Models;
using NBMS.Core.Services;
using NBMS.Studio.App.Import;
using NBMS.Studio.App.Playback;

namespace NBMS.Studio.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly NbmsProjectService _projectService = new();
    private readonly TimelineService _timelineService;
    private readonly PackageService _packageService = new();
    private readonly AudioArchiveService _audioArchiveService = new();
    private readonly BmsConversionService _bmsConversionService = new();
    private readonly ReferenceCheckService _referenceCheckService = new();
    private readonly DispatcherTimer _playbackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private readonly Stopwatch _playbackClock = new();
    private readonly NbmsAudioPlayer _audioPlayer = new();
    private readonly PlaybackLookaheadScheduler _playbackScheduler = new();
    private readonly Queue<string> _playbackLogLines = [];
    private readonly HashSet<string> _urgentAudioPrepareIds = new(StringComparer.Ordinal);
    private readonly object _monoGameViewerLock = new();
    private readonly Stack<EditorCommand> _undoStack = new();
    private readonly Stack<EditorCommand> _redoStack = new();

    private NbmsProject? _project;
    private LoadedChart? _selectedChart;
    private NbmsAudioCache? _audioCache;
    private Task<NbmsAudioCache?>? _audioCacheTask;
    private CancellationTokenSource? _audioCachePreparationCts;
    private PlaybackSession? _playbackSession;
    private Process? _monoGameViewerProcess;
    private List<PlaybackEvent> _playbackEvents = [];
    private List<TimelineRow> _playbackTimeline = [];
    private HashSet<(int Tick, long TimeKey)> _playbackStopPoints = [];
    private int _nextPlaybackEventIndex;
    private double _playbackEndSeconds;
    private double _playbackOffsetSeconds;
    private double _lastPlaybackPositionTextSeconds = -1;
    private const double UnknownAudioTailSeconds = 120.0;
    private const double PlaybackLookaheadSeconds = 0.25;
    private const double InitialAudioPrepareSeconds = 6.0;
    private const double UrgentAudioPrepareSeconds = 4.0;
    private const int InitialAudioFallbackCount = 24;
    private string _statusText = "NBMS Studio を起動しました。";
    private string _titleText = "";
    private string _artistText = "";
    private string _genreText = "";
    private string _bpmText = "";
    private string _licenseText = "";
    private string _projectPathText = "";
    private NoteRow? _selectedNote;
    private AudioRow? _selectedAudioRow;
    private MediaRow? _selectedMediaRow;
    private IssueRow? _selectedIssueRow;
    private ChartRow? _selectedChartRow;
    private EventRow? _selectedEventRow;
    private string _draftTick = "0";
    private string _draftLane = "key1";
    private string _draftType = "tap";
    private string _draftAudioId = "";
    private string _draftDurationTicks = "";
    private bool _isPlaying;
    private double _playheadTick;
    private double _editorTimelineHeight = 960;
    private int _editorTimelineStartTick;
    private int _editorTimelineFocusTick;
    private int _editorSelectedTick = -1;
    private string _editorSelectedLane = "";
    private EditorClipboardItem? _editorClipboard;
    private int _editorGridDivision = 16;
    private double _viewerHiSpeed = 1.0;
    private string _playbackPositionText = "00:00.000";
    private string _playbackDebugText = "session: none";
    private string _playbackLogText = "";
    private string _audioPreparationText = "";
    private bool _isPlaybackLogVisible;
    private bool _isApplyingEditorHistory;

    public MainWindowViewModel()
    {
        _timelineService = new TimelineService(_projectService.ExtensionRegistry);
        _playbackTimer.Tick += (_, _) => HandlePlaybackTimerTick();
        _audioPlayer.LogMessage += message =>
        {
            if (IsPlaybackLogVisible)
            {
                Dispatcher.UIThread.Post(() => AppendPlaybackLog("AUDIO", message));
            }
        };
    }

    public ObservableCollection<ChartRow> Charts { get; } = [];
    public ObservableCollection<NoteRow> Notes { get; } = [];
    public ObservableCollection<AudioRow> AudioEntries { get; } = [];
    public ObservableCollection<MediaRow> MediaEntries { get; } = [];
    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<TimelineRow> Timeline { get; } = [];
    public ObservableCollection<MeasureGridLineRow> MeasureGridLines { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<string> Extensions { get; } = [];
    public ObservableCollection<int> EditorGridDivisions { get; } = [4, 8, 12, 16, 24, 32, 48, 64];

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string TitleText
    {
        get => _titleText;
        set => SetProperty(ref _titleText, value);
    }

    public string ArtistText
    {
        get => _artistText;
        set => SetProperty(ref _artistText, value);
    }

    public string GenreText
    {
        get => _genreText;
        set => SetProperty(ref _genreText, value);
    }

    public string BpmText
    {
        get => _bpmText;
        set => SetProperty(ref _bpmText, value);
    }

    public string LicenseText
    {
        get => _licenseText;
        set => SetProperty(ref _licenseText, value);
    }

    public string ProjectPathText
    {
        get => _projectPathText;
        set => SetProperty(ref _projectPathText, value);
    }

    public NoteRow? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (!SetProperty(ref _selectedNote, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanEditSelectedNote));
            if (value is null)
            {
                return;
            }

            DraftTick = value.Tick.ToString();
            DraftLane = value.Lane;
            DraftType = value.Type;
            DraftAudioId = value.AudioId;
            DraftDurationTicks = value.DurationTicks?.ToString() ?? "";
        }
    }

    public bool CanEditSelectedNote => SelectedNote is not null;

    public AudioRow? SelectedAudioRow
    {
        get => _selectedAudioRow;
        set
        {
            if (!SetProperty(ref _selectedAudioRow, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanPreviewSelectedAudio));
            OnPropertyChanged(nameof(CanEditSelectedAudio));
            OnPropertyChanged(nameof(CanRepairMissingAudioReference));
            if (value is null)
            {
                return;
            }

            DraftAudioId = value.AudioId;
            StatusText = $"Selected audio: {value.AudioId}";
        }
    }

    public IssueRow? SelectedIssueRow
    {
        get => _selectedIssueRow;
        set
        {
            if (SetProperty(ref _selectedIssueRow, value))
            {
                OnPropertyChanged(nameof(CanRepairMissingAudioReference));
            }
        }
    }

    public bool CanPreviewSelectedAudio => SelectedAudioRow is not null;

    public bool CanEditSelectedAudio => SelectedAudioRow is not null;

    public MediaRow? SelectedMediaRow
    {
        get => _selectedMediaRow;
        set
        {
            if (SetProperty(ref _selectedMediaRow, value) && value is not null)
            {
                StatusText = $"Selected media: {value.MediaId} tick {value.Tick}";
            }
        }
    }

    public bool CanRepairMissingAudioReference =>
        SelectedIssueRow is { Index: not null } issue &&
        !string.IsNullOrWhiteSpace(issue.ReferenceType) &&
        SelectedAudioRow is not null;

    public ChartRow? SelectedChartRow
    {
        get => _selectedChartRow;
        set
        {
            if (!SetProperty(ref _selectedChartRow, value) || value is null)
            {
                return;
            }

            SelectChartById(value.Id);
        }
    }

    public EventRow? SelectedEventRow
    {
        get => _selectedEventRow;
        set
        {
            if (SetProperty(ref _selectedEventRow, value) && value is not null)
            {
                EditorSelectedTick = value.Tick;
                EditorSelectedLane = value.Lane;
                DraftTick = value.Tick.ToString();
                DraftLane = value.Lane;
                StatusText = $"Selected event: {value.Type} tick {value.Tick}";
            }
        }
    }

    public string DraftTick
    {
        get => _draftTick;
        set => SetProperty(ref _draftTick, value);
    }

    public string DraftLane
    {
        get => _draftLane;
        set => SetProperty(ref _draftLane, value);
    }

    public string DraftType
    {
        get => _draftType;
        set => SetProperty(ref _draftType, value);
    }

    public string DraftAudioId
    {
        get => _draftAudioId;
        set => SetProperty(ref _draftAudioId, value);
    }

    public string DraftDurationTicks
    {
        get => _draftDurationTicks;
        set => SetProperty(ref _draftDurationTicks, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlaybackButtonText));
            }
        }
    }

    public double PlayheadTick
    {
        get => _playheadTick;
        set => SetProperty(ref _playheadTick, value);
    }

    public string PlaybackPositionText
    {
        get => _playbackPositionText;
        set => SetProperty(ref _playbackPositionText, value);
    }

    public string PlaybackDebugText
    {
        get => _playbackDebugText;
        set => SetProperty(ref _playbackDebugText, value);
    }

    public string PlaybackLogText
    {
        get => _playbackLogText;
        set => SetProperty(ref _playbackLogText, value);
    }

    public string AudioPreparationText
    {
        get => _audioPreparationText;
        set => SetProperty(ref _audioPreparationText, value);
    }

    public bool IsPlaybackLogVisible
    {
        get => _isPlaybackLogVisible;
        set
        {
            if (SetProperty(ref _isPlaybackLogVisible, value))
            {
                OnPropertyChanged(nameof(PlaybackLogButtonText));
            }
        }
    }

    public double EditorTimelineHeight
    {
        get => _editorTimelineHeight;
        set => SetProperty(ref _editorTimelineHeight, value);
    }

    public int EditorTimelineStartTick
    {
        get => _editorTimelineStartTick;
        set => SetProperty(ref _editorTimelineStartTick, value);
    }

    public int EditorTimelineFocusTick
    {
        get => _editorTimelineFocusTick;
        set => SetProperty(ref _editorTimelineFocusTick, value);
    }

    public int EditorSelectedTick
    {
        get => _editorSelectedTick;
        set => SetProperty(ref _editorSelectedTick, value);
    }

    public string EditorSelectedLane
    {
        get => _editorSelectedLane;
        set => SetProperty(ref _editorSelectedLane, value);
    }

    public int EditorGridDivision
    {
        get => _editorGridDivision;
        set
        {
            var normalized = EditorGridDivisions.Contains(value) ? value : 16;
            if (SetProperty(ref _editorGridDivision, normalized))
            {
                OnPropertyChanged(nameof(EditorGridTicks));
                RefreshMeasureGridLines();
                StatusText = $"Grid 1/{normalized}";
            }
        }
    }

    public int EditorGridTicks => ResolveEditorGridTicks();

    public double ViewerHiSpeed
    {
        get => _viewerHiSpeed;
        set
        {
            var clamped = Math.Clamp(value, 1.0, 4.0);
            if (SetProperty(ref _viewerHiSpeed, clamped))
            {
                StatusText = $"HiSpeed {clamped:0.0}";
            }
        }
    }

    public void SetViewerHiSpeed(double value)
    {
        ViewerHiSpeed = value;
    }

    public bool CanUndoEditorCommand => _undoStack.Count > 0;

    public bool CanRedoEditorCommand => _redoStack.Count > 0;

    public bool CanPasteEditorObject => _editorClipboard is not null;

    public void SetEditorTimelineDraftFromHit(int tick, string lane)
    {
        var snappedTick = SnapEditorTick(tick);
        DraftTick = snappedTick.ToString();
        DraftLane = lane;
        StatusText = $"Editor position: tick {snappedTick}, lane {lane}";
    }

    public void UndoEditorCommand()
    {
        if (_undoStack.Count == 0)
        {
            StatusText = "Undoできる編集履歴がありません。";
            return;
        }

        var command = _undoStack.Pop();
        _isApplyingEditorHistory = true;
        try
        {
            command.Undo();
            _redoStack.Push(command);
            StatusText = $"Undo: {command.Name}";
        }
        finally
        {
            _isApplyingEditorHistory = false;
            NotifyEditorHistoryChanged();
        }
    }

    public void RedoEditorCommand()
    {
        if (_redoStack.Count == 0)
        {
            StatusText = "Redoできる編集履歴がありません。";
            return;
        }

        var command = _redoStack.Pop();
        _isApplyingEditorHistory = true;
        try
        {
            command.Redo();
            _undoStack.Push(command);
            StatusText = $"Redo: {command.Name}";
        }
        finally
        {
            _isApplyingEditorHistory = false;
            NotifyEditorHistoryChanged();
        }
    }

    public void CopyEditorObject()
    {
        if (_selectedChart is null || EditorSelectedTick < 0 || string.IsNullOrWhiteSpace(EditorSelectedLane))
        {
            StatusText = "コピー対象のオブジェクトが選択されていません。";
            return;
        }

        var note = Notes.FirstOrDefault(item =>
            item.Tick == EditorSelectedTick &&
            string.Equals(item.Lane, EditorSelectedLane, StringComparison.OrdinalIgnoreCase));
        if (note is not null)
        {
            _editorClipboard = EditorClipboardItem.FromNote(note);
            NotifyEditorClipboardChanged();
            StatusText = $"Copied note: tick {note.Tick}, lane {note.Lane}";
            return;
        }

        var bgm = _selectedChart.Chart.BackgroundAudio.FirstOrDefault(item =>
            item.Tick == EditorSelectedTick &&
            string.Equals(item.Lane ?? "background1", EditorSelectedLane, StringComparison.OrdinalIgnoreCase));
        if (bgm is not null)
        {
            _editorClipboard = EditorClipboardItem.FromBgm(bgm);
            NotifyEditorClipboardChanged();
            StatusText = $"Copied BGM: tick {bgm.Tick}, lane {bgm.Lane ?? "background1"}";
            return;
        }

        StatusText = "コピー対象のオブジェクトが見つかりません。";
    }

    public void PasteEditorObject()
    {
        if (_selectedChart is null)
        {
            return;
        }

        if (_editorClipboard is null)
        {
            StatusText = "貼り付けるオブジェクトがありません。";
            return;
        }

        var tick = int.TryParse(DraftTick, out var parsedTick)
            ? SnapEditorTick(parsedTick)
            : EditorSelectedTick >= 0 ? EditorSelectedTick : 0;
        var lane = string.IsNullOrWhiteSpace(DraftLane)
            ? _editorClipboard.Lane
            : DraftLane;

        if (_editorClipboard.Kind == EditorClipboardKind.Bgm || lane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
        {
            var bgmLane = ResolveBackgroundLaneForTick(
                tick,
                lane.StartsWith("background", StringComparison.OrdinalIgnoreCase) ? lane : _editorClipboard.Lane);
            var bgm = new BackgroundAudioEvent
            {
                Tick = tick,
                Lane = bgmLane,
                AudioId = _editorClipboard.AudioId
            };
            _selectedChart.Chart.BackgroundAudio.Add(bgm);
            SelectedNote = null;
            EditorSelectedTick = bgm.Tick;
            EditorSelectedLane = bgm.Lane ?? "background1";
            PushEditorCommand(new EditorCommand(
                "Paste BGM",
                () =>
                {
                    _selectedChart.Chart.BackgroundAudio.Remove(bgm);
                    ClearEditorSelection();
                    RefreshTimelineOnly();
                },
                () =>
                {
                    _selectedChart.Chart.BackgroundAudio.Add(bgm);
                    SelectedNote = null;
                    EditorSelectedTick = bgm.Tick;
                    EditorSelectedLane = bgm.Lane ?? "background1";
                    RefreshTimelineOnly();
                }));
            RefreshTimelineOnly();
            StatusText = $"Pasted BGM: tick {bgm.Tick}, lane {EditorSelectedLane}";
            return;
        }

        if (!IsPlayableEditorLane(lane))
        {
            StatusText = $"{lane} レーンにはノートを貼り付けられません。";
            return;
        }

        var row = new NoteRow
        {
            Tick = tick,
            Lane = lane,
            Type = _editorClipboard.Type,
            AudioId = _editorClipboard.AudioId,
            DurationTicks = _editorClipboard.DurationTicks
        };
        Notes.Add(row);
        SelectedNote = row;
        EditorSelectedTick = row.Tick;
        EditorSelectedLane = row.Lane;
        PushEditorCommand(new EditorCommand(
            "Paste Note",
            () =>
            {
                Notes.Remove(row);
                ClearEditorSelection();
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                Notes.Add(row);
                SelectedNote = row;
                EditorSelectedTick = row.Tick;
                EditorSelectedLane = row.Lane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        ApplyNoteRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Pasted note: tick {row.Tick}, lane {row.Lane}";
    }

    public void SetSelectedNoteLongNote()
    {
        if (SelectedNote is null)
        {
            StatusText = "LN化するノートが選択されていません。";
            return;
        }

        var note = SelectedNote;
        var oldType = note.Type;
        var oldDurationTicks = note.DurationTicks;
        var newDurationTicks = Math.Max(ResolveEditorGridTicks(), note.DurationTicks ?? 0);
        ApplySelectedNoteShape(
            note,
            oldType,
            oldDurationTicks,
            "hold",
            newDurationTicks,
            "Set LN",
            $"LN set: tick {note.Tick}, lane {note.Lane}, duration {newDurationTicks}");
    }

    public void ClearSelectedNoteLongNote()
    {
        if (SelectedNote is null)
        {
            StatusText = "LN解除するノートが選択されていません。";
            return;
        }

        var note = SelectedNote;
        var oldType = note.Type;
        var oldDurationTicks = note.DurationTicks;
        ApplySelectedNoteShape(
            note,
            oldType,
            oldDurationTicks,
            "tap",
            null,
            "Clear LN",
            $"LN cleared: tick {note.Tick}, lane {note.Lane}");
    }

    public void SetSelectedNoteChargeNote()
    {
        SetSelectedNoteExtendedLongNote("cn", "Set CN", "CN set");
    }

    public void SetSelectedNoteHellChargeNote()
    {
        SetSelectedNoteExtendedLongNote("hcn", "Set HCN", "HCN set");
    }

    public void SetSelectedNoteMine()
    {
        SetSelectedNoteSimpleType("mine", "Set Mine", "Mine note set");
    }

    public void SetSelectedNoteInvisible()
    {
        SetSelectedNoteSimpleType("invisible", "Set Invisible", "Invisible note set");
    }

    public void RepairSelectedMissingAudioReference()
    {
        if (_project is null ||
            SelectedIssueRow is not { Index: not null } issue ||
            SelectedAudioRow is null)
        {
            StatusText = "修正する参照切れIssueと置換先Audioを選択してください。";
            return;
        }

        var chart = _project.Charts.FirstOrDefault(item => item.Reference.Id == issue.Source);
        if (chart is null)
        {
            StatusText = $"修正対象の譜面が見つかりません: {issue.Source}";
            return;
        }

        var newAudioId = SelectedAudioRow.AudioId;
        if (string.IsNullOrWhiteSpace(newAudioId))
        {
            StatusText = "置換先AudioIdが空です。";
            return;
        }

        if (issue.ReferenceType == "note")
        {
            if (issue.Index.Value < 0 || issue.Index.Value >= chart.Chart.Notes.Count)
            {
                StatusText = "修正対象noteのindexが範囲外です。";
                return;
            }

            chart.Chart.Notes[issue.Index.Value].AudioId = newAudioId;
        }
        else if (issue.ReferenceType == "backgroundAudio")
        {
            if (issue.Index.Value < 0 || issue.Index.Value >= chart.Chart.BackgroundAudio.Count)
            {
                StatusText = "修正対象BGMのindexが範囲外です。";
                return;
            }

            chart.Chart.BackgroundAudio[issue.Index.Value].AudioId = newAudioId;
        }
        else
        {
            StatusText = $"このIssueは自動修正対象ではありません: {issue.ReferenceType}";
            return;
        }

        RefreshCollections();
        StatusText = $"参照切れを修正しました: {issue.AudioId} -> {newAudioId}";
    }

    public async Task PreviewSelectedAudioAsync()
    {
        if (_project is null || SelectedAudioRow is null)
        {
            StatusText = "プレビューする音源を選択してください。";
            return;
        }

        var audioId = SelectedAudioRow.AudioId;
        if (string.IsNullOrWhiteSpace(audioId))
        {
            StatusText = "AudioIdが空の音源はプレビューできません。";
            return;
        }

        await PreviewAudioIdAsync(audioId, stopTimelinePlayback: true);
    }

    public async Task PreviewAudioIdAsync(string audioId, bool stopTimelinePlayback = false)
    {
        if (_project is null || string.IsNullOrWhiteSpace(audioId))
        {
            return;
        }

        try
        {
            if (stopTimelinePlayback)
            {
                StopPlayback();
            }

            ApplyAudioRows();
            _audioCache ??= CreateAudioCache(_project);
            var cache = _audioCache;
            var progress = CreateAudioCacheProgress("preview");
            await Task.Run(() => cache.PrepareAudioIds([audioId], progress));
            if (!cache.TryGetFilePath(audioId, out var filePath))
            {
                StatusText = $"音源を展開できませんでした: {audioId}";
                return;
            }

            _audioPlayer.PlayOneShot(filePath);
            StatusText = $"Preview audio: {audioId}";
        }
        catch (Exception ex)
        {
            StatusText = $"音源プレビューに失敗しました: {audioId} ({ex.Message})";
        }
        finally
        {
            AudioPreparationText = "";
        }
    }

    public async Task AddAudioAssetAsync(string sourceFilePath, string requestedAudioId)
    {
        if (_project?.AudioManifest is null)
        {
            StatusText = "NBMSを開いてから音声を追加してください。";
            return;
        }

        try
        {
            CancelAudioCachePreparation();
            _audioCache?.Dispose();
            _audioCache = null;
            _audioCacheTask = null;

            var audioPath = ResolveAudioArchivePath(_project);
            var entry = await _audioArchiveService.AddAudioFileAsync(
                audioPath,
                _project.AudioManifest,
                sourceFilePath,
                requestedAudioId);
            RefreshCollections();
            SelectedAudioRow = AudioEntries.FirstOrDefault(row => row.AudioId == entry.AudioId);
            StatusText = $"音声を追加しました: {entry.AudioId}";
        }
        catch (Exception ex)
        {
            StatusText = $"音声追加に失敗しました: {ex.Message}";
        }
    }

    public void RenameSelectedAudioAsset(string newAudioId)
    {
        if (_project?.AudioManifest is null || SelectedAudioRow is null)
        {
            StatusText = "リネームする音声を選択してください。";
            return;
        }

        var oldAudioId = SelectedAudioRow.AudioId;
        var normalizedNewId = NormalizeAudioId(newAudioId);
        if (string.IsNullOrWhiteSpace(normalizedNewId) || string.Equals(oldAudioId, normalizedNewId, StringComparison.Ordinal))
        {
            return;
        }

        if (_project.AudioManifest.Entries.Any(entry => string.Equals(entry.AudioId, normalizedNewId, StringComparison.Ordinal)))
        {
            StatusText = $"同じAudioIdがすでに存在します: {normalizedNewId}";
            return;
        }

        var entryToRename = _project.AudioManifest.Entries.FirstOrDefault(entry => entry.AudioId == oldAudioId);
        if (entryToRename is null)
        {
            StatusText = $"manifestにAudioIdが見つかりません: {oldAudioId}";
            return;
        }

        entryToRename.AudioId = normalizedNewId;
        foreach (var chart in _project.Charts)
        {
            foreach (var note in chart.Chart.Notes.Where(note => note.AudioId == oldAudioId))
            {
                note.AudioId = normalizedNewId;
            }

            foreach (var bgm in chart.Chart.BackgroundAudio.Where(background => background.AudioId == oldAudioId))
            {
                bgm.AudioId = normalizedNewId;
            }
        }

        RefreshCollections();
        SelectedAudioRow = AudioEntries.FirstOrDefault(row => row.AudioId == normalizedNewId);
        StatusText = $"AudioIdを変更しました: {oldAudioId} -> {normalizedNewId}";
    }

    public async Task DeleteSelectedAudioAssetAsync()
    {
        if (_project?.AudioManifest is null || SelectedAudioRow is null)
        {
            StatusText = "削除する音声を選択してください。";
            return;
        }

        var audioId = SelectedAudioRow.AudioId;
        if (IsAudioReferenced(audioId))
        {
            StatusText = $"参照中の音声は削除できません: {audioId}";
            return;
        }

        await RemoveAudioAssetsAsync([audioId], $"音声を削除しました: {audioId}");
    }

    public async Task RemoveUnusedAudioAssetsAsync()
    {
        if (_project?.AudioManifest is null)
        {
            return;
        }

        var referenced = ResolveReferencedAudioIds();
        var unused = _project.AudioManifest.Entries
            .Where(entry => !referenced.Contains(entry.AudioId))
            .Select(entry => entry.AudioId)
            .ToList();
        if (unused.Count == 0)
        {
            StatusText = "未使用音声はありません。";
            return;
        }

        await RemoveAudioAssetsAsync(unused, $"未使用音声を削除しました: {unused.Count}件");
    }

    public string? SelectTimelineObjectFromHit(int tick, string lane)
    {
        if (_selectedChart is null)
        {
            return null;
        }

        var snappedTick = SnapEditorTick(tick);
        var gridHalf = ResolveEditorGridTicks() / 2;
        var note = Notes
            .OrderBy(item => Math.Abs(item.Tick - snappedTick))
            .FirstOrDefault(item =>
                string.Equals(item.Lane, lane, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(item.Tick - snappedTick) <= gridHalf);

        if (note is not null)
        {
            SelectedNote = note;
            EditorSelectedTick = note.Tick;
            EditorSelectedLane = note.Lane;
            StatusText = $"Selected note: tick {note.Tick}, lane {note.Lane}, audio {note.AudioId}";
            return string.IsNullOrWhiteSpace(note.AudioId) ? null : note.AudioId;
        }

        var bgm = _selectedChart.Chart.BackgroundAudio
            .OrderBy(item => Math.Abs(item.Tick - snappedTick))
            .FirstOrDefault(item =>
                string.Equals(item.Lane ?? "background1", lane, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(item.Tick - snappedTick) <= gridHalf);

        if (bgm is not null)
        {
            SelectedNote = null;
            EditorSelectedTick = bgm.Tick;
            EditorSelectedLane = bgm.Lane ?? "background1";
            DraftAudioId = bgm.AudioId;
            StatusText = $"Selected BGM: tick {bgm.Tick}, lane {EditorSelectedLane}, audio {bgm.AudioId}";
            return bgm.AudioId;
        }

        if (IsTimingEditorLane(lane))
        {
            var timing = FindTimingEventNear(snappedTick, lane);
            if (timing is not null)
            {
                SelectedNote = null;
                EditorSelectedTick = timing.Tick;
                EditorSelectedLane = ResolveTimingLane(timing);
                StatusText = $"Selected timing: {timing.Type} tick {timing.Tick}";
                return null;
            }
        }

        EditorSelectedTick = -1;
        EditorSelectedLane = "";
        return null;
    }

    public void MoveSelectedTimelineObject(int fromTick, string fromLane, int toTick, string toLane)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var snappedFromTick = SnapEditorTick(fromTick);
        var snappedToTick = SnapEditorTick(toTick);
        if (snappedFromTick == snappedToTick &&
            string.Equals(fromLane, toLane, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (SelectedNote is not null &&
            Math.Abs(SelectedNote.Tick - snappedFromTick) <= ResolveEditorGridTicks() / 2 &&
            string.Equals(SelectedNote.Lane, fromLane, StringComparison.OrdinalIgnoreCase))
        {
            MoveSelectedNote(snappedToTick, toLane);
            return;
        }

        var bgm = _selectedChart.Chart.BackgroundAudio
            .OrderBy(item => Math.Abs(item.Tick - snappedFromTick))
            .FirstOrDefault(item =>
                string.Equals(item.Lane ?? "background1", fromLane, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(item.Tick - snappedFromTick) <= ResolveEditorGridTicks() / 2);
        if (bgm is not null)
        {
            MoveBackgroundAudio(bgm, snappedToTick, toLane);
            return;
        }

        if (IsTimingEditorLane(fromLane) || IsTimingEditorLane(toLane))
        {
            if (!string.Equals(fromLane, toLane, StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "Eventレーンの横移動はできません。同じEventレーン内で上下に移動してください。";
                return;
            }

            var timing = FindTimingEventNear(snappedFromTick, fromLane);
            if (timing is not null)
            {
                MoveTimingEvent(timing, snappedToTick, toLane);
                return;
            }
        }

        StatusText = "移動対象のオブジェクトが見つかりません。";
    }

    public void AddTimelineObjectFromHit(int tick, string lane)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var snappedTick = SnapEditorTick(tick);
        if (IsTimingEditorLane(lane))
        {
            AddTimelineTimingEventFromHit(snappedTick, lane);
            return;
        }

        var audioId = ResolveEditorAudioId();
        if (string.IsNullOrWhiteSpace(audioId))
        {
            StatusText = "追加するaudioIdがありません。先に音源を選択してください。";
            return;
        }

        if (lane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
        {
            var bgmLane = ResolveBackgroundLaneForTick(snappedTick, lane);
            var bgm = new BackgroundAudioEvent
            {
                Tick = snappedTick,
                Lane = bgmLane,
                AudioId = audioId
            };
            _selectedChart.Chart.BackgroundAudio.Add(bgm);
            SelectedNote = null;
            EditorSelectedTick = bgm.Tick;
            EditorSelectedLane = bgm.Lane ?? "background1";
            PushEditorCommand(new EditorCommand(
                "Add BGM",
                () =>
                {
                    _selectedChart.Chart.BackgroundAudio.Remove(bgm);
                    ClearEditorSelection();
                    RefreshTimelineOnly();
                },
                () =>
                {
                    _selectedChart.Chart.BackgroundAudio.Add(bgm);
                    SelectedNote = null;
                    EditorSelectedTick = bgm.Tick;
                    EditorSelectedLane = bgm.Lane ?? "background1";
                    RefreshTimelineOnly();
                }));
            RefreshTimelineOnly();
            StatusText = $"BGM object added: tick {snappedTick}, lane {EditorSelectedLane}, audio {audioId}";
            return;
        }

        if (!IsPlayableEditorLane(lane))
        {
            StatusText = $"{lane} レーンの編集は後続Phaseで実装します。";
            return;
        }

        var row = new NoteRow
        {
            Tick = snappedTick,
            Lane = lane,
            Type = "tap",
            AudioId = audioId
        };

        Notes.Add(row);
        SelectedNote = row;
        EditorSelectedTick = row.Tick;
        EditorSelectedLane = row.Lane;
        PushEditorCommand(new EditorCommand(
            "Add Note",
            () =>
            {
                Notes.Remove(row);
                ClearEditorSelection();
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                Notes.Add(row);
                SelectedNote = row;
                EditorSelectedTick = row.Tick;
                EditorSelectedLane = row.Lane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        ApplyNoteRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Note object added: tick {snappedTick}, lane {lane}, audio {audioId}";
    }

    public void AddTimelineTimingEventFromHit(int tick, string lane, double? value = null, int? durationTicks = null)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var snappedTick = SnapEditorTick(tick);
        AddTimingEventFromHit(snappedTick, lane, value, durationTicks);
    }

    public bool IsTimingLane(string lane) => IsTimingEditorLane(lane);

    public void DeleteTimelineObjectFromHit(int tick, string lane)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var snappedTick = SnapEditorTick(tick);
        if (lane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
        {
            var bgm = _selectedChart.Chart.BackgroundAudio
                .OrderBy(item => Math.Abs(item.Tick - snappedTick))
                .FirstOrDefault(item =>
                    string.Equals(item.Lane ?? "background1", lane, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(item.Tick - snappedTick) <= ResolveEditorGridTicks() / 2);
            if (bgm is null)
            {
                StatusText = $"削除対象がありません: tick {snappedTick}, lane {lane}";
                return;
            }

            _selectedChart.Chart.BackgroundAudio.Remove(bgm);
            EditorSelectedTick = -1;
            EditorSelectedLane = "";
            PushEditorCommand(new EditorCommand(
                "Delete BGM",
                () =>
                {
                    _selectedChart.Chart.BackgroundAudio.Add(bgm);
                    SelectedNote = null;
                    EditorSelectedTick = bgm.Tick;
                    EditorSelectedLane = bgm.Lane ?? "background1";
                    RefreshTimelineOnly();
                },
                () =>
                {
                    _selectedChart.Chart.BackgroundAudio.Remove(bgm);
                    ClearEditorSelection();
                    RefreshTimelineOnly();
                }));
            RefreshTimelineOnly();
            StatusText = $"BGM object deleted: tick {bgm.Tick}, lane {lane}";
            return;
        }

        var note = Notes
            .OrderBy(item => Math.Abs(item.Tick - snappedTick))
            .FirstOrDefault(item =>
                string.Equals(item.Lane, lane, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(item.Tick - snappedTick) <= ResolveEditorGridTicks() / 2);
        if (note is null)
        {
            StatusText = $"削除対象がありません: tick {snappedTick}, lane {lane}";
            return;
        }

        Notes.Remove(note);
        if (ReferenceEquals(SelectedNote, note))
        {
            SelectedNote = null;
        }

        EditorSelectedTick = -1;
        EditorSelectedLane = "";
        PushEditorCommand(new EditorCommand(
            "Delete Note",
            () =>
            {
                Notes.Add(note);
                SelectedNote = note;
                EditorSelectedTick = note.Tick;
                EditorSelectedLane = note.Lane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                Notes.Remove(note);
                ClearEditorSelection();
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        ApplyNoteRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Note object deleted: tick {note.Tick}, lane {lane}";
    }

    public bool HasProject => _project is not null;

    public string ChartSummaryText => _selectedChart is null
        ? "譜面未選択"
        : $"{_selectedChart.Reference.Id} / {_selectedChart.Reference.Mode} / Lv.{_selectedChart.Reference.Difficulty}";

    public string AudioSummaryText => $"{AudioEntries.Count} audio";

    public string MediaSummaryText => $"{MediaEntries.Count} media";

    public string EventSummaryText => $"{Events.Count} events";

    public string IssueSummaryText => Issues.Count == 0 ? "参照切れなし" : $"{Issues.Count}件の確認事項";

    public string PlaybackButtonText => IsPlaying ? "再生中" : _playbackOffsetSeconds > 0 ? "再開" : "再生";

    public string PlaybackLogButtonText => IsPlaybackLogVisible ? "ログ非表示" : "ログ表示";

    public void TogglePlaybackLogVisibility()
    {
        IsPlaybackLogVisible = !IsPlaybackLogVisible;
    }

    public void LaunchMonoGameViewer()
    {
        if (_project is null || _selectedChart is null)
        {
            StatusText = "MonoGame Viewerを起動するNBMSを開いてください。";
            return;
        }

        var executablePath = FindMonoGameViewerExecutablePath();
        var startInfo = executablePath is not null
            ? new ProcessStartInfo(executablePath)
            : null;

        if (startInfo is null)
        {
            StatusText = "MonoGame Viewerの実行ファイルまたはプロジェクトが見つかりません。";
            return;
        }

        lock (_monoGameViewerLock)
        {
            CleanupMonoGameViewerProcess();
            if (_monoGameViewerProcess is not null)
            {
                StatusText = "MonoGame Viewerはすでに起動中です。";
                return;
            }
        }

        startInfo.UseShellExecute = true;
        startInfo.WindowStyle = ProcessWindowStyle.Normal;
        startInfo.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        startInfo.Arguments = $"{QuoteProcessArgument(_project.HeaderPath)} --chart {QuoteProcessArgument(_selectedChart.Reference.Id)}";

        var process = Process.Start(startInfo);
        if (process is null)
        {
            StatusText = "MonoGame Viewerを起動できませんでした。";
            return;
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            lock (_monoGameViewerLock)
            {
                if (ReferenceEquals(_monoGameViewerProcess, process))
                {
                    _monoGameViewerProcess = null;
                }
            }
        };

        lock (_monoGameViewerLock)
        {
            _monoGameViewerProcess = process;
        }

        _ = MonitorMonoGameViewerStartupAsync(process);
        StatusText = "MonoGame Viewerを起動しました。";
    }

    public void SetMonoGameViewerPath(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            StatusText = "指定されたMonoGame Viewerが見つかりません。";
            return;
        }

        var fileName = Path.GetFileName(executablePath);
        if (!fileName.Equals("NBMS.Studio.MonoGameViewer.exe", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "NBMS.Studio.MonoGameViewer.exeを指定してください。";
            return;
        }

        SaveStudioSettings(new StudioSettings
        {
            MonoGameViewerPath = executablePath
        });
        StatusText = $"MonoGame Viewerのパスを保存しました: {executablePath}";
    }

    private async Task MonitorMonoGameViewerStartupAsync(Process process)
    {
        try
        {
            try
            {
                process.WaitForInputIdle(3000);
            }
            catch
            {
                // MonoGameの初期化状態によってはInputIdleを待てないため、通常のpollに戻す。
            }

            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(200);
                process.Refresh();
                if (process.HasExited)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusText = $"MonoGame Viewerが終了しました。ExitCode={process.ExitCode}";
                    });
                    return;
                }

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return;
                }
            }

            process.Refresh();
            if (!process.HasExited && process.MainWindowHandle == IntPtr.Zero)
            {
                process.Kill(entireProcessTree: true);
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = "MonoGame Viewerのウィンドウが表示されなかったため、プロセスを停止しました。%TEMP%\\NBMS.Studio.MonoGameViewer.logを確認してください。";
                });
            }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"MonoGame Viewer起動監視で例外が発生しました: {ex.Message}";
            });
        }
        finally
        {
            lock (_monoGameViewerLock)
            {
                CleanupMonoGameViewerProcess();
            }
        }
    }

    private void CleanupMonoGameViewerProcess()
    {
        if (_monoGameViewerProcess is null)
        {
            return;
        }

        try
        {
            _monoGameViewerProcess.Refresh();
            if (!_monoGameViewerProcess.HasExited && _monoGameViewerProcess.MainWindowHandle == IntPtr.Zero)
            {
                _monoGameViewerProcess.Kill(entireProcessTree: true);
                _monoGameViewerProcess.WaitForExit(1000);
            }
        }
        catch
        {
        }

        if (_monoGameViewerProcess.HasExited)
        {
            _monoGameViewerProcess.Dispose();
            _monoGameViewerProcess = null;
        }
    }

    private void StopMonoGameViewer()
    {
        Process? process;
        lock (_monoGameViewerLock)
        {
            process = _monoGameViewerProcess;
            _monoGameViewerProcess = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            process.Refresh();
            if (!process.HasExited)
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1500))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task OpenProjectAsync(string headerPath)
    {
        StopPlayback();
        CancelAudioCachePreparation();
        _audioCache?.Dispose();
        _audioCache = null;
        _audioCacheTask = null;
        _urgentAudioPrepareIds.Clear();

        _project = await _projectService.OpenHeaderAsync(headerPath);
        _selectedChart = _project.Charts.FirstOrDefault();
        AudioPreparationText = "";
        ClearEditorHistory();

        LoadHeaderFields();
        RefreshCollections();
        StatusText = $"読み込み完了: {headerPath}";
        OnPropertyChanged(nameof(HasProject));
    }

    private static string? FindMonoGameViewerExecutablePath()
    {
        var relativeExecutable = Path.Combine(
            AppContext.BaseDirectory,
            "Viewers",
            "MonoGame",
            "NBMS.Studio.MonoGameViewer.exe");
        if (File.Exists(relativeExecutable))
        {
            return relativeExecutable;
        }

        var settings = LoadStudioSettings();
        if (!string.IsNullOrWhiteSpace(settings.MonoGameViewerPath) &&
            File.Exists(settings.MonoGameViewerPath))
        {
            return settings.MonoGameViewerPath;
        }

        return null;
    }

    private static StudioSettings LoadStudioSettings()
    {
        var path = ResolveStudioSettingsPath();
        if (!File.Exists(path))
        {
            return new StudioSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StudioSettings>(json) ?? new StudioSettings();
        }
        catch
        {
            return new StudioSettings();
        }
    }

    private static void SaveStudioSettings(StudioSettings settings)
    {
        var path = ResolveStudioSettingsPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }

    private static string ResolveStudioSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "NBMS Studio", "settings.json");
    }

    private static string QuoteProcessArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private sealed class StudioSettings
    {
        public string? MonoGameViewerPath { get; set; }
    }

    public async Task SaveProjectAsync()
    {
        if (_project is null)
        {
            return;
        }

        ApplyHeaderFields();
        ApplyNoteRows();
        ApplyAudioRows();
        ApplyMediaRows();
        await _projectService.SaveAsync(_project);

        // 保存後はハッシュと参照チェックを再計算するため、開き直して画面を同期する。
        await OpenProjectAsync(_project.HeaderPath);
        StatusText = "保存しました。";
    }

    public async Task CreatePackageAsync(string outputPath)
    {
        if (_project is null)
        {
            return;
        }

        await SaveProjectAsync();
        await _packageService.CreatePackageAsync(_project, outputPath);
        StatusText = $"配布パッケージを作成しました: {outputPath}";
    }

    public async Task ConvertBmsAsync(string bmsPath, string outputDirectory)
    {
        StopPlayback();
        StatusText = "BMSをNBMSへ変換しています...";

        var result = await _bmsConversionService.ConvertAsync(bmsPath, outputDirectory);
        await OpenProjectAsync(result.HeaderPath);
        StatusText = $"BMS変換完了: {result.HeaderPath}";
    }

    public Task<BmsFolderConversionPreview> PreviewBmsFolderConversionAsync(string bmsDirectory)
    {
        return _bmsConversionService.PreviewFolderAsync(bmsDirectory);
    }

    public async Task ConvertBmsFolderAsync(
        string bmsDirectory,
        string outputDirectory,
        BmsFolderConversionOptions? options = null)
    {
        StopPlayback();
        StatusText = "BMSフォルダをNBMSへ一括変換しています...";

        var result = await _bmsConversionService.ConvertFolderAsync(bmsDirectory, outputDirectory, options);
        var first = result.Results.FirstOrDefault()
            ?? throw new InvalidDataException("変換対象のBMS譜面がありません。");

        await OpenProjectAsync(first.HeaderPath);
        StatusText = $"BMS一括変換完了: {result.Results.Count}件";
    }

    public void SelectChartById(string chartId)
    {
        if (_project is null)
        {
            return;
        }

        if (_selectedChart?.Reference.Id == chartId)
        {
            return;
        }

        StopPlayback();
        CancelAudioCachePreparation();
        ApplyNoteRows();
        ApplyAudioRows();
        ApplyMediaRows();
        ClearEditorHistory();
        _selectedChart = _project.Charts.FirstOrDefault(chart => chart.Reference.Id == chartId) ?? _project.Charts.FirstOrDefault();
        _audioCache?.Dispose();
        _audioCache = null;
        _audioCacheTask = null;
        _urgentAudioPrepareIds.Clear();
        AudioPreparationText = "";
        _selectedChartRow = Charts.FirstOrDefault(row => row.Id == _selectedChart?.Reference.Id);
        OnPropertyChanged(nameof(SelectedChartRow));
        RefreshNotesAndTimeline();
        RefreshMediaRows();
        OnPropertyChanged(nameof(ChartSummaryText));
        StatusText = $"譜面を選択しました: {_selectedChart?.Reference.Id}";
    }

    public void AddDraftNote()
    {
        if (_selectedChart is null)
        {
            return;
        }

        if (!int.TryParse(DraftTick, out var tick))
        {
            StatusText = "Tickには整数を入力してください。";
            return;
        }

        int? durationTicks = null;
        if (!string.IsNullOrWhiteSpace(DraftDurationTicks))
        {
            if (!int.TryParse(DraftDurationTicks, out var parsedDuration))
            {
                StatusText = "Durationには整数を入力してください。";
                return;
            }

            durationTicks = parsedDuration;
        }

        var row = new NoteRow
        {
            Tick = tick,
            Lane = string.IsNullOrWhiteSpace(DraftLane) ? "key1" : DraftLane,
            Type = string.IsNullOrWhiteSpace(DraftType) ? "tap" : DraftType,
            AudioId = DraftAudioId,
            DurationTicks = durationTicks
        };

        Notes.Add(row);
        SelectedNote = row;
        ApplyNoteRows();
        RefreshTimelineOnly();
        StatusText = "ノーツを追加しました。";
    }

    private int SnapEditorTick(int tick)
    {
        var gridTicks = ResolveEditorGridTicks();
        return Math.Max(0, (int)Math.Round(tick / (double)gridTicks) * gridTicks);
    }

    private int ResolveEditorGridTicks()
    {
        if (_selectedChart is null)
        {
            return 240;
        }

        const int defaultMeasureTicks = 3840;
        var measureTicks = _selectedChart.Chart.Resolution > 0
            ? _selectedChart.Chart.Resolution * 4
            : defaultMeasureTicks;
        return Math.Max(1, measureTicks / Math.Max(1, EditorGridDivision));
    }

    private string ResolveEditorAudioId()
    {
        if (!string.IsNullOrWhiteSpace(DraftAudioId))
        {
            return DraftAudioId;
        }

        if (SelectedAudioRow is not null && !string.IsNullOrWhiteSpace(SelectedAudioRow.AudioId))
        {
            return SelectedAudioRow.AudioId;
        }

        return AudioEntries.FirstOrDefault()?.AudioId ?? "";
    }

    private void AddTimingEventFromHit(int tick, string lane, double? value = null, int? durationTicks = null)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var timing = CreateDefaultTimingEvent(tick, lane);
        if (value.HasValue)
        {
            timing.Value = value.Value;
        }

        if (durationTicks.HasValue)
        {
            timing.DurationTicks = durationTicks.Value;
        }

        _selectedChart.Chart.Timing.Add(timing);
        EditorSelectedTick = timing.Tick;
        EditorSelectedLane = lane;
        PushEditorCommand(new EditorCommand(
            $"Add {timing.Type.ToUpperInvariant()}",
            () =>
            {
                _selectedChart.Chart.Timing.Remove(timing);
                ClearEditorSelection();
                RefreshTimelineOnly();
            },
            () =>
            {
                _selectedChart.Chart.Timing.Add(timing);
                EditorSelectedTick = timing.Tick;
                EditorSelectedLane = lane;
                RefreshTimelineOnly();
            }));
        RefreshTimelineOnly();
        StatusText = $"Timing event added: {timing.Type} tick {tick}";
    }

    private TimingEvent CreateDefaultTimingEvent(int tick, string lane)
    {
        return lane switch
        {
            "bpm" => new TimingEvent
            {
                Tick = tick,
                Type = "bpm",
                Value = ResolveDefaultBpmValue()
            },
            "stop" => new TimingEvent
            {
                Tick = tick,
                Type = "stop",
                DurationTicks = ResolveEditorGridTicks()
            },
            "measure" => new TimingEvent
            {
                Tick = tick,
                Type = "measureLength",
                Value = 1.0,
                ExtensionId = "nbms.bmsCompat",
                Event = "measureLength"
            },
            "scroll" => new TimingEvent
            {
                Tick = tick,
                Type = "scroll",
                Value = 1.0,
                ExtensionId = "nbms.scroll",
                Event = "scroll"
            },
            "speed" => new TimingEvent
            {
                Tick = tick,
                Type = "speed",
                Value = 1.0
            },
            _ => new TimingEvent
            {
                Tick = tick,
                Type = lane,
                Value = 1.0
            }
        };
    }

    private double ResolveDefaultBpmValue()
    {
        if (double.TryParse(BpmText, out var bpm) && bpm > 0)
        {
            return bpm;
        }

        if (_selectedChart is not null)
        {
            var currentBpm = _selectedChart.Chart.Timing
                .Where(timing => timing.Type == "bpm" && timing.Value is > 0)
                .OrderBy(timing => timing.Tick)
                .LastOrDefault();
            if (currentBpm?.Value is > 0)
            {
                return currentBpm.Value.Value;
            }
        }

        return 120.0;
    }

    private static bool IsPlayableEditorLane(string lane)
    {
        return lane is "scratch" or "scratch2" ||
               lane.StartsWith("key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTimingEditorLane(string lane)
    {
        return lane is "bpm" or "stop" or "scroll" or "speed" or "measure";
    }

    private void MoveSelectedNote(int newTick, string newLane)
    {
        if (SelectedNote is null)
        {
            return;
        }

        if (newLane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
        {
            MoveSelectedNoteToBackgroundAudio(newTick, newLane);
            return;
        }

        if (!IsPlayableEditorLane(newLane))
        {
            StatusText = "ノートはプレイレーンへ移動してください。";
            return;
        }

        var note = SelectedNote;
        var oldTick = note.Tick;
        var oldLane = note.Lane;
        note.Tick = newTick;
        note.Lane = newLane;
        EditorSelectedTick = newTick;
        EditorSelectedLane = newLane;

        PushEditorCommand(new EditorCommand(
            "Move Note",
            () =>
            {
                note.Tick = oldTick;
                note.Lane = oldLane;
                SelectedNote = note;
                EditorSelectedTick = oldTick;
                EditorSelectedLane = oldLane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                note.Tick = newTick;
                note.Lane = newLane;
                SelectedNote = note;
                EditorSelectedTick = newTick;
                EditorSelectedLane = newLane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        ApplyNoteRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Moved note: {oldLane}@{oldTick} -> {newLane}@{newTick}";
    }

    private void MoveSelectedNoteToBackgroundAudio(int newTick, string newLane)
    {
        if (SelectedNote is null || _selectedChart is null)
        {
            return;
        }

        var note = SelectedNote;
        var oldTick = note.Tick;
        var oldLane = note.Lane;
        var bgmLane = ResolveBackgroundLaneForTick(newTick, newLane);
        var bgm = new BackgroundAudioEvent
        {
            Tick = newTick,
            Lane = bgmLane,
            AudioId = note.AudioId
        };

        Notes.Remove(note);
        _selectedChart.Chart.BackgroundAudio.Add(bgm);
        SelectedNote = null;
        EditorSelectedTick = newTick;
        EditorSelectedLane = bgmLane;

        PushEditorCommand(new EditorCommand(
            "Move Note to BGM",
            () =>
            {
                _selectedChart.Chart.BackgroundAudio.Remove(bgm);
                Notes.Add(note);
                SelectedNote = note;
                EditorSelectedTick = oldTick;
                EditorSelectedLane = oldLane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                Notes.Remove(note);
                _selectedChart.Chart.BackgroundAudio.Add(bgm);
                SelectedNote = null;
                EditorSelectedTick = newTick;
                EditorSelectedLane = bgmLane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        ApplyNoteRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Moved note to BGM: {oldLane}@{oldTick} -> {bgmLane}@{newTick}";
    }

    private void MoveBackgroundAudio(BackgroundAudioEvent bgm, int newTick, string newLane)
    {
        if (IsPlayableEditorLane(newLane))
        {
            MoveBackgroundAudioToNote(bgm, newTick, newLane);
            return;
        }

        if (!newLane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "BGMはbackgroundレーンへ移動してください。";
            return;
        }

        var oldTick = bgm.Tick;
        var oldLane = bgm.Lane ?? "background1";
        var resolvedLane = ResolveBackgroundLaneForTick(newTick, newLane);
        bgm.Tick = newTick;
        bgm.Lane = resolvedLane;
        SelectedNote = null;
        EditorSelectedTick = newTick;
        EditorSelectedLane = resolvedLane;

        PushEditorCommand(new EditorCommand(
            "Move BGM",
            () =>
            {
                bgm.Tick = oldTick;
                bgm.Lane = oldLane;
                SelectedNote = null;
                EditorSelectedTick = oldTick;
                EditorSelectedLane = oldLane;
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                bgm.Tick = newTick;
                bgm.Lane = resolvedLane;
                SelectedNote = null;
                EditorSelectedTick = newTick;
                EditorSelectedLane = resolvedLane;
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Moved BGM: {oldLane}@{oldTick} -> {resolvedLane}@{newTick}";
    }

    private void MoveBackgroundAudioToNote(BackgroundAudioEvent bgm, int newTick, string newLane)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var oldTick = bgm.Tick;
        var oldLane = bgm.Lane ?? "background1";
        var note = new NoteRow
        {
            Tick = newTick,
            Lane = newLane,
            Type = "tap",
            AudioId = bgm.AudioId
        };

        _selectedChart.Chart.BackgroundAudio.Remove(bgm);
        Notes.Add(note);
        SelectedNote = note;
        EditorSelectedTick = newTick;
        EditorSelectedLane = newLane;

        PushEditorCommand(new EditorCommand(
            "Move BGM to Note",
            () =>
            {
                Notes.Remove(note);
                _selectedChart.Chart.BackgroundAudio.Add(bgm);
                SelectedNote = null;
                EditorSelectedTick = oldTick;
                EditorSelectedLane = oldLane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                _selectedChart.Chart.BackgroundAudio.Remove(bgm);
                Notes.Add(note);
                SelectedNote = note;
                EditorSelectedTick = newTick;
                EditorSelectedLane = newLane;
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        ApplyNoteRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Moved BGM to note: {oldLane}@{oldTick} -> {newLane}@{newTick}";
    }

    private void MoveTimingEvent(TimingEvent timing, int newTick, string newLane)
    {
        var oldTick = timing.Tick;
        var oldLane = ResolveTimingLane(timing);
        timing.Tick = newTick;
        EditorSelectedTick = newTick;
        EditorSelectedLane = oldLane;

        PushEditorCommand(new EditorCommand(
            "Move Timing",
            () =>
            {
                timing.Tick = oldTick;
                EditorSelectedTick = oldTick;
                EditorSelectedLane = oldLane;
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                timing.Tick = newTick;
                EditorSelectedTick = newTick;
                EditorSelectedLane = oldLane;
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Moved timing: {oldLane}@{oldTick} -> {oldLane}@{newTick}";
    }

    private TimingEvent? FindTimingEventNear(int tick, string lane)
    {
        if (_selectedChart is null)
        {
            return null;
        }

        var halfGrid = ResolveEditorGridTicks() / 2;
        return _selectedChart.Chart.Timing
            .Where(timing => string.Equals(ResolveTimingLane(timing), lane, StringComparison.OrdinalIgnoreCase))
            .OrderBy(timing => Math.Abs(timing.Tick - tick))
            .FirstOrDefault(timing => Math.Abs(timing.Tick - tick) <= halfGrid);
    }

    private static string ResolveTimingLane(TimingEvent timing)
    {
        return timing.Type switch
        {
            "bpm" => "bpm",
            "stop" => "stop",
            "measureLength" => "measure",
            "scroll" => "scroll",
            "speed" => "speed",
            _ => timing.Type
        };
    }

    private static void ApplyTimingLane(TimingEvent timing, string lane, string? fallbackType = null)
    {
        switch (lane)
        {
            case "bpm":
                timing.Type = "bpm";
                timing.Value ??= 120.0;
                timing.DurationTicks = null;
                break;
            case "stop":
                timing.Type = "stop";
                timing.DurationTicks ??= 240;
                timing.Value = null;
                break;
            case "measure":
                timing.Type = "measureLength";
                timing.Value ??= 1.0;
                timing.DurationTicks = null;
                timing.ExtensionId ??= "nbms.bmsCompat";
                timing.Event ??= "measureLength";
                break;
            case "scroll":
                timing.Type = "scroll";
                timing.Value ??= 1.0;
                timing.DurationTicks = null;
                timing.ExtensionId ??= "nbms.scroll";
                timing.Event ??= "scroll";
                break;
            case "speed":
                timing.Type = "speed";
                timing.Value ??= 1.0;
                timing.DurationTicks = null;
                break;
            default:
                timing.Type = fallbackType ?? lane;
                break;
        }
    }

    private string ResolveBackgroundLaneForTick(int tick, string preferredLane)
    {
        if (_selectedChart is null)
        {
            return string.IsNullOrWhiteSpace(preferredLane) ? "background1" : preferredLane;
        }

        var occupied = _selectedChart.Chart.BackgroundAudio
            .Where(item => item.Tick == tick)
            .Select(item => item.Lane ?? "background1")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preferred = string.IsNullOrWhiteSpace(preferredLane) ? "background1" : preferredLane;
        if (!occupied.Contains(preferred))
        {
            return preferred;
        }

        var maxExistingIndex = _selectedChart.Chart.BackgroundAudio
            .Select(item => ResolveBackgroundLaneNumber(item.Lane ?? "background1"))
            .DefaultIfEmpty(8)
            .Max();
        for (var index = 1; index <= Math.Max(8, maxExistingIndex + 1); index++)
        {
            var candidate = $"background{index}";
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"background{maxExistingIndex + 1}";
    }

    private static int ResolveBackgroundLaneNumber(string lane)
    {
        const string prefix = "background";
        return lane.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(lane[prefix.Length..], out var number)
            ? Math.Max(1, number)
            : 1;
    }

    private void ApplySelectedNoteShape(
        NoteRow note,
        string oldType,
        int? oldDurationTicks,
        string newType,
        int? newDurationTicks,
        string commandName,
        string statusText)
    {
        if (newType is "cn" or "hcn")
        {
            EnsureSelectedChartExtension("nbms.longNote", "0.1.0");
        }

        note.Type = newType;
        note.DurationTicks = newDurationTicks;
        DraftType = newType;
        DraftDurationTicks = newDurationTicks?.ToString() ?? "";
        PushEditorCommand(new EditorCommand(
            commandName,
            () =>
            {
                note.Type = oldType;
                note.DurationTicks = oldDurationTicks;
                SelectedNote = note;
                ApplyNoteRows();
                RefreshTimelineOnly();
            },
            () =>
            {
                note.Type = newType;
                note.DurationTicks = newDurationTicks;
                SelectedNote = note;
                ApplyNoteRows();
                RefreshTimelineOnly();
            }));
        ApplyNoteRows();
        RefreshTimelineOnly();
        StatusText = statusText;
    }

    private void SetSelectedNoteExtendedLongNote(string noteType, string commandName, string statusPrefix)
    {
        if (SelectedNote is null)
        {
            StatusText = $"{statusPrefix}するノートが選択されていません。";
            return;
        }

        var note = SelectedNote;
        var oldType = note.Type;
        var oldDurationTicks = note.DurationTicks;
        var newDurationTicks = Math.Max(ResolveEditorGridTicks(), note.DurationTicks ?? 0);
        ApplySelectedNoteShape(
            note,
            oldType,
            oldDurationTicks,
            noteType,
            newDurationTicks,
            commandName,
            $"{statusPrefix}: tick {note.Tick}, lane {note.Lane}, duration {newDurationTicks}");
    }

    private void SetSelectedNoteSimpleType(string noteType, string commandName, string statusPrefix)
    {
        if (SelectedNote is null)
        {
            StatusText = $"{statusPrefix}するノートが選択されていません。";
            return;
        }

        var note = SelectedNote;
        var oldType = note.Type;
        var oldDurationTicks = note.DurationTicks;
        ApplySelectedNoteShape(
            note,
            oldType,
            oldDurationTicks,
            noteType,
            null,
            commandName,
            $"{statusPrefix}: tick {note.Tick}, lane {note.Lane}");
    }

    private void EnsureSelectedChartExtension(string id, string version)
    {
        if (_selectedChart is null ||
            _selectedChart.Chart.Extensions.Any(extension => extension.Id == id))
        {
            return;
        }

        _selectedChart.Chart.Extensions.Add(new ExtensionDeclaration
        {
            Id = id,
            Version = version,
            Required = false
        });
    }

    private void PushEditorCommand(EditorCommand command)
    {
        if (_isApplyingEditorHistory)
        {
            return;
        }

        _undoStack.Push(command);
        _redoStack.Clear();
        NotifyEditorHistoryChanged();
    }

    private void ClearEditorHistory()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        NotifyEditorHistoryChanged();
    }

    private void NotifyEditorHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndoEditorCommand));
        OnPropertyChanged(nameof(CanRedoEditorCommand));
    }

    private void NotifyEditorClipboardChanged()
    {
        OnPropertyChanged(nameof(CanPasteEditorObject));
    }

    private void ClearEditorSelection()
    {
        SelectedNote = null;
        EditorSelectedTick = -1;
        EditorSelectedLane = "";
    }

    public void ApplyDraftToSelectedNote()
    {
        if (SelectedNote is null)
        {
            AddDraftNote();
            return;
        }

        if (!int.TryParse(DraftTick, out var tick))
        {
            StatusText = "Tickには整数を入力してください。";
            return;
        }

        SelectedNote.Tick = tick;
        SelectedNote.Lane = DraftLane;
        SelectedNote.Type = string.IsNullOrWhiteSpace(DraftType) ? "tap" : DraftType;
        SelectedNote.AudioId = DraftAudioId;
        SelectedNote.DurationTicks = int.TryParse(DraftDurationTicks, out var duration) ? duration : null;

        ApplyNoteRows();
        RefreshNotesAndTimeline();
        StatusText = "選択ノーツを反映しました。";
    }

    public void DeleteSelectedNote()
    {
        if (SelectedNote is null)
        {
            return;
        }

        Notes.Remove(SelectedNote);
        SelectedNote = null;
        ApplyNoteRows();
        RefreshTimelineOnly();
        StatusText = "選択ノーツを削除しました。";
    }

    public async Task StartPlaybackAsync()
    {
        if (IsPlaying)
        {
            AppendPlaybackLog("PLAY", "already playing");
            return;
        }

        if (_project is null || _selectedChart is null)
        {
            StatusText = "再生するNBMSを開いてください。";
            AppendPlaybackLog("PLAY", "start failed: project or chart is null");
            return;
        }

        ApplyNoteRows();
        BuildPlaybackSchedule();
        AppendPlaybackLog(
            "PLAY",
            $"schedule events={_playbackSession?.Events.Count ?? 0} timeline={_playbackSession?.TimelineMap.Points.Count ?? 0} assets={_playbackSession?.AssetPlan.Count ?? 0} end={_playbackSession?.EndSeconds ?? 0:0.000}s offset={_playbackOffsetSeconds:0.000}s");

        if (_playbackSession is null || _playbackSession.Events.Count == 0)
        {
            StatusText = "再生対象の音声イベントがありません。";
            AppendPlaybackLog("PLAY", "start failed: no playback events");
            return;
        }

        var allAudioIds = ResolveRequiredAudioIds(_playbackSession);
        var initialAudioIds = ResolveInitialAudioIds(_playbackSession, _playbackOffsetSeconds);
        var startupAudioIds = ResolveStartupAudioIds(_playbackSession, initialAudioIds);
        if (_audioCache is null || !_audioCache.Covers(startupAudioIds))
        {
            AppendPlaybackLog("PLAY", $"wait startup audio cache required={startupAudioIds.Count}/{allAudioIds.Count}");
            StatusText = "object音源のPCMキャッシュを準備しています...";
            AudioPreparationText = $"Audio preparing... 0/{startupAudioIds.Count}";
            await EnsureAudioCacheAsync(startupAudioIds, "startup");
        }

        if (_audioCache is null)
        {
            StatusText = "音源パックを準備できませんでした。";
            AudioPreparationText = "";
            AppendPlaybackLog("PLAY", "start failed: audio cache is null");
            return;
        }

        // duration未設定音源は展開後に読めるため、キャッシュ準備後に再度スケジュールを作り直す。
        BuildPlaybackSchedule();
        AudioPreparationText = "";
        _audioPlayer.StopAll();
        _audioPlayer.ClearPreloaded();
        PreloadPlaybackAssets(startupAudioIds);

        _playbackScheduler.Reset(_playbackSession, _playbackOffsetSeconds);
        _nextPlaybackEventIndex = _playbackScheduler.NextEventIndex;

        PlayheadTick = EstimateTickAt(_playbackOffsetSeconds);
        PlaybackPositionText = TimeSpan.FromSeconds(_playbackOffsetSeconds).ToString(@"mm\:ss\.fff");
        _lastPlaybackPositionTextSeconds = _playbackOffsetSeconds;
        IsPlaying = true;
        StatusText = "再生中です。";
        AppendPlaybackLog("PLAY", $"start next={_nextPlaybackEventIndex} tick={PlayheadTick:0.##}");
        _audioPlayer.StartPlaybackClock(_playbackOffsetSeconds);
        _playbackClock.Restart();
        _playbackTimer.Start();
        StartBackgroundAudioCachePreparation(allAudioIds, startupAudioIds);
    }

    public void StartPlayback()
    {
        _ = StartPlaybackAsync();
    }

    public void StopPlayback()
    {
        ResetPlaybackState();
    }

    public void PausePlayback()
    {
        _playbackTimer.Stop();
        if (_playbackClock.IsRunning)
        {
            _playbackOffsetSeconds = ResolvePlaybackElapsedSeconds();
        }

        _playbackClock.Reset();
        _audioPlayer.StopPlaybackClock(_playbackOffsetSeconds);
        _audioPlayer.StopAll();
        IsPlaying = false;
        PlaybackPositionText = TimeSpan.FromSeconds(_playbackOffsetSeconds).ToString(@"mm\:ss\.fff");
        _lastPlaybackPositionTextSeconds = _playbackOffsetSeconds;
        PlayheadTick = EstimateTickAt(_playbackOffsetSeconds);
        StatusText = "一時停止しました。";
        AppendPlaybackLog("PLAY", $"pause offset={_playbackOffsetSeconds:0.000}s tick={PlayheadTick:0.##}");
        OnPropertyChanged(nameof(PlaybackButtonText));
    }

    public void ResetPlaybackView()
    {
        ResetPlaybackState();
        StatusText = "表示を1小節目に戻しました。";
        AppendPlaybackLog("PLAY", "reset to start");
    }

    private void ResetPlaybackState()
    {
        _playbackTimer.Stop();
        _playbackClock.Reset();
        _audioPlayer.StopPlaybackClock(0);
        _audioPlayer.StopAll();
        _nextPlaybackEventIndex = 0;
        _playbackScheduler.Clear();
        _playbackOffsetSeconds = 0;
        IsPlaying = false;
        PlayheadTick = 0;
        PlaybackPositionText = "00:00.000";
        PlaybackDebugText = "session: none";
        _lastPlaybackPositionTextSeconds = -1;
        OnPropertyChanged(nameof(PlaybackButtonText));
    }

    private void LoadHeaderFields()
    {
        if (_project is null)
        {
            return;
        }

        ProjectPathText = _project.HeaderPath;
        TitleText = _project.Header.Title;
        ArtistText = _project.Header.Artist;
        GenreText = _project.Header.Genre;
        BpmText = _project.Header.Bpm.Initial.ToString("0.###");
        LicenseText = _project.Header.Rights.License;
    }

    private async Task EnsureAudioCacheAsync(IReadOnlyCollection<string> requiredAudioIds, string label)
    {
        if (_audioCache is not null && _audioCache.Covers(requiredAudioIds))
        {
            return;
        }

        if (_project is null)
        {
            return;
        }

        _audioCache ??= CreateAudioCache(_project);
        var cache = _audioCache;
        var progress = CreateAudioCacheProgress(label);
        var token = ResetAudioCachePreparationToken();
        _audioCacheTask = Task.Run<NbmsAudioCache?>(() =>
        {
            cache.PrepareAudioIds(requiredAudioIds, progress, token);
            return cache;
        }, token);

        await _audioCacheTask;
    }

    private void StartBackgroundAudioCachePreparation(
        IReadOnlyCollection<string> allAudioIds,
        IReadOnlyCollection<string> initialAudioIds)
    {
        if (_project is null || _audioCache is null)
        {
            return;
        }

        var remainingAudioIds = allAudioIds
            .Except(initialAudioIds, StringComparer.Ordinal)
            .Where(audioId => !_audioCache.Covers([audioId]))
            .ToArray();
        if (remainingAudioIds.Length == 0)
        {
            return;
        }

        var cache = _audioCache;
        var progress = CreateAudioCacheProgress("background");
        var token = _audioCachePreparationCts?.Token ?? CancellationToken.None;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var audioId in remainingAudioIds)
                {
                    token.ThrowIfCancellationRequested();
                    if (cache.Covers([audioId]))
                    {
                        continue;
                    }

                    cache.PrepareAudioIds([audioId], progress, token);
                    PreloadPlaybackAssets([audioId]);
                    Thread.Yield();
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(_audioCache, cache))
                    {
                        AudioPreparationText = "";
                        AppendPlaybackLog("AUDIO", $"background cache ready {remainingAudioIds.Length}");
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendPlaybackLog("AUDIO", $"background cache failed: {ex.Message}"));
            }
        }, token);
    }

    private IProgress<AudioCacheProgress> CreateAudioCacheProgress(string label)
    {
        return new Progress<AudioCacheProgress>(progress =>
        {
            AudioPreparationText = $"Audio preparing {label}... {progress.Processed}/{progress.Total} {progress.Stage} {progress.AudioId}";
        });
    }

    private static NbmsAudioCache CreateAudioCache(NbmsProject project)
    {
        if (project.AudioManifest is null)
        {
            throw new InvalidDataException("音源manifestがありません。");
        }

        return NbmsAudioCache.CreateEmpty(ResolveAudioArchivePath(project), project.AudioManifest);
    }

    private async Task RemoveAudioAssetsAsync(IReadOnlyCollection<string> audioIds, string successMessage)
    {
        if (_project?.AudioManifest is null || audioIds.Count == 0)
        {
            return;
        }

        try
        {
            CancelAudioCachePreparation();
            _audioCache?.Dispose();
            _audioCache = null;
            _audioCacheTask = null;

            await _audioArchiveService.RemoveAudioEntriesAsync(
                ResolveAudioArchivePath(_project),
                _project.AudioManifest,
                audioIds);
            RefreshCollections();
            StatusText = successMessage;
        }
        catch (Exception ex)
        {
            StatusText = $"音声削除に失敗しました: {ex.Message}";
        }
    }

    private bool IsAudioReferenced(string audioId)
    {
        return ResolveReferencedAudioIds().Contains(audioId);
    }

    private HashSet<string> ResolveReferencedAudioIds()
    {
        if (_project is null)
        {
            return [];
        }

        return _project.Charts
            .SelectMany(chart => chart.Chart.Notes.Select(note => note.AudioId)
                .Concat(chart.Chart.BackgroundAudio.Select(background => background.AudioId)))
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .Select(audioId => audioId!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ResolveAudioArchivePath(NbmsProject project)
    {
        return Path.GetFullPath(Path.Combine(
            project.RootDirectory,
            project.Header.Audio.File.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string NormalizeAudioId(string value)
    {
        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private CancellationToken ResetAudioCachePreparationToken()
    {
        CancelAudioCachePreparation();
        _audioCachePreparationCts = new CancellationTokenSource();
        return _audioCachePreparationCts.Token;
    }

    private void CancelAudioCachePreparation()
    {
        if (_audioCachePreparationCts is null)
        {
            return;
        }

        _audioCachePreparationCts.Cancel();
        _audioCachePreparationCts.Dispose();
        _audioCachePreparationCts = null;
    }

    private void ApplyHeaderFields()
    {
        if (_project is null)
        {
            return;
        }

        _project.Header.Title = TitleText;
        _project.Header.Artist = ArtistText;
        _project.Header.Genre = GenreText;
        _project.Header.Rights.License = LicenseText;

        if (double.TryParse(BpmText, out var bpm))
        {
            _project.Header.Bpm.Initial = bpm;
        }
    }

    private void RefreshCollections()
    {
        Charts.Clear();
        AudioEntries.Clear();
        MediaEntries.Clear();
        Issues.Clear();
        Events.Clear();
        Extensions.Clear();

        if (_project is null)
        {
            return;
        }

        foreach (var chart in _project.Charts)
        {
            Charts.Add(new ChartRow
            {
                Id = chart.Reference.Id,
                File = chart.Reference.File,
                Mode = chart.Reference.Mode,
                Difficulty = chart.Reference.Difficulty,
                LevelName = chart.Reference.LevelName
            });
        }

        _selectedChartRow = Charts.FirstOrDefault(row => row.Id == _selectedChart?.Reference.Id) ?? Charts.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedChartRow));

        foreach (var entry in _project.AudioManifest?.Entries ?? [])
        {
            AudioEntries.Add(new AudioRow
            {
                AudioId = entry.AudioId,
                Codec = entry.Codec,
                DurationMs = entry.DurationMs,
                SampleRate = entry.SampleRate,
                Channels = entry.Channels,
                Encrypted = entry.Encrypted,
                Path = entry.Path
            });
        }

        RefreshMediaRows();

        foreach (var issue in _project.Issues)
        {
            Issues.Add(new IssueRow
            {
                Severity = issue.Severity,
                Source = issue.Source,
                Message = issue.Message
            });
        }

        AddMissingReferenceRepairIssues();
        AddUnusedAudioIssues();
        AddDuplicateAssetIssues();
        AddChartValidationIssues();

        foreach (var module in _projectService.ExtensionRegistry.Modules)
        {
            Extensions.Add($"{module.Id} {module.Version}");
        }

        RefreshNotesAndTimeline();
        OnPropertyChanged(nameof(AudioSummaryText));
        OnPropertyChanged(nameof(EventSummaryText));
        OnPropertyChanged(nameof(IssueSummaryText));
        OnPropertyChanged(nameof(ChartSummaryText));
    }

    private void RefreshNotesAndTimeline()
    {
        Notes.Clear();
        SelectedNote = null;

        if (_selectedChart is not null)
        {
            foreach (var note in _selectedChart.Chart.Notes)
            {
                Notes.Add(NoteRow.FromNote(note));
            }
        }

        RefreshTimelineOnly();
    }

    private void RefreshMediaRows()
    {
        MediaEntries.Clear();
        if (_selectedChart is not null)
        {
            foreach (var mediaEvent in _selectedChart.Chart.MediaEvents)
            {
                MediaEntries.Add(MediaRow.FromMediaEvent(mediaEvent));
            }
        }

        OnPropertyChanged(nameof(MediaSummaryText));
    }

    private void RefreshTimelineOnly(bool preserveEditorRange = false)
    {
        Timeline.Clear();
        MeasureGridLines.Clear();
        Events.Clear();
        SelectedEventRow = null;

        if (_selectedChart is null)
        {
            OnPropertyChanged(nameof(EventSummaryText));
            return;
        }

        foreach (var item in _timelineService.BuildTimeline(_selectedChart.Chart))
        {
            var row = new TimelineRow
            {
                Tick = item.Tick,
                TimeSeconds = Math.Round(item.TimeSeconds, 3),
                Kind = item.Kind,
                Lane = item.Lane,
                Detail = item.Detail,
                DurationTicks = item.DurationTicks
            };
            Timeline.Add(row);

            if (IsEditorEventRow(row))
            {
                Events.Add(new EventRow
                {
                    Tick = row.Tick,
                    TimeSeconds = row.TimeSeconds,
                    Type = row.Detail.StartsWith("BPM ", StringComparison.Ordinal) ? "BPM" : row.Detail.Split(' ')[0],
                    Lane = row.Lane,
                    Detail = row.Detail
                });
            }
        }

        foreach (var note in _selectedChart.Chart.Notes.Where(IsLongNoteType))
        {
            var time = Timeline.FirstOrDefault(row => row.Kind == "Note" && row.Tick == note.Tick && row.Lane == note.Lane)?.TimeSeconds ?? 0;
            Events.Add(new EventRow
            {
                Tick = note.Tick,
                TimeSeconds = time,
                Type = note.Type.Equals("cn", StringComparison.OrdinalIgnoreCase)
                    ? "CN"
                    : note.Type.Equals("hcn", StringComparison.OrdinalIgnoreCase)
                        ? "HCN"
                        : "LN",
                Lane = note.Lane,
                Detail = $"{note.Type} {note.DurationTicks ?? 0} ticks {note.AudioId}"
            });
        }

        if (preserveEditorRange)
        {
            RefreshEditorTimelineHeightKeepingStart();
        }
        else
        {
            RefreshEditorTimelineHeight();
        }
        RefreshMeasureGridLines();
        OnPropertyChanged(nameof(EditorGridTicks));
        OnPropertyChanged(nameof(EventSummaryText));
    }

    private static bool IsEditorEventRow(TimelineRow row)
    {
        if (row.Kind != "Timing")
        {
            return false;
        }

        return row.Detail.StartsWith("BPM ", StringComparison.Ordinal) ||
               row.Detail.StartsWith("STOP ", StringComparison.Ordinal) ||
               row.Detail.StartsWith("SCROLL ", StringComparison.Ordinal) ||
               row.Detail.StartsWith("SPEED ", StringComparison.Ordinal) ||
               row.Detail.StartsWith("MEASURE ", StringComparison.Ordinal) ||
               row.Detail.StartsWith("LNOBJ ", StringComparison.Ordinal);
    }

    private static bool IsLongNoteType(NoteEvent note)
    {
        return note.DurationTicks is > 0 && IsLongNoteKind(note);
    }

    private static bool IsLongNoteKind(NoteEvent note)
    {
        return note.Type.Equals("hold", StringComparison.OrdinalIgnoreCase) ||
               note.Type.Equals("cn", StringComparison.OrdinalIgnoreCase) ||
               note.Type.Equals("hcn", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshEditorTimelineHeight()
    {
        const double pixelsPerTick = 0.125;
        var measureMap = _selectedChart is null ? null : MeasureMap.FromChart(_selectedChart.Chart);
        var firstContentTick = ResolveEditorTimelineFirstContentTick();
        // Editorでは最初の実オブジェクトより2小節手前を下端余白として確保する。
        var startTick = measureMap?.ResolveStartTickWithMeasurePadding(firstContentTick, 2) ?? firstContentTick - 3840 * 2;
        var maxTick = Timeline.Count == 0
            ? firstContentTick
            : Timeline.Max(row => row.Tick + (row.DurationTicks ?? 0));

        EditorTimelineFocusTick = firstContentTick;
        EditorTimelineStartTick = startTick;
        EditorTimelineHeight = Math.Max(360, 96 + Math.Max(0, maxTick - startTick) * pixelsPerTick);
    }

    private void RefreshEditorTimelineHeightKeepingStart()
    {
        const double pixelsPerTick = 0.125;
        var maxTick = Timeline.Count == 0
            ? EditorTimelineStartTick
            : Timeline.Max(row => row.Tick + (row.DurationTicks ?? 0));
        EditorTimelineHeight = Math.Max(360, 96 + Math.Max(0, maxTick - EditorTimelineStartTick) * pixelsPerTick);
    }

    private void RefreshMeasureGridLines()
    {
        MeasureGridLines.Clear();
        if (_selectedChart is null)
        {
            return;
        }

        const double pixelsPerTick = 0.125;
        var measureMap = MeasureMap.FromChart(_selectedChart.Chart);
        var endTick = EditorTimelineStartTick + (int)Math.Ceiling(EditorTimelineHeight / pixelsPerTick);
        foreach (var line in measureMap.BuildGridLines(EditorTimelineStartTick, endTick, EditorGridDivision))
        {
            MeasureGridLines.Add(new MeasureGridLineRow
            {
                Tick = line.Tick,
                MeasureNumber = line.MeasureNumber,
                DivisionIndex = line.DivisionIndex,
                IsMeasureStart = line.IsMeasureStart
            });
        }
    }

    private int ResolveEditorTimelineFirstContentTick()
    {
        if (_selectedChart is null)
        {
            return 0;
        }

        var noteTicks = _selectedChart.Chart.Notes.Select(note => note.Tick);
        var backgroundTicks = _selectedChart.Chart.BackgroundAudio.Select(item => item.Tick);
        var timingTicks = _selectedChart.Chart.Timing
            .Where(timing => timing.Type is "bpm" or "stop" or "scroll" or "speed" or "measureLength")
            .Select(timing => timing.Tick);

        var audioOrNoteTicks = noteTicks
            .Concat(backgroundTicks)
            .ToList();

        if (audioOrNoteTicks.Count > 0)
        {
            return audioOrNoteTicks.Min();
        }

        return timingTicks.DefaultIfEmpty(0).Min();
    }

    private void BuildPlaybackSchedule()
    {
        _playbackEvents = [];
        _playbackTimeline = [];
        _playbackStopPoints = [];
        _playbackSession = null;
        _lastPlaybackPositionTextSeconds = -1;

        if (_selectedChart is null)
        {
            return;
        }

        _playbackSession = PlaybackSession.Create(
            _selectedChart.Chart,
            _project?.AudioManifest,
            _timelineService,
            _audioCache?.DurationsByAudioId);
        _playbackEndSeconds = _playbackSession.EndSeconds;
        UpdatePlaybackDebugText(_playbackOffsetSeconds);
    }

    private static IReadOnlyCollection<string> ResolveRequiredAudioIds(PlaybackSession session)
    {
        return session.Events
            .Select(playbackEvent => playbackEvent.AudioId)
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyCollection<string> ResolveInitialAudioIds(PlaybackSession session, double startSeconds)
    {
        var initialEndSeconds = startSeconds + InitialAudioPrepareSeconds;
        var initialAudioIds = session.Events
            .Where(playbackEvent => playbackEvent.TimeSeconds >= startSeconds &&
                                    playbackEvent.TimeSeconds <= initialEndSeconds)
            .Select(playbackEvent => playbackEvent.AudioId)
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (initialAudioIds.Length > 0)
        {
            return initialAudioIds;
        }

        // 曲頭に長い無音がある譜面でも、最初の発音でReader生成が集中しないよう少数だけ先に準備する。
        return session.Events
            .Where(playbackEvent => playbackEvent.TimeSeconds >= startSeconds)
            .Select(playbackEvent => playbackEvent.AudioId)
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .Distinct(StringComparer.Ordinal)
            .Take(InitialAudioFallbackCount)
            .ToArray();
    }

    private static IReadOnlyCollection<string> ResolveStartupAudioIds(
        PlaybackSession session,
        IReadOnlyCollection<string> initialAudioIds)
    {
        var shortPcmAssets = session.AssetPlan
            .Where(asset => asset.Mode == PlaybackAssetLoadMode.ShortPcm)
            .ToList();
        var hasOggShortPcm = shortPcmAssets.Any(asset => IsOggVorbisAsset(asset));
        if (!hasOggShortPcm)
        {
            return initialAudioIds;
        }

        // OGG短音は発音時streamや直前decodeだと欠落しやすいため、object音を再生開始前にPCM化する。
        return initialAudioIds
            .Concat(shortPcmAssets.Select(asset => asset.AudioId))
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsOggVorbisAsset(PlaybackAssetPlan asset)
    {
        return asset.Codec.Equals("ogg", StringComparison.OrdinalIgnoreCase) ||
               asset.Codec.Equals("vorbis", StringComparison.OrdinalIgnoreCase) ||
               asset.Codec.Equals("ogg-vorbis", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(asset.Path).Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(asset.Path).Equals(".oga", StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolvePlaybackTailSeconds(PlaybackEvent playbackEvent)
    {
        return playbackEvent.DurationSeconds > 0
            ? Math.Max(2.0, playbackEvent.DurationSeconds)
            : UnknownAudioTailSeconds;
    }

    private static string ResolveAudioId(TimelineItem item)
    {
        if (item.Kind == "BGM")
        {
            return item.Detail.Trim();
        }

        var parts = item.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^1] : "";
    }

    private void HandlePlaybackTimerTick()
    {
        try
        {
            UpdatePlayback();
        }
        catch (Exception ex)
        {
            _playbackTimer.Stop();
            _playbackClock.Reset();
            _audioPlayer.StopPlaybackClock(_playbackOffsetSeconds);
            IsPlaying = false;
            StatusText = $"再生タイマーで例外が発生しました: {ex.Message}";
            AppendPlaybackLog("ERROR", $"{ex.GetType().Name}: {ex.Message}");
            OnPropertyChanged(nameof(PlaybackButtonText));
        }
    }

    private void UpdatePlayback()
    {
        var elapsedSeconds = ResolvePlaybackElapsedSeconds();
        if (_lastPlaybackPositionTextSeconds < 0 || elapsedSeconds - _lastPlaybackPositionTextSeconds >= 0.05)
        {
            PlaybackPositionText = TimeSpan.FromSeconds(elapsedSeconds).ToString(@"mm\:ss\.fff");
            _lastPlaybackPositionTextSeconds = elapsedSeconds;
        }

        PlayheadTick = EstimateTickAt(elapsedSeconds);

        if (_playbackSession is not null)
        {
            QueueUrgentAudioPreparation(elapsedSeconds);

            foreach (var playbackEvent in _playbackScheduler.Poll(elapsedSeconds, PlaybackLookaheadSeconds))
            {
                PlayPlaybackEvent(playbackEvent, elapsedSeconds);
            }

            _nextPlaybackEventIndex = _playbackScheduler.NextEventIndex;
        }

        if (_lastPlaybackPositionTextSeconds < 0 || Math.Abs(elapsedSeconds - _lastPlaybackPositionTextSeconds) < 0.0001)
        {
            UpdatePlaybackDebugText(elapsedSeconds);
        }

        if (elapsedSeconds >= _playbackEndSeconds)
        {
            FinishPlaybackTimelineOnly();
            StatusText = "再生を終了しました。";
            AppendPlaybackLog("PLAY", $"finish elapsed={elapsedSeconds:0.000}s end={_playbackEndSeconds:0.000}s");
        }
    }

    private void QueueUrgentAudioPreparation(double elapsedSeconds)
    {
        if (_playbackSession is null || _audioCache is null)
        {
            return;
        }

        var urgentEndSeconds = elapsedSeconds + UrgentAudioPrepareSeconds;
        var missingAudioIds = _playbackSession.Events
            .Where(playbackEvent => playbackEvent.TimeSeconds >= elapsedSeconds &&
                                    playbackEvent.TimeSeconds <= urgentEndSeconds)
            .Select(playbackEvent => playbackEvent.AudioId)
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .Distinct(StringComparer.Ordinal)
            .Where(audioId => !_audioCache.Covers([audioId]))
            .Where(audioId => _urgentAudioPrepareIds.Add(audioId))
            .ToArray();

        if (missingAudioIds.Length == 0)
        {
            return;
        }

        var cache = _audioCache;
        var progress = CreateAudioCacheProgress("urgent");
        var token = _audioCachePreparationCts?.Token ?? CancellationToken.None;
        AppendPlaybackLog("AUDIO", $"urgent cache queue {missingAudioIds.Length}");

        _ = Task.Run(() =>
        {
            try
            {
                cache.PrepareAudioIds(missingAudioIds, progress, token);
                PreloadPlaybackAssets(missingAudioIds);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => AppendPlaybackLog("AUDIO", $"urgent cache failed: {ex.Message}"));
            }
        }, token);
    }

    private void FinishPlaybackTimelineOnly()
    {
        _playbackTimer.Stop();
        _playbackClock.Reset();
        _audioPlayer.StopPlaybackClock(0);
        _playbackOffsetSeconds = 0;
        IsPlaying = false;
        _nextPlaybackEventIndex = 0;
        _playbackScheduler.Clear();
        PlayheadTick = 0;
        PlaybackPositionText = "00:00.000";
        PlaybackDebugText = "session: finished";
        _lastPlaybackPositionTextSeconds = -1;
        OnPropertyChanged(nameof(PlaybackButtonText));
        // 末尾の音やフェードアウトを切らないため、ここではミキサーを停止しない。
    }

    private void PlayPlaybackEvent(AudioScheduleEvent playbackEvent, double elapsedSeconds)
    {
        if (_audioCache is null)
        {
            AppendPlaybackLog("AUDIO", $"skip {playbackEvent.AudioId}: audio cache is null");
            return;
        }

        if (!_audioCache.TryGetFilePath(playbackEvent.AudioId, out var filePath))
        {
            AppendPlaybackLog("AUDIO", $"last-chance prepare {playbackEvent.AudioId}");
            try
            {
                _audioCache.PrepareAudioIds([playbackEvent.AudioId], CreateAudioCacheProgress("last"), _audioCachePreparationCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppendPlaybackLog("AUDIO", $"last-chance failed {playbackEvent.AudioId}: {ex.Message}");
            }

            if (!_audioCache.TryGetFilePath(playbackEvent.AudioId, out filePath))
            {
                AppendPlaybackLog("AUDIO", $"missing {playbackEvent.AudioId}");
                return;
            }
        }

        if ((_playbackSession?.ResolveAssetLoadMode(playbackEvent.AudioId) ?? PlaybackAssetLoadMode.ShortPcm) == PlaybackAssetLoadMode.ShortPcm &&
            !_audioPlayer.IsPreloaded(playbackEvent.AudioId))
        {
            PreloadPlaybackAssets([playbackEvent.AudioId]);
        }
 
        try
        {
            AppendPlaybackLog(
                "EVENT",
                $"{playbackEvent.TimeSeconds:0.000}s tick={playbackEvent.Tick} delay={Math.Max(0, playbackEvent.TimeSeconds - elapsedSeconds):0.000}s {playbackEvent.Kind}/{playbackEvent.Lane} {playbackEvent.AudioId}");
            if (_audioPlayer.PlayPreloadedOneShot(playbackEvent.AudioId, playbackEvent.TimeSeconds - elapsedSeconds))
            {
                AppendPlaybackLog("EVENT", $"route=short-pcm {playbackEvent.AudioId}");
            }
            else
            {
                var mode = _playbackSession?.ResolveAssetLoadMode(playbackEvent.AudioId) ?? PlaybackAssetLoadMode.ShortPcm;
                if (mode == PlaybackAssetLoadMode.ShortPcm)
                {
                    AppendPlaybackLog("AUDIO", $"short-pcm not ready; route=emergency-stream {playbackEvent.AudioId} file={Path.GetFileName(filePath)}");
                    _audioPlayer.PlayOneShot(filePath, playbackEvent.TimeSeconds - elapsedSeconds);
                    return;
                }

                AppendPlaybackLog("EVENT", $"route=long-stream {playbackEvent.AudioId} file={Path.GetFileName(filePath)}");
                _audioPlayer.PlayOneShot(filePath, playbackEvent.TimeSeconds - elapsedSeconds);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"音声再生に失敗しました: {playbackEvent.AudioId} ({ex.Message})";
            AppendPlaybackLog("ERROR", $"play {playbackEvent.AudioId}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private double EstimateTickAt(double elapsedSeconds)
    {
        return _playbackSession?.EstimateTickAt(elapsedSeconds) ?? 0;
    }

    private double ResolvePlaybackElapsedSeconds()
    {
        return IsPlaying
            ? _audioPlayer.GetPlaybackClockSeconds()
            : _playbackOffsetSeconds;
    }

    private void PreloadPlaybackAssets(IEnumerable<string>? audioIds = null)
    {
        if (_playbackSession is null || _audioCache is null)
        {
            return;
        }

        var filter = audioIds?
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .ToHashSet(StringComparer.Ordinal);
        var requests = new List<PlaybackPreloadRequest>();
        foreach (var asset in _playbackSession.AssetPlan.Where(asset => asset.Mode == PlaybackAssetLoadMode.ShortPcm))
        {
            if (filter is not null && !filter.Contains(asset.AudioId))
            {
                continue;
            }

            if (_audioCache.TryGetFilePath(asset.AudioId, out var filePath))
            {
                requests.Add(new PlaybackPreloadRequest(asset.AudioId, filePath));
            }
        }

        if (requests.Count == 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => AppendPlaybackLog("PLAY", $"preload assets={requests.Count}"));
        _audioPlayer.Preload(requests);
    }

    private bool HasStopAt(int tick, double timeSeconds)
    {
        return _playbackStopPoints.Contains((tick, ToPlaybackTimeKey(timeSeconds)));
    }

    private static long ToPlaybackTimeKey(double timeSeconds)
    {
        return (long)Math.Round(timeSeconds * 10000);
    }
    private double ResolveInitialTicksPerSecond()
    {
        if (_selectedChart is null)
        {
            return 960 * 120 / 60.0;
        }

        var bpm = _selectedChart.Chart.Timing
            .Where(timing => timing.Type == "bpm" && timing.Value is not null)
            .OrderBy(timing => timing.Tick)
            .Select(timing => timing.Value!.Value)
            .FirstOrDefault();

        if (bpm <= 0)
        {
            bpm = 120;
        }

        return _selectedChart.Chart.Resolution * bpm / 60.0;
    }

    private void AppendPlaybackLog(string category, string message)
    {
        if (!IsPlaybackLogVisible)
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";
        _playbackLogLines.Enqueue(line);
        while (_playbackLogLines.Count > 80)
        {
            _playbackLogLines.Dequeue();
        }

        PlaybackLogText = string.Join(Environment.NewLine, _playbackLogLines);
    }

    private void UpdatePlaybackDebugText(double elapsedSeconds)
    {
        if (_playbackSession is null)
        {
            PlaybackDebugText = "session: none";
            return;
        }

        PlaybackDebugText =
            $"events {_nextPlaybackEventIndex}/{_playbackSession.Events.Count}  assets {_playbackSession.AssetPlan.Count}  lookahead {PlaybackLookaheadSeconds * 1000:0}ms  tick {PlayheadTick:0.##}  end {_playbackSession.EndSeconds:0.000}s  t {elapsedSeconds:0.000}s";
    }

    private void ApplyNoteRows()
    {
        if (_selectedChart is null)
        {
            return;
        }

        // DataGridで編集した表データを、選択中の譜面モデルへ戻す。
        _selectedChart.Chart.Notes = Notes
            .Select(row => new NoteEvent
            {
                Tick = row.Tick,
                Lane = row.Lane,
                Type = string.IsNullOrWhiteSpace(row.Type) ? "tap" : row.Type,
                AudioId = string.IsNullOrWhiteSpace(row.AudioId) ? null : row.AudioId,
                DurationTicks = row.DurationTicks
            })
            .OrderBy(note => note.Tick)
            .ThenBy(note => note.Lane, StringComparer.Ordinal)
            .ToList();
    }

    private void ApplyAudioRows()
    {
        if (_project?.AudioManifest is null)
        {
            return;
        }

        var existingById = _project.AudioManifest.Entries
            .ToDictionary(entry => entry.AudioId, StringComparer.Ordinal);

        _project.AudioManifest.Entries = AudioEntries
            .Where(row => !string.IsNullOrWhiteSpace(row.AudioId))
            .Select(row =>
            {
                var audioId = row.AudioId.Trim();
                existingById.TryGetValue(audioId, out var existing);
                return new AudioEntry
                {
                    AudioId = audioId,
                    Path = row.Path.Trim(),
                    Codec = string.IsNullOrWhiteSpace(row.Codec) ? "unknown" : row.Codec.Trim(),
                    SampleRate = row.SampleRate,
                    Channels = row.Channels,
                    DurationMs = row.DurationMs,
                    Hash = existing?.Hash ?? "",
                    RightsId = existing?.RightsId ?? "",
                    Encrypted = row.Encrypted,
                    Encryption = existing?.Encryption
                };
            })
            .OrderBy(entry => entry.AudioId, StringComparer.Ordinal)
            .ToList();
        _project.AudioManifest.CodecRequired = _project.AudioManifest.Entries
            .Select(entry => entry.Codec)
            .Where(codec => !string.IsNullOrWhiteSpace(codec) && codec != "unknown")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(codec => codec, StringComparer.Ordinal)
            .ToList();
    }

    private void ApplyMediaRows()
    {
        if (_selectedChart is null)
        {
            return;
        }

        _selectedChart.Chart.MediaEvents = MediaEntries
            .Where(row => !string.IsNullOrWhiteSpace(row.MediaId))
            .Select(row => new MediaEvent
            {
                Tick = Math.Max(0, row.Tick),
                MediaId = row.MediaId.Trim(),
                Type = string.IsNullOrWhiteSpace(row.Type) ? "image" : row.Type.Trim(),
                Layer = row.Layer
            })
            .OrderBy(mediaEvent => mediaEvent.Tick)
            .ThenBy(mediaEvent => mediaEvent.Layer ?? 0)
            .ThenBy(mediaEvent => mediaEvent.MediaId, StringComparer.Ordinal)
            .ToList();
    }

    private void AddUnusedAudioIssues()
    {
        if (_project?.AudioManifest is null)
        {
            return;
        }

        var referenced = _project.Charts
            .SelectMany(chart => chart.Chart.Notes.Select(note => note.AudioId)
                .Concat(chart.Chart.BackgroundAudio.Select(background => background.AudioId)))
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entry in _project.AudioManifest.Entries.Where(entry => !referenced.Contains(entry.AudioId)))
        {
            Issues.Add(new IssueRow
            {
                Severity = "Warning",
                Source = "Audio",
                Message = $"未使用音源: {entry.AudioId} ({entry.Path})"
            });
        }
    }

    private void AddMissingReferenceRepairIssues()
    {
        if (_project is null)
        {
            return;
        }

        foreach (var issue in _referenceCheckService.FindMissingAudioReferences(_project))
        {
            Issues.Add(new IssueRow
            {
                Severity = "Error",
                Source = issue.ChartId,
                ReferenceType = issue.ReferenceType,
                Index = issue.Index,
                AudioId = issue.AudioId,
                Message = $"参照切れ: {issue.ReferenceType}[{issue.Index}] {issue.AudioId}"
            });
        }
    }

    private void AddDuplicateAssetIssues()
    {
        if (_project?.AudioManifest is null)
        {
            return;
        }

        foreach (var group in _project.AudioManifest.Entries.GroupBy(entry => entry.AudioId, StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Error",
                Source = "Audio",
                Message = $"重複AudioId: {group.Key} ({group.Count()} entries)"
            });
        }
    }

    private void AddChartValidationIssues()
    {
        if (_project is null)
        {
            return;
        }

        foreach (var loadedChart in _project.Charts)
        {
            AddViewerCompatibilityIssue(loadedChart);
            AddBrokenLongNoteIssues(loadedChart);
            AddOverlapIssues(loadedChart);
            AddHorizontalDuplicationIssues(loadedChart);
            AddLongNoteInternalObjectIssues(loadedChart);
        }
    }

    private void AddViewerCompatibilityIssue(LoadedChart loadedChart)
    {
        if (loadedChart.Chart.Mode is "beat-7k" or "beat-14k")
        {
            return;
        }

        Issues.Add(new IssueRow
        {
            Severity = "Warning",
            Source = loadedChart.Reference.Id,
            Message = $"Viewer未対応の可能性があるmode: {loadedChart.Chart.Mode}"
        });
    }

    private void AddBrokenLongNoteIssues(LoadedChart loadedChart)
    {
        for (var index = 0; index < loadedChart.Chart.Notes.Count; index++)
        {
            var note = loadedChart.Chart.Notes[index];
            if (!IsLongNoteKind(note))
            {
                continue;
            }

            if (note.DurationTicks is not > 0)
            {
                Issues.Add(new IssueRow
                {
                    Severity = "Error",
                    Source = loadedChart.Reference.Id,
                    ReferenceType = "note",
                    Index = index,
                    AudioId = note.AudioId ?? "",
                    Message = $"Broken LN: note[{index}] {note.Lane} tick {note.Tick}"
                });
            }
        }
    }

    private void AddOverlapIssues(LoadedChart loadedChart)
    {
        foreach (var group in loadedChart.Chart.Notes.GroupBy(note => (note.Tick, note.Lane)))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Warning",
                Source = loadedChart.Reference.Id,
                Message = $"overlap: tick {group.Key.Tick}, lane {group.Key.Lane}, count {group.Count()}"
            });
        }
    }

    private void AddHorizontalDuplicationIssues(LoadedChart loadedChart)
    {
        foreach (var group in loadedChart.Chart.Notes
            .Where(note => !string.IsNullOrWhiteSpace(note.AudioId))
            .GroupBy(note => (note.Tick, note.AudioId)))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Info",
                Source = loadedChart.Reference.Id,
                Message = $"horizontal duplication: tick {group.Key.Tick}, audio {group.Key.AudioId}, count {group.Count()}"
            });
        }
    }

    private void AddLongNoteInternalObjectIssues(LoadedChart loadedChart)
    {
        var longNotes = loadedChart.Chart.Notes
            .Where(IsLongNoteType)
            .Where(note => note.DurationTicks is > 0)
            .ToList();

        foreach (var longNote in longNotes)
        {
            var endTick = longNote.Tick + longNote.DurationTicks!.Value;
            var internalCount = loadedChart.Chart.Notes.Count(note =>
                !ReferenceEquals(note, longNote) &&
                string.Equals(note.Lane, longNote.Lane, StringComparison.OrdinalIgnoreCase) &&
                note.Tick > longNote.Tick &&
                note.Tick < endTick);
            if (internalCount == 0)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Warning",
                Source = loadedChart.Reference.Id,
                Message = $"LN内部OBJ: lane {longNote.Lane}, tick {longNote.Tick}-{endTick}, count {internalCount}"
            });
        }
    }

    public void Dispose()
    {
        StopPlayback();
        StopMonoGameViewer();
        _audioPlayer.Dispose();
        _audioCache?.Dispose();
    }

    private sealed record EditorCommand(string Name, Action Undo, Action Redo);

    private sealed record EditorClipboardItem(
        EditorClipboardKind Kind,
        string Lane,
        string Type,
        string AudioId,
        int? DurationTicks)
    {
        public static EditorClipboardItem FromNote(NoteRow note)
        {
            return new EditorClipboardItem(
                EditorClipboardKind.Note,
                note.Lane,
                string.IsNullOrWhiteSpace(note.Type) ? "tap" : note.Type,
                note.AudioId,
                note.DurationTicks);
        }

        public static EditorClipboardItem FromBgm(BackgroundAudioEvent bgm)
        {
            return new EditorClipboardItem(
                EditorClipboardKind.Bgm,
                bgm.Lane ?? "background1",
                "bgm",
                bgm.AudioId,
                null);
        }
    }

    private enum EditorClipboardKind
    {
        Note,
        Bgm
    }
}
