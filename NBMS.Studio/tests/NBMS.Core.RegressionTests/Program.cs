using NBMS.Core.Import;
using NBMS.Core.Models;
using NBMS.Core.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("BMS #xxx02 measure length conversion", TestMeasureLengthConversionAsync),
    ("BGA/LAYER/POOR conversion", TestVisualConversionAsync),
    ("compact and pretty compact chart roundtrip", TestCompactChartRoundtripAsync),
    ("media event stable order", TestMediaEventStableOrderAsync),
    ("media layer state restore at start tick", TestMediaLayerStateRestoreAsync),
    ("no-bga equivalent skips media state", TestNoBgaEquivalentAsync),
    ("BMS media alternative file search", TestBmsMediaAlternativeSearchAsync),
    ("BMS Shift_JIS charset import", TestShiftJisCharsetImportAsync)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"[PASS] {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"[FAIL] {test.Name}");
        Console.Error.WriteLine(ex);
    }
}

return failed == 0 ? 0 : 1;

static async Task TestMeasureLengthConversionAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Measure Test
        #BPM 120
        #WAV01 kick.wav
        #00102:0.5
        #00111:0101
        #00211:01
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    var chart = result.Chart;
    Assert(chart.Timing.Any(timing =>
        timing.Type == "measureLength" &&
        timing.Tick == 3840 &&
        Math.Abs((timing.Value ?? 0) - 0.5) < 0.0001), "measureLength event was not imported at measure 1.");

    var key1Ticks = chart.Notes
        .Where(note => note.Lane == "key1")
        .Select(note => note.Tick)
        .Order()
        .ToArray();
    AssertSequence(key1Ticks, [3840, 4800, 5760], "note ticks should follow #00102 half measure length.");
}

static async Task TestVisualConversionAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Visual Test
        #BPM 120
        #WAV01 hit.wav
        #BMP01 base.png
        #BGA02 01
        #LAYER03 layer.png
        #POOR04 poor.png
        #00111:01
        #00104:02
        #00107:03
        #00106:04
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    var note = result.Chart.Notes.Single(note => note.Lane == "key1");
    var eventsByType = result.Chart.MediaEvents
        .GroupBy(mediaEvent => mediaEvent.Type)
        .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

    Assert(eventsByType.TryGetValue("bga", out var bgaEvents) && bgaEvents.Count == 1, "BGA event was not imported.");
    Assert(eventsByType.TryGetValue("layer", out var layerEvents) && layerEvents.Count == 1, "LAYER event was not imported.");
    Assert(eventsByType.TryGetValue("poor", out var poorEvents) && poorEvents.Count == 1, "POOR event was not imported.");
    Assert(bgaEvents![0].Layer == 0, "BGA layer should be 0.");
    Assert(layerEvents![0].Layer == 1, "LAYER layer should be 1.");
    Assert(poorEvents![0].Layer == 0, "POOR layer should be 0.");
    Assert(bgaEvents[0].Tick == note.Tick, "BGA and note placed in the same BMS measure should resolve to the same tick.");
    Assert(layerEvents[0].Tick == note.Tick, "LAYER and note placed in the same BMS measure should resolve to the same tick.");
    Assert(poorEvents[0].Tick == note.Tick, "POOR and note placed in the same BMS measure should resolve to the same tick.");
    Assert(result.MediaFiles.ContainsKey(layerEvents[0].MediaId), "direct LAYER media file was not collected.");
    Assert(result.MediaFiles.ContainsKey(poorEvents[0].MediaId), "direct POOR media file was not collected.");
}

static async Task TestCompactChartRoundtripAsync()
{
    var chart = new NbmsChart
    {
        ChartId = "roundtrip",
        Mode = "beat-7k",
        Resolution = 960,
        Timing =
        [
            new TimingEvent { Tick = 0, Type = "bpm", Value = 140 },
            new TimingEvent { Tick = 3840, Type = "measureLength", Value = 0.75 },
            new TimingEvent { Tick = 5760, Type = "stop", DurationTicks = 240 }
        ],
        Notes =
        [
            new NoteEvent { Tick = 0, Lane = "scratch", Type = "tap", AudioId = "a01" },
            new NoteEvent { Tick = 960, Lane = "key1", Type = "hold", AudioId = "a02", DurationTicks = 480 }
        ],
        BackgroundAudio =
        [
            new BackgroundAudioEvent { Tick = 0, Lane = "background1", AudioId = "bgm" }
        ],
        MediaEvents =
        [
            new MediaEvent { Tick = 0, MediaId = "bga01", Type = "bga", Layer = 0 },
            new MediaEvent { Tick = 960, MediaId = "layer01", Type = "layer", Layer = 1 }
        ]
    };

    var directory = CreateTempDirectory();
    var compactPath = Path.Combine(directory, "compact.nbmc");
    var prettyPath = Path.Combine(directory, "pretty.nbmc");
    await NbmsJson.WriteCompactChartAsync(compactPath, chart);
    await NbmsJson.WritePrettyCompactChartAsync(prettyPath, chart);

    var compact = await NbmsJson.ReadChartAsync(compactPath);
    var pretty = await NbmsJson.ReadChartAsync(prettyPath);
    AssertEquivalentChart(chart, compact, "compact");
    AssertEquivalentChart(chart, pretty, "pretty compact");
}

