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
    private readonly BmsConversionService _bmsConversionService = new();
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
    private string _playbackDebugText = "session: none";
    private string _playbackLogText = "";
    private string _audioPreparationText = "";
    private bool _isPlaybackLogVisible;

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
        CancelAudioCachePreparation();
        ApplyNoteRows();
        _selectedChart = _project.Charts.FirstOrDefault(chart => chart.Reference.Id == chartId) ?? _project.Charts.FirstOrDefault();
        _audioCache?.Dispose();
        _audioCache = null;
        _audioCacheTask = null;
        _urgentAudioPrepareIds.Clear();
        AudioPreparationText = "";
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

        var audioPath = Path.GetFullPath(Path.Combine(
            project.RootDirectory,
            project.Header.Audio.File.Replace('/', Path.DirectorySeparatorChar)));

        return NbmsAudioCache.CreateEmpty(audioPath, project.AudioManifest);
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

    public void Dispose()
    {
        StopPlayback();
        StopMonoGameViewer();
        _audioPlayer.Dispose();
        _audioCache?.Dispose();
    }
}
