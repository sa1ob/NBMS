using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Avalonia.Threading;
using NBMS.Core.Models;
using NBMS.Core.Services;
using NBMS.Studio.App.Import;
using NBMS.Studio.App.Playback;
using NBMS.Studio.App.Services;

namespace NBMS.Studio.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly NbmsProjectService _projectService = new();
    private readonly TimelineService _timelineService;
    private readonly PackageService _packageService = new();
    private readonly PackageValidatorService _packageValidatorService = new();
    private readonly AudioArchiveService _audioArchiveService = new();
    private readonly MediaArchiveService _mediaArchiveService = new();
    private readonly BmsConversionService _bmsConversionService = new();
    private readonly BmsExportService _bmsExportService = new();
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
    private double? _playbackRangeEndSeconds;
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
    private MediaAssetRow? _selectedMediaAssetRow;
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
    private List<EditorClipboardItem> _editorClipboardItems = [];
    private int _editorGridDivision = 16;
    private bool _isEditorSnapEnabled = true;
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
    public ObservableCollection<MediaAssetRow> MediaAssetEntries { get; } = [];
    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<TimelineRow> Timeline { get; } = [];
    public ObservableCollection<MeasureGridLineRow> MeasureGridLines { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<ImportReportRow> ImportReportEntries { get; } = [];
    public ObservableCollection<string> Extensions { get; } = [];
    public ObservableCollection<string> EditorSelectedObjectKeys { get; } = [];
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
                NotifyInspectorChanged();
                return;
            }

            DraftTick = value.Tick.ToString();
            DraftLane = value.Lane;
            DraftType = value.Type;
            DraftAudioId = value.AudioId;
            DraftDurationTicks = value.DurationTicks?.ToString() ?? "";
            NotifyInspectorChanged();
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
                OnPropertyChanged(nameof(CanJumpToSelectedIssue));
                OnPropertyChanged(nameof(CanAutoFixSelectedIssue));
            }
        }
    }

    public bool CanJumpToSelectedIssue => SelectedIssueRow is { Source.Length: > 0 };

    public bool CanAutoFixSelectedIssue => SelectedIssueRow is { Code.Length: > 0 };

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

    public MediaAssetRow? SelectedMediaAssetRow
    {
        get => _selectedMediaAssetRow;
        set
        {
            if (SetProperty(ref _selectedMediaAssetRow, value) && value is not null)
            {
                OnPropertyChanged(nameof(CanEditSelectedMediaAsset));
                OnPropertyChanged(nameof(CanPreviewSelectedMediaAsset));
                StatusText = $"Selected media asset: {value.MediaId}";
            }
            else
            {
                OnPropertyChanged(nameof(CanEditSelectedMediaAsset));
                OnPropertyChanged(nameof(CanPreviewSelectedMediaAsset));
            }
        }
    }

    public bool CanEditSelectedMediaAsset => SelectedMediaAssetRow is not null;

    public bool CanPreviewSelectedMediaAsset => SelectedMediaAssetRow is not null;

    public bool CanRepairMissingAudioReference =>
        SelectedIssueRow is { Index: not null } issue &&
        !string.IsNullOrWhiteSpace(issue.ReferenceType);

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
                LoadInspectorDraftFromEventRow(value);
                OnPropertyChanged(nameof(CanEditSelectedEvent));
                OnPropertyChanged(nameof(IsSelectedEventTiming));
                OnPropertyChanged(nameof(IsSelectedEventMedia));
                NotifyInspectorChanged();
                StatusText = $"Selected event: {value.Type} tick {value.Tick}";
            }
            else
            {
                OnPropertyChanged(nameof(CanEditSelectedEvent));
                OnPropertyChanged(nameof(IsSelectedEventTiming));
                OnPropertyChanged(nameof(IsSelectedEventMedia));
                NotifyInspectorChanged();
            }
        }
    }

    public bool CanEditSelectedEvent => SelectedEventRow is not null;

    public bool IsSelectedEventTiming => SelectedEventRow is { } row && IsTimingEditorLane(row.Lane);

    public bool IsSelectedEventMedia => SelectedEventRow is { } row && IsMediaEditorLane(row.Lane);

    public string InspectorKindText
    {
        get
        {
            if (SelectedNote is not null)
            {
                return "Inspector: Note";
            }

            if (SelectedEventRow is { } eventRow)
            {
                return IsTimingEditorLane(eventRow.Lane)
                    ? "Inspector: Timing/Event"
                    : IsMediaEditorLane(eventRow.Lane)
                        ? "Inspector: Media Event"
                        : "Inspector: Event";
            }

            if (EditorSelectedLane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
            {
                return "Inspector: BGM";
            }

            return "Inspector: Position";
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

    public bool IsEditorSnapEnabled
    {
        get => _isEditorSnapEnabled;
        set
        {
            if (SetProperty(ref _isEditorSnapEnabled, value))
            {
                StatusText = value ? "Snap on" : "Snap off";
            }
        }
    }

    public int EditorGridTicks => ResolveEditorGridTicks();

    public void ApplyEditorGridSettings(int division, bool snapEnabled)
    {
        EditorGridDivision = division;
        IsEditorSnapEnabled = snapEnabled;
        StatusText = $"Grid 1/{EditorGridDivision}, snap {(IsEditorSnapEnabled ? "on" : "off")}";
    }

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

        var selectedRows = ResolveSelectedTimelineRows();
        if (selectedRows.Count > 1)
        {
            var lanes = ResolveEditorLaneOrder();
            var anchorTick = selectedRows.Min(row => row.Tick);
            var anchorLaneIndex = selectedRows
                .Select(row => FindLaneIndex(lanes, row.Lane))
                .Where(index => index >= 0)
                .DefaultIfEmpty(0)
                .Min();
            var items = new List<EditorClipboardItem>();
            foreach (var row in selectedRows)
            {
                if (TryCreateClipboardItem(row, anchorTick, anchorLaneIndex, lanes, out var item))
                {
                    items.Add(item);
                }
            }

            if (items.Count > 0)
            {
                _editorClipboardItems = items;
                _editorClipboard = items[0];
                NotifyEditorClipboardChanged();
                StatusText = $"Copied {items.Count} objects";
                return;
            }
        }

        var note = Notes.FirstOrDefault(item =>
            item.Tick == EditorSelectedTick &&
            string.Equals(item.Lane, EditorSelectedLane, StringComparison.OrdinalIgnoreCase));
        if (note is not null)
        {
            _editorClipboard = EditorClipboardItem.FromNote(note, 0, 0);
            _editorClipboardItems = [_editorClipboard];
            NotifyEditorClipboardChanged();
            StatusText = $"Copied note: tick {note.Tick}, lane {note.Lane}";
            return;
        }

        var bgm = _selectedChart.Chart.BackgroundAudio.FirstOrDefault(item =>
            item.Tick == EditorSelectedTick &&
                string.Equals(item.Lane ?? "background1", EditorSelectedLane, StringComparison.OrdinalIgnoreCase));
        if (bgm is not null)
        {
            _editorClipboard = EditorClipboardItem.FromBgm(bgm, 0, 0);
            _editorClipboardItems = [_editorClipboard];
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

        if (_editorClipboard is null || _editorClipboardItems.Count == 0)
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

        if (_editorClipboardItems.Count > 1)
        {
            PasteMultipleEditorObjects(tick, lane);
            return;
        }

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

    public void ReplaceSelectedObjectsAudioId()
    {
        if (_selectedChart is null)
        {
            return;
        }

        var audioId = ResolveEditorAudioId();
        if (string.IsNullOrWhiteSpace(audioId))
        {
            StatusText = "置換先のaudioIdがありません。先に音源を選択してください。";
            return;
        }

        var selectedRows = ResolveSelectedTimelineRows();
        if (selectedRows.Count == 0 && SelectedNote is not null)
        {
            selectedRows.Add(new TimelineRow
            {
                Tick = SelectedNote.Tick,
                Kind = "Note",
                Lane = SelectedNote.Lane,
                Detail = $"{SelectedNote.Type} {SelectedNote.AudioId}",
                DurationTicks = SelectedNote.DurationTicks
            });
        }

        var changes = new List<AudioIdReplacementTarget>();
        foreach (var row in selectedRows)
        {
            if (row.Kind == "Note")
            {
                var note = Notes.FirstOrDefault(item =>
                    item.Tick == row.Tick &&
                    string.Equals(item.Lane, row.Lane, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals($"{item.Type} {item.AudioId}", row.Detail, StringComparison.Ordinal));
                if (note is not null && !string.Equals(note.AudioId, audioId, StringComparison.Ordinal))
                {
                    changes.Add(new AudioIdReplacementTarget(note, note.AudioId, audioId));
                }
            }
            else if (row.Kind == "BGM")
            {
                var bgm = _selectedChart.Chart.BackgroundAudio.FirstOrDefault(item =>
                    item.Tick == row.Tick &&
                    string.Equals(item.Lane ?? "background1", row.Lane, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.AudioId, row.Detail, StringComparison.Ordinal));
                if (bgm is not null && !string.Equals(bgm.AudioId, audioId, StringComparison.Ordinal))
                {
                    changes.Add(new AudioIdReplacementTarget(bgm, bgm.AudioId, audioId));
                }
            }
        }

        if (changes.Count == 0)
        {
            StatusText = "audioId置換対象がありません。";
            return;
        }

        ApplyAudioIdReplacements(changes, useNewValue: true);
        PushEditorCommand(new EditorCommand(
            "Replace AudioId",
            () => ApplyAudioIdReplacements(changes, useNewValue: false),
            () => ApplyAudioIdReplacements(changes, useNewValue: true)));
        StatusText = $"Replaced audioId: {changes.Count} objects -> {audioId}";
    }

    public TimingEventEditDraft? CreateSelectedTimingEventEditDraft()
    {
        if (SelectedEventRow is not { } row || !IsTimingEditorLane(row.Lane))
        {
            StatusText = "編集するTiming/Eventを選択してください。";
            return null;
        }

        var timing = FindTimingEventNear(row.Tick, row.Lane);
        if (timing is null)
        {
            StatusText = "編集対象のTiming/Eventが見つかりません。";
            return null;
        }

        return new TimingEventEditDraft(
            row.Tick,
            row.Lane,
            timing.Type,
            timing.Value,
            timing.DurationTicks,
            timing.Event ?? "");
    }

    public void ApplySelectedTimingEventEdit(double? value, int? durationTicks)
    {
        if (_selectedChart is null || SelectedEventRow is not { } row)
        {
            return;
        }

        var timing = FindTimingEventNear(row.Tick, row.Lane);
        if (timing is null)
        {
            StatusText = "編集対象のTiming/Eventが見つかりません。";
            return;
        }

        var oldValue = timing.Value;
        var oldDuration = timing.DurationTicks;
        if (timing.Type == "stop")
        {
            timing.DurationTicks = durationTicks;
            timing.Value = null;
        }
        else
        {
            timing.Value = value;
            timing.DurationTicks = null;
        }

        PushEditorCommand(new EditorCommand(
            "Edit Timing Event",
            () =>
            {
                timing.Value = oldValue;
                timing.DurationTicks = oldDuration;
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                timing.Value = value;
                timing.DurationTicks = timing.Type == "stop" ? durationTicks : null;
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Timing/Eventを更新しました: {timing.Type} tick {timing.Tick}";
    }

    public MediaEventEditDraft? CreateSelectedMediaEventEditDraft()
    {
        if (SelectedEventRow is not { } row || !IsMediaEditorLane(row.Lane))
        {
            StatusText = "編集するMedia eventを選択してください。";
            return null;
        }

        var mediaEvent = FindMediaEventNear(row.Tick, row.Lane);
        if (mediaEvent is null)
        {
            StatusText = "編集対象のMedia eventが見つかりません。";
            return null;
        }

        return new MediaEventEditDraft(
            mediaEvent.Tick,
            row.Lane,
            mediaEvent.MediaId,
            mediaEvent.Type,
            mediaEvent.Layer ?? 0);
    }

    public void ApplySelectedMediaEventEdit(string mediaId, string type, int? layer)
    {
        if (_selectedChart is null || SelectedEventRow is not { } row)
        {
            return;
        }

        var mediaEvent = FindMediaEventNear(row.Tick, row.Lane);
        if (mediaEvent is null)
        {
            StatusText = "編集対象のMedia eventが見つかりません。";
            return;
        }

        var oldMediaId = mediaEvent.MediaId;
        var oldType = mediaEvent.Type;
        var oldLayer = mediaEvent.Layer;
        var newMediaId = string.IsNullOrWhiteSpace(mediaId) ? oldMediaId : mediaId.Trim();
        var newType = string.IsNullOrWhiteSpace(type) ? ResolveMediaTypeForLane(row.Lane, newMediaId) : type.Trim();
        var newLayer = row.Lane == "layer"
            ? Math.Max(1, layer ?? oldLayer ?? 1)
            : Math.Max(0, layer ?? oldLayer ?? 0);

        mediaEvent.MediaId = newMediaId;
        mediaEvent.Type = newType;
        mediaEvent.Layer = newLayer;
        PushEditorCommand(new EditorCommand(
            "Edit Media Event",
            () =>
            {
                mediaEvent.MediaId = oldMediaId;
                mediaEvent.Type = oldType;
                mediaEvent.Layer = oldLayer;
                RefreshMediaRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                mediaEvent.MediaId = newMediaId;
                mediaEvent.Type = newType;
                mediaEvent.Layer = newLayer;
                RefreshMediaRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        RefreshMediaRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Media eventを更新しました: {newMediaId} layer {newLayer}";
    }

    public void DeleteSelectedEvent()
    {
        if (_selectedChart is null || SelectedEventRow is not { } row)
        {
            StatusText = "削除するEventを選択してください。";
            return;
        }

        if (IsTimingEditorLane(row.Lane))
        {
            var timing = FindTimingEventNear(row.Tick, row.Lane);
            if (timing is null)
            {
                StatusText = "削除対象のTiming/Eventが見つかりません。";
                return;
            }

            _selectedChart.Chart.Timing.Remove(timing);
            PushEditorCommand(new EditorCommand(
                "Delete Timing Event",
                () =>
                {
                    _selectedChart.Chart.Timing.Add(timing);
                    RefreshTimelineOnly(preserveEditorRange: true);
                },
                () =>
                {
                    _selectedChart.Chart.Timing.Remove(timing);
                    RefreshTimelineOnly(preserveEditorRange: true);
                }));
            ClearEditorSelection();
            RefreshTimelineOnly(preserveEditorRange: true);
            StatusText = $"Timing/Eventを削除しました: {row.Type} tick {row.Tick}";
            return;
        }

        if (IsMediaEditorLane(row.Lane))
        {
            var mediaEvent = FindMediaEventNear(row.Tick, row.Lane);
            if (mediaEvent is null)
            {
                StatusText = "削除対象のMedia eventが見つかりません。";
                return;
            }

            _selectedChart.Chart.MediaEvents.Remove(mediaEvent);
            PushEditorCommand(new EditorCommand(
                "Delete Media Event",
                () =>
                {
                    _selectedChart.Chart.MediaEvents.Add(mediaEvent);
                    RefreshMediaRows();
                    RefreshTimelineOnly(preserveEditorRange: true);
                },
                () =>
                {
                    _selectedChart.Chart.MediaEvents.Remove(mediaEvent);
                    RefreshMediaRows();
                    RefreshTimelineOnly(preserveEditorRange: true);
                }));
            ClearEditorSelection();
            RefreshMediaRows();
            RefreshTimelineOnly(preserveEditorRange: true);
            StatusText = $"Media eventを削除しました: {mediaEvent.MediaId} tick {row.Tick}";
            return;
        }

        StatusText = "削除対象外のEventです。";
    }

    public void ApplyInspectorEdits()
    {
        if (SelectedNote is not null)
        {
            ApplyDraftToSelectedNote();
            return;
        }

        if (SelectedEventRow is { } eventRow)
        {
            if (IsTimingEditorLane(eventRow.Lane))
            {
                ApplyInspectorTimingEvent(eventRow);
                return;
            }

            if (IsMediaEditorLane(eventRow.Lane))
            {
                ApplyInspectorMediaEvent(eventRow);
                return;
            }
        }

        if (EditorSelectedLane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
        {
            ApplyInspectorBgm();
            return;
        }

        AddDraftNote();
    }

    public void DeleteInspectorTarget()
    {
        if (SelectedNote is not null)
        {
            DeleteSelectedNote();
            return;
        }

        if (SelectedEventRow is not null)
        {
            DeleteSelectedEvent();
            return;
        }

        if (EditorSelectedTick >= 0 && !string.IsNullOrWhiteSpace(EditorSelectedLane))
        {
            DeleteTimelineObjectFromHit(EditorSelectedTick, EditorSelectedLane);
        }
    }

    public void InsertMeasureAtSelection()
    {
        EditMeasuresAtSelection(insert: true);
    }

    public void DeleteMeasureAtSelection()
    {
        EditMeasuresAtSelection(insert: false);
    }

    private void EditMeasuresAtSelection(bool insert)
    {
        if (_selectedChart is null)
        {
            StatusText = "編集する譜面を選択してください。";
            return;
        }

        ApplyNoteRows();
        ApplyMediaRows();

        var measureTicks = ResolveEditorMeasureTicks();
        var startTick = ResolveCurrentMeasureStartTick(measureTicks);
        var deltaTicks = measureTicks;
        var oldChart = CloneNbmsChart(_selectedChart.Chart);

        if (insert)
        {
            ShiftChartTicks(_selectedChart.Chart, startTick, deltaTicks);
        }
        else
        {
            DeleteChartTickRange(_selectedChart.Chart, startTick, startTick + deltaTicks);
        }

        SortChartObjects(_selectedChart.Chart);
        var newChart = CloneNbmsChart(_selectedChart.Chart);
        RestoreChartSnapshot(newChart, startTick);

        PushEditorCommand(new EditorCommand(
            insert ? "Insert Measure" : "Delete Measure",
            () => RestoreChartSnapshot(oldChart, startTick),
            () => RestoreChartSnapshot(newChart, startTick)));
        StatusText = insert
            ? $"Inserted measure at tick {startTick} (+{deltaTicks})"
            : $"Deleted measure at tick {startTick} (-{deltaTicks})";
    }

    private void RestoreChartSnapshot(NbmsChart snapshot, int focusTick)
    {
        if (_selectedChart is null)
        {
            return;
        }

        _selectedChart.Chart = CloneNbmsChart(snapshot);
        ClearEditorSelection();
        EditorSelectedTick = Math.Max(0, focusTick);
        DraftTick = EditorSelectedTick.ToString(CultureInfo.InvariantCulture);
        RefreshNotesAndTimeline();
    }

    private int ResolveEditorMeasureTicks()
    {
        if (_selectedChart?.Chart.Resolution is > 0)
        {
            return _selectedChart.Chart.Resolution * 4;
        }

        return 960 * 4;
    }

    private int ResolveCurrentMeasureStartTick(int measureTicks)
    {
        var tick = EditorSelectedTick >= 0
            ? EditorSelectedTick
            : int.TryParse(DraftTick, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTick)
                ? parsedTick
                : 0;
        tick = Math.Max(0, tick);
        return tick / Math.Max(1, measureTicks) * Math.Max(1, measureTicks);
    }

    private static void ShiftChartTicks(NbmsChart chart, int startTick, int deltaTicks)
    {
        foreach (var note in chart.Notes.Where(note => note.Tick >= startTick))
        {
            note.Tick += deltaTicks;
        }

        foreach (var timing in chart.Timing.Where(timing => timing.Tick >= startTick))
        {
            timing.Tick += deltaTicks;
        }

        foreach (var background in chart.BackgroundAudio.Where(background => background.Tick >= startTick))
        {
            background.Tick += deltaTicks;
        }

        foreach (var mediaEvent in chart.MediaEvents.Where(mediaEvent => mediaEvent.Tick >= startTick))
        {
            mediaEvent.Tick += deltaTicks;
        }
    }

    private static void DeleteChartTickRange(NbmsChart chart, int startTick, int endTick)
    {
        var deltaTicks = Math.Max(0, endTick - startTick);
        chart.Notes.RemoveAll(note => note.Tick >= startTick && note.Tick < endTick);
        chart.Timing.RemoveAll(timing => timing.Tick >= startTick && timing.Tick < endTick);
        chart.BackgroundAudio.RemoveAll(background => background.Tick >= startTick && background.Tick < endTick);
        chart.MediaEvents.RemoveAll(mediaEvent => mediaEvent.Tick >= startTick && mediaEvent.Tick < endTick);

        foreach (var note in chart.Notes.Where(note => note.Tick >= endTick))
        {
            note.Tick = Math.Max(0, note.Tick - deltaTicks);
        }

        foreach (var timing in chart.Timing.Where(timing => timing.Tick >= endTick))
        {
            timing.Tick = Math.Max(0, timing.Tick - deltaTicks);
        }

        foreach (var background in chart.BackgroundAudio.Where(background => background.Tick >= endTick))
        {
            background.Tick = Math.Max(0, background.Tick - deltaTicks);
        }

        foreach (var mediaEvent in chart.MediaEvents.Where(mediaEvent => mediaEvent.Tick >= endTick))
        {
            mediaEvent.Tick = Math.Max(0, mediaEvent.Tick - deltaTicks);
        }
    }

    private static void SortChartObjects(NbmsChart chart)
    {
        chart.Notes = chart.Notes
            .OrderBy(note => note.Tick)
            .ThenBy(note => note.Lane, StringComparer.Ordinal)
            .ToList();
        chart.Timing = chart.Timing
            .OrderBy(timing => timing.Tick)
            .ThenBy(timing => timing.Type, StringComparer.Ordinal)
            .ToList();
        chart.BackgroundAudio = chart.BackgroundAudio
            .OrderBy(background => background.Tick)
            .ThenBy(background => background.Lane ?? "", StringComparer.Ordinal)
            .ThenBy(background => background.AudioId, StringComparer.Ordinal)
            .ToList();
        chart.MediaEvents = chart.MediaEvents
            .OrderBy(mediaEvent => mediaEvent.Tick)
            .ThenBy(mediaEvent => mediaEvent.Layer ?? 0)
            .ThenBy(mediaEvent => mediaEvent.MediaId, StringComparer.Ordinal)
            .ToList();
    }

    private static NbmsChart CloneNbmsChart(NbmsChart chart)
    {
        var json = JsonSerializer.Serialize(chart, NbmsJson.SerializerOptions);
        return JsonSerializer.Deserialize<NbmsChart>(json, NbmsJson.SerializerOptions) ?? new NbmsChart();
    }

    private void PasteMultipleEditorObjects(int anchorTick, string anchorLane)
    {
        if (_selectedChart is null || _editorClipboardItems.Count == 0)
        {
            return;
        }

        var lanes = ResolveEditorLaneOrder();
        var anchorLaneIndex = FindLaneIndex(lanes, anchorLane);
        if (anchorLaneIndex < 0)
        {
            StatusText = $"貼り付け先レーンが見つかりません: {anchorLane}";
            return;
        }

        var pasted = new List<object>();
        foreach (var item in _editorClipboardItems)
        {
            var lane = ResolveShiftedLane(lanes, lanes[anchorLaneIndex], item.LaneOffset);
            var tick = Math.Max(0, SnapEditorTick(anchorTick + item.TickOffset));
            if (string.IsNullOrWhiteSpace(lane))
            {
                StatusText = $"貼り付け先レーンを解決できません: offset {item.LaneOffset}";
                return;
            }

            switch (item.Kind)
            {
                case EditorClipboardKind.Note:
                    if (!IsPlayableEditorLane(lane))
                    {
                        StatusText = $"Noteを貼り付けられないレーンです: {lane}";
                        return;
                    }

                    var note = new NoteRow
                    {
                        Tick = tick,
                        Lane = lane,
                        Type = item.Type,
                        AudioId = item.AudioId,
                        DurationTicks = item.DurationTicks
                    };
                    Notes.Add(note);
                    pasted.Add(note);
                    break;
                case EditorClipboardKind.Bgm:
                    if (!lane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText = $"BGMを貼り付けられないレーンです: {lane}";
                        return;
                    }

                    var bgm = new BackgroundAudioEvent
                    {
                        Tick = tick,
                        Lane = lane,
                        AudioId = item.AudioId
                    };
                    _selectedChart.Chart.BackgroundAudio.Add(bgm);
                    pasted.Add(bgm);
                    break;
                case EditorClipboardKind.Media:
                    if (!IsMediaEditorLane(lane))
                    {
                        StatusText = $"Media eventを貼り付けられないレーンです: {lane}";
                        return;
                    }

                    var mediaEvent = new MediaEvent
                    {
                        Tick = tick,
                        MediaId = item.MediaId,
                        Type = ResolveMediaTypeForLane(lane, item.MediaId),
                        Layer = lane == "layer" ? Math.Max(1, item.Layer ?? 1) : 0
                    };
                    _selectedChart.Chart.MediaEvents.Add(mediaEvent);
                    pasted.Add(mediaEvent);
                    break;
                case EditorClipboardKind.Timing:
                    if (!IsTimingEditorLane(lane) || !string.Equals(lane, item.Lane, StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText = "Timing/Eventは同じイベントレーンへ貼り付けてください。";
                        return;
                    }

                    var timing = new TimingEvent
                    {
                        Tick = tick,
                        Type = item.Type,
                        Value = item.Value,
                        DurationTicks = item.DurationTicks,
                        ExtensionId = item.ExtensionId,
                        Event = item.Event
                    };
                    _selectedChart.Chart.Timing.Add(timing);
                    pasted.Add(timing);
                    break;
            }
        }

        ApplyNoteRows();
        RefreshMediaRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        SetEditorSelectionKeys(pasted.Select(CreatePastedObjectKey));
        PushEditorCommand(new EditorCommand(
            "Paste Multiple Objects",
            () =>
            {
                foreach (var item in pasted)
                {
                    RemovePastedObject(item);
                }

                ClearEditorSelection();
                ApplyNoteRows();
                RefreshMediaRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                foreach (var item in pasted)
                {
                    AddPastedObject(item);
                }

                ApplyNoteRows();
                RefreshMediaRows();
                RefreshTimelineOnly(preserveEditorRange: true);
                SetEditorSelectionKeys(pasted.Select(CreatePastedObjectKey));
            }));
        StatusText = $"Pasted {pasted.Count} objects";
    }

    private void ApplyAudioIdReplacements(IReadOnlyList<AudioIdReplacementTarget> changes, bool useNewValue)
    {
        foreach (var change in changes)
        {
            var value = useNewValue ? change.NewAudioId : change.OldAudioId;
            switch (change.Target)
            {
                case NoteRow note:
                    note.AudioId = value;
                    break;
                case BackgroundAudioEvent bgm:
                    bgm.AudioId = value;
                    break;
            }
        }

        ApplyNoteRows();
        RefreshTimelineOnly(preserveEditorRange: true);
    }

    private void AddPastedObject(object item)
    {
        if (_selectedChart is null)
        {
            return;
        }

        switch (item)
        {
            case NoteRow note when !Notes.Contains(note):
                Notes.Add(note);
                break;
            case BackgroundAudioEvent bgm when !_selectedChart.Chart.BackgroundAudio.Contains(bgm):
                _selectedChart.Chart.BackgroundAudio.Add(bgm);
                break;
            case MediaEvent mediaEvent when !_selectedChart.Chart.MediaEvents.Contains(mediaEvent):
                _selectedChart.Chart.MediaEvents.Add(mediaEvent);
                break;
            case TimingEvent timing when !_selectedChart.Chart.Timing.Contains(timing):
                _selectedChart.Chart.Timing.Add(timing);
                break;
        }
    }

    private void RemovePastedObject(object item)
    {
        if (_selectedChart is null)
        {
            return;
        }

        switch (item)
        {
            case NoteRow note:
                Notes.Remove(note);
                break;
            case BackgroundAudioEvent bgm:
                _selectedChart.Chart.BackgroundAudio.Remove(bgm);
                break;
            case MediaEvent mediaEvent:
                _selectedChart.Chart.MediaEvents.Remove(mediaEvent);
                break;
            case TimingEvent timing:
                _selectedChart.Chart.Timing.Remove(timing);
                break;
        }
    }

    private string CreatePastedObjectKey(object item)
    {
        return item switch
        {
            NoteRow note => CreateTimelineObjectKey("Note", note.Tick, note.Lane, $"{note.Type} {note.AudioId}"),
            BackgroundAudioEvent bgm => CreateTimelineObjectKey("BGM", bgm.Tick, bgm.Lane ?? "background1", bgm.AudioId),
            MediaEvent mediaEvent => CreateTimelineObjectKey("Visual", mediaEvent.Tick, ResolveMediaLane(mediaEvent.Type, mediaEvent.Layer), $"{mediaEvent.Type} {mediaEvent.MediaId}"),
            TimingEvent timing => CreateTimelineObjectKey("Timing", timing.Tick, ResolveTimingLane(timing), DescribeTimingForSelection(timing)),
            _ => ""
        };
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

    public IReadOnlyList<AudioRepairCandidate> GetSelectedMissingAudioRepairCandidates()
    {
        if (_project?.AudioManifest is null || SelectedIssueRow is not { AudioId.Length: > 0 })
        {
            return [];
        }

        var missingAudioId = SelectedIssueRow.AudioId;
        return _project.AudioManifest.Entries
            .Select(entry => new AudioRepairCandidate(
                entry.AudioId,
                entry.Path,
                entry.Codec,
                entry.DurationMs,
                ScoreAudioRepairCandidate(missingAudioId, entry)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.AudioId, StringComparer.Ordinal)
            .Take(12)
            .ToList();
    }

    public void RepairSelectedMissingAudioReference(string newAudioId)
    {
        if (AudioEntries.FirstOrDefault(row => row.AudioId == newAudioId) is { } row)
        {
            SelectedAudioRow = row;
        }

        RepairSelectedMissingAudioReference();
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
            if (AudioMetadataReader.TryRead(sourceFilePath) is { } metadata)
            {
                entry.DurationMs = metadata.DurationMs;
                entry.SampleRate = metadata.SampleRate;
                entry.Channels = metadata.Channels;
                await _audioArchiveService.WriteManifestAsync(audioPath, _project.AudioManifest);
            }

            RefreshCollections();
            SelectedAudioRow = AudioEntries.FirstOrDefault(row => row.AudioId == entry.AudioId);
            StatusText = $"音声を追加しました: {entry.AudioId}";
        }
        catch (Exception ex)
        {
            StatusText = $"音声追加に失敗しました: {ex.Message}";
        }
    }

    public async Task RenameSelectedAudioAssetAsync(string newAudioId)
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
        await _audioArchiveService.RenameAudioEntryArchivePathAsync(
            ResolveAudioArchivePath(_project),
            _project.AudioManifest,
            entryToRename,
            normalizedNewId);
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

    public async Task DeleteAudioAssetsAsync(IReadOnlyCollection<string> audioIds)
    {
        if (_project?.AudioManifest is null || audioIds.Count == 0)
        {
            StatusText = "削除する音声assetを選択してください。";
            return;
        }

        var requested = audioIds
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var deletable = requested
            .Where(audioId => !IsAudioReferenced(audioId))
            .ToList();
        var skipped = requested.Count - deletable.Count;

        if (deletable.Count == 0)
        {
            StatusText = $"参照中の音声assetは削除できません: {requested.Count}件";
            return;
        }

        var message = skipped == 0
            ? $"音声assetを削除しました: {deletable.Count}件"
            : $"音声assetを削除しました: {deletable.Count}件 / 参照中のためスキップ: {skipped}件";
        await RemoveAudioAssetsAsync(deletable, message);
    }

    private async Task RemoveUnreferencedDuplicateAudioAssetsAsync()
    {
        if (_project?.AudioManifest is null)
        {
            return;
        }

        var referenced = ResolveReferencedAudioIds();
        var duplicateIds = ResolveDuplicateAudioEntries()
            .Where(entry => !referenced.Contains(entry.AudioId))
            .Select(entry => entry.AudioId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (duplicateIds.Count == 0)
        {
            StatusText = "削除可能な未参照の重複音声assetはありません。";
            return;
        }

        await RemoveAudioAssetsAsync(duplicateIds, $"未参照の重複音声assetを削除しました: {duplicateIds.Count}件");
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

    public async Task ValidateAudioArchiveAsync()
    {
        if (_project?.AudioManifest is null)
        {
            StatusText = "audio packがありません。";
            return;
        }

        try
        {
            var issues = await _audioArchiveService.ValidateArchiveEntriesAsync(
                ResolveAudioArchivePath(_project),
                _project.AudioManifest);
            ReplaceArchiveIssues("AudioArchive", issues);
            StatusText = issues.Count == 0
                ? ".nbma archive check OK"
                : $".nbma archive check found {issues.Count} issues";
        }
        catch (Exception ex)
        {
            ReplaceArchiveIssues("AudioArchive", [new ArchiveIntegrityIssue("Error", "", "", ex.Message)]);
            StatusText = $".nbma archive check failed: {ex.Message}";
        }
    }

    public async Task AddMediaAssetAsync(string sourceFilePath, string requestedMediaId, string mediaType)
    {
        if (_project is null)
        {
            StatusText = "NBMSを開いてからメディアを追加してください。";
            return;
        }

        try
        {
            EnsureMediaManifest();
            var mediaPath = ResolveMediaArchivePath(_project);
            var entry = await _mediaArchiveService.AddMediaFileAsync(
                mediaPath,
                _project.MediaManifest!,
                sourceFilePath,
                requestedMediaId,
                mediaType);
            var metadata = MediaMetadataReader.TryRead(sourceFilePath);
            entry.Width = metadata.Width;
            entry.Height = metadata.Height;
            entry.DurationMs = metadata.DurationMs;
            await _mediaArchiveService.WriteManifestAsync(mediaPath, _project.MediaManifest!);

            RefreshCollections();
            SelectedMediaAssetRow = MediaAssetEntries.FirstOrDefault(row => row.MediaId == entry.MediaId);
            StatusText = $"メディアを追加しました: {entry.MediaId}";
        }
        catch (Exception ex)
        {
            StatusText = $"メディア追加に失敗しました: {ex.Message}";
        }
    }

    public async Task RenameSelectedMediaAssetAsync(string newMediaId)
    {
        if (_project?.MediaManifest is null || SelectedMediaAssetRow is null)
        {
            StatusText = "リネームするメディアを選択してください。";
            return;
        }

        var oldMediaId = SelectedMediaAssetRow.MediaId;
        var normalizedNewId = NormalizeAssetId(newMediaId);
        if (string.IsNullOrWhiteSpace(normalizedNewId) || string.Equals(oldMediaId, normalizedNewId, StringComparison.Ordinal))
        {
            return;
        }

        if (_project.MediaManifest.Entries.Any(entry => entry.MediaId == normalizedNewId))
        {
            StatusText = $"同じMediaIdがすでに存在します: {normalizedNewId}";
            return;
        }

        var entryToRename = _project.MediaManifest.Entries.FirstOrDefault(entry => entry.MediaId == oldMediaId);
        if (entryToRename is null)
        {
            StatusText = $"manifestにMediaIdが見つかりません: {oldMediaId}";
            return;
        }

        entryToRename.MediaId = normalizedNewId;
        await _mediaArchiveService.RenameMediaEntryArchivePathAsync(
            ResolveMediaArchivePath(_project),
            _project.MediaManifest,
            entryToRename,
            normalizedNewId);
        foreach (var chart in _project.Charts)
        {
            foreach (var mediaEvent in chart.Chart.MediaEvents.Where(mediaEvent => mediaEvent.MediaId == oldMediaId))
            {
                mediaEvent.MediaId = normalizedNewId;
            }
        }

        RefreshCollections();
        SelectedMediaAssetRow = MediaAssetEntries.FirstOrDefault(row => row.MediaId == normalizedNewId);
        StatusText = $"MediaIdを変更しました: {oldMediaId} -> {normalizedNewId}";
    }

    public async Task DeleteSelectedMediaAssetAsync()
    {
        if (_project?.MediaManifest is null || SelectedMediaAssetRow is null)
        {
            StatusText = "削除するメディアを選択してください。";
            return;
        }

        var mediaId = SelectedMediaAssetRow.MediaId;
        if (IsMediaReferenced(mediaId))
        {
            StatusText = $"参照中のメディアは削除できません: {mediaId}";
            return;
        }

        await RemoveMediaAssetsAsync([mediaId], $"メディアを削除しました: {mediaId}");
    }

    public async Task DeleteMediaAssetsAsync(IReadOnlyCollection<string> mediaIds)
    {
        if (_project?.MediaManifest is null || mediaIds.Count == 0)
        {
            StatusText = "削除するmedia assetを選択してください。";
            return;
        }

        var requested = mediaIds
            .Where(mediaId => !string.IsNullOrWhiteSpace(mediaId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var deletable = requested
            .Where(mediaId => !IsMediaReferenced(mediaId))
            .ToList();
        var skipped = requested.Count - deletable.Count;

        if (deletable.Count == 0)
        {
            StatusText = $"参照中のmedia assetは削除できません: {requested.Count}件";
            return;
        }

        var message = skipped == 0
            ? $"media assetを削除しました: {deletable.Count}件"
            : $"media assetを削除しました: {deletable.Count}件 / 参照中のためスキップ: {skipped}件";
        await RemoveMediaAssetsAsync(deletable, message);
    }

    private async Task RemoveUnreferencedDuplicateMediaAssetsAsync()
    {
        if (_project?.MediaManifest is null)
        {
            return;
        }

        var referenced = ResolveReferencedMediaIds();
        var duplicateIds = ResolveDuplicateMediaEntries()
            .Where(entry => !referenced.Contains(entry.MediaId))
            .Select(entry => entry.MediaId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (duplicateIds.Count == 0)
        {
            StatusText = "削除可能な未参照の重複media assetはありません。";
            return;
        }

        await RemoveMediaAssetsAsync(duplicateIds, $"未参照の重複media assetを削除しました: {duplicateIds.Count}件");
    }

    public async Task RemoveUnusedMediaAssetsAsync()
    {
        if (_project?.MediaManifest is null)
        {
            StatusText = "media packがありません。";
            return;
        }

        var referenced = ResolveReferencedMediaIds();
        var unused = _project.MediaManifest.Entries
            .Where(entry => !referenced.Contains(entry.MediaId))
            .Select(entry => entry.MediaId)
            .ToList();
        if (unused.Count == 0)
        {
            StatusText = "未使用メディアはありません。";
            return;
        }

        await RemoveMediaAssetsAsync(unused, $"未使用メディアを削除しました: {unused.Count}件");
    }

    public async Task ValidateMediaArchiveAsync()
    {
        if (_project?.MediaManifest is null)
        {
            StatusText = "media packがありません。";
            return;
        }

        try
        {
            var issues = await _mediaArchiveService.ValidateArchiveEntriesAsync(
                ResolveMediaArchivePath(_project),
                _project.MediaManifest);
            ReplaceArchiveIssues("MediaArchive", issues);
            StatusText = issues.Count == 0
                ? ".nbmg archive check OK"
                : $".nbmg archive check found {issues.Count} issues";
        }
        catch (Exception ex)
        {
            ReplaceArchiveIssues("MediaArchive", [new ArchiveIntegrityIssue("Error", "", "", ex.Message)]);
            StatusText = $".nbmg archive check failed: {ex.Message}";
        }
    }

    public async Task ValidatePackageAsync()
    {
        if (_project is null)
        {
            StatusText = "検証するNBMSを開いてください。";
            return;
        }

        ApplyHeaderFields();
        ApplyNoteRows();
        ApplyAudioRows();
        ApplyMediaRows();
        var issues = await _packageValidatorService.ValidateProjectAsync(_project);
        ReplacePackageIssues(issues);
        StatusText = issues.Count == 0
            ? "Package validator OK"
            : $"Package validator: {issues.Count}件のIssueを検出しました。";
    }

    public void JumpToSelectedIssue()
    {
        if (_project is null || SelectedIssueRow is null)
        {
            return;
        }

        var issue = SelectedIssueRow;
        if (_project.Charts.Any(chart => chart.Reference.Id == issue.Source))
        {
            SelectChartById(issue.Source);
            JumpToIssuePosition(issue);
            return;
        }

        if (issue.TargetReference.StartsWith("audio:", StringComparison.OrdinalIgnoreCase))
        {
            var audioId = issue.TargetReference["audio:".Length..];
            SelectedAudioRow = AudioEntries.FirstOrDefault(row => row.AudioId == audioId);
            StatusText = $"Issue target audioへ移動しました: {audioId}";
            return;
        }

        if (issue.TargetReference.StartsWith("media:", StringComparison.OrdinalIgnoreCase))
        {
            var mediaId = issue.TargetReference["media:".Length..];
            SelectedMediaAssetRow = MediaAssetEntries.FirstOrDefault(row => row.MediaId == mediaId);
            StatusText = $"Issue target mediaへ移動しました: {mediaId}";
            return;
        }

        StatusText = $"Issue target: {issue.TargetReference}";
    }

    public async Task AutoFixSelectedIssueAsync()
    {
        if (_project is null || SelectedIssueRow is null)
        {
            return;
        }

        var code = string.IsNullOrWhiteSpace(SelectedIssueRow.Code)
            ? InferIssueCode(SelectedIssueRow)
            : SelectedIssueRow.Code;

        if (code == "NBMS_AUDIO_UNUSED")
        {
            await RemoveUnusedAudioAssetsAsync();
            return;
        }

        if (code == "NBMS_MEDIA_UNUSED")
        {
            await RemoveUnusedMediaAssetsAsync();
            return;
        }

        if (code == "NBMS_AUDIO_REF_MISSING")
        {
            var candidate = GetSelectedMissingAudioRepairCandidates().FirstOrDefault();
            if (candidate is null)
            {
                StatusText = "参照切れ修復候補がありません。";
                return;
            }

            RepairSelectedMissingAudioReference(candidate.AudioId);
            return;
        }

        if (code.StartsWith("NBMS_AUDIO_DUP_", StringComparison.Ordinal))
        {
            await RemoveUnreferencedDuplicateAudioAssetsAsync();
            return;
        }

        if (code.StartsWith("NBMS_MEDIA_DUP_", StringComparison.Ordinal))
        {
            await RemoveUnreferencedDuplicateMediaAssetsAsync();
            return;
        }

        if (code.StartsWith("PKG_", StringComparison.Ordinal))
        {
            await SaveProjectAsync();
            await ValidatePackageAsync();
            return;
        }

        StatusText = $"Auto fix未対応のIssueです: {code}";
    }

    public async Task<MediaPreviewData?> LoadSelectedMediaPreviewAsync()
    {
        if (_project?.MediaManifest is null || SelectedMediaAssetRow is null)
        {
            StatusText = "プレビューするメディアを選択してください。";
            return null;
        }

        var row = SelectedMediaAssetRow;
        var entry = _project.MediaManifest.Entries.FirstOrDefault(item => item.MediaId == row.MediaId);
        if (entry is null)
        {
            StatusText = $"manifestにMediaIdが見つかりません: {row.MediaId}";
            return null;
        }

        var mediaPath = ResolveMediaArchivePath(_project);
        if (!File.Exists(mediaPath))
        {
            StatusText = $"media packが見つかりません: {mediaPath}";
            return null;
        }

        try
        {
            await using var fileStream = File.OpenRead(mediaPath);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
            var archiveEntry = archive.GetEntry(entry.Path.Replace('\\', '/'));
            if (archiveEntry is null)
            {
                StatusText = $"media pack内に実体が見つかりません: {entry.Path}";
                return null;
            }

            await using var entryStream = archiveEntry.Open();
            using var memory = new MemoryStream();
            await entryStream.CopyToAsync(memory);
            StatusText = $"Media preview: {entry.MediaId}";
            return new MediaPreviewData(entry.MediaId, entry.Type, entry.MimeType, entry.Path, memory.ToArray());
        }
        catch (Exception ex)
        {
            StatusText = $"メディアプレビューに失敗しました: {ex.Message}";
            return null;
        }
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
            SelectedEventRow = null;
            EditorSelectedTick = note.Tick;
            EditorSelectedLane = note.Lane;
            SetEditorSelectionKeys([CreateTimelineObjectKey("Note", note.Tick, note.Lane, $"{note.Type} {note.AudioId}")]);
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
            SelectedEventRow = null;
            EditorSelectedTick = bgm.Tick;
            EditorSelectedLane = bgm.Lane ?? "background1";
            DraftTick = bgm.Tick.ToString();
            DraftLane = EditorSelectedLane;
            DraftType = "bgm";
            DraftAudioId = bgm.AudioId;
            DraftDurationTicks = "";
            NotifyInspectorChanged();
            SetEditorSelectionKeys([CreateTimelineObjectKey("BGM", bgm.Tick, EditorSelectedLane, bgm.AudioId)]);
            StatusText = $"Selected BGM: tick {bgm.Tick}, lane {EditorSelectedLane}, audio {bgm.AudioId}";
            return bgm.AudioId;
        }

        if (IsMediaEditorLane(lane))
        {
            var mediaEvent = FindMediaEventNear(snappedTick, lane);
            if (mediaEvent is not null)
            {
                SelectedNote = null;
                SelectedMediaRow = MediaEntries.FirstOrDefault(row =>
                    row.Tick == mediaEvent.Tick &&
                    row.MediaId == mediaEvent.MediaId &&
                    ResolveMediaLane(row.Type, row.Layer) == lane);
                SelectedMediaAssetRow = MediaAssetEntries.FirstOrDefault(row => row.MediaId == mediaEvent.MediaId);
                EditorSelectedTick = mediaEvent.Tick;
                EditorSelectedLane = lane;
                SelectedEventRow = Events.FirstOrDefault(row =>
                    row.Tick == mediaEvent.Tick &&
                    string.Equals(row.Lane, lane, StringComparison.OrdinalIgnoreCase) &&
                    row.Detail.Contains(mediaEvent.MediaId, StringComparison.Ordinal));
                SetEditorSelectionKeys([CreateTimelineObjectKey("Visual", mediaEvent.Tick, lane, $"{mediaEvent.Type} {mediaEvent.MediaId}")]);
                StatusText = $"Selected media event: {mediaEvent.Type} tick {mediaEvent.Tick}, media {mediaEvent.MediaId}";
                return null;
            }
        }

        if (IsTimingEditorLane(lane))
        {
            var timing = FindTimingEventNear(snappedTick, lane);
            if (timing is not null)
            {
                SelectedNote = null;
                EditorSelectedTick = timing.Tick;
                EditorSelectedLane = ResolveTimingLane(timing);
                SelectedEventRow = Events.FirstOrDefault(row =>
                    row.Tick == timing.Tick &&
                    string.Equals(row.Lane, EditorSelectedLane, StringComparison.OrdinalIgnoreCase));
                SetEditorSelectionKeys([CreateTimelineObjectKey("Timing", timing.Tick, EditorSelectedLane, DescribeTimingForSelection(timing))]);
                StatusText = $"Selected timing: {timing.Type} tick {timing.Tick}";
                return null;
            }
        }

        ClearEditorSelection();
        return null;
    }

    public void SelectTimelineObjectsInRange(int startTick, int endTick, IReadOnlyList<string> lanes)
    {
        if (_selectedChart is null || lanes.Count == 0)
        {
            return;
        }

        var laneSet = lanes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var minTick = SnapEditorTick(Math.Min(startTick, endTick));
        var maxTick = SnapEditorTick(Math.Max(startTick, endTick));
        var selectedRows = Timeline
            .Where(row => row.Kind != "Timing" || !row.Detail.Equals("Bar", StringComparison.OrdinalIgnoreCase))
            .Where(row => row.Tick >= minTick && row.Tick <= maxTick)
            .Where(row => laneSet.Contains(row.Lane))
            .Select(CloneTimelineRow)
            .ToList();

        SetEditorSelectionKeys(selectedRows.Select(CreateTimelineObjectKey));
        SelectedNote = null;
        if (selectedRows.Count > 0)
        {
            EditorSelectedTick = selectedRows[0].Tick;
            EditorSelectedLane = selectedRows[0].Lane;
            StatusText = $"Range selected: {selectedRows.Count} objects";
        }
        else
        {
            EditorSelectedTick = -1;
            EditorSelectedLane = "";
            StatusText = "Range selected: 0 objects";
        }
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

        if (TryResizeSelectedLongNoteEnd(snappedFromTick, fromLane, snappedToTick, toLane))
        {
            return;
        }

        if (EditorSelectedObjectKeys.Count > 1 && IsSelectedObjectNear(snappedFromTick, fromLane))
        {
            MoveSelectedTimelineObjects(snappedFromTick, fromLane, snappedToTick, toLane);
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

        if (IsMediaEditorLane(fromLane) || IsMediaEditorLane(toLane))
        {
            if (!IsMediaEditorLane(fromLane) || !IsMediaEditorLane(toLane))
            {
                StatusText = "Media eventはBGA/LAYER/POORレーン内で移動してください。";
                return;
            }

            var mediaEvent = FindMediaEventNear(snappedFromTick, fromLane);
            if (mediaEvent is not null)
            {
                MoveMediaEvent(mediaEvent, snappedToTick, toLane);
                return;
            }
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

        if (IsMediaEditorLane(lane))
        {
            AddTimelineMediaEventFromHit(snappedTick, lane);
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

        if (IsMediaEditorLane(lane))
        {
            var mediaEvent = FindMediaEventNear(snappedTick, lane);
            if (mediaEvent is null)
            {
                StatusText = $"削除対象がありません: tick {snappedTick}, lane {lane}";
                return;
            }

            _selectedChart.Chart.MediaEvents.Remove(mediaEvent);
            var removedRow = MediaEntries.FirstOrDefault(row =>
                row.Tick == mediaEvent.Tick &&
                row.MediaId == mediaEvent.MediaId &&
                ResolveMediaLane(row.Type, row.Layer) == lane);
            if (removedRow is not null)
            {
                MediaEntries.Remove(removedRow);
            }
            EditorSelectedTick = -1;
            EditorSelectedLane = "";
            PushEditorCommand(new EditorCommand(
                "Delete Media Event",
                () =>
                {
                    _selectedChart.Chart.MediaEvents.Add(mediaEvent);
                    SelectedNote = null;
                    EditorSelectedTick = mediaEvent.Tick;
                    EditorSelectedLane = ResolveMediaLane(mediaEvent.Type, mediaEvent.Layer);
                    RefreshMediaRows();
                    RefreshTimelineOnly();
                },
                () =>
                {
                    _selectedChart.Chart.MediaEvents.Remove(mediaEvent);
                    ClearEditorSelection();
                    RefreshMediaRows();
                    RefreshTimelineOnly();
                }));
            RefreshMediaRows();
            RefreshTimelineOnly();
            StatusText = $"Media event deleted: tick {mediaEvent.Tick}, lane {lane}";
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

    public string MediaSummaryText => $"{MediaAssetEntries.Count} media";

    public string EventSummaryText => $"{Events.Count} events";

    public string IssueSummaryText => Issues.Count == 0 ? "参照切れなし" : $"{Issues.Count}件の確認事項";

    public string ImportReportSummaryText => $"{ImportReportEntries.Count} import";

    public string PlaybackButtonText => IsPlaying ? "再生中" : _playbackOffsetSeconds > 0 ? "再開" : "再生";

    public string PlaybackLogButtonText => IsPlaybackLogVisible ? "ログ非表示" : "ログ表示";

    public void TogglePlaybackLogVisibility()
    {
        IsPlaybackLogVisible = !IsPlaybackLogVisible;
    }

    public void LaunchMonoGameViewer()
    {
        LaunchMonoGameViewer(startTick: null, endTick: null, replaceExisting: false, "MonoGame Viewer");
    }

    private void LaunchMonoGameViewer(int? startTick, int? endTick, bool replaceExisting, string launchLabel)
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

        if (replaceExisting)
        {
            StopMonoGameViewer();
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

        startInfo.UseShellExecute = false;
        startInfo.WindowStyle = ProcessWindowStyle.Normal;
        startInfo.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
        var settings = LoadStudioSettings();
        startInfo.ArgumentList.Add(_project.HeaderPath);
        startInfo.ArgumentList.Add("--chart");
        startInfo.ArgumentList.Add(_selectedChart.Reference.Id);
        if (startTick is { } startTickValue)
        {
            startInfo.ArgumentList.Add("--start-tick");
            startInfo.ArgumentList.Add(Math.Max(0, startTickValue).ToString(CultureInfo.InvariantCulture));
        }
        if (endTick is { } endTickValue)
        {
            startInfo.ArgumentList.Add("--end-tick");
            startInfo.ArgumentList.Add(Math.Max(0, endTickValue).ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath) && File.Exists(settings.FfmpegPath))
        {
            startInfo.ArgumentList.Add("--ffmpeg");
            startInfo.ArgumentList.Add(settings.FfmpegPath);
        }
        if (settings.DisableBga)
        {
            startInfo.ArgumentList.Add("--no-bga");
        }
        var videoLeadMs = Math.Clamp(settings.VideoLeadMs, 0, 1500);
        startInfo.ArgumentList.Add("--video-lead-ms");
        startInfo.ArgumentList.Add(videoLeadMs.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--audio-volume");
        startInfo.ArgumentList.Add(ClampAudioVolume(settings.AudioVolume).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--master-gain");
        startInfo.ArgumentList.Add(ClampMasterGain(settings.MasterGain).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--limiter-threshold");
        startInfo.ArgumentList.Add(ClampLimiterThreshold(settings.LimiterThreshold).ToString(CultureInfo.InvariantCulture));

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
        StatusText = $"{launchLabel}を起動しました。";
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

        var settings = LoadStudioSettings();
        settings.MonoGameViewerPath = executablePath;
        SaveStudioSettings(settings);
        StatusText = $"MonoGame Viewerのパスを保存しました: {executablePath}";
    }

    public void SetFfmpegPath(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            StatusText = "指定されたffmpeg.exeが見つかりません。";
            return;
        }

        var fileName = Path.GetFileName(executablePath);
        if (!fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "ffmpeg.exeを指定してください。";
            return;
        }

        var settings = LoadStudioSettings();
        settings.FfmpegPath = executablePath;
        SaveStudioSettings(settings);
        StatusText = $"ffmpegのパスを保存しました: {executablePath}";
    }

    public void ToggleMonoGameViewerBga()
    {
        var settings = LoadStudioSettings();
        settings.DisableBga = !settings.DisableBga;
        SaveStudioSettings(settings);
        StatusText = settings.DisableBga
            ? "MonoGame ViewerのBGA読み込みを無効にしました。"
            : "MonoGame ViewerのBGA読み込みを有効にしました。";
    }

    public int GetMonoGameViewerVideoLeadMs()
    {
        return Math.Clamp(LoadStudioSettings().VideoLeadMs, 0, 1500);
    }

    public void SetMonoGameViewerVideoLeadMs(int videoLeadMs)
    {
        var settings = LoadStudioSettings();
        settings.VideoLeadMs = Math.Clamp(videoLeadMs, 0, 1500);
        SaveStudioSettings(settings);
        StatusText = $"MonoGame Viewer video lead saved: {settings.VideoLeadMs} ms";
    }

    public MonoGameViewerAudioSettings GetMonoGameViewerAudioSettings()
    {
        var settings = LoadStudioSettings();
        return new MonoGameViewerAudioSettings(
            ClampAudioVolume(settings.AudioVolume),
            ClampMasterGain(settings.MasterGain),
            ClampLimiterThreshold(settings.LimiterThreshold));
    }

    public void SetMonoGameViewerAudioSettings(float audioVolume, float masterGain, float limiterThreshold)
    {
        var settings = LoadStudioSettings();
        settings.AudioVolume = ClampAudioVolume(audioVolume);
        settings.MasterGain = ClampMasterGain(masterGain);
        settings.LimiterThreshold = ClampLimiterThreshold(limiterThreshold);
        SaveStudioSettings(settings);
        StatusText = $"MonoGame Viewer audio saved: volume={settings.AudioVolume:0.00}, gain={settings.MasterGain:0.00}, limiter={settings.LimiterThreshold:0.00}";
    }

    public void ShowFfmpegDetectionStatus()
    {
        var settings = LoadStudioSettings();
        var configured = ResolveExistingFile(settings.FfmpegPath);
        var environment = ResolveExistingFile(Environment.GetEnvironmentVariable("NBMS_FFMPEG_PATH"));
        var viewerDirectory = Path.GetDirectoryName(FindMonoGameViewerExecutablePath() ?? "") ?? "";
        var bundled = ResolveExistingFile(Path.Combine(viewerDirectory, "ffmpeg.exe"));
        var path = FindExecutableOnPath("ffmpeg.exe");
        var active = configured ?? environment ?? bundled ?? path;
        StatusText = active is null
            ? "ffmpeg not found. Viewer will use WindowsMedia fallback."
            : $"ffmpeg detected: {active}";
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

            for (var attempt = 0; attempt < 180; attempt++)
            {
                await Task.Delay(500);
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
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusText = "MonoGame Viewerのウィンドウを確認しました。";
                    });
                    return;
                }
            }

            process.Refresh();
            if (!process.HasExited && process.MainWindowHandle == IntPtr.Zero)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = "MonoGame Viewerはまだ起動中です。重い譜面の場合は表示まで待機してください。%TEMP%\\NBMS.Studio.MonoGameViewer.logも確認できます。";
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
            if (process.HasExited)
            {
                lock (_monoGameViewerLock)
                {
                    CleanupMonoGameViewerProcess();
                }
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
        using var measure = PerformanceLog.Measure($"Studio.OpenProject file={Path.GetFileName(headerPath)}");
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

    private static string? ResolveExistingFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private static string? FindExecutableOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
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
        public string? FfmpegPath { get; set; }
        public bool DisableBga { get; set; }
        public int VideoLeadMs { get; set; } = 360;
        public float AudioVolume { get; set; } = 0.28f;
        public float MasterGain { get; set; } = 0.82f;
        public float LimiterThreshold { get; set; } = 0.90f;
    }

    public readonly record struct MonoGameViewerAudioSettings(float AudioVolume, float MasterGain, float LimiterThreshold);

    private static float ClampAudioVolume(float value)
    {
        return Math.Clamp(value, 0f, 2f);
    }

    private static float ClampMasterGain(float value)
    {
        return Math.Clamp(value, 0f, 2f);
    }

    private static float ClampLimiterThreshold(float value)
    {
        return Math.Clamp(value, 0.1f, 1f);
    }

    public async Task SaveProjectAsync()
    {
        if (_project is null)
        {
            return;
        }

        using var measure = PerformanceLog.Measure($"Studio.SaveProject file={Path.GetFileName(_project.HeaderPath)}");
        ApplyHeaderFields();
        ApplyNoteRows();
        ApplyAudioRows();
        ApplyMediaRows();
        await _projectService.SaveAsync(_project);

        // 保存後はディスクから全再読み込みせず、現在のViewModel状態から一覧だけ同期する。
        RefreshCollections();
        StatusText = "保存しました。";
    }

    public async Task CreatePackageAsync(string outputPath)
    {
        if (_project is null)
        {
            return;
        }

        await SaveProjectAsync();
        if (_project is null)
        {
            return;
        }

        var issues = await _packageValidatorService.ValidateProjectAsync(_project);
        ReplacePackageIssues(issues);
        if (issues.Any(issue => IsErrorSeverity(issue.Severity)))
        {
            StatusText = $"Package validatorでErrorがあるため作成を中止しました: {issues.Count(issue => IsErrorSeverity(issue.Severity))}件";
            return;
        }

        await _packageService.CreatePackageAsync(_project, outputPath);
        StatusText = $"配布パッケージを作成しました: {outputPath}";
    }

    public async Task ExportSelectedChartToBmsAsync(string outputPath)
    {
        if (_project is null || _selectedChart is null)
        {
            StatusText = "BMS互換出力できる譜面が開かれていません。";
            return;
        }

        ApplyHeaderFields();
        ApplyNoteRows();
        ApplyAudioRows();
        ApplyMediaRows();

        var audioFileNames = BuildBmsExportAudioFileNames(_project);
        var mediaFileNames = BuildBmsExportMediaFileNames(_project);
        var result = _bmsExportService.Export(_project.Header, _selectedChart.Chart, audioFileNames, mediaFileNames);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, result.BmsText, System.Text.Encoding.UTF8);
        if (_project.AudioManifest is not null)
        {
            await _audioArchiveService.ExtractAudioFilesAsync(
                ResolveAudioArchivePath(_project),
                _project.AudioManifest,
                Path.GetDirectoryName(outputPath)!,
                result.AudioFileNames);
        }
        if (_project.MediaManifest is not null && _project.Header.Media is not null)
        {
            await _mediaArchiveService.ExtractMediaFilesAsync(
                ResolveMediaArchivePath(_project),
                _project.MediaManifest,
                Path.GetDirectoryName(outputPath)!,
                result.MediaFileNames);
        }

        var reportPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $"{Path.GetFileNameWithoutExtension(outputPath)}-loss-report.md");
        await File.WriteAllTextAsync(reportPath, result.LossReportMarkdown, System.Text.Encoding.UTF8);

        StatusText = result.Issues.Count > 0
            ? $"BMS互換出力完了: {outputPath} / loss report {result.Issues.Count}件"
            : $"BMS互換出力完了: {outputPath}";
    }

    private static Dictionary<string, string> BuildBmsExportAudioFileNames(NbmsProject project)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (project.AudioManifest is null)
        {
            return result;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in project.AudioManifest.Entries)
        {
            var extension = Path.GetExtension(entry.Path);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ResolveBmsExportAudioExtension(entry.Codec);
            }

            var baseName = SanitizeBmsExportFileName(entry.AudioId);
            var fileName = EnsureUniqueBmsExportFileName($"{baseName}{extension}", used);
            result[entry.AudioId] = fileName;
        }

        return result;
    }

    private static Dictionary<string, string> BuildBmsExportMediaFileNames(NbmsProject project)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (project.MediaManifest is null)
        {
            return result;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in project.MediaManifest.Entries)
        {
            var extension = Path.GetExtension(entry.Path);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ResolveBmsExportMediaExtension(entry.MimeType, entry.Type);
            }

            var baseName = SanitizeBmsExportFileName(entry.MediaId);
            var fileName = EnsureUniqueBmsExportFileName($"{baseName}{extension}", used);
            result[entry.MediaId] = fileName;
        }

        return result;
    }

    private static string ResolveBmsExportMediaExtension(string mimeType, string type)
    {
        var normalizedMime = mimeType.ToLowerInvariant();
        if (normalizedMime.Contains("png", StringComparison.Ordinal))
        {
            return ".png";
        }

        if (normalizedMime.Contains("jpeg", StringComparison.Ordinal) || normalizedMime.Contains("jpg", StringComparison.Ordinal))
        {
            return ".jpg";
        }

        if (normalizedMime.Contains("gif", StringComparison.Ordinal))
        {
            return ".gif";
        }

        if (normalizedMime.Contains("mpeg", StringComparison.Ordinal))
        {
            return ".mpg";
        }

        if (normalizedMime.Contains("mp4", StringComparison.Ordinal))
        {
            return ".mp4";
        }

        if (normalizedMime.Contains("avi", StringComparison.Ordinal))
        {
            return ".avi";
        }

        return type.Equals("video", StringComparison.OrdinalIgnoreCase) ? ".mpg" : ".bmp";
    }

    private static string ResolveBmsExportAudioExtension(string codec)
    {
        return codec.ToLowerInvariant() switch
        {
            "flac" => ".flac",
            "ogg-vorbis" => ".ogg",
            "mp3" => ".mp3",
            _ => ".wav"
        };
    }

    private static string SanitizeBmsExportFileName(string value)
    {
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray();
        var result = new string(chars).Trim('_', '.');
        return string.IsNullOrWhiteSpace(result) ? "audio" : result;
    }

    private static string EnsureUniqueBmsExportFileName(string fileName, HashSet<string> used)
    {
        var candidate = fileName;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var index = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseName}_{index}{extension}";
            index++;
        }

        return candidate;
    }

    public async Task ConvertBmsAsync(string bmsPath, string outputDirectory)
    {
        StopPlayback();
        StatusText = "BMSをNBMSへ変換しています...";

        var result = await _bmsConversionService.ConvertAsync(bmsPath, outputDirectory);
        ReplaceImportReport([result]);
        var reportPath = await WriteBmsImportReportAsync(outputDirectory, [result]);
        await OpenProjectAsync(result.HeaderPath);
        StatusText = result.ImportReport.Count > 0
            ? $"BMS変換完了: {result.HeaderPath} / import report {result.ImportReport.Count}件 ({reportPath})"
            : $"BMS変換完了: {result.HeaderPath}";
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
        ReplaceImportReport(result.Results);
        var reportPath = await WriteBmsImportReportAsync(outputDirectory, result.Results);

        await OpenProjectAsync(first.HeaderPath);
        var reportCount = result.Results.Sum(item => item.ImportReport.Count);
        StatusText = reportCount > 0
            ? $"BMS一括変換完了: {result.Results.Count}件 / import report {reportCount}件 ({reportPath})"
            : $"BMS一括変換完了: {result.Results.Count}件";
    }

    private static async Task<string> WriteBmsImportReportAsync(
        string outputDirectory,
        IReadOnlyList<BmsConversionResult> results)
    {
        Directory.CreateDirectory(outputDirectory);
        var reportPath = Path.Combine(outputDirectory, "import-report.md");
        var lines = new List<string>
        {
            "# BMS Import Report",
            "",
            $"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}",
            ""
        };

        foreach (var result in results)
        {
            lines.Add($"## {Path.GetFileName(result.ChartPath)}");
            lines.Add("");
            lines.Add($"- Header: `{result.HeaderPath}`");
            lines.Add($"- Chart: `{result.ChartPath}`");
            lines.Add($"- Audio: `{result.AudioPath}`");
            if (result.ImportReport.Count == 0)
            {
                lines.Add("- No import report items.");
            }
            else
            {
                foreach (var item in result.ImportReport)
                {
                    lines.Add($"- {item}");
                }
            }

            lines.Add("");
        }

        await File.WriteAllLinesAsync(reportPath, lines, System.Text.Encoding.UTF8);
        return reportPath;
    }

    private void ReplaceImportReport(IReadOnlyList<BmsConversionResult> results)
    {
        ImportReportEntries.Clear();
        foreach (var result in results)
        {
            var chartName = Path.GetFileName(result.ChartPath);
            if (result.ImportReport.Count == 0)
            {
                ImportReportEntries.Add(new ImportReportRow
                {
                    Chart = chartName,
                    Message = "No import report items."
                });
                continue;
            }

            foreach (var item in result.ImportReport)
            {
                ImportReportEntries.Add(new ImportReportRow
                {
                    Chart = chartName,
                    Message = item
                });
            }
        }

        OnPropertyChanged(nameof(ImportReportSummaryText));
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

    private void JumpToIssuePosition(IssueRow issue)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var tick = issue.TargetTick;
        var lane = issue.TargetLane;
        if (tick is null && issue.Index is { } index)
        {
            if (issue.ReferenceType == "note" &&
                index >= 0 &&
                index < _selectedChart.Chart.Notes.Count)
            {
                var note = _selectedChart.Chart.Notes[index];
                tick = note.Tick;
                lane = note.Lane;
            }
            else if (issue.ReferenceType == "backgroundAudio" &&
                     index >= 0 &&
                     index < _selectedChart.Chart.BackgroundAudio.Count)
            {
                var bgm = _selectedChart.Chart.BackgroundAudio[index];
                tick = bgm.Tick;
                lane = bgm.Lane ?? "background1";
            }
        }

        if (tick is null)
        {
            StatusText = $"Issue targetへ移動できませんでした: {issue.TargetReference}";
            return;
        }

        EditorSelectedTick = Math.Max(0, tick.Value);
        EditorSelectedLane = string.IsNullOrWhiteSpace(lane) ? "key1" : lane;
        EditorTimelineFocusTick = EditorSelectedTick;
        DraftTick = EditorSelectedTick.ToString(CultureInfo.InvariantCulture);
        DraftLane = EditorSelectedLane;
        SelectedNote = Notes.FirstOrDefault(note =>
            note.Tick == EditorSelectedTick &&
            string.Equals(note.Lane, EditorSelectedLane, StringComparison.OrdinalIgnoreCase));
        StatusText = $"Issue targetへ移動しました: tick {EditorSelectedTick}, lane {EditorSelectedLane}";
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

    private void LoadInspectorDraftFromEventRow(EventRow row)
    {
        DraftTick = row.Tick.ToString();
        DraftLane = row.Lane;
        DraftType = row.Type;
        DraftAudioId = "";
        DraftDurationTicks = "";

        if (IsTimingEditorLane(row.Lane) && FindTimingEventNear(row.Tick, row.Lane) is { } timing)
        {
            DraftType = timing.Type;
            DraftAudioId = timing.Value?.ToString("0.###", CultureInfo.InvariantCulture) ?? timing.Event ?? "";
            DraftDurationTicks = timing.DurationTicks?.ToString(CultureInfo.InvariantCulture) ?? "";
            return;
        }

        if (IsMediaEditorLane(row.Lane) && FindMediaEventNear(row.Tick, row.Lane) is { } mediaEvent)
        {
            DraftType = mediaEvent.Type;
            DraftAudioId = mediaEvent.MediaId;
            DraftDurationTicks = (mediaEvent.Layer ?? 0).ToString(CultureInfo.InvariantCulture);
        }
    }

    private void NotifyInspectorChanged()
    {
        OnPropertyChanged(nameof(InspectorKindText));
    }

    private int SnapEditorTick(int tick)
    {
        if (!IsEditorSnapEnabled)
        {
            return Math.Max(0, tick);
        }

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

    private void AddTimelineMediaEventFromHit(int tick, string lane)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var mediaId = ResolveEditorMediaId();
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            StatusText = "追加するmediaIdがありません。先にMedia assetを選択してください。";
            return;
        }

        var mediaEvent = new MediaEvent
        {
            Tick = tick,
            MediaId = mediaId,
            Type = ResolveMediaTypeForLane(lane, mediaId),
            Layer = lane == "layer" ? ResolveNextLayerAtTick(tick) : 0
        };
        _selectedChart.Chart.MediaEvents.Add(mediaEvent);
        SelectedNote = null;
        EditorSelectedTick = tick;
        EditorSelectedLane = lane;
        PushEditorCommand(new EditorCommand(
            "Add Media Event",
            () =>
            {
                _selectedChart.Chart.MediaEvents.Remove(mediaEvent);
                ClearEditorSelection();
                RefreshMediaRows();
                RefreshTimelineOnly();
            },
            () =>
            {
                _selectedChart.Chart.MediaEvents.Add(mediaEvent);
                SelectedNote = null;
                EditorSelectedTick = mediaEvent.Tick;
                EditorSelectedLane = ResolveMediaLane(mediaEvent.Type, mediaEvent.Layer);
                RefreshMediaRows();
                RefreshTimelineOnly();
            }));
        RefreshMediaRows();
        RefreshTimelineOnly();
        StatusText = $"Media event added: {lane} tick {tick}, media {mediaId}";
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

    private static bool IsMediaEditorLane(string lane)
    {
        return lane is "bga" or "layer" or "poor";
    }

    private bool IsSelectedObjectNear(int tick, string lane)
    {
        var halfGrid = ResolveEditorGridTicks() / 2;
        return Timeline
            .Where(row => Math.Abs(row.Tick - tick) <= halfGrid)
            .Where(row => string.Equals(row.Lane, lane, StringComparison.OrdinalIgnoreCase))
            .Select(CreateTimelineObjectKey)
            .Any(key => EditorSelectedObjectKeys.Contains(key));
    }

    private List<TimelineRow> ResolveSelectedTimelineRows()
    {
        return Timeline
            .Where(row => EditorSelectedObjectKeys.Contains(CreateTimelineObjectKey(row)))
            .Select(CloneTimelineRow)
            .ToList();
    }

    private bool TryCreateClipboardItem(
        TimelineRow row,
        int anchorTick,
        int anchorLaneIndex,
        IReadOnlyList<string> lanes,
        out EditorClipboardItem item)
    {
        item = default!;
        var laneIndex = FindLaneIndex(lanes, row.Lane);
        if (laneIndex < 0)
        {
            return false;
        }

        var tickOffset = row.Tick - anchorTick;
        var laneOffset = laneIndex - anchorLaneIndex;
        if (row.Kind == "Note")
        {
            var note = Notes.FirstOrDefault(candidate =>
                candidate.Tick == row.Tick &&
                string.Equals(candidate.Lane, row.Lane, StringComparison.OrdinalIgnoreCase) &&
                string.Equals($"{candidate.Type} {candidate.AudioId}", row.Detail, StringComparison.Ordinal));
            if (note is null)
            {
                return false;
            }

            item = EditorClipboardItem.FromNote(note, tickOffset, laneOffset);
            return true;
        }

        if (row.Kind == "BGM")
        {
            var bgm = _selectedChart?.Chart.BackgroundAudio.FirstOrDefault(candidate =>
                candidate.Tick == row.Tick &&
                string.Equals(candidate.Lane ?? "background1", row.Lane, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.AudioId, row.Detail, StringComparison.Ordinal));
            if (bgm is null)
            {
                return false;
            }

            item = EditorClipboardItem.FromBgm(bgm, tickOffset, laneOffset);
            return true;
        }

        if (row.Kind == "Visual")
        {
            var mediaEvent = FindMediaEventExact(row);
            if (mediaEvent is null)
            {
                return false;
            }

            item = EditorClipboardItem.FromMedia(mediaEvent, row.Lane, tickOffset, laneOffset);
            return true;
        }

        if (row.Kind == "Timing")
        {
            var timing = FindTimingEventExact(row);
            if (timing is null)
            {
                return false;
            }

            item = EditorClipboardItem.FromTiming(timing, row.Lane, tickOffset, laneOffset);
            return true;
        }

        return false;
    }

    private void MoveSelectedTimelineObjects(int fromTick, string fromLane, int toTick, string toLane)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var selectedRows = Timeline
            .Where(row => EditorSelectedObjectKeys.Contains(CreateTimelineObjectKey(row)))
            .Select(CloneTimelineRow)
            .ToList();
        if (selectedRows.Count == 0)
        {
            StatusText = "複数移動対象がありません。";
            return;
        }

        var tickDelta = toTick - fromTick;
        var lanes = ResolveEditorLaneOrder();
        var laneDelta = ResolveLaneDelta(lanes, fromLane, toLane);
        if (laneDelta is null)
        {
            StatusText = "移動先レーンを解決できません。";
            return;
        }

        var targets = new List<TimelineMoveTarget>();
        foreach (var row in selectedRows)
        {
            var newTick = Math.Max(0, SnapEditorTick(row.Tick + tickDelta));
            var newLane = ResolveShiftedLane(lanes, row.Lane, laneDelta.Value);
            if (string.IsNullOrWhiteSpace(newLane))
            {
                StatusText = $"移動先レーンを解決できません: {row.Lane}";
                return;
            }

            if (!TryCreateMoveTarget(row, newTick, newLane, out var target, out var error))
            {
                StatusText = error;
                return;
            }

            targets.Add(target);
        }

        ApplyTimelineMoveTargets(targets, useNewValues: true);
        PushEditorCommand(new EditorCommand(
            "Move Multiple Objects",
            () => ApplyTimelineMoveTargets(targets, useNewValues: false),
            () => ApplyTimelineMoveTargets(targets, useNewValues: true)));
        StatusText = $"Moved {targets.Count} objects";
    }

    private bool TryResizeSelectedLongNoteEnd(int fromTick, string fromLane, int toTick, string toLane)
    {
        if (SelectedNote is not { DurationTicks: > 0 } note ||
            !string.Equals(note.Lane, fromLane, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fromLane, toLane, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var endTick = note.Tick + note.DurationTicks.Value;
        if (Math.Abs(endTick - fromTick) > ResolveEditorGridTicks())
        {
            return false;
        }

        var oldDuration = note.DurationTicks;
        var newDuration = Math.Max(ResolveEditorGridTicks(), SnapEditorTick(toTick) - note.Tick);
        if (oldDuration == newDuration)
        {
            return true;
        }

        note.DurationTicks = newDuration;
        DraftDurationTicks = newDuration.ToString();
        PushEditorCommand(new EditorCommand(
            "Resize LN",
            () =>
            {
                note.DurationTicks = oldDuration;
                SelectedNote = note;
                DraftDurationTicks = oldDuration?.ToString() ?? "";
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                note.DurationTicks = newDuration;
                SelectedNote = note;
                DraftDurationTicks = newDuration.ToString();
                ApplyNoteRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        ApplyNoteRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"LN end resized: tick {note.Tick}, duration {newDuration}";
        return true;
    }

    private bool TryCreateMoveTarget(TimelineRow row, int newTick, string newLane, out TimelineMoveTarget target, out string error)
    {
        target = default!;
        error = "";

        if (row.Kind == "Note")
        {
            if (newLane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
            {
                var sourceNote = Notes.FirstOrDefault(item =>
                    item.Tick == row.Tick &&
                    string.Equals(item.Lane, row.Lane, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals($"{item.Type} {item.AudioId}", row.Detail, StringComparison.Ordinal));
                if (sourceNote is null)
                {
                    error = $"Note move target not found: {row.Lane}@{row.Tick}";
                    return false;
                }

                var bgmLane = ResolveBackgroundLaneForTick(newTick, newLane);
                var bgm = new BackgroundAudioEvent
                {
                    Tick = newTick,
                    Lane = bgmLane,
                    AudioId = sourceNote.AudioId
                };
                target = new TimelineMoveTarget("NoteToBGM", sourceNote, row.Tick, row.Lane, newTick, bgmLane, ConvertedTarget: bgm);
                return true;
            }

            if (!IsPlayableEditorLane(newLane))
            {
                error = "Noteの複数移動先はプレイレーンにしてください。";
                return false;
            }

            var note = Notes.FirstOrDefault(item =>
                item.Tick == row.Tick &&
                string.Equals(item.Lane, row.Lane, StringComparison.OrdinalIgnoreCase) &&
                string.Equals($"{item.Type} {item.AudioId}", row.Detail, StringComparison.Ordinal));
            if (note is null)
            {
                error = $"Note移動対象が見つかりません: {row.Lane}@{row.Tick}";
                return false;
            }

            target = new TimelineMoveTarget(row.Kind, note, row.Tick, row.Lane, newTick, newLane);
            return true;
        }

        if (row.Kind == "BGM")
        {
            if (IsPlayableEditorLane(newLane))
            {
                var sourceBgm = _selectedChart?.Chart.BackgroundAudio.FirstOrDefault(item =>
                    item.Tick == row.Tick &&
                    string.Equals(item.Lane ?? "background1", row.Lane, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.AudioId, row.Detail, StringComparison.Ordinal));
                if (sourceBgm is null)
                {
                    error = $"BGM move target not found: {row.Lane}@{row.Tick}";
                    return false;
                }

                var note = new NoteRow
                {
                    Tick = newTick,
                    Lane = newLane,
                    Type = "tap",
                    AudioId = sourceBgm.AudioId
                };
                target = new TimelineMoveTarget("BGMToNote", sourceBgm, row.Tick, row.Lane, newTick, newLane, ConvertedTarget: note);
                return true;
            }

            if (!newLane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
            {
                error = "BGMの複数移動先はbackgroundレーンにしてください。";
                return false;
            }

            var bgm = _selectedChart?.Chart.BackgroundAudio.FirstOrDefault(item =>
                item.Tick == row.Tick &&
                string.Equals(item.Lane ?? "background1", row.Lane, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.AudioId, row.Detail, StringComparison.Ordinal));
            if (bgm is null)
            {
                error = $"BGM移動対象が見つかりません: {row.Lane}@{row.Tick}";
                return false;
            }

            target = new TimelineMoveTarget(row.Kind, bgm, row.Tick, row.Lane, newTick, newLane);
            return true;
        }

        if (row.Kind == "Timing")
        {
            if (!string.Equals(row.Lane, newLane, StringComparison.OrdinalIgnoreCase))
            {
                error = "Timing/Eventの複数移動は同じイベントレーン内で行ってください。";
                return false;
            }

            var timing = FindTimingEventExact(row);
            if (timing is null)
            {
                error = $"Timing移動対象が見つかりません: {row.Lane}@{row.Tick}";
                return false;
            }

            target = new TimelineMoveTarget(row.Kind, timing, row.Tick, row.Lane, newTick, row.Lane);
            return true;
        }

        if (row.Kind == "Visual")
        {
            if (!IsMediaEditorLane(newLane))
            {
                error = "Media eventの複数移動先はBGA/LAYER/POORレーンにしてください。";
                return false;
            }

            var mediaEvent = FindMediaEventExact(row);
            if (mediaEvent is null)
            {
                error = $"Media event移動対象が見つかりません: {row.Lane}@{row.Tick}";
                return false;
            }

            var newType = ResolveMediaTypeForLane(newLane, mediaEvent.MediaId);
            var newLayer = newLane == "layer" ? Math.Max(1, mediaEvent.Layer ?? 1) : 0;
            target = new TimelineMoveTarget(
                row.Kind,
                mediaEvent,
                row.Tick,
                row.Lane,
                newTick,
                newLane,
                mediaEvent.Type,
                mediaEvent.Layer,
                newType,
                newLayer);
            return true;
        }

        error = $"未対応の移動対象です: {row.Kind}";
        return false;
    }

    private void ApplyTimelineMoveTargets(IReadOnlyList<TimelineMoveTarget> targets, bool useNewValues)
    {
        foreach (var target in targets)
        {
            var tick = useNewValues ? target.NewTick : target.OldTick;
            var lane = useNewValues ? target.NewLane : target.OldLane;
            switch (target.Target)
            {
                case NoteRow note:
                    if (target.Kind == "NoteToBGM" && target.ConvertedTarget is BackgroundAudioEvent convertedBgm)
                    {
                        if (useNewValues)
                        {
                            Notes.Remove(note);
                            if (!_selectedChart!.Chart.BackgroundAudio.Contains(convertedBgm))
                            {
                                _selectedChart.Chart.BackgroundAudio.Add(convertedBgm);
                            }
                        }
                        else
                        {
                            _selectedChart!.Chart.BackgroundAudio.Remove(convertedBgm);
                            if (!Notes.Contains(note))
                            {
                                Notes.Add(note);
                            }

                            note.Tick = tick;
                            note.Lane = lane;
                        }

                        break;
                    }

                    note.Tick = tick;
                    note.Lane = lane;
                    break;
                case BackgroundAudioEvent bgm:
                    if (target.Kind == "BGMToNote" && target.ConvertedTarget is NoteRow convertedNote)
                    {
                        if (useNewValues)
                        {
                            _selectedChart!.Chart.BackgroundAudio.Remove(bgm);
                            if (!Notes.Contains(convertedNote))
                            {
                                Notes.Add(convertedNote);
                            }
                        }
                        else
                        {
                            Notes.Remove(convertedNote);
                            if (!_selectedChart!.Chart.BackgroundAudio.Contains(bgm))
                            {
                                _selectedChart.Chart.BackgroundAudio.Add(bgm);
                            }

                            bgm.Tick = tick;
                            bgm.Lane = lane;
                        }

                        break;
                    }

                    bgm.Tick = tick;
                    bgm.Lane = lane;
                    break;
                case TimingEvent timing:
                    timing.Tick = tick;
                    break;
                case MediaEvent mediaEvent:
                    mediaEvent.Tick = tick;
                    mediaEvent.Type = useNewValues ? target.NewType ?? mediaEvent.Type : target.OldType ?? mediaEvent.Type;
                    mediaEvent.Layer = useNewValues ? target.NewLayer : target.OldLayer;
                    break;
            }
        }

        ApplyNoteRows();
        RefreshMediaRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        SetEditorSelectionKeys(targets.Select(target => CreateMovedObjectKey(target, useNewValues)));
        if (targets.Count > 0)
        {
            EditorSelectedTick = useNewValues ? targets[0].NewTick : targets[0].OldTick;
            EditorSelectedLane = useNewValues ? targets[0].NewLane : targets[0].OldLane;
        }
    }

    private string CreateMovedObjectKey(TimelineMoveTarget target, bool useNewValues)
    {
        var tick = useNewValues ? target.NewTick : target.OldTick;
        var lane = useNewValues ? target.NewLane : target.OldLane;
        return target.Target switch
        {
            NoteRow when target.Kind == "NoteToBGM" && useNewValues && target.ConvertedTarget is BackgroundAudioEvent bgm =>
                CreateTimelineObjectKey("BGM", tick, lane, bgm.AudioId),
            BackgroundAudioEvent when target.Kind == "BGMToNote" && useNewValues && target.ConvertedTarget is NoteRow note =>
                CreateTimelineObjectKey("Note", tick, lane, $"{note.Type} {note.AudioId}"),
            NoteRow note => CreateTimelineObjectKey("Note", tick, lane, $"{note.Type} {note.AudioId}"),
            BackgroundAudioEvent bgm => CreateTimelineObjectKey("BGM", tick, lane, bgm.AudioId),
            TimingEvent timing => CreateTimelineObjectKey("Timing", tick, lane, DescribeTimingForSelection(timing)),
            MediaEvent mediaEvent => CreateTimelineObjectKey("Visual", tick, lane, $"{mediaEvent.Type} {mediaEvent.MediaId}"),
            _ => ""
        };
    }

    private TimingEvent? FindTimingEventExact(TimelineRow row)
    {
        if (_selectedChart is null)
        {
            return null;
        }

        return _selectedChart.Chart.Timing.FirstOrDefault(timing =>
            timing.Tick == row.Tick &&
            string.Equals(ResolveTimingLane(timing), row.Lane, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(DescribeTimingForSelection(timing), row.Detail, StringComparison.Ordinal));
    }

    private MediaEvent? FindMediaEventExact(TimelineRow row)
    {
        if (_selectedChart is null)
        {
            return null;
        }

        return _selectedChart.Chart.MediaEvents.FirstOrDefault(mediaEvent =>
            mediaEvent.Tick == row.Tick &&
            string.Equals(ResolveMediaLane(mediaEvent.Type, mediaEvent.Layer), row.Lane, StringComparison.OrdinalIgnoreCase) &&
            string.Equals($"{mediaEvent.Type} {mediaEvent.MediaId}", row.Detail, StringComparison.Ordinal));
    }

    private List<string> ResolveEditorLaneOrder()
    {
        var hasDouble = Timeline.Any(row => row.Lane is "scratch2" or "key8" or "key9" or "key10" or "key11" or "key12" or "key13" or "key14");
        var lanes = hasDouble
            ? new List<string>
            {
                "speed", "scroll", "bpm", "stop", "measure", "bga", "layer", "poor",
                "scratch", "key1", "key2", "key3", "key4", "key5", "key6", "key7",
                "key8", "key9", "key10", "key11", "key12", "key13", "key14", "scratch2"
            }
            : new List<string>
            {
                "speed", "scroll", "bpm", "stop", "measure", "bga", "layer", "poor",
                "scratch", "key1", "key2", "key3", "key4", "key5", "key6", "key7"
            };

        var maxBackground = Timeline
            .Select(row => ResolveBackgroundLaneNumber(row.Lane))
            .DefaultIfEmpty(8)
            .Max();
        for (var index = 1; index <= Math.Max(8, maxBackground); index++)
        {
            lanes.Add($"background{index}");
        }

        return lanes;
    }

    private static int? ResolveLaneDelta(IReadOnlyList<string> lanes, string fromLane, string toLane)
    {
        var fromIndex = FindLaneIndex(lanes, fromLane);
        var toIndex = FindLaneIndex(lanes, toLane);
        return fromIndex < 0 || toIndex < 0 ? null : toIndex - fromIndex;
    }

    private static string ResolveShiftedLane(IReadOnlyList<string> lanes, string lane, int laneDelta)
    {
        var index = FindLaneIndex(lanes, lane);
        var shifted = index + laneDelta;
        return index < 0 || shifted < 0 || shifted >= lanes.Count ? "" : lanes[shifted];
    }

    private static int FindLaneIndex(IReadOnlyList<string> lanes, string lane)
    {
        for (var index = 0; index < lanes.Count; index++)
        {
            if (lanes[index].Equals(lane, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private string ResolveEditorMediaId()
    {
        if (SelectedMediaAssetRow is not null && !string.IsNullOrWhiteSpace(SelectedMediaAssetRow.MediaId))
        {
            return SelectedMediaAssetRow.MediaId;
        }

        if (SelectedMediaRow is not null && !string.IsNullOrWhiteSpace(SelectedMediaRow.MediaId))
        {
            return SelectedMediaRow.MediaId;
        }

        return MediaAssetEntries.FirstOrDefault()?.MediaId ?? "";
    }

    private string ResolveMediaTypeForLane(string lane, string mediaId)
    {
        if (lane == "poor")
        {
            return "poor";
        }

        if (lane == "layer")
        {
            return "layer";
        }

        var assetType = MediaAssetEntries.FirstOrDefault(row => row.MediaId == mediaId)?.Type ?? "";
        return assetType.Equals("video", StringComparison.OrdinalIgnoreCase) ? "video" : "bga";
    }

    private int ResolveNextLayerAtTick(int tick)
    {
        if (_selectedChart is null)
        {
            return 1;
        }

        var maxLayer = _selectedChart.Chart.MediaEvents
            .Where(mediaEvent => mediaEvent.Tick == tick)
            .Select(mediaEvent => mediaEvent.Layer ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(1, maxLayer + 1);
    }

    private static string ResolveMediaLane(string type, int? layer)
    {
        if (type.Equals("poor", StringComparison.OrdinalIgnoreCase))
        {
            return "poor";
        }

        if (type.Equals("layer", StringComparison.OrdinalIgnoreCase) || layer is > 0)
        {
            return "layer";
        }

        return "bga";
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

    private void MoveMediaEvent(MediaEvent mediaEvent, int newTick, string newLane)
    {
        var oldTick = mediaEvent.Tick;
        var oldType = mediaEvent.Type;
        var oldLayer = mediaEvent.Layer;
        var oldLane = ResolveMediaLane(oldType, oldLayer);
        mediaEvent.Tick = newTick;
        mediaEvent.Type = ResolveMediaTypeForLane(newLane, mediaEvent.MediaId);
        mediaEvent.Layer = newLane == "layer" ? Math.Max(1, mediaEvent.Layer ?? 1) : 0;
        SelectedNote = null;
        EditorSelectedTick = newTick;
        EditorSelectedLane = newLane;

        PushEditorCommand(new EditorCommand(
            "Move Media Event",
            () =>
            {
                mediaEvent.Tick = oldTick;
                mediaEvent.Type = oldType;
                mediaEvent.Layer = oldLayer;
                SelectedNote = null;
                EditorSelectedTick = oldTick;
                EditorSelectedLane = oldLane;
                RefreshMediaRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            },
            () =>
            {
                mediaEvent.Tick = newTick;
                mediaEvent.Type = ResolveMediaTypeForLane(newLane, mediaEvent.MediaId);
                mediaEvent.Layer = newLane == "layer" ? Math.Max(1, mediaEvent.Layer ?? 1) : 0;
                SelectedNote = null;
                EditorSelectedTick = newTick;
                EditorSelectedLane = newLane;
                RefreshMediaRows();
                RefreshTimelineOnly(preserveEditorRange: true);
            }));
        RefreshMediaRows();
        RefreshTimelineOnly(preserveEditorRange: true);
        StatusText = $"Moved media event: {oldLane}@{oldTick} -> {newLane}@{newTick}";
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

    private MediaEvent? FindMediaEventNear(int tick, string lane)
    {
        if (_selectedChart is null)
        {
            return null;
        }

        var halfGrid = ResolveEditorGridTicks() / 2;
        return _selectedChart.Chart.MediaEvents
            .Where(mediaEvent => string.Equals(ResolveMediaLane(mediaEvent.Type, mediaEvent.Layer), lane, StringComparison.OrdinalIgnoreCase))
            .OrderBy(mediaEvent => Math.Abs(mediaEvent.Tick - tick))
            .ThenBy(mediaEvent => mediaEvent.Layer ?? 0)
            .FirstOrDefault(mediaEvent => Math.Abs(mediaEvent.Tick - tick) <= halfGrid);
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
        SelectedEventRow = null;
        EditorSelectedTick = -1;
        EditorSelectedLane = "";
        EditorSelectedObjectKeys.Clear();
        NotifyInspectorChanged();
    }

    private void SetEditorSelectionKeys(IEnumerable<string> keys)
    {
        EditorSelectedObjectKeys.Clear();
        foreach (var key in keys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.Ordinal))
        {
            EditorSelectedObjectKeys.Add(key);
        }
    }

    private static string CreateTimelineObjectKey(TimelineRow row)
    {
        return CreateTimelineObjectKey(row.Kind, row.Tick, row.Lane, row.Detail);
    }

    private static string CreateTimelineObjectKey(string kind, int tick, string lane, string detail)
    {
        return $"{kind}|{tick}|{lane}|{detail}";
    }

    private static TimelineRow CloneTimelineRow(TimelineRow row)
    {
        return new TimelineRow
        {
            Tick = row.Tick,
            TimeSeconds = row.TimeSeconds,
            Kind = row.Kind,
            Lane = row.Lane,
            Detail = row.Detail,
            DurationTicks = row.DurationTicks
        };
    }

    private static string DescribeTimingForSelection(TimingEvent timing)
    {
        return timing.Type switch
        {
            "bpm" => $"BPM {timing.Value}",
            "bar" => "Bar",
            "measureLength" => $"MEASURE {timing.Value ?? 1.0}",
            "stop" => $"STOP {timing.DurationTicks} ticks",
            "scroll" => $"SCROLL {timing.Value ?? 1.0}",
            "speed" => $"SPEED {timing.Value ?? 1.0}",
            _ => timing.Type
        };
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

        SelectedNote.Tick = SnapEditorTick(tick);
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

    private void ApplyInspectorTimingEvent(EventRow row)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var timing = FindTimingEventNear(row.Tick, row.Lane);
        if (timing is null)
        {
            StatusText = "Inspector target timing event was not found.";
            return;
        }

        var oldTick = timing.Tick;
        var oldLane = ResolveTimingLane(timing);
        var oldType = timing.Type;
        var oldValue = timing.Value;
        var oldDuration = timing.DurationTicks;
        var oldEvent = timing.Event;
        var oldExtensionId = timing.ExtensionId;

        var newTick = int.TryParse(DraftTick, out var parsedTick) ? SnapEditorTick(parsedTick) : timing.Tick;
        var newLane = string.IsNullOrWhiteSpace(DraftLane) ? row.Lane : DraftLane.Trim();
        var newType = string.IsNullOrWhiteSpace(DraftType) ? timing.Type : DraftType.Trim();
        var newValue = double.TryParse(DraftAudioId, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue)
            ? parsedValue
            : (double?)null;
        var newDuration = int.TryParse(DraftDurationTicks, out var parsedDuration) ? Math.Max(0, parsedDuration) : (int?)null;
        var newEvent = string.IsNullOrWhiteSpace(DraftAudioId) ? timing.Event : DraftAudioId.Trim();

        void ApplyNew()
        {
            timing.Tick = newTick;
            timing.Type = newType;
            timing.Event = newEvent;
            ApplyTimingLane(timing, newLane, newType);
            if (timing.Type == "stop")
            {
                timing.DurationTicks = newDuration ?? timing.DurationTicks;
                timing.Value = null;
            }
            else if (newValue is not null)
            {
                timing.Value = newValue;
                timing.DurationTicks = null;
            }

            EditorSelectedTick = timing.Tick;
            EditorSelectedLane = ResolveTimingLane(timing);
            RefreshTimelineOnly(preserveEditorRange: true);
        }

        void ApplyOld()
        {
            timing.Tick = oldTick;
            timing.Type = oldType;
            timing.Value = oldValue;
            timing.DurationTicks = oldDuration;
            timing.Event = oldEvent;
            timing.ExtensionId = oldExtensionId;
            EditorSelectedTick = oldTick;
            EditorSelectedLane = oldLane;
            RefreshTimelineOnly(preserveEditorRange: true);
        }

        ApplyNew();
        PushEditorCommand(new EditorCommand("Inspector Edit Timing", ApplyOld, ApplyNew));
        StatusText = $"Inspector updated timing: {timing.Type} tick {timing.Tick}";
    }

    private void ApplyInspectorMediaEvent(EventRow row)
    {
        if (_selectedChart is null)
        {
            return;
        }

        var mediaEvent = FindMediaEventNear(row.Tick, row.Lane);
        if (mediaEvent is null)
        {
            StatusText = "Inspector target media event was not found.";
            return;
        }

        var oldTick = mediaEvent.Tick;
        var oldType = mediaEvent.Type;
        var oldMediaId = mediaEvent.MediaId;
        var oldLayer = mediaEvent.Layer;
        var oldLane = ResolveMediaLane(oldType, oldLayer);

        var newTick = int.TryParse(DraftTick, out var parsedTick) ? SnapEditorTick(parsedTick) : mediaEvent.Tick;
        var newLane = string.IsNullOrWhiteSpace(DraftLane) ? row.Lane : DraftLane.Trim();
        var newMediaId = string.IsNullOrWhiteSpace(DraftAudioId) ? mediaEvent.MediaId : DraftAudioId.Trim();
        var newType = string.IsNullOrWhiteSpace(DraftType)
            ? ResolveMediaTypeForLane(newLane, newMediaId)
            : DraftType.Trim();
        var newLayer = int.TryParse(DraftDurationTicks, out var parsedLayer)
            ? Math.Max(newLane == "layer" ? 1 : 0, parsedLayer)
            : (newLane == "layer" ? Math.Max(1, mediaEvent.Layer ?? 1) : Math.Max(0, mediaEvent.Layer ?? 0));

        void ApplyNew()
        {
            mediaEvent.Tick = newTick;
            mediaEvent.MediaId = newMediaId;
            mediaEvent.Type = newType;
            mediaEvent.Layer = newLayer;
            EditorSelectedTick = newTick;
            EditorSelectedLane = ResolveMediaLane(mediaEvent.Type, mediaEvent.Layer);
            RefreshMediaRows();
            RefreshTimelineOnly(preserveEditorRange: true);
        }

        void ApplyOld()
        {
            mediaEvent.Tick = oldTick;
            mediaEvent.MediaId = oldMediaId;
            mediaEvent.Type = oldType;
            mediaEvent.Layer = oldLayer;
            EditorSelectedTick = oldTick;
            EditorSelectedLane = oldLane;
            RefreshMediaRows();
            RefreshTimelineOnly(preserveEditorRange: true);
        }

        ApplyNew();
        PushEditorCommand(new EditorCommand("Inspector Edit Media", ApplyOld, ApplyNew));
        StatusText = $"Inspector updated media: {mediaEvent.MediaId} tick {mediaEvent.Tick}";
    }

    private void ApplyInspectorBgm()
    {
        if (_selectedChart is null)
        {
            return;
        }

        var currentTick = EditorSelectedTick >= 0 ? EditorSelectedTick : 0;
        var bgm = _selectedChart.Chart.BackgroundAudio
            .OrderBy(item => Math.Abs(item.Tick - currentTick))
            .FirstOrDefault(item =>
                string.Equals(item.Lane ?? "background1", EditorSelectedLane, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(item.Tick - currentTick) <= ResolveEditorGridTicks() / 2);
        if (bgm is null)
        {
            StatusText = "Inspector target BGM was not found.";
            return;
        }

        var oldTick = bgm.Tick;
        var oldLane = bgm.Lane;
        var oldAudioId = bgm.AudioId;
        var newTick = int.TryParse(DraftTick, out var parsedTick) ? SnapEditorTick(parsedTick) : bgm.Tick;
        var newLane = string.IsNullOrWhiteSpace(DraftLane) ? bgm.Lane ?? "background1" : DraftLane.Trim();
        var newAudioId = string.IsNullOrWhiteSpace(DraftAudioId) ? bgm.AudioId : DraftAudioId.Trim();

        void ApplyNew()
        {
            bgm.Tick = newTick;
            bgm.Lane = newLane;
            bgm.AudioId = newAudioId;
            EditorSelectedTick = newTick;
            EditorSelectedLane = newLane;
            RefreshTimelineOnly(preserveEditorRange: true);
        }

        void ApplyOld()
        {
            bgm.Tick = oldTick;
            bgm.Lane = oldLane;
            bgm.AudioId = oldAudioId;
            EditorSelectedTick = oldTick;
            EditorSelectedLane = oldLane ?? "background1";
            RefreshTimelineOnly(preserveEditorRange: true);
        }

        ApplyNew();
        PushEditorCommand(new EditorCommand("Inspector Edit BGM", ApplyOld, ApplyNew));
        StatusText = $"Inspector updated BGM: {newAudioId} tick {newTick}";
    }

    public async Task StartPlaybackFromEditorPositionAsync()
    {
        if (_selectedChart is null)
        {
            return;
        }

        var startTick = Math.Max(0, EditorSelectedTick >= 0 ? EditorSelectedTick : EditorTimelineFocusTick);
        StopPlayback();
        LaunchMonoGameViewer(startTick, endTick: null, replaceExisting: true, "MonoGame Viewer From tick");
        await Task.CompletedTask;
    }

    public async Task StartSelectedRangePlaybackAsync()
    {
        if (_selectedChart is null)
        {
            return;
        }

        var selectedRows = ResolveSelectedTimelineRows();
        if (selectedRows.Count == 0)
        {
            StatusText = "範囲再生するオブジェクトが選択されていません。";
            return;
        }

        var startTick = Math.Max(0, selectedRows.Min(row => row.Tick));
        var endTick = selectedRows.Max(row => row.Tick + Math.Max(0, row.DurationTicks ?? 0));
        if (endTick <= startTick)
        {
            endTick = startTick + ResolveEditorGridTicks();
        }

        StopPlayback();
        LaunchMonoGameViewer(startTick, endTick, replaceExisting: true, "MonoGame Viewer Range play");
        await Task.CompletedTask;
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
        _playbackRangeEndSeconds = null;
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

    private async Task RemoveMediaAssetsAsync(IReadOnlyCollection<string> mediaIds, string successMessage)
    {
        if (_project?.MediaManifest is null || mediaIds.Count == 0)
        {
            return;
        }

        try
        {
            await _mediaArchiveService.RemoveMediaEntriesAsync(
                ResolveMediaArchivePath(_project),
                _project.MediaManifest,
                mediaIds);
            RefreshCollections();
            StatusText = successMessage;
        }
        catch (Exception ex)
        {
            StatusText = $"メディア削除に失敗しました: {ex.Message}";
        }
    }

    private void EnsureMediaManifest()
    {
        if (_project is null)
        {
            return;
        }

        _project.MediaManifest ??= new MediaManifest();
        _project.Header.Media ??= new FileReference
        {
            File = "media.nbmg",
            Optional = true
        };
    }

    private bool IsAudioReferenced(string audioId)
    {
        return ResolveReferencedAudioIds().Contains(audioId);
    }

    private bool IsMediaReferenced(string mediaId)
    {
        return ResolveReferencedMediaIds().Contains(mediaId);
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

    private HashSet<string> ResolveReferencedMediaIds()
    {
        if (_project is null)
        {
            return [];
        }

        return _project.Charts
            .SelectMany(chart => chart.Chart.MediaEvents.Select(mediaEvent => mediaEvent.MediaId))
            .Where(mediaId => !string.IsNullOrWhiteSpace(mediaId))
            .ToHashSet(StringComparer.Ordinal);
    }

    private List<AudioEntry> ResolveDuplicateAudioEntries()
    {
        if (_project?.AudioManifest is null)
        {
            return [];
        }

        return _project.AudioManifest.Entries
            .GroupBy(entry => string.IsNullOrWhiteSpace(entry.Hash)
                ? NormalizeArchivePathForComparison(entry.Path)
                : entry.Hash,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group
                .OrderBy(entry => entry.AudioId, StringComparer.Ordinal)
                .Skip(1))
            .ToList();
    }

    private List<MediaAssetEntry> ResolveDuplicateMediaEntries()
    {
        if (_project?.MediaManifest is null)
        {
            return [];
        }

        return _project.MediaManifest.Entries
            .GroupBy(entry => string.IsNullOrWhiteSpace(entry.Hash)
                ? NormalizeArchivePathForComparison(entry.Path)
                : entry.Hash,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group
                .OrderBy(entry => entry.MediaId, StringComparer.Ordinal)
                .Skip(1))
            .ToList();
    }

    private static string ResolveAudioArchivePath(NbmsProject project)
    {
        return Path.GetFullPath(Path.Combine(
            project.RootDirectory,
            project.Header.Audio.File.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ResolveMediaArchivePath(NbmsProject project)
    {
        var mediaFile = project.Header.Media?.File;
        if (string.IsNullOrWhiteSpace(mediaFile))
        {
            mediaFile = "media.nbmg";
        }

        return Path.GetFullPath(Path.Combine(
            project.RootDirectory,
            mediaFile.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string NormalizeAudioId(string value)
    {
        return NormalizeAssetId(value);
    }

    private static string NormalizeAssetId(string value)
    {
        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private static int ScoreAudioRepairCandidate(string missingAudioId, AudioEntry entry)
    {
        var missing = NormalizeCandidateText(missingAudioId);
        var audioId = NormalizeCandidateText(entry.AudioId);
        var pathName = NormalizeCandidateText(Path.GetFileNameWithoutExtension(entry.Path));
        var score = 0;

        if (audioId == missing)
        {
            score += 100;
        }

        if (audioId.Contains(missing, StringComparison.Ordinal) || missing.Contains(audioId, StringComparison.Ordinal))
        {
            score += 40;
        }

        if (pathName.Contains(missing, StringComparison.Ordinal) || missing.Contains(pathName, StringComparison.Ordinal))
        {
            score += 25;
        }

        score += Math.Max(0, 30 - ComputeLevenshteinDistance(missing, audioId));

        if (!string.IsNullOrWhiteSpace(entry.Hash))
        {
            score += 5;
        }

        return score;
    }

    private static string NormalizeCandidateText(string value)
    {
        return new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];

        for (var leftIndex = 0; leftIndex < left.Length; leftIndex++)
        {
            current[0] = leftIndex + 1;
            for (var rightIndex = 0; rightIndex < right.Length; rightIndex++)
            {
                var substitutionCost = left[leftIndex] == right[rightIndex] ? 0 : 1;
                current[rightIndex + 1] = Math.Min(
                    Math.Min(current[rightIndex] + 1, previous[rightIndex + 1] + 1),
                    previous[rightIndex] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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
        using var measure = PerformanceLog.Measure("Studio.RefreshCollections");
        Charts.Clear();
        AudioEntries.Clear();
        MediaEntries.Clear();
        MediaAssetEntries.Clear();
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
        RefreshMediaAssetRows();

        foreach (var issue in _project.Issues)
        {
            Issues.Add(ToIssueRow(issue));
        }

        AddMissingReferenceRepairIssues();
        AddUnusedAudioIssues();
        AddUnusedMediaIssues();
        AddDuplicateAssetIssues();
        AddChartValidationIssues();
        StandardizeIssueRows();

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

    private void RefreshMediaAssetRows()
    {
        MediaAssetEntries.Clear();
        if (_project?.MediaManifest is not null)
        {
            foreach (var entry in _project.MediaManifest.Entries)
            {
                MediaAssetEntries.Add(new MediaAssetRow
                {
                    MediaId = entry.MediaId,
                    Type = entry.Type,
                    MimeType = entry.MimeType,
                    Path = entry.Path,
                    Width = entry.Width,
                    Height = entry.Height,
                    DurationMs = entry.DurationMs
                });
            }
        }

        OnPropertyChanged(nameof(MediaSummaryText));
    }

    private void RefreshTimelineOnly(bool preserveEditorRange = false)
    {
        using var measure = PerformanceLog.Measure($"Studio.RefreshTimelineOnly preserve={preserveEditorRange}");
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
            else if (row.Kind == "Visual")
            {
                Events.Add(new EventRow
                {
                    Tick = row.Tick,
                    TimeSeconds = row.TimeSeconds,
                    Type = row.Lane.ToUpperInvariant(),
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
               row.Detail.StartsWith("MEASURE ", StringComparison.Ordinal);
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
        _playbackEndSeconds = _playbackRangeEndSeconds is { } rangeEnd
            ? Math.Min(_playbackSession.EndSeconds, rangeEnd)
            : _playbackSession.EndSeconds;
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
                Code = "NBMS_AUDIO_UNUSED",
                Source = "Audio",
                TargetReference = $"audio:{entry.AudioId}",
                AssetId = entry.AudioId,
                Message = $"未使用音源: {entry.AudioId} ({entry.Path})"
            });
        }
    }

    private void AddUnusedMediaIssues()
    {
        if (_project?.MediaManifest is null)
        {
            return;
        }

        var referenced = ResolveReferencedMediaIds();
        foreach (var entry in _project.MediaManifest.Entries.Where(entry => !referenced.Contains(entry.MediaId)))
        {
            Issues.Add(new IssueRow
            {
                Severity = "Warning",
                Code = "NBMS_MEDIA_UNUSED",
                Source = "Media",
                TargetReference = $"media:{entry.MediaId}",
                AssetId = entry.MediaId,
                Message = $"未使用media asset: {entry.MediaId} ({entry.Path})"
            });
        }
    }

    private void ReplaceArchiveIssues(string source, IReadOnlyList<ArchiveIntegrityIssue> archiveIssues)
    {
        foreach (var issue in Issues.Where(issue => issue.Source == source).ToList())
        {
            Issues.Remove(issue);
        }

        foreach (var issue in archiveIssues)
        {
            Issues.Add(new IssueRow
            {
                Severity = NormalizeIssueSeverity(issue.Severity),
                Code = source == "AudioArchive"
                    ? "PKG_AUDIO_ARCHIVE_ENTRY_INVALID"
                    : source == "MediaArchive"
                        ? "PKG_MEDIA_ARCHIVE_ENTRY_INVALID"
                        : "PKG_ARCHIVE_ENTRY_INVALID",
                Source = source,
                TargetReference = string.IsNullOrWhiteSpace(issue.AssetId) ? source : $"{source}:{issue.AssetId}",
                AssetId = issue.AssetId,
                Message = string.IsNullOrWhiteSpace(issue.AssetId)
                    ? issue.Message
                    : $"{issue.AssetId} / {issue.Path}: {issue.Message}"
            });
        }

        OnPropertyChanged(nameof(IssueSummaryText));
    }

    private void ReplacePackageIssues(IReadOnlyList<ProjectIssue> packageIssues)
    {
        foreach (var issue in Issues.Where(issue => issue.Code.StartsWith("PKG_", StringComparison.Ordinal)).ToList())
        {
            Issues.Remove(issue);
        }

        foreach (var issue in packageIssues)
        {
            Issues.Add(ToIssueRow(issue));
        }

        StandardizeIssueRows();
        OnPropertyChanged(nameof(IssueSummaryText));
    }

    private static IssueRow ToIssueRow(ProjectIssue issue)
    {
        return new IssueRow
        {
            Severity = NormalizeIssueSeverity(issue.Severity),
            Code = issue.Code,
            Source = issue.Source,
            TargetReference = issue.TargetReference,
            Message = issue.Message
        };
    }

    private static string NormalizeIssueSeverity(string severity)
    {
        return severity.Trim().ToLowerInvariant() switch
        {
            "error" => "Error",
            "warning" => "Warning",
            "warn" => "Warning",
            "info" => "Info",
            _ => "Info"
        };
    }

    private static bool IsErrorSeverity(string severity)
    {
        return NormalizeIssueSeverity(severity) == "Error";
    }

    private void StandardizeIssueRows()
    {
        foreach (var issue in Issues)
        {
            issue.Severity = NormalizeIssueSeverity(issue.Severity);
            if (string.IsNullOrWhiteSpace(issue.Code))
            {
                issue.Code = InferIssueCode(issue);
            }

            if (string.IsNullOrWhiteSpace(issue.TargetReference))
            {
                issue.TargetReference = InferIssueTargetReference(issue);
            }
        }
    }

    private static string InferIssueCode(IssueRow issue)
    {
        var message = issue.Message.ToLowerInvariant();
        if (issue.ReferenceType is "note" or "backgroundAudio" || message.Contains("audioid", StringComparison.OrdinalIgnoreCase))
        {
            return "NBMS_AUDIO_REF_MISSING";
        }

        if (issue.Source == "Audio" && message.Contains("hash", StringComparison.OrdinalIgnoreCase))
        {
            return "NBMS_AUDIO_DUP_HASH";
        }

        if (issue.Source == "Audio" && message.Contains("path", StringComparison.OrdinalIgnoreCase))
        {
            return "NBMS_AUDIO_DUP_PATH";
        }

        if (issue.Source == "Audio" && message.Contains("unused", StringComparison.OrdinalIgnoreCase))
        {
            return "NBMS_AUDIO_UNUSED";
        }

        if (issue.Source == "Audio")
        {
            return "NBMS_AUDIO_DUP_ID";
        }

        if (issue.Source == "Media" && message.Contains("hash", StringComparison.OrdinalIgnoreCase))
        {
            return "NBMS_MEDIA_DUP_HASH";
        }

        if (issue.Source == "Media" && message.Contains("path", StringComparison.OrdinalIgnoreCase))
        {
            return "NBMS_MEDIA_DUP_PATH";
        }

        if (issue.Source == "Media" && message.Contains("unused", StringComparison.OrdinalIgnoreCase))
        {
            return "NBMS_MEDIA_UNUSED";
        }

        if (issue.Source == "Media")
        {
            return "NBMS_MEDIA_DUP_ID";
        }

        return "NBMS_VALIDATION";
    }

    private static string InferIssueTargetReference(IssueRow issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.AssetId))
        {
            return $"{issue.Source}:{issue.AssetId}";
        }

        if (!string.IsNullOrWhiteSpace(issue.ReferenceType) && issue.Index is not null)
        {
            return $"{issue.Source}:{issue.ReferenceType}[{issue.Index}]";
        }

        return issue.Source;
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
                Code = "NBMS_AUDIO_REF_MISSING",
                Source = issue.ChartId,
                TargetReference = $"{issue.ChartId}:{issue.ReferenceType}[{issue.Index}]",
                ReferenceType = issue.ReferenceType,
                Index = issue.Index,
                AudioId = issue.AudioId,
                Message = $"参照切れ: {issue.ReferenceType}[{issue.Index}] {issue.AudioId}"
            });
        }
    }

    private void AddDuplicateAssetIssues()
    {
        AddDuplicateAudioIssues();
        AddDuplicateMediaIssues();

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

    private void AddDuplicateAudioIssues()
    {
        if (_project?.AudioManifest is null)
        {
            return;
        }

        foreach (var group in _project.AudioManifest.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .GroupBy(entry => NormalizeArchivePathForComparison(entry.Path), StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Error",
                Source = "Audio",
                Message = $"重複audio archive path: {group.Key} ({group.Count()} entries)"
            });
        }

        foreach (var group in _project.AudioManifest.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Hash))
            .GroupBy(entry => entry.Hash, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Warning",
                Source = "Audio",
                Message = $"重複音声hash: {FormatDuplicateAssetIds(group.Select(entry => entry.AudioId))}"
            });
        }
    }

    private void AddDuplicateMediaIssues()
    {
        if (_project?.MediaManifest is null)
        {
            return;
        }

        foreach (var group in _project.MediaManifest.Entries.GroupBy(entry => entry.MediaId, StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Error",
                Source = "Media",
                Message = $"重複MediaId: {group.Key} ({group.Count()} entries)"
            });
        }

        foreach (var group in _project.MediaManifest.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .GroupBy(entry => NormalizeArchivePathForComparison(entry.Path), StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Error",
                Source = "Media",
                Message = $"重複media archive path: {group.Key} ({group.Count()} entries)"
            });
        }

        foreach (var group in _project.MediaManifest.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Hash))
            .GroupBy(entry => entry.Hash, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() <= 1)
            {
                continue;
            }

            Issues.Add(new IssueRow
            {
                Severity = "Warning",
                Source = "Media",
                Message = $"重複media hash: {FormatDuplicateAssetIds(group.Select(entry => entry.MediaId))}"
            });
        }
    }

    private static string NormalizeArchivePathForComparison(string path)
    {
        return path.Replace('\\', '/').Trim().ToLowerInvariant();
    }

    private static string FormatDuplicateAssetIds(IEnumerable<string> ids)
    {
        return string.Join(", ", ids.Where(id => !string.IsNullOrWhiteSpace(id)).OrderBy(id => id, StringComparer.Ordinal).Take(8));
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
            AddBmsCompatibilityIssues(loadedChart);
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

    private void AddBmsCompatibilityIssues(LoadedChart loadedChart)
    {
        if (loadedChart.Chart.Metadata is not { ValueKind: JsonValueKind.Object } metadata ||
            !metadata.TryGetProperty("bmsCompat", out var bmsCompat) ||
            bmsCompat.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (TryGetBoolean(bmsCompat, "randomPreserved") || TryGetBoolean(bmsCompat, "branchNotExpanded"))
        {
            Issues.Add(new IssueRow
            {
                Severity = "Warning",
                Code = "NBMS_RANDOM_PRESERVED",
                Source = loadedChart.Reference.Id,
                TargetReference = $"chart:{loadedChart.Reference.Id}:metadata:bmsCompat.randomDirectives",
                Message = "BMSのRANDOM/IF分岐は互換metadataとして保持しています。現在のEditorでは分岐展開や編集は行いません。"
            });
        }

        if (bmsCompat.TryGetProperty("unsupportedDirectives", out var unsupported) &&
            unsupported.ValueKind == JsonValueKind.Array)
        {
            foreach (var directive in unsupported.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                Issues.Add(new IssueRow
                {
                    Severity = "Warning",
                    Code = "NBMS_IMPORT_UNSUPPORTED_DIRECTIVE",
                    Source = loadedChart.Reference.Id,
                    TargetReference = $"chart:{loadedChart.Reference.Id}:metadata:bmsCompat.unsupportedDirectives",
                    Message = $"未対応BMS命令をimport reportへ保持しています: {directive}"
                });
            }
        }
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.True;
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

    private sealed record TimelineMoveTarget(
        string Kind,
        object Target,
        int OldTick,
        string OldLane,
        int NewTick,
        string NewLane,
        string? OldType = null,
        int? OldLayer = null,
        string? NewType = null,
        int? NewLayer = null,
        object? ConvertedTarget = null);

    private sealed record EditorClipboardItem(
        EditorClipboardKind Kind,
        string Lane,
        string Type,
        string AudioId,
        int? DurationTicks,
        int TickOffset,
        int LaneOffset,
        string MediaId = "",
        int? Layer = null,
        double? Value = null,
        string? ExtensionId = null,
        string? Event = null)
    {
        public static EditorClipboardItem FromNote(NoteRow note, int tickOffset, int laneOffset)
        {
            return new EditorClipboardItem(
                EditorClipboardKind.Note,
                note.Lane,
                string.IsNullOrWhiteSpace(note.Type) ? "tap" : note.Type,
                note.AudioId,
                note.DurationTicks,
                tickOffset,
                laneOffset);
        }

        public static EditorClipboardItem FromBgm(BackgroundAudioEvent bgm, int tickOffset, int laneOffset)
        {
            return new EditorClipboardItem(
                EditorClipboardKind.Bgm,
                bgm.Lane ?? "background1",
                "bgm",
                bgm.AudioId,
                null,
                tickOffset,
                laneOffset);
        }

        public static EditorClipboardItem FromMedia(MediaEvent mediaEvent, string lane, int tickOffset, int laneOffset)
        {
            return new EditorClipboardItem(
                EditorClipboardKind.Media,
                lane,
                mediaEvent.Type,
                "",
                null,
                tickOffset,
                laneOffset,
                mediaEvent.MediaId,
                mediaEvent.Layer);
        }

        public static EditorClipboardItem FromTiming(TimingEvent timing, string lane, int tickOffset, int laneOffset)
        {
            return new EditorClipboardItem(
                EditorClipboardKind.Timing,
                lane,
                timing.Type,
                "",
                timing.DurationTicks,
                tickOffset,
                laneOffset,
                Value: timing.Value,
                ExtensionId: timing.ExtensionId,
                Event: timing.Event);
        }
    }

    private enum EditorClipboardKind
    {
        Note,
        Bgm,
        Media,
        Timing
    }

    private sealed record AudioIdReplacementTarget(object Target, string OldAudioId, string NewAudioId);

    public sealed record AudioRepairCandidate(
        string AudioId,
        string Path,
        string Codec,
        int DurationMs,
        int Score)
    {
        public override string ToString()
        {
            var codec = string.IsNullOrWhiteSpace(Codec) ? "unknown" : Codec;
            var duration = DurationMs > 0 ? $" / {DurationMs}ms" : "";
            return $"{AudioId} / {codec}{duration} / score {Score} / {Path}";
        }
    }

    public sealed record TimingEventEditDraft(
        int Tick,
        string Lane,
        string Type,
        double? Value,
        int? DurationTicks,
        string Event);

    public sealed record MediaEventEditDraft(
        int Tick,
        string Lane,
        string MediaId,
        string Type,
        int Layer);
}
