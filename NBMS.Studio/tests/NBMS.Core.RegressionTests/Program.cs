using NBMS.Core.Import;
using NBMS.Core.Extensions;
using NBMS.Core.Models;
using NBMS.Core.Services;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var tests = new (string Name, Func<Task> Run)[]
{
    ("BMS #xxx02 measure length conversion", TestMeasureLengthConversionAsync),
    ("BGA/LAYER/POOR conversion", TestVisualConversionAsync),
    ("compact and pretty compact chart roundtrip", TestCompactChartRoundtripAsync),
    ("media event stable order", TestMediaEventStableOrderAsync),
    ("media layer state restore at start tick", TestMediaLayerStateRestoreAsync),
    ("no-bga equivalent skips media state", TestNoBgaEquivalentAsync),
    ("BMS media alternative file search", TestBmsMediaAlternativeSearchAsync),
    ("BMS audio alternative file search", TestBmsAudioAlternativeSearchAsync),
    ("audio archive short codec fixtures", TestAudioArchiveShortCodecFixturesAsync),
    ("BMS Shift_JIS charset import", TestShiftJisCharsetImportAsync),
    ("BMS #SCROLL import", TestScrollImportAsync),
    ("NBMS minimal BMS export", TestMinimalBmsExportAsync),
    ("BMS RANDOM directives preserve", TestRandomDirectivesPreserveAsync),
    ("BMS compatibility report", TestCompatibilityReportAsync),
    ("BMS LNOBJ long note metadata", TestLnObjLongNoteMetadataAsync),
    ("Timeline timing event lanes", TestTimingEventLanesAsync),
    ("BMS #STOP import", TestStopImportAsync),
    ("BMS dense #STOP import", TestDenseStopImportAsync),
    ("TimelineService dense BPM monotonic", TestDenseBpmTimelineMonotonicAsync),
    ("MeasureMap tiny measure length", TestTinyMeasureLengthGridAsync),
    ("BMS .Divergence source format", TestDivergenceSourceFormatAsync)
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

static Task TestMinimalBmsExportAsync()
{
    var header = new NbmsHeader
    {
        Title = "Export Test",
        Artist = "NBMS",
        Bpm = new BpmInfo { Initial = 120 }
    };
    var chart = new NbmsChart
    {
        ChartId = "export",
        Mode = "beat-7k",
        Resolution = 960,
        Timing =
        [
            new TimingEvent { Tick = 0, Type = "bpm", Value = 120 },
            new TimingEvent { Tick = 3840, Type = "measureLength", Value = 0.5 },
            new TimingEvent { Tick = 4800, Type = "bpm", Value = 150 },
            new TimingEvent { Tick = 5760, Type = "stop", DurationTicks = 240 }
        ],
        Notes =
        [
            new NoteEvent { Tick = 0, Lane = "key1", Type = "tap", AudioId = "kick" },
            new NoteEvent { Tick = 960, Lane = "scratch", Type = "hold", AudioId = "scratch", DurationTicks = 960 }
        ],
        BackgroundAudio =
        [
            new BackgroundAudioEvent { Tick = 0, AudioId = "bgm", Lane = "background1" }
        ],
        MediaEvents =
        [
            new MediaEvent { Tick = 0, MediaId = "base_bga", Type = "bga", Layer = 0 },
            new MediaEvent { Tick = 960, MediaId = "layer_bga", Type = "layer", Layer = 1 },
            new MediaEvent { Tick = 1920, MediaId = "poor_bga", Type = "poor", Layer = 0 }
        ]
    };

    var result = new BmsExportService().Export(
        header,
        chart,
        mediaFileNames: new Dictionary<string, string>
        {
            ["base_bga"] = "base.bmp",
            ["layer_bga"] = "layer.png",
            ["poor_bga"] = "poor.bmp"
        });
    Assert(result.BmsText.Contains("#TITLE Export Test", StringComparison.Ordinal), "BMS export should include title.");
    Assert(result.BmsText.Contains("#BPM 120", StringComparison.Ordinal), "BMS export should include initial BPM.");
    Assert(result.BmsText.Contains("#WAV", StringComparison.Ordinal), "BMS export should include WAV definitions.");
    Assert(result.BmsText.Contains("#BMP", StringComparison.Ordinal), "BMS export should include BMP definitions.");
    Assert(result.BmsText.Contains("#00011:", StringComparison.Ordinal), "BMS export should include key1 channel.");
    Assert(result.BmsText.Contains("#00056:", StringComparison.Ordinal), "BMS export should include scratch LN channel.");
    Assert(result.BmsText.Contains("#00004:", StringComparison.Ordinal), "BMS export should include BGA channel.");
    Assert(result.BmsText.Contains("#00007:", StringComparison.Ordinal), "BMS export should include LAYER channel.");
    Assert(result.BmsText.Contains("#00006:", StringComparison.Ordinal), "BMS export should include POOR channel.");
    Assert(result.BmsText.Contains("#00102:0.5", StringComparison.Ordinal), "BMS export should include measure length.");
    Assert(result.BmsText.Contains("#00108:", StringComparison.Ordinal), "BMS export should include extended BPM channel.");
    Assert(result.BmsText.Contains("#00209:", StringComparison.Ordinal), "BMS export should include STOP channel.");
    Assert(result.Issues.All(issue => issue.Severity != "Error"), "minimal export should not emit errors.");
    return Task.CompletedTask;
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

static async Task TestBmsAudioAlternativeSearchAsync()
{
    var directory = CreateTempDirectory();
    await File.WriteAllBytesAsync(Path.Combine(directory, "alarm1.ogg"), [0x4F, 0x67, 0x67, 0x53]);

    var resolved = BmsAudioAlternativeResolver.Resolve(directory, "alarm1.wav");

    Assert(resolved.IsAlternative, "audio alternative resolution should mark extension fallback.");
    Assert(resolved.RelativePath == "alarm1.ogg", "audio alternative resolution should find ogg for wav declaration.");
    Assert(File.Exists(resolved.SourcePath), "audio alternative source path should exist.");
}

static async Task TestAudioArchiveShortCodecFixturesAsync()
{
    var directory = CreateTempDirectory();
    var audioPath = Path.Combine(directory, "audio.nbma");
    var manifest = new AudioManifest
    {
        CodecRequired = ["flac", "ogg", "wav"],
        Entries =
        [
            CreateAudioFixtureEntry("short_wav", "audio/short_wav.wav", "wav", [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00]),
            CreateAudioFixtureEntry("short_ogg", "audio/short_ogg.ogg", "ogg", [0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00]),
            CreateAudioFixtureEntry("short_flac", "audio/short_flac.flac", "flac", [0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22])
        ]
    };

    using (var stream = File.Create(audioPath))
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        foreach (var entry in manifest.Entries)
        {
            var archiveEntry = archive.CreateEntry(entry.Path, CompressionLevel.NoCompression);
            await using var entryStream = archiveEntry.Open();
            await entryStream.WriteAsync(ResolveAudioFixtureBytes(entry.AudioId));
        }

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(manifestStream, manifest, NbmsJson.SerializerOptions);
    }

    var service = new AudioArchiveService();
    var loaded = await service.ReadManifestAsync(audioPath);
    AssertSequence(
        loaded.CodecRequired.Order(StringComparer.Ordinal).ToArray(),
        ["flac", "ogg", "wav"],
        "audio manifest should preserve short fixture codec requirements.");

    var issues = await service.ValidateArchiveEntriesAsync(audioPath, loaded);
    Assert(issues.Count == 0, "short audio fixtures should pass archive hash validation.");

    var extractDirectory = Path.Combine(directory, "extract");
    await service.ExtractAudioFilesAsync(
        audioPath,
        loaded,
        extractDirectory,
        loaded.Entries.ToDictionary(entry => entry.AudioId, entry => Path.GetFileName(entry.Path), StringComparer.Ordinal));

    Assert(File.Exists(Path.Combine(extractDirectory, "short_wav.wav")), "wav fixture should be extracted.");
    Assert(File.Exists(Path.Combine(extractDirectory, "short_ogg.ogg")), "ogg fixture should be extracted.");
    Assert(File.Exists(Path.Combine(extractDirectory, "short_flac.flac")), "flac fixture should be extracted.");
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

static async Task TestScrollImportAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Scroll Test
        #BPM 120
        #SCROLL01 0.5
        #SCROLL02 -2.25
        #001SC:0102
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    var scrollEvents = result.Chart.Timing
        .Where(timing => timing.Type == "scroll")
        .OrderBy(timing => timing.Tick)
        .ToArray();

    Assert(scrollEvents.Length == 2, "SCROLL channel events should be imported.");
    Assert(scrollEvents[0].Tick == 3840, $"first SCROLL tick mismatch: {scrollEvents[0].Tick}");
    Assert(Math.Abs((scrollEvents[0].Value ?? 0) - 0.5) < 0.0001, "first SCROLL value mismatch.");
    Assert(scrollEvents[0].ExtensionId == "nbms.scroll", "SCROLL extension id should be nbms.scroll.");
    Assert(scrollEvents[0].Event == "scroll", "SCROLL event name should be scroll.");
    Assert(scrollEvents[1].Tick == 5760, $"second SCROLL tick mismatch: {scrollEvents[1].Tick}");
    Assert(Math.Abs((scrollEvents[1].Value ?? 0) + 2.25) < 0.0001, "negative SCROLL value should be preserved.");
    Assert(result.Chart.Extensions.Any(extension => extension.Id == "nbms.scroll"), "nbms.scroll extension declaration should be added.");
}

static async Task TestRandomDirectivesPreserveAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Random Test
        #BPM 120
        #RANDOM 3
        #IF 1
        #00111:01
        #ELSE
        #00112:01
        #ENDIF
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    Assert(result.Chart.Metadata is not null, "random directives should create bmsCompat metadata.");
    var metadata = result.Chart.Metadata.GetValueOrDefault();
    Assert(metadata.TryGetProperty("bmsCompat", out var bmsCompat), "bmsCompat metadata is missing.");
    var randomDirectives = bmsCompat.GetProperty("randomDirectives").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
    AssertSequence(
        randomDirectives,
        ["RANDOM 3", "IF 1", "ELSE", "ENDIF"],
        "random directives should be preserved in source order.");
    Assert(result.CompatibilityReport.RandomPreserved, "compatibility report should mark RANDOM as preserved.");
    Assert(result.CompatibilityReport.BranchNotExpanded, "compatibility report should mark RANDOM branch as not expanded.");
}