static Task TestMediaEventStableOrderAsync()
{
    var events = new[]
    {
        new MediaEvent { Tick = 960, MediaId = "layer_b", Type = "layer", Layer = 1 },
        new MediaEvent { Tick = 960, MediaId = "", Type = "clear" },
        new MediaEvent { Tick = 960, MediaId = "base_video", Type = "video", Layer = 0 },
        new MediaEvent { Tick = 960, MediaId = "poor", Type = "poor", Layer = 0 },
        new MediaEvent { Tick = 960, MediaId = "layer_a", Type = "layer", Layer = 1 }
    };

    var ordered = MediaEventStateResolver.OrderEvents(events);
    AssertSequence(
        ordered.Select(item => item.Type + ":" + item.MediaId).ToArray(),
        ["clear:", "video:base_video", "poor:poor", "layer:layer_a", "layer:layer_b"],
        "media event stable order should follow layer/type/mediaId.");
    return Task.CompletedTask;
}

static Task TestMediaLayerStateRestoreAsync()
{
    var events = new[]
    {
        new MediaEvent { Tick = 0, MediaId = "base_a", Type = "bga", Layer = 0 },
        new MediaEvent { Tick = 480, MediaId = "layer_video", Type = "layer", Layer = 1 },
        new MediaEvent { Tick = 960, MediaId = "base_b", Type = "video", Layer = 0 },
        new MediaEvent { Tick = 1440, MediaId = "", Type = "clear", Layer = 1 },
        new MediaEvent { Tick = 1920, MediaId = "layer_b", Type = "layer", Layer = 1 }
    };

    var state = MediaEventStateResolver.ResolveStateAtTick(events, 1600);
    Assert(state.Count == 1, "layer video clear should remove only layer 1 before start tick.");
    if (!state.TryGetValue(0, out var baseLayer))
    {
        throw new InvalidOperationException("base video state should be restored at start tick.");
    }

    Assert(baseLayer.MediaId == "base_b", "base video mediaId should be latest event before start tick.");
    Assert(baseLayer.Type == "bga", "video event type should normalize to bga state.");

    state = MediaEventStateResolver.ResolveStateAtTick(events, 2400);
    Assert(state.Count == 2, "layer event after clear should restore layer 1.");
    Assert(state.TryGetValue(1, out var layer) && layer.MediaId == "layer_b", "latest layer state should be restored.");
    return Task.CompletedTask;
}

static Task TestNoBgaEquivalentAsync()
{
    var events = new[]
    {
        new MediaEvent { Tick = 0, MediaId = "base", Type = "bga", Layer = 0 },
        new MediaEvent { Tick = 480, MediaId = "layer", Type = "layer", Layer = 1 }
    };

    var noBgaEvents = Array.Empty<MediaEvent>();
    var state = MediaEventStateResolver.ResolveStateAtTick(noBgaEvents, 960);
    Assert(state.Count == 0, "--no-bga equivalent should not restore any media layer state.");
    Assert(MediaEventStateResolver.ResolveStateAtTick(events, 960).Count == 2, "control media state should restore when not disabled.");
    return Task.CompletedTask;
}

