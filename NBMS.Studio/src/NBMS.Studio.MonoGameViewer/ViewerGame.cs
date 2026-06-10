using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NBMS.Core.Models;
using NBMS.Core.Services;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Color = Microsoft.Xna.Framework.Color;
using Keys = Microsoft.Xna.Framework.Input.Keys;

namespace NBMS.Studio.MonoGameViewer;

public sealed class ViewerGame : Game
{
    private const int LaneCount7K = 8;
    private const int LaneCount14K = 16;
    private const float LaneWidth = 38f;
    private const float SideGap = 20f;
    private const float JudgeLineOffset = 92f;
    private const float PixelsPerSecondBase = 420f;
    private const float StatusScale = 2f;

    private readonly ViewerOptions _options;
    private readonly GraphicsDeviceManager _graphics;
    private readonly NbmsProjectService _projectService = new();
    private readonly TimelineService _timelineService;
    private SpriteBatch? _spriteBatch;
    private Texture2D? _pixel;
    private NbmsHeader? _header;
    private string _rootDirectory = "";
    private ViewerAudioBank? _audioBank;
    private ViewerAudioPlayer? _audioPlayer;
    private LoadedChart? _chart;
    private MediaManifest? _mediaManifest;
    private readonly Dictionary<string, Texture2D> _mediaTextures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _mediaVideoFiles = new(StringComparer.Ordinal);
    private List<RenderNote> _notes = [];
    private List<AudioScheduleItem> _audioSchedule = [];
    private List<MediaScheduleItem> _mediaSchedule = [];
    private List<double> _measureSeconds = [];
    private List<BpmMarker> _bpmMarkers = [];
    private List<string> _statusLines = [];
    private readonly Queue<string> _logLines = [];
    private readonly Stopwatch _playbackClock = new();
    private KeyboardState _previousKeyboardState;
    private double _playbackSeconds;
    private int _nextAudioIndex;
    private int _nextMediaIndex;
    private int _nextHitIndex;
    private int _combo;
    private float _hiSpeed = 1.5f;
    private bool _isLogVisible;
    private BgaPlayfieldSide _bgaPlayfieldSide = BgaPlayfieldSide.OneP;
    private Texture2D? _currentBgaTexture;
    private string? _currentBgaVideoId;
    private FfmpegVideoDecoder? _videoDecoder;
    private Texture2D? _videoTexture;
    private string? _mediaTempDirectory;
    private FpsLimitMode _fpsLimitMode = FpsLimitMode.Unlimited;
    private double _fpsSampleSeconds;
    private int _fpsSampleFrames;
    private double _displayFps;
    private string _statusText = "Drop NBMS header path as argument.";

    public ViewerGame(ViewerOptions options)
    {
        _options = options;
        _timelineService = new TimelineService(_projectService.ExtensionRegistry);
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 960,
            PreferredBackBufferHeight = 720,
            SynchronizeWithVerticalRetrace = true
        };

        ApplyFpsLimitMode(applyGraphicsChanges: false);
        Window.Title = "NBMS MonoGame Viewer";
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _audioPlayer?.Dispose();
            _audioBank?.Dispose();
            foreach (var texture in _mediaTextures.Values)
            {
                texture.Dispose();
            }