static async Task TestCompatibilityReportAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Compatibility Report Test
        #BPM 120
        #RANDOM 2
        #IF 1
        #ENDIF
        #UNKNOWNEXT value
        #001ZZ:01
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    Assert(result.CompatibilityReport.RandomPreserved, "randomPreserved should be true when RANDOM directives exist.");
    Assert(result.CompatibilityReport.BranchNotExpanded, "branchNotExpanded should be true when RANDOM directives exist.");
    Assert(result.CompatibilityReport.UnsupportedDirectives.Contains("#UNKNOWNEXT"), "unknown header should be reported.");
    Assert(result.CompatibilityReport.UnsupportedDirectives.Contains("#001ZZ"), "unknown channel should be reported.");

    var metadata = result.Chart.Metadata.GetValueOrDefault();
    var bmsCompat = metadata.GetProperty("bmsCompat");
    Assert(bmsCompat.GetProperty("randomPreserved").GetBoolean(), "metadata randomPreserved should be true.");
    Assert(bmsCompat.GetProperty("branchNotExpanded").GetBoolean(), "metadata branchNotExpanded should be true.");
    var unsupported = bmsCompat.GetProperty("unsupportedDirectives").EnumerateArray().Select(item => item.GetString()).ToArray();
    Assert(unsupported.Contains("#UNKNOWNEXT"), "metadata should include unsupported header.");
    Assert(unsupported.Contains("#001ZZ"), "metadata should include unsupported channel.");
}

static async Task TestLnObjLongNoteMetadataAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE LNOBJ Test
        #BPM 120
        #WAV01 lnend.wav
        #WAV02 start.wav
        #LNOBJ 01
        #00111:0201
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    Assert(result.CompatibilityReport.LnObj == "01", "LNOBJ should be preserved in compatibility report.");
    Assert(result.Chart.Extensions.Any(extension => extension.Id == "nbms.longNote"), "LNOBJ import should declare nbms.longNote.");
    Assert(!result.Chart.Timing.Any(timing => timing.Type == "lnobj"), "LNOBJ should not be emitted as a timing event.");
    Assert(result.Chart.Notes.Any(note =>
        note.Lane == "key1" &&
        note.Type == "hold" &&
        note.DurationTicks is > 0), "LNOBJ should resolve to a long note on the play lane.");

    var metadata = result.Chart.Metadata.GetValueOrDefault();
    var bmsCompat = metadata.GetProperty("bmsCompat");
    Assert(bmsCompat.GetProperty("lnObj").GetString() == "01", "LNOBJ should be stored in bmsCompat metadata.");
    var longNoteCompat = metadata.GetProperty("extensions").GetProperty("nbms.longNote").GetProperty("bmsCompat");
    Assert(longNoteCompat.GetProperty("lnObj").GetString() == "01", "LNOBJ should be stored in nbms.longNote bmsCompat metadata.");
}