static async Task TestBmsMediaAlternativeSearchAsync()
{
    var directory = CreateTempDirectory();
    var bmsPath = Path.Combine(directory, "alt.bms");
    await File.WriteAllTextAsync(
        bmsPath,
        """
        #TITLE Alternative Media Test
        #BPM 120
        #BMP01 intro.bmp
        #BMP02 movie.avi
        #00104:0102
        """.ReplaceLineEndings(Environment.NewLine));
    await File.WriteAllBytesAsync(Path.Combine(directory, "intro.png"), [0x89, 0x50, 0x4E, 0x47]);
    await File.WriteAllBytesAsync(Path.Combine(directory, "movie.mp4"), [0, 0, 0, 0]);

    var intro = BmsMediaAlternativeResolver.Resolve(directory, "intro.bmp");
    var movie = BmsMediaAlternativeResolver.Resolve(directory, "movie.avi");

    Assert(intro.IsAlternative && intro.RelativePath == "intro.png", "image alternative resolution should prefer image extensions.");
    Assert(movie.IsAlternative && movie.RelativePath == "movie.mp4", "video alternative resolution should prefer video extensions.");
}

static async Task TestShiftJisCharsetImportAsync()
{
    var directory = CreateTempDirectory();
    var bmsPath = Path.Combine(directory, "charset.bms");

    // "#TITLE テスト" encoded in Shift_JIS. テ=83 65, ス=83 58, ト=83 67.
    byte[] bytes =
    [
        0x23, 0x43, 0x48, 0x41, 0x52, 0x53, 0x45, 0x54, 0x20, 0x53, 0x48, 0x49, 0x46, 0x54, 0x2D, 0x4A, 0x49, 0x53, 0x0A,
        0x23, 0x54, 0x49, 0x54, 0x4C, 0x45, 0x20, 0x83, 0x65, 0x83, 0x58, 0x83, 0x67, 0x0A,
        0x23, 0x42, 0x50, 0x4D, 0x20, 0x31, 0x32, 0x30, 0x0A
    ];
    await File.WriteAllBytesAsync(bmsPath, bytes);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    Assert(result.Header.Title == "テスト", $"Shift_JIS title was not decoded. actual={result.Header.Title}");
    Assert(result.Chart.Metadata is not null, "bmsCompat metadata should be written.");
    var metadata = result.Chart.Metadata.GetValueOrDefault();
    Assert(metadata.TryGetProperty("bmsCompat", out var bmsCompat), "bmsCompat metadata is missing.");
    Assert(bmsCompat.GetProperty("sourceEncoding").GetString() == "shift_jis", "sourceEncoding should be shift_jis.");
    Assert(bmsCompat.GetProperty("charsetDirective").GetString() == "SHIFT-JIS", "charsetDirective should be preserved.");
    Assert(bmsCompat.GetProperty("encodingDetection").GetString() == "charset", "encodingDetection should be charset.");
}

static async Task<string> WriteTempBmsAsync(string content)
{
    var directory = CreateTempDirectory();
    var path = Path.Combine(directory, "test.bms");
    await File.WriteAllTextAsync(path, content.ReplaceLineEndings(Environment.NewLine));
    return path;
}

static string CreateTempDirectory()
{
    var directory = Path.Combine(Path.GetTempPath(), "NBMS.Core.RegressionTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

static void AssertEquivalentChart(NbmsChart expected, NbmsChart actual, string label)
{
    Assert(actual.ChartId == expected.ChartId, $"{label}: chartId mismatch.");
    Assert(actual.Mode == expected.Mode, $"{label}: mode mismatch.");
    Assert(actual.Resolution == expected.Resolution, $"{label}: resolution mismatch.");
    Assert(actual.Timing.Count == expected.Timing.Count, $"{label}: timing count mismatch.");
    Assert(actual.Notes.Count == expected.Notes.Count, $"{label}: note count mismatch.");
    Assert(actual.BackgroundAudio.Count == expected.BackgroundAudio.Count, $"{label}: background count mismatch.");
    Assert(actual.MediaEvents.Count == expected.MediaEvents.Count, $"{label}: media count mismatch.");
    Assert(actual.Notes.Any(note => note.Type == "hold" && note.DurationTicks == 480), $"{label}: hold note was not preserved.");
    Assert(actual.MediaEvents.Any(media => media.Type == "layer" && media.Layer == 1), $"{label}: layer media event was not preserved.");
    Assert(actual.Timing.Any(timing => timing.Type == "measureLength" && Math.Abs((timing.Value ?? 0) - 0.75) < 0.0001), $"{label}: measureLength was not preserved.");
}

static void AssertSequence<T>(IReadOnlyList<T> actual, IReadOnlyList<T> expected, string message)
    where T : IEquatable<T>
{
    Assert(actual.Count == expected.Count, $"{message} count={actual.Count}, expected={expected.Count}");
    for (var index = 0; index < actual.Count; index++)
    {
        Assert(actual[index].Equals(expected[index]), $"{message} index={index}, actual={actual[index]}, expected={expected[index]}");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