            _videoDecoder?.Dispose();
            _videoTexture?.Dispose();
            DeleteMediaTempDirectory();
        }

        base.Dispose(disposing);
    }

    protected override void Initialize()
    {
        AppendViewerLog("Initialize begin");
        base.Initialize();
        AppendViewerLog($"Initialize after base viewport={GraphicsDevice.Viewport.Width}x{GraphicsDevice.Viewport.Height}");
        LoadProject();
        _playbackClock.Restart();
        AppendViewerLog("Initialize end");
    }

    protected override void LoadContent()
    {
        AppendViewerLog("LoadContent begin");
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);
        AppendViewerLog("LoadContent end");
    }

    protected override void Update(GameTime gameTime)
    {
        var keyboard = Keyboard.GetState();
        if (keyboard.IsKeyDown(Keys.Escape))
        {
            Exit();
            return;
        }

        if (keyboard.IsKeyDown(Keys.D1))
        {
            _hiSpeed = 1.0f;
        }
        else if (keyboard.IsKeyDown(Keys.D2))
        {
            _hiSpeed = 1.5f;
        }
        else if (keyboard.IsKeyDown(Keys.D3))
        {
            _hiSpeed = 2.0f;
        }
        else if (keyboard.IsKeyDown(Keys.D4))
        {
            _hiSpeed = 3.0f;
        }

        if (IsPressed(keyboard, Keys.D7))
        {
            CycleFpsLimitMode();
        }

        if (IsPressed(keyboard, Keys.D5))
        {
            _bgaPlayfieldSide = _bgaPlayfieldSide == BgaPlayfieldSide.OneP
                ? BgaPlayfieldSide.TwoP
                : BgaPlayfieldSide.OneP;
            AddLog($"bga side {_bgaPlayfieldSide}");
        }

        if (IsPressed(keyboard, Keys.D9))
        {
            _isLogVisible = !_isLogVisible;
            if (_isLogVisible)
            {
                AddLog("log on");
            }
        }

        _playbackSeconds = _playbackClock.Elapsed.TotalSeconds;
        QueueUpcomingAudioEvents();
        ProcessDueMediaEvents();
        ProcessDueHitEvents();
        _previousKeyboardState = keyboard;
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        if (_playbackSeconds == 0)
        {
            AppendViewerLog("Draw first frame");
        }

        GraphicsDevice.Clear(new Color(6, 8, 12));
        if (_spriteBatch is null || _pixel is null)
        {
            return;
        }

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        DrawBgaBackground();
        DrawPlayfield();
        DrawStatusOverlay();
        DrawInfoPanels();
        UpdateFrameDiagnostics(gameTime);
        _spriteBatch.End();
        base.Draw(gameTime);
    }

    private void LoadProject()
    {
        AppendViewerLog($"LoadProject header={_options.HeaderPath ?? "<null>"} chart={_options.ChartId ?? "<null>"}");
        if (string.IsNullOrWhiteSpace(_options.HeaderPath) || !File.Exists(_options.HeaderPath))
        {
            _statusText = "NBMS header was not specified.";
            return;
        }

        try
        {
            var headerPath = Path.GetFullPath(_options.HeaderPath);
            var rootDirectory = Path.GetDirectoryName(headerPath)
                ?? throw new InvalidDataException("NBMS header directory was not resolved.");
            var header = ReadJson<NbmsHeader>(headerPath);
            _header = header;
            _rootDirectory = rootDirectory;
            var chartReference = string.IsNullOrWhiteSpace(_options.ChartId)
                ? header.Charts.FirstOrDefault()
                : header.Charts.FirstOrDefault(chart => chart.Id.Equals(_options.ChartId, StringComparison.OrdinalIgnoreCase))
                  ?? header.Charts.FirstOrDefault();

            if (chartReference is null)
            {
                _statusText = "No chart found.";
                _statusLines = BuildStatusLines("No chart found.", 0, 0);
                return;
            }

            var chartPath = Path.GetFullPath(Path.Combine(
                rootDirectory,
                chartReference.File.Replace('/', Path.DirectorySeparatorChar)));
            var chart = NbmsJson.ReadChart(chartPath);
            _chart = new LoadedChart
            {
                Reference = chartReference,
                Path = chartPath,
                Chart = chart
            };

            BuildRenderData(_chart.Chart);
            ApplyPreferredWindowSize(_chart.Reference.Mode);
            LoadAudioBank(header, rootDirectory);
            LoadMediaBank(header, rootDirectory, _chart.Chart);
            _statusText = $"{header.Title} / {_chart.Reference.Id} / {_chart.Reference.Mode}";
            _statusLines = BuildStatusLines(_statusText, _notes.Count, _measureSeconds.Count);
            AppendViewerLog($"LoadProject ok notes={_notes.Count} measures={_measureSeconds.Count}");
            AddLog($"chart {_chart.Reference.Id} notes={_notes.Count} audio={_audioBank?.Count ?? 0}");
        }
        catch (Exception ex)
        {
            _statusText = $"Load failed: {ex.Message}";
            _statusLines = BuildStatusLines(_statusText, 0, 0);
            AppendViewerLog($"LoadProject failed {ex}");
        }
    }

    private void BuildRenderData(NbmsChart chart)
    {
        var maxTick = Math.Max(chart.Notes.Count == 0 ? 0 : chart.Notes.Max(note => note.Tick), chart.Resolution * 4);
        var measureTicks = new List<int>();
        for (var tick = 0; tick <= maxTick + chart.Resolution * 4; tick += chart.Resolution * 4)
        {
            measureTicks.Add(tick);
        }

        var noteTicks = chart.Notes.Select(note => note.Tick);
        var holdEndTicks = chart.Notes
            .Where(note => note.DurationTicks is > 0)
            .Select(note => note.Tick + note.DurationTicks!.Value);
        var bpmEvents = chart.Timing
            .Where(timing => timing.Type == "bpm" && timing.Value is not null)
            .OrderBy(timing => timing.Tick)
            .ToList();
        if (bpmEvents.Count == 0)
        {
            bpmEvents.Add(new TimingEvent { Tick = 0, Type = "bpm", Value = 120 });
        }

        var secondsByTick = _timelineService.BuildTickTimeMap(
            chart,
            noteTicks
                .Concat(holdEndTicks)
                .Concat(bpmEvents.Select(timing => timing.Tick))
                .Concat(chart.BackgroundAudio.Select(item => item.Tick))
                .Concat(chart.MediaEvents.Select(item => item.Tick))
                .Concat(measureTicks));

        _notes = chart.Notes
            .Select(note => new RenderNote(
                note.Tick,
                ResolveLaneIndex(note.Lane),
                secondsByTick[note.Tick],
                note.DurationTicks is > 0 ? secondsByTick[note.Tick + note.DurationTicks!.Value] : secondsByTick[note.Tick],
                note.Type ?? "tap",
                note.AudioId ?? ""))
            .Where(note => note.LaneIndex >= 0)
            .OrderBy(note => note.TimeSeconds)
            .ToList();

        var noteAudio = chart.Notes
            .Where(note => !string.IsNullOrWhiteSpace(note.AudioId))
            .Select(note => new AudioScheduleItem(
                secondsByTick[note.Tick],
                note.Tick,
                note.Lane,
                note.AudioId!,
                true));
        var backgroundAudio = chart.BackgroundAudio
            .Where(item => !string.IsNullOrWhiteSpace(item.AudioId))
            .Select(item => new AudioScheduleItem(
                secondsByTick[item.Tick],
                item.Tick,
                item.Lane ?? "background",
                item.AudioId,
                false));
        _audioSchedule = noteAudio
            .Concat(backgroundAudio)
            .OrderBy(item => item.TimeSeconds)
            .ThenBy(item => item.Tick)
            .ToList();

        _mediaSchedule = chart.MediaEvents
            .Where(item => !string.IsNullOrWhiteSpace(item.MediaId))
            .Select(item => new MediaScheduleItem(
                secondsByTick[item.Tick],
                item.Tick,
                item.MediaId,
                item.Type,
                item.Layer ?? 0))
            .OrderBy(item => item.TimeSeconds)
            .ThenBy(item => item.Layer)
            .ThenBy(item => item.MediaId, StringComparer.Ordinal)
            .ToList();

        _measureSeconds = [];
        foreach (var tick in measureTicks)
        {
            _measureSeconds.Add(secondsByTick[tick]);
        }

        _bpmMarkers = bpmEvents
            .Select(timing => new BpmMarker(secondsByTick[timing.Tick], timing.Value!.Value))
            .OrderBy(marker => marker.TimeSeconds)
            .ToList();
    }

    private void LoadAudioBank(NbmsHeader header, string rootDirectory)
    {
        try
        {
            var audioPath = Path.GetFullPath(Path.Combine(
                rootDirectory,
                header.Audio.File.Replace('/', Path.DirectorySeparatorChar)));
            var requiredAudioIds = _audioSchedule.Select(item => item.AudioId).Distinct(StringComparer.Ordinal);
            _audioBank = ViewerAudioBank.Load(audioPath, requiredAudioIds);
            _audioPlayer = new ViewerAudioPlayer();
            var preloaded = _audioPlayer.Preload(_audioBank.Files);
            AddLog($"audio ready {_audioBank.Count} preload={preloaded}");
        }
        catch (Exception ex)
        {
            AddLog($"audio failed {ex.GetType().Name}: {ex.Message}");
            AppendViewerLog($"LoadAudioBank failed {ex}");
        }
    }

    private void QueueUpcomingAudioEvents()
    {
        const double scheduleLookaheadSeconds = 0.12;
        while (_nextAudioIndex < _audioSchedule.Count &&
               _audioSchedule[_nextAudioIndex].TimeSeconds <= _playbackSeconds + scheduleLookaheadSeconds)
        {
            var item = _audioSchedule[_nextAudioIndex++];
            var delaySeconds = item.TimeSeconds - _playbackSeconds;
            if (_audioBank is not null &&
                _audioPlayer is not null &&
                _audioBank.TryGetFilePath(item.AudioId, out var filePath))
            {
                if (!_audioPlayer.PlayPreloadedOneShot(item.AudioId, delaySeconds))
                {
                    _audioPlayer.PlayOneShot(filePath, delaySeconds);
                }

                AddLog($"queue {item.AudioId} {item.Lane} d={Math.Max(0, delaySeconds):0.000}s");
            }
            else
            {
                AddLog($"missing {item.AudioId}");
            }
        }
    }

    private void ProcessDueMediaEvents()
    {
        while (_nextMediaIndex < _mediaSchedule.Count &&
               _mediaSchedule[_nextMediaIndex].TimeSeconds <= _playbackSeconds)
        {
            var item = _mediaSchedule[_nextMediaIndex++];
            if (item.Type.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                _currentBgaTexture = null;
                _currentBgaVideoId = null;
                StopVideoDecoder();
                AddLog("bga clear");
                continue;
            }

            if (_mediaVideoFiles.TryGetValue(item.MediaId, out var videoPath))
            {
                _currentBgaTexture = null;
                _currentBgaVideoId = item.MediaId;
                PlayVideo(videoPath);
                AddLog($"bga video {item.MediaId}");
            }
            else if (_mediaTextures.TryGetValue(item.MediaId, out var texture))
            {
                _currentBgaTexture = texture;
                _currentBgaVideoId = null;
                StopVideoDecoder();
                AddLog($"bga {item.MediaId}");
            }
            else
            {
                AddLog($"bga missing {item.MediaId}");
            }
        }
    }

    private void ProcessDueHitEvents()
    {
        while (_nextHitIndex < _audioSchedule.Count &&
               _audioSchedule[_nextHitIndex].TimeSeconds <= _playbackSeconds)
        {
            var item = _audioSchedule[_nextHitIndex++];
            if (item.IsObject)
            {
                _combo++;
            }
        }
    }

    private void DrawPlayfield()
    {
        if (_spriteBatch is null || _pixel is null)
        {
            return;
        }

        var viewport = GraphicsDevice.Viewport.Bounds;
        var layout = ResolvePlayfieldLayout(viewport);
        var laneCount = layout.LaneCount;
        var is14K = layout.IsDoublePlay;
        var playfieldWidth = layout.Width;
        var left = layout.Left;
        var top = 56f;
        var bottom = viewport.Height - 28f;
        var judgeY = bottom - JudgeLineOffset;
        var speed = PixelsPerSecondBase * _hiSpeed;

        DrawRect(left - 8, top - 8, playfieldWidth + 16, bottom - top + 16, new Color(18, 22, 30));
        DrawRect(left - 2, top, playfieldWidth + 4, bottom - top, new Color(2, 3, 5));

        for (var lane = 0; lane < laneCount; lane++)
        {
            var x = ResolveLaneX(left, lane, is14K);
            var color = ResolveLaneColor(lane, is14K);
            DrawRect(x, top, LaneWidth - 1, bottom - top, color);
            DrawRect(x, top, 1, bottom - top, new Color(115, 118, 128));
        }

        DrawRect(left + playfieldWidth - 1, top, 1, bottom - top, new Color(115, 118, 128));

        foreach (var measureSecond in _measureSeconds)
        {
            var y = judgeY - (float)(measureSecond - _playbackSeconds) * speed;
            if (y < top || y > bottom)
            {
                continue;
            }

            DrawRect(left, y, playfieldWidth, 2f, new Color(200, 206, 216));
        }

        foreach (var note in _notes)
        {
            var y = judgeY - (float)(note.TimeSeconds - _playbackSeconds) * speed;
            var endY = judgeY - (float)(note.EndTimeSeconds - _playbackSeconds) * speed;
            var visibleTop = Math.Min(y, endY);
            var visibleBottom = Math.Max(y, endY);
            if (visibleBottom < top - 16 || visibleTop > bottom + 16)
            {
                continue;
            }

            var x = ResolveLaneX(left, note.LaneIndex, is14K);
            var noteColor = ResolveNoteColor(note.LaneIndex, is14K);
            if (note.EndTimeSeconds > note.TimeSeconds)
            {
                DrawLongNote(x, y, endY, noteColor);
            }
            else
            {
                DrawRect(x + 4, y - 5, LaneWidth - 9, 10, noteColor);
                DrawRect(x + 4, y - 5, LaneWidth - 9, 1.5f, Color.White);
                DrawRect(x + 4, y + 4, LaneWidth - 9, 1.5f, new Color(60, 60, 60));
            }
        }

        DrawRect(left, judgeY, playfieldWidth, 4, new Color(255, 48, 48));
    }

    private void DrawBgaBackground()
    {
        if (_spriteBatch is null || (_currentBgaTexture is null && _currentBgaVideoId is null))
        {
            return;
        }

        var viewport = GraphicsDevice.Viewport.Bounds;
        var layout = ResolvePlayfieldLayout(viewport);
        var rects = ResolveBgaRects(viewport, layout);
        var texture = ResolveCurrentBgaTexture();
        if (texture is null)
        {
            return;
        }

        if (_currentBgaVideoId is not null)
        {
            DrawVideoStatus(rects[0]);
        }

        if (layout.IsDoublePlay)
        {
            foreach (var rect in rects)
            {
                DrawBgaTexture(texture, rect);
            }

            return;
        }

        DrawBgaTexture(texture, rects[0]);
    }

    private void DrawBgaTexture(Texture2D texture, Microsoft.Xna.Framework.Rectangle rect)
    {
        if (_spriteBatch is null)
        {
            return;
        }

        DrawRect(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4, new Color(18, 22, 30));
        var fit = FitTexture(texture, rect);
        _spriteBatch.Draw(texture, fit, Color.White);
    }

    private void LoadMediaBank(NbmsHeader header, string rootDirectory, NbmsChart chart)
    {
        if (header.Media is not { File.Length: > 0 } mediaReference)
        {
            AddLog("media none");
            return;
        }

        try
        {
            var mediaPath = Path.GetFullPath(Path.Combine(
                rootDirectory,
                mediaReference.File.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(mediaPath))
            {
                AddLog($"media missing {mediaReference.File}");
                return;
            }

            using var stream = File.OpenRead(mediaPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("media manifest missing");
            using (var manifestStream = manifestEntry.Open())
            {
                _mediaManifest = JsonSerializer.Deserialize<MediaManifest>(manifestStream, NbmsJson.SerializerOptions);
            }

            var referenced = chart.MediaEvents
                .Select(item => item.MediaId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var entry in _mediaManifest?.Entries ?? [])
            {
                if (!referenced.Contains(entry.MediaId))
                {
                    continue;
                }

                if (!IsImageMedia(entry))
                {
                    if (IsVideoMedia(entry))
                    {
                        ExtractVideoMedia(archive, entry);
                    }
                    else
                    {
                        AddLog($"media unsupported {entry.MediaId} type={entry.Type} mime={entry.MimeType}");
                    }

                    continue;
                }

                var archiveEntry = archive.GetEntry(entry.Path.Replace('\\', '/'));
                if (archiveEntry is null)
                {
                    AddLog($"media entry missing {entry.MediaId} {entry.Path}");
                    continue;
                }

                using var entryStream = archiveEntry.Open();
                _mediaTextures[entry.MediaId] = Texture2D.FromStream(GraphicsDevice, entryStream);
            }

            AddLog($"media ready image={_mediaTextures.Count} video={_mediaVideoFiles.Count}/{referenced.Count}");
        }
        catch (Exception ex)
        {
            AddLog($"media failed {ex.GetType().Name}: {ex.Message}");
            AppendViewerLog($"LoadMediaBank failed {ex}");
        }
    }

    private void ExtractVideoMedia(ZipArchive archive, MediaAssetEntry entry)
    {
        var archiveEntry = archive.GetEntry(entry.Path.Replace('\\', '/'));
        if (archiveEntry is null)
        {
            AddLog($"media entry missing {entry.MediaId} {entry.Path}");
            return;
        }

        _mediaTempDirectory ??= CreateMediaTempDirectory();
        var extension = Path.GetExtension(entry.Path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".mp4";
        }

        var outputPath = Path.Combine(_mediaTempDirectory, $"{SanitizeFileName(entry.MediaId)}{extension}");
        archiveEntry.ExtractToFile(outputPath, overwrite: true);
        _mediaVideoFiles[entry.MediaId] = outputPath;
    }

    private void DrawLongNote(float laneX, float startY, float endY, Color noteColor)
    {
        var x = laneX + 4;
        var width = LaneWidth - 9;
        var top = Math.Min(startY, endY);
        var height = Math.Max(8f, Math.Abs(endY - startY));
        var bodyColor = new Color(noteColor.R, noteColor.G, noteColor.B, (byte)135);

        DrawRect(x, top, width, height, bodyColor);
        DrawRect(x, startY - 5, width, 10, noteColor);
        DrawRect(x, endY - 5, width, 10, bodyColor);
        DrawRect(x, startY - 5, width, 1.5f, Color.White);
        DrawRect(x, startY + 4, width, 1.5f, new Color(60, 60, 60));
        DrawRect(x, endY - 5, width, 1.5f, Color.White);
        DrawRect(x, endY + 4, width, 1.5f, new Color(60, 60, 60));
    }

    private void DrawStatusOverlay()
    {
        if (_statusLines.Count == 0)
        {
            _statusLines = BuildStatusLines(_statusText, _notes.Count, _measureSeconds.Count);
        }

        DrawRect(12, 10, GraphicsDevice.Viewport.Width - 24, 36, new Color(12, 16, 24));
        DrawRect(12, 45, GraphicsDevice.Viewport.Width - 24, 2, new Color(54, 180, 255));

        var y = 16f;
        foreach (var line in _statusLines.Take(2))
        {
            DrawText(line, 18, y, StatusScale, new Color(230, 236, 245));
            y += 15f;
        }
    }

    private void DrawInfoPanels()
    {
        var viewport = GraphicsDevice.Viewport.Bounds;
        if (IsCurrentChart14K())
        {
            DrawCompactInfoPanel(viewport);
            return;
        }

        var panelWidth = 300f;
        var panelX = _bgaPlayfieldSide == BgaPlayfieldSide.OneP
            ? Math.Max(12f, viewport.Width - panelWidth - 12f)
            : 12f;
        DrawRect(panelX, 56, panelWidth, 144, new Color(10, 14, 22, 220));
        DrawText($"{_combo} COMBO / {_notes.Count} NOTES", panelX + 12, 68, 2f, new Color(255, 235, 110));
        DrawText($"TIME {_playbackSeconds:0.000}", panelX + 12, 96, 2f, new Color(220, 230, 245));
        DrawText($"BPM {ResolveCurrentBpm():0.##}", panelX + 12, 116, 2f, new Color(255, 190, 120));
        DrawText($"FPS {_displayFps:0} {GetFpsModeLabel()}", panelX + 12, 136, 2f, new Color(180, 220, 255));
        DrawText($"AUDIO {_nextAudioIndex}/{_audioSchedule.Count}", panelX + 12, 156, 2f, new Color(180, 220, 255));

        if (_isLogVisible)
        {
            var logX = panelX <= 12f ? viewport.Width - 288f : 12f;
            DrawRect(logX, 56, 276, 150, new Color(10, 14, 22, 220));
            DrawText("LOG", logX + 12, 68, 2f, new Color(180, 220, 255));
            var y = 90f;
            foreach (var line in _logLines.TakeLast(5))
            {
                DrawText(SanitizeForPixelFont(line), logX + 12, y, 1.5f, new Color(220, 230, 245));
                y += 15f;
            }
        }
    }

    private void DrawCompactInfoPanel(Microsoft.Xna.Framework.Rectangle viewport)
    {
        var panelWidth = 330f;
        var x = Math.Max(12f, viewport.Width - panelWidth - 12f);
        DrawRect(x, 12, panelWidth, 31, new Color(10, 14, 22, 230));
        DrawText($"{_combo} COMBO / {_notes.Count} NOTES", x + 10, 18, 1.5f, new Color(255, 235, 110));
        DrawText($"BPM {ResolveCurrentBpm():0.##}", x + 10, 31, 1.5f, new Color(255, 190, 120));
        DrawText($"FPS {_displayFps:0} {GetFpsModeLabel()}", x + 118, 31, 1.5f, new Color(180, 220, 255));

        if (_isLogVisible)
        {
            DrawRect(viewport.Width - 288, 56, 276, 150, new Color(10, 14, 22, 220));
            DrawText("LOG", viewport.Width - 276, 68, 2f, new Color(180, 220, 255));
            var y = 90f;
            foreach (var line in _logLines.TakeLast(5))
            {
                DrawText(SanitizeForPixelFont(line), viewport.Width - 276, y, 1.5f, new Color(220, 230, 245));
                y += 15f;
            }
        }
    }

    private void UpdateFrameDiagnostics(GameTime gameTime)
    {
        _fpsSampleSeconds += gameTime.ElapsedGameTime.TotalSeconds;
        _fpsSampleFrames++;
        if (_fpsSampleSeconds >= 0.25)
        {
            _displayFps = _fpsSampleFrames / _fpsSampleSeconds;
            _fpsSampleSeconds = 0;
            _fpsSampleFrames = 0;
        }

        Window.Title = $"NBMS MonoGame Viewer - {_statusText} - FPS {_displayFps:0} {GetFpsModeLabel()} - t {_playbackSeconds:0.000}s - HS {_hiSpeed:0.0}";
    }

    private void CycleFpsLimitMode()
    {
        _fpsLimitMode = _fpsLimitMode switch
        {
            FpsLimitMode.Fixed60 => FpsLimitMode.Fixed120,
            FpsLimitMode.Fixed120 => FpsLimitMode.Unlimited,
            _ => FpsLimitMode.Fixed60
        };

        ApplyFpsLimitMode(applyGraphicsChanges: true);
        AddLog($"fps mode {GetFpsModeLabel()}");
    }

    private void ApplyFpsLimitMode(bool applyGraphicsChanges)
    {
        // Runtime switch for separating frame pacing issues from rendering load.
        switch (_fpsLimitMode)
        {
            case FpsLimitMode.Fixed60:
                IsFixedTimeStep = true;
                TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60.0);
                _graphics.SynchronizeWithVerticalRetrace = false;
                break;
            case FpsLimitMode.Fixed120:
                IsFixedTimeStep = true;
                TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 120.0);
                _graphics.SynchronizeWithVerticalRetrace = false;
                break;
            default:
                IsFixedTimeStep = false;
                _graphics.SynchronizeWithVerticalRetrace = false;
                break;
        }

        if (applyGraphicsChanges)
        {
            _graphics.ApplyChanges();
        }
    }

    private string GetFpsModeLabel()
    {
        return _fpsLimitMode switch
        {
            FpsLimitMode.Fixed60 => "LOCK60",
            FpsLimitMode.Fixed120 => "LOCK120",
            _ => "UNLIMIT"
        };
    }

    private double ResolveCurrentBpm()
    {
        if (_bpmMarkers.Count == 0)
        {
            return 120;
        }

        var current = _bpmMarkers[0].Bpm;
        foreach (var marker in _bpmMarkers)
        {
            if (marker.TimeSeconds > _playbackSeconds)
            {
                break;
            }

            current = marker.Bpm;
        }

        return current;
    }

    private bool IsCurrentChart14K()
    {
        var mode = _chart?.Reference.Mode ?? "beat-7k";
        return IsDoublePlayMode(mode);
    }

    private void ApplyPreferredWindowSize(string mode)
    {
        if (IsDoublePlayMode(mode))
        {
            _graphics.PreferredBackBufferWidth = 1280;
            _graphics.PreferredBackBufferHeight = 820;
            _graphics.ApplyChanges();
        }
    }

    private PlayfieldLayout ResolvePlayfieldLayout(Microsoft.Xna.Framework.Rectangle viewport)
    {
        var mode = _chart?.Reference.Mode ?? "beat-7k";
        var isDoublePlay = IsDoublePlayMode(mode);
        var laneCount = ResolveLaneCount(mode);
        var playfieldWidth = laneCount * LaneWidth + (isDoublePlay ? SideGap : 0);
        var left = isDoublePlay
            ? (viewport.Width - playfieldWidth) * 0.5f
            : _bgaPlayfieldSide == BgaPlayfieldSide.OneP
                ? 28f
                : viewport.Width - playfieldWidth - 28f;
        return new PlayfieldLayout(left, playfieldWidth, laneCount, isDoublePlay);
    }

    private static int ResolveLaneCount(string mode)
    {
        if (mode.Contains("14", StringComparison.OrdinalIgnoreCase) ||
            mode.Contains("10", StringComparison.OrdinalIgnoreCase))
        {
            return LaneCount14K;
        }

        return mode.Contains("5", StringComparison.OrdinalIgnoreCase) ? 6 : LaneCount7K;
    }

    private static bool IsDoublePlayMode(string mode)
    {
        return mode.Contains("14", StringComparison.OrdinalIgnoreCase) ||
               mode.Contains("10", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageMedia(MediaAssetEntry entry)
    {
        if (entry.Type.Equals("image", StringComparison.OrdinalIgnoreCase) ||
            entry.Type.Equals("layer", StringComparison.OrdinalIgnoreCase) ||
            entry.Type.Equals("poor", StringComparison.OrdinalIgnoreCase) ||
            entry.Type.Equals("banner", StringComparison.OrdinalIgnoreCase) ||
            entry.Type.Equals("stagefile", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(entry.Path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";
    }

    private static bool IsVideoMedia(MediaAssetEntry entry)
    {
        if (entry.Type.Equals("video", StringComparison.OrdinalIgnoreCase) ||
            entry.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var extension = Path.GetExtension(entry.Path).ToLowerInvariant();
        return extension is ".mp4" or ".avi" or ".webm" or ".mov" or ".mkv" or ".wmv" or ".mpg" or ".mpeg";
    }

    private static Microsoft.Xna.Framework.Rectangle FitTexture(Texture2D texture, Microsoft.Xna.Framework.Rectangle bounds)
    {
        var scale = Math.Min(
            bounds.Width / (float)Math.Max(1, texture.Width),
            bounds.Height / (float)Math.Max(1, texture.Height));
        var width = Math.Max(1, (int)Math.Round(texture.Width * scale));
        var height = Math.Max(1, (int)Math.Round(texture.Height * scale));
        return new Microsoft.Xna.Framework.Rectangle(
            bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2,
            width,
            height);
    }

    private List<Microsoft.Xna.Framework.Rectangle> ResolveBgaRects(
        Microsoft.Xna.Framework.Rectangle viewport,
        PlayfieldLayout layout)
    {
        if (layout.IsDoublePlay)
        {
            var width = Math.Max(160, (viewport.Width - (int)layout.Width - 48) / 2);
            var height = Math.Max(120, (viewport.Height - 84) / 2);
            var top = 56;
            var bottom = viewport.Height - 28 - height;
            var left = 12;
            var right = viewport.Width - 12 - width;
            return
            [
                new Microsoft.Xna.Framework.Rectangle(left, top, width, height),
                new Microsoft.Xna.Framework.Rectangle(left, bottom, width, height),
                new Microsoft.Xna.Framework.Rectangle(right, top, width, height),
                new Microsoft.Xna.Framework.Rectangle(right, bottom, width, height)
            ];
        }

        var bgaLeft = _bgaPlayfieldSide == BgaPlayfieldSide.OneP
            ? (int)(layout.Left + layout.Width + 18)
            : 12;
        var bgaRight = _bgaPlayfieldSide == BgaPlayfieldSide.OneP
            ? viewport.Width - 12
            : (int)layout.Left - 18;
        return
        [
            new Microsoft.Xna.Framework.Rectangle(
                bgaLeft,
                56,
                Math.Max(1, bgaRight - bgaLeft),
                viewport.Height - 84)
        ];
    }

    private Texture2D? ResolveCurrentBgaTexture()
    {
        if (_currentBgaVideoId is null)
        {
            return _currentBgaTexture;
        }

        if (_videoDecoder is null)
        {
            return null;
        }

        var frame = _videoDecoder.TakeLatestFrame();
        if (frame is not null)
        {
            if (_videoTexture is null ||
                _videoTexture.Width != FfmpegVideoDecoder.OutputWidth ||
                _videoTexture.Height != FfmpegVideoDecoder.OutputHeight)
            {
                _videoTexture?.Dispose();
                _videoTexture = new Texture2D(
                    GraphicsDevice,
                    FfmpegVideoDecoder.OutputWidth,
                    FfmpegVideoDecoder.OutputHeight,
                    mipmap: false,
                    SurfaceFormat.Color);
            }

            _videoTexture.SetData(frame);
        }

        return _videoTexture;
    }

    private void PlayVideo(string videoPath)
    {
        try
        {
            if (_videoDecoder is not null && _videoDecoder.SourcePath.Equals(videoPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            StopVideoDecoder();
            _videoDecoder = FfmpegVideoDecoder.Start(videoPath);
        }
        catch (Exception ex)
        {
            _currentBgaVideoId = null;
            AddLog($"video failed {ex.GetType().Name}: {ex.Message}");
            AppendViewerLog($"PlayVideo failed {ex}");
        }
    }

    private void StopVideoDecoder()
    {
        _videoDecoder?.Dispose();
        _videoDecoder = null;
    }

    private void DrawVideoStatus(Microsoft.Xna.Framework.Rectangle rect)
    {
        if (_videoDecoder is null || _videoDecoder.IsRunning)
        {
            return;
        }

        DrawRect(rect.X + 8, rect.Y + 8, Math.Min(rect.Width - 16, 360), 34, new Color(10, 14, 22, 220));
        DrawText("BGA VIDEO STOPPED / CHECK FFMPEG", rect.X + 18, rect.Y + 18, 1.5f, new Color(255, 190, 120));
    }

    private void AddVideoDecoderLog()
    {
        if (_videoDecoder?.LastError is { Length: > 0 } error)
        {
            AddLog($"video {error}");
        }
    }

    private static string CreateMediaTempDirectory()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "NBMS.Studio.MonoGameViewer.Media", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private void DeleteMediaTempDirectory()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_mediaTempDirectory) && Directory.Exists(_mediaTempDirectory))
            {
                Directory.Delete(_mediaTempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private void DrawRect(float x, float y, float width, float height, Color color)
    {
        if (_spriteBatch is null || _pixel is null || width <= 0 || height <= 0)
        {
            return;
        }

        _spriteBatch.Draw(
            _pixel,
            new Vector2(x, y),
            sourceRectangle: null,
            color,
            rotation: 0f,
            origin: Vector2.Zero,
            scale: new Vector2(width, height),
            effects: SpriteEffects.None,
            layerDepth: 0f);
    }

    private static float ResolveLaneX(float left, int lane, bool is14K)
    {
        var gap = is14K && lane >= 8 ? SideGap : 0;
        return left + lane * LaneWidth + gap;
    }

    private static Color ResolveLaneColor(int lane, bool is14K)
    {
        var sideLane = ResolveSideLane(lane, is14K);
        if (sideLane == 0)
        {
            return new Color(76, 18, 24);
        }

        return sideLane % 2 == 0
            ? new Color(18, 45, 72)
            : new Color(18, 18, 20);
    }

    private static Color ResolveNoteColor(int lane, bool is14K)
    {
        var sideLane = ResolveSideLane(lane, is14K);
        if (sideLane == 0)
        {
            return new Color(245, 64, 64);
        }

        return sideLane % 2 == 0
            ? new Color(80, 132, 255)
            : new Color(230, 234, 240);
    }

    private static int ResolveSideLane(int lane, bool is14K)
    {
        if (!is14K || lane < 8)
        {
            return lane;
        }

        return lane == 15 ? 0 : lane - 7;
    }

    private static int ResolveLaneIndex(string lane)
    {
        if (lane.Equals("scratch", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (lane.Equals("scratch2", StringComparison.OrdinalIgnoreCase))
        {
            return 15;
        }

        if (lane.StartsWith("key", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(lane[3..], out var key) &&
            key >= 1 &&
            key <= 14)
        {
            return key <= 7 ? key : key;
        }

        return -1;
    }

    private static List<string> BuildStatusLines(string status, int noteCount, int measureCount)
    {
        return
        [
            $"NBMS MONOGAME VIEWER  {SanitizeForPixelFont(status)}",
            $"NOTES {noteCount}  MEASURES {measureCount}  1-5 HS/BGA  7 FPS  9 LOG  ESC EXIT"
        ];
    }

    private static string SanitizeForPixelFont(string value)
    {
        var chars = value
            .Select(ch => ch < 128 ? char.ToUpperInvariant(ch) : '?')
            .ToArray();
        return new string(chars);
    }

    // Draw the minimal diagnostic text without using the Content Pipeline.
    private void DrawText(string text, float x, float y, float scale, Color color)
    {
        var cursorX = x;
        foreach (var ch in text)
        {
            DrawGlyph(char.ToUpperInvariant(ch), cursorX, y, scale, color);
            cursorX += 6 * scale;
        }
    }

    private void DrawGlyph(char ch, float x, float y, float scale, Color color)
    {
        var glyph = PixelFont.GetValueOrDefault(ch, PixelFont['?']);
        for (var row = 0; row < glyph.Length; row++)
        {
            for (var column = 0; column < glyph[row].Length; column++)
            {
                if (glyph[row][column] == '1')
                {
                    DrawRect(x + column * scale, y + row * scale, scale, scale, color);
                }
            }
        }
    }

    private static readonly Dictionary<char, string[]> PixelFont = new()
    {
        [' '] = ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
        ['?'] = ["01110", "10001", "00001", "00010", "00100", "00000", "00100"],
        ['-'] = ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
        ['.'] = ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
        ['/'] = ["00001", "00010", "00010", "00100", "01000", "01000", "10000"],
        [':'] = ["00000", "01100", "01100", "00000", "01100", "01100", "00000"],
        ['0'] = ["01110", "10001", "10011", "10101", "11001", "10001", "01110"],
        ['1'] = ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
        ['2'] = ["01110", "10001", "00001", "00010", "00100", "01000", "11111"],
        ['3'] = ["11110", "00001", "00001", "01110", "00001", "00001", "11110"],
        ['4'] = ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
        ['5'] = ["11111", "10000", "10000", "11110", "00001", "00001", "11110"],
        ['6'] = ["01110", "10000", "10000", "11110", "10001", "10001", "01110"],
        ['7'] = ["11111", "00001", "00010", "00100", "01000", "01000", "01000"],
        ['8'] = ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
        ['9'] = ["01110", "10001", "10001", "01111", "00001", "00001", "01110"],
        ['A'] = ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['B'] = ["11110", "10001", "10001", "11110", "10001", "10001", "11110"],
        ['C'] = ["01110", "10001", "10000", "10000", "10000", "10001", "01110"],
        ['D'] = ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
        ['E'] = ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
        ['F'] = ["11111", "10000", "10000", "11110", "10000", "10000", "10000"],
        ['G'] = ["01110", "10001", "10000", "10111", "10001", "10001", "01110"],
        ['H'] = ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
        ['I'] = ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
        ['J'] = ["00111", "00010", "00010", "00010", "00010", "10010", "01100"],
        ['K'] = ["10001", "10010", "10100", "11000", "10100", "10010", "10001"],
        ['L'] = ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
        ['M'] = ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
        ['N'] = ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
        ['O'] = ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['P'] = ["11110", "10001", "10001", "11110", "10000", "10000", "10000"],
        ['Q'] = ["01110", "10001", "10001", "10001", "10101", "10010", "01101"],
        ['R'] = ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
        ['S'] = ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
        ['T'] = ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
        ['U'] = ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
        ['V'] = ["10001", "10001", "10001", "10001", "10001", "01010", "00100"],
        ['W'] = ["10001", "10001", "10001", "10101", "10101", "10101", "01010"],
        ['X'] = ["10001", "10001", "01010", "00100", "01010", "10001", "10001"],
        ['Y'] = ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
        ['Z'] = ["11111", "00001", "00010", "00100", "01000", "10000", "11111"]
    };

    private sealed record RenderNote(
        int Tick,
        int LaneIndex,
        double TimeSeconds,
        double EndTimeSeconds,
        string Type,
        string AudioId);

    private sealed record BpmMarker(double TimeSeconds, double Bpm);

    private sealed record MediaScheduleItem(double TimeSeconds, int Tick, string MediaId, string Type, int Layer);

    private sealed record PlayfieldLayout(float Left, float Width, int LaneCount, bool IsDoublePlay);

    private static void AppendViewerLog(string message)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "NBMS.Studio.MonoGameViewer.log");
        File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
    }

    private static T ReadJson<T>(string path)
    {
        AppendViewerLog($"ReadJson begin {path}");
        var json = File.ReadAllText(path);
        var value = JsonSerializer.Deserialize<T>(json, NbmsJson.SerializerOptions);
        AppendViewerLog($"ReadJson end {path}");
        return value ?? throw new InvalidDataException($"JSON was empty: {path}");
    }

    private void AddLog(string message)
    {
        if (!_isLogVisible)
        {
            return;
        }

        _logLines.Enqueue($"{_playbackSeconds:0.000} {message}");
        while (_logLines.Count > 20)
        {
            _logLines.Dequeue();
        }

        AppendViewerLog(message);
    }

    private bool IsPressed(KeyboardState keyboard, Keys key)
    {
        return keyboard.IsKeyDown(key) && !_previousKeyboardState.IsKeyDown(key);
    }

    private sealed record AudioScheduleItem(double TimeSeconds, int Tick, string Lane, string AudioId, bool IsObject);

    private enum BgaPlayfieldSide
    {
        OneP,
        TwoP
    }

    private enum FpsLimitMode
    {
        Fixed60,
        Fixed120,
        Unlimited
    }
}