static Task TestTimingEventLanesAsync()
{
    var chart = new NbmsChart
    {
        ChartId = "timing-lanes",
        Mode = "beat-7k",
        Resolution = 960,
        Timing =
        [
            new TimingEvent { Tick = 0, Type = "bpm", Value = 120 },
            new TimingEvent { Tick = 960, Type = "stop", DurationTicks = 120 },
            new TimingEvent { Tick = 1920, Type = "measureLength", Value = 0.5 }
        ]
    };

    var timeline = new TimelineService(new ExtensionRegistry()).BuildTimeline(chart);
    Assert(timeline.Any(item => item.Kind == "Timing" && item.Lane == "bpm" && item.Detail.StartsWith("BPM ")), "BPM should be on bpm lane.");
    Assert(timeline.Any(item => item.Kind == "Timing" && item.Lane == "stop" && item.Detail.StartsWith("STOP ")), "STOP should be on stop lane.");
    Assert(timeline.Any(item => item.Kind == "Timing" && item.Lane == "measure" && item.Detail.StartsWith("MEASURE ")), "measureLength should be on measure lane.");
    Assert(!timeline.Any(item => item.Kind == "Timing" && item.Detail.StartsWith("LNOBJ ")), "LNOBJ should not be a timing lane event.");
    return Task.CompletedTask;
}

static async Task TestStopImportAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Stop Test
        #BPM 120
        #STOP01 192
        #STOP02 48
        #00109:0102
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    var stops = result.Chart.Timing
        .Where(timing => timing.Type == "stop")
        .OrderBy(timing => timing.Tick)
        .ToArray();

    Assert(stops.Length == 2, "STOP channel events should be imported.");
    Assert(stops[0].Tick == 3840, $"first STOP tick mismatch: {stops[0].Tick}");
    Assert(stops[0].DurationTicks == 3840, $"first STOP duration mismatch: {stops[0].DurationTicks}");
    Assert(stops[1].Tick == 5760, $"second STOP tick mismatch: {stops[1].Tick}");
    Assert(stops[1].DurationTicks == 960, $"second STOP duration mismatch: {stops[1].DurationTicks}");
}

