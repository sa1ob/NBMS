using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private readonly BmsConversionService _bmsConversionService = new();
    private readonly DispatcherTimer _playbackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private readonly Stopwatch _playbackClock = new();
    private readonly NbmsAudioPlayer _audioPlayer = new();
    private readonly Queue<string> _playbackLogLines = [];

    private NbmsProject? _project;
    private LoadedChart? _selectedChart;
    private NbmsAudioCache? _audioCache;
    private List<PlaybackEvent> _playbackEvents = [];
    private List<TimelineRow> _playbackTimeline = [];
    private HashSet<(int Tick, long TimeKey)> _playbackStopPoints = [];
    private int _nextPlaybackEventIndex;
    private double _playbackEndSeconds;
    private double _playbackOffsetSeconds;
    private double _lastPlaybackPositionTextSeconds = -1;
    private const double UnknownAudioTailSeconds = 120.0;
    private string _statusText = "NBMS Studio を起動しました。";
    private string _titleText = "";
    private string _artistText = "";
    private string _genreText = "";
    private string _bpmText = "";
    private string _licenseText = "";
    private string _projectPathText = "";
    private NoteRow? _selectedNote;
    private ChartRow? _selectedChartRow;
    private string _draftTick = "0";
    private string _draftLane = "key1";
    private string _draftType = "tap";
    private string _draftAudioId = "";
    private string _draftDurationTicks = "";
    private bool _isPlaying;
    private double _playheadTick;
    private double _editorTimelineHeight = 2400;
    private double _viewerHiSpeed = 1.0;
    private string _playbackPositionText = "00:00.000";
    private string _playbackLogText = "";
    private bool _isPlaybackLogVisible = true;

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
    public ObservableCollection<IssueRow> Issues { get; } = [];
    public ObservableCollection<TimelineRow> Timeline { get; } = [];
    public ObservableCollection<string> Extensions { get; } = [];

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
            if (!SetProperty(ref _selectedNote, value) || value is null)
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

    public string PlaybackLogText
    {
        get => _playbackLogText;
        set => SetProperty(ref _playbackLogText, value);
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

    public bool HasProject => _project is not null;

    public string ChartSummaryText => _selectedChart is null
        ? "譜面未選択"
        : $"{_selectedChart.Reference.Id} / {_selectedChart.Reference.Mode} / Lv.{_selectedChart.Reference.Difficulty}";

    public string AudioSummaryText => $"{AudioEntries.Count} audio";

    public string IssueSummaryText => Issues.Count == 0 ? "参照切れなし" : $"{Issues.Count}件の確認事項";

    public string PlaybackButtonText => IsPlaying ? "再生中" : _playbackOffsetSeconds > 0 ? "再開" : "再生";

    public string PlaybackLogButtonText => IsPlaybackLogVisible ? "ログ非表示" : "ログ表示";

    public void TogglePlaybackLogVisibility()
    {
        IsPlaybackLogVisible = !IsPlaybackLogVisible;
    }

    public async Task OpenProjectAsync(string headerPath)
    {
        StopPlayback();
        _audioCache?.Dispose();
        _audioCache = null;

        _project = await _projectService.OpenHeaderAsync(headerPath);
        _selectedChart = _project.Charts.FirstOrDefault();
        PrepareAudioCache();

        LoadHeaderFields();
        RefreshCollections();
        StatusText = $"読み込み完了: {headerPath}";
        OnPropertyChanged(nameof(HasProject));
    }

    public async Task SaveProjectAsync()
    {
        if (_project is null)
        {
            return;
        }

        ApplyHeaderFields();
        ApplyNoteRows();
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

    public async Task ConvertBmsFolderAsync(string bmsDirectory, string outputDirectory)
    {
        StopPlayback();
        StatusText = "BMSフォルダをNBMSへ一括変換しています...";

        var result = await _bmsConversionService.ConvertFolderAsync(bmsDirectory, outputDirectory);
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
        ApplyNoteRows();
        _selectedChart = _project.Charts.FirstOrDefault(chart => chart.Reference.Id == chartId) ?? _project.Charts.FirstOrDefault();
        _selectedChartRow = Charts.FirstOrDefault(row => row.Id == _selectedChart?.Reference.Id);
        OnPropertyChanged(nameof(SelectedChartRow));
        RefreshNotesAndTimeline();
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

    public void StartPlayback()
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

        if (_audioCache is null)
        {
            AppendPlaybackLog("PLAY", "prepare audio cache");
            PrepareAudioCache();
        }

        if (_audioCache is null)
        {
            StatusText = "音源パックを準備できませんでした。";
            AppendPlaybackLog("PLAY", "start failed: audio cache is null");
            return;
        }

        ApplyNoteRows();
        BuildPlaybackSchedule();
        AppendPlaybackLog(
            "PLAY",
            $"schedule events={_playbackEvents.Count} timeline={_playbackTimeline.Count} end={_playbackEndSeconds:0.000}s offset={_playbackOffsetSeconds:0.000}s");

        if (_playbackEvents.Count == 0)
        {
            StatusText = "再生対象の音声イベントがありません。";
            AppendPlaybackLog("PLAY", "start failed: no playback events");
            return;
        }

        _audioPlayer.StopAll();
        _nextPlaybackEventIndex = _playbackEvents.FindIndex(item => item.TimeSeconds >= _playbackOffsetSeconds);
        if (_nextPlaybackEventIndex < 0)
        {
            _nextPlaybackEventIndex = _playbackEvents.Count;
        }

        PlayheadTick = EstimateTickAt(_playbackOffsetSeconds);
        PlaybackPositionText = TimeSpan.FromSeconds(_playbackOffsetSeconds).ToString(@"mm\:ss\.fff");
        _lastPlaybackPositionTextSeconds = _playbackOffsetSeconds;
        IsPlaying = true;
        StatusText = "再生中です。";
        AppendPlaybackLog("PLAY", $"start next={_nextPlaybackEventIndex} tick={PlayheadTick:0.##}");
        _playbackClock.Restart();
        _playbackTimer.Start();
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
            _playbackOffsetSeconds += _playbackClock.Elapsed.TotalSeconds;
        }

        _playbackClock.Reset();
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
        _audioPlayer.StopAll();
        _nextPlaybackEventIndex = 0;
        _playbackOffsetSeconds = 0;
        IsPlaying = false;
        PlayheadTick = 0;
        PlaybackPositionText = "00:00.000";
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

    private void PrepareAudioCache()
    {
        if (_project?.AudioManifest is null)
        {
            return;
        }

        var audioPath = Path.GetFullPath(Path.Combine(
            _project.RootDirectory,
            _project.Header.Audio.File.Replace('/', Path.DirectorySeparatorChar)));

        _audioCache?.Dispose();
        _audioCache = NbmsAudioCache.Create(audioPath, _project.AudioManifest);
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
        Issues.Clear();
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

        foreach (var issue in _project.Issues)
        {
            Issues.Add(new IssueRow
            {
                Severity = issue.Severity,
                Source = issue.Source,
                Message = issue.Message
            });
        }

        foreach (var module in _projectService.ExtensionRegistry.Modules)
        {
            Extensions.Add($"{module.Id} {module.Version}");
        }

        RefreshNotesAndTimeline();
        OnPropertyChanged(nameof(AudioSummaryText));
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

    private void RefreshTimelineOnly()
    {
        Timeline.Clear();

        if (_selectedChart is null)
        {
            return;
        }

        foreach (var item in _timelineService.BuildTimeline(_selectedChart.Chart))
        {
            Timeline.Add(new TimelineRow
            {
                Tick = item.Tick,
                TimeSeconds = Math.Round(item.TimeSeconds, 3),
                Kind = item.Kind,
                Lane = item.Lane,
                Detail = item.Detail
            });
        }

        RefreshEditorTimelineHeight();
    }

    private void RefreshEditorTimelineHeight()
    {
        var maxTick = Timeline.Count == 0 ? 0 : Timeline.Max(row => row.Tick);
        EditorTimelineHeight = Math.Max(2400, 160 + maxTick * 0.125);
    }

    private void BuildPlaybackSchedule()
    {
        _playbackEvents = [];
        _playbackTimeline = [];
        _playbackStopPoints = [];
        _lastPlaybackPositionTextSeconds = -1;

        if (_selectedChart is null)
        {
            return;
        }

        var timelineItems = _timelineService.BuildTimeline(_selectedChart.Chart);
        _playbackTimeline = timelineItems
            .Select(item => new TimelineRow
            {
                Tick = item.Tick,
                TimeSeconds = item.TimeSeconds,
                Kind = item.Kind,
                Lane = item.Lane,
                Detail = item.Detail
            })
            .OrderBy(row => row.TimeSeconds)
            .ThenBy(row => row.Tick)
            .ToList();
        _playbackStopPoints = _playbackTimeline
            .Where(item =>
                item.Kind.Equals("Timing", StringComparison.OrdinalIgnoreCase) &&
                item.Detail.StartsWith("STOP", StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.Tick, TimeKey: ToPlaybackTimeKey(item.TimeSeconds)))
            .ToHashSet();

        var audioDurations = (_project?.AudioManifest?.Entries ?? [])
            .ToDictionary(entry => entry.AudioId, entry => entry.DurationMs / 1000.0, StringComparer.Ordinal);

        foreach (var item in timelineItems.Where(item => item.Kind == "Note" || item.Kind == "BGM"))
        {
            var audioId = ResolveAudioId(item);
            if (string.IsNullOrWhiteSpace(audioId))
            {
                continue;
            }

            _playbackEvents.Add(new PlaybackEvent
            {
                TimeSeconds = item.TimeSeconds,
                Tick = item.Tick,
                Kind = item.Kind,
                Lane = item.Lane,
                AudioId = audioId,
                DurationSeconds = audioDurations.GetValueOrDefault(audioId)
            });
        }

        _playbackEvents = _playbackEvents
            .OrderBy(item => item.TimeSeconds)
            .ThenBy(item => item.Tick)
            .ToList();

        var lastTimelineSeconds = _playbackTimeline.Count == 0 ? 0 : _playbackTimeline.Max(item => item.TimeSeconds);
        var lastAudioSeconds = _playbackEvents.Count == 0
            ? 0
            : _playbackEvents.Max(item => item.TimeSeconds + ResolvePlaybackTailSeconds(item));
        _playbackEndSeconds = Math.Max(lastTimelineSeconds, lastAudioSeconds);
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
            IsPlaying = false;
            StatusText = $"再生タイマーで例外が発生しました: {ex.Message}";
            AppendPlaybackLog("ERROR", $"{ex.GetType().Name}: {ex.Message}");
            OnPropertyChanged(nameof(PlaybackButtonText));
        }
    }

    private void UpdatePlayback()
    {
        var elapsedSeconds = _playbackOffsetSeconds + _playbackClock.Elapsed.TotalSeconds;
        if (_lastPlaybackPositionTextSeconds < 0 || elapsedSeconds - _lastPlaybackPositionTextSeconds >= 0.05)
        {
            PlaybackPositionText = TimeSpan.FromSeconds(elapsedSeconds).ToString(@"mm\:ss\.fff");
            _lastPlaybackPositionTextSeconds = elapsedSeconds;
        }

        PlayheadTick = EstimateTickAt(elapsedSeconds);

        while (_nextPlaybackEventIndex < _playbackEvents.Count &&
               _playbackEvents[_nextPlaybackEventIndex].TimeSeconds <= elapsedSeconds)
        {
            PlayPlaybackEvent(_playbackEvents[_nextPlaybackEventIndex]);
            _nextPlaybackEventIndex++;
        }

        if (elapsedSeconds >= _playbackEndSeconds)
        {
            FinishPlaybackTimelineOnly();
            StatusText = "再生を終了しました。";
            AppendPlaybackLog("PLAY", $"finish elapsed={elapsedSeconds:0.000}s end={_playbackEndSeconds:0.000}s");
        }
    }

    private void FinishPlaybackTimelineOnly()
    {
        _playbackTimer.Stop();
        _playbackClock.Reset();
        _playbackOffsetSeconds = 0;
        IsPlaying = false;
        _nextPlaybackEventIndex = 0;
        PlayheadTick = 0;
        PlaybackPositionText = "00:00.000";
        _lastPlaybackPositionTextSeconds = -1;
        OnPropertyChanged(nameof(PlaybackButtonText));
        // 末尾の音やフェードアウトを切らないため、ここではミキサーを停止しない。
    }

    private void PlayPlaybackEvent(PlaybackEvent playbackEvent)
    {
        if (_audioCache is null)
        {
            AppendPlaybackLog("AUDIO", $"skip {playbackEvent.AudioId}: audio cache is null");
            return;
        }

        if (!_audioCache.TryGetFilePath(playbackEvent.AudioId, out var filePath))
        {
            AppendPlaybackLog("AUDIO", $"missing {playbackEvent.AudioId}");
            return;
        }
 
        try
        {
            AppendPlaybackLog(
                "EVENT",
                $"{playbackEvent.TimeSeconds:0.000}s tick={playbackEvent.Tick} {playbackEvent.Kind}/{playbackEvent.Lane} {playbackEvent.AudioId}");
            _audioPlayer.PlayOneShot(filePath);
        }
        catch (Exception ex)
        {
            StatusText = $"音声再生に失敗しました: {playbackEvent.AudioId} ({ex.Message})";
            AppendPlaybackLog("ERROR", $"play {playbackEvent.AudioId}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private double EstimateTickAt(double elapsedSeconds)
    {
        if (_playbackTimeline.Count == 0)
        {
            return 0;
        }

        if (elapsedSeconds <= _playbackTimeline[0].TimeSeconds)
        {
            return _playbackTimeline[0].Tick;
        }

        var left = 0;
        var right = _playbackTimeline.Count - 1;
        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            if (_playbackTimeline[middle].TimeSeconds <= elapsedSeconds)
            {
                left = middle + 1;
            }
            else
            {
                right = middle - 1;
            }
        }

        var previousIndex = Math.Clamp(right, 0, _playbackTimeline.Count - 1);
        if (previousIndex >= _playbackTimeline.Count - 1)
        {
            var tail = _playbackTimeline[^1];
            var tailSeconds = Math.Max(0, elapsedSeconds - tail.TimeSeconds);
            var ticksPerSecond = ResolveInitialTicksPerSecond();
            return tail.Tick + tailSeconds * ticksPerSecond;
        }

        var previous = _playbackTimeline[previousIndex];
        var next = _playbackTimeline[previousIndex + 1];
        var span = next.TimeSeconds - previous.TimeSeconds;
        if (span <= 0)
        {
            return next.Tick;
        }

        if (HasStopAt(previous.Tick, previous.TimeSeconds))
        {
            return previous.Tick;
        }

        var ratio = Math.Clamp((elapsedSeconds - previous.TimeSeconds) / span, 0, 1);
        return previous.Tick + (next.Tick - previous.Tick) * ratio;
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

    public void Dispose()
    {
        StopPlayback();
        _audioPlayer.Dispose();
        _audioCache?.Dispose();
    }
}
