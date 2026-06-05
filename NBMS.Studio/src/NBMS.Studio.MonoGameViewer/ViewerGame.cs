using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NBMS.Core.Models;
using NBMS.Core.Services;
using System.Diagnostics;
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
    private List<RenderNote> _notes = [];
    private List<AudioScheduleItem> _audioSchedule = [];
    private List<double> _measureSeconds = [];
    private List<string> _statusLines = [];
    private readonly Queue<string> _logLines = [];
    private readonly Stopwatch _playbackClock = new();
    private KeyboardState _previousKeyboardState;
    private double _playbackSeconds;
    private int _nextAudioIndex;
    private int _nextHitIndex;
    private int _combo;
    private float _hiSpeed = 1.5f;
    private bool _isLogVisible;
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
            var chart = ReadJson<NbmsChart>(chartPath);
            _chart = new LoadedChart
            {
                Reference = chartReference,
                Path = chartPath,
                Chart = chart
            };

            BuildRenderData(_chart.Chart);
            LoadAudioBank(header, rootDirectory);
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

        var secondsByTick = _timelineService.BuildTickTimeMap(
            chart,
            chart.Notes.Select(note => note.Tick)
                .Concat(chart.BackgroundAudio.Select(item => item.Tick))
                .Concat(measureTicks));

        _notes = chart.Notes
            .Select(note => new RenderNote(
                note.Tick,
                ResolveLaneIndex(note.Lane),
                secondsByTick[note.Tick],
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

        _measureSeconds = [];
        foreach (var tick in measureTicks)
        {
            _measureSeconds.Add(secondsByTick[tick]);
        }
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
        var mode = _chart?.Reference.Mode ?? "beat-7k";
        var is14K = mode.Contains("14", StringComparison.OrdinalIgnoreCase);
        var laneCount = is14K ? LaneCount14K : LaneCount7K;
        var playfieldWidth = laneCount * LaneWidth + (is14K ? SideGap : 0);
        var left = (viewport.Width - playfieldWidth) * 0.5f;
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
            if (y < top - 16 || y > bottom + 16)
            {
                continue;
            }

            var x = ResolveLaneX(left, note.LaneIndex, is14K);
            var noteColor = note.Type.Equals("hold", StringComparison.OrdinalIgnoreCase)
                ? new Color(80, 180, 255)
                : ResolveNoteColor(note.LaneIndex, is14K);
            DrawRect(x + 4, y - 5, LaneWidth - 9, 10, noteColor);
            DrawRect(x + 4, y - 5, LaneWidth - 9, 1.5f, Color.White);
            DrawRect(x + 4, y + 4, LaneWidth - 9, 1.5f, new Color(60, 60, 60));
        }

        DrawRect(left, judgeY, playfieldWidth, 4, new Color(255, 48, 48));
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

        DrawRect(12, 56, 240, 128, new Color(10, 14, 22, 220));
        DrawText($"COMBO {_combo}", 24, 68, 3f, new Color(255, 235, 110));
        DrawText($"TIME {_playbackSeconds:0.000}", 24, 96, 2f, new Color(220, 230, 245));
        DrawText($"FPS {_displayFps:0} {GetFpsModeLabel()}", 24, 116, 2f, new Color(180, 220, 255));
        DrawText($"AUDIO {_nextAudioIndex}/{_audioSchedule.Count}", 24, 136, 2f, new Color(180, 220, 255));

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

    private void DrawCompactInfoPanel(Microsoft.Xna.Framework.Rectangle viewport)
    {
        var panelWidth = 330f;
        var x = Math.Max(12f, viewport.Width - panelWidth - 12f);
        DrawRect(x, 12, panelWidth, 31, new Color(10, 14, 22, 230));
        DrawText($"COMBO {_combo}", x + 10, 18, 1.5f, new Color(255, 235, 110));
        DrawText($"FPS {_displayFps:0} {GetFpsModeLabel()}", x + 118, 18, 1.5f, new Color(180, 220, 255));
        DrawText($"AUDIO {_nextAudioIndex}/{_audioSchedule.Count}", x + 10, 31, 1.5f, new Color(180, 220, 255));

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

    private bool IsCurrentChart14K()
    {
        var mode = _chart?.Reference.Mode ?? "beat-7k";
        return mode.Contains("14", StringComparison.OrdinalIgnoreCase);
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
            $"NOTES {noteCount}  MEASURES {measureCount}  1-4 HS  7 FPS  9 LOG  ESC EXIT"
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

    private sealed record RenderNote(int Tick, int LaneIndex, double TimeSeconds, string Type, string AudioId);

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

    private enum FpsLimitMode
    {
        Fixed60,
        Fixed120,
        Unlimited
    }
}