static async Task TestDenseStopImportAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Dense Stop Test
        #BPM 120
        #STOP01 48
        #STOP02 96
        #STOP03 24
        #00109:010203
        #00109:02
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    var stops = result.Chart.Timing
        .Where(timing => timing.Type == "stop")
        .OrderBy(timing => timing.Tick)
        .ThenBy(timing => timing.DurationTicks)
        .ToArray();

    Assert(stops.Length == 4, $"dense STOP events should not be dropped. actual={stops.Length}");
    Assert(stops.Count(timing => timing.Tick == 3840) == 2, "same tick STOP events should be preserved.");

    var map = new TimelineService(new ExtensionRegistry()).BuildTickTimeMap(result.Chart, [3840, 5120, 6400, 7680]);
    Assert(map[5120] > map[3840], "time should advance after first dense STOP position.");
    Assert(map[7680] > map[6400], "time should advance after repeated STOP line.");
}

static async Task TestDenseBpmTimelineMonotonicAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Dense BPM Test
        #BPM 120
        #BPM01 90
        #BPM02 180
        #BPM03 60
        #BPM04 240
        #00108:01020304
        #00208:04030201
        #00308:01040203
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    var ticks = result.Chart.Timing
        .Where(timing => timing.Type == "bpm")
        .Select(timing => timing.Tick)
        .Concat(Enumerable.Range(0, 13).Select(index => index * 960))
        .Distinct()
        .Order()
        .ToArray();
    var map = new TimelineService(new ExtensionRegistry()).BuildTickTimeMap(result.Chart, ticks);
    var previous = -1.0;
    foreach (var tick in ticks)
    {
        var seconds = map[tick];
        Assert(seconds >= previous, $"tick->seconds should be monotonic. tick={tick} seconds={seconds} previous={previous}");
        previous = seconds;
    }
}

static async Task TestTinyMeasureLengthGridAsync()
{
    var bmsPath = await WriteTempBmsAsync(
        """
        #TITLE Tiny Measure Test
        #BPM 120
        #00102:0.0009765625
        #00202:1
        """);

    var result = await new BmsImportService().ImportAsync(bmsPath);
    var measureMap = MeasureMap.FromChart(result.Chart);
    var lines = measureMap.BuildGridLines(0, result.Chart.Resolution * 8, 4);

    Assert(lines.Count > 0, "tiny measure grid should still produce grid lines.");
    Assert(lines.Select(line => line.Tick).SequenceEqual(lines.Select(line => line.Tick).Order()), "tiny measure grid should be ordered.");
    Assert(lines.All(line => line.Tick >= 0), "tiny measure grid should not produce negative ticks for a non-negative range.");
}

static async Task TestDivergenceSourceFormatAsync()
{
    var directory = CreateTempDirectory();
    var bmsPath = Path.Combine(directory, "chart.Divergence");
    await File.WriteAllTextAsync(
        bmsPath,
        """
        #TITLE Divergence Test
        #BPM 120
        """.ReplaceLineEndings(Environment.NewLine));

    var result = await new BmsImportService().ImportAsync(bmsPath);
    Assert(result.Chart.Metadata is not null, "Divergence source format should create bmsCompat metadata.");
    var metadata = result.Chart.Metadata.GetValueOrDefault();
    Assert(metadata.TryGetProperty("bmsCompat", out var bmsCompat), "bmsCompat metadata is missing.");
    Assert(bmsCompat.GetProperty("sourceFormat").GetString() == "divergence", "sourceFormat should be divergence.");
    Assert(bmsCompat.GetProperty("sourceExtension").GetString() == ".divergence", "sourceExtension should be normalized.");
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

static AudioEntry CreateAudioFixtureEntry(string audioId, string archivePath, string codec, byte[] bytes)
{
    return new AudioEntry
    {
        AudioId = audioId,
        Path = archivePath,
        Codec = codec,
        SampleRate = 44100,
        Channels = 2,
        DurationMs = 100,
        Hash = ComputeFixtureHash(bytes)
    };
}

static byte[] ResolveAudioFixtureBytes(string audioId)
{
    return audioId switch
    {
        "short_wav" => [0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00],
        "short_ogg" => [0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00],
        "short_flac" => [0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22],
        _ => []
    };
}

static string ComputeFixtureHash(byte[] bytes)
{
    var hash = SHA256.HashData(bytes);
    return "sha256-" + Convert.ToHexString(hash).ToLowerInvariant();
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
