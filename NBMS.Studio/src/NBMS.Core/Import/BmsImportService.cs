using System.Globalization;
using System.Text.RegularExpressions;
using NBMS.Core.Models;

namespace NBMS.Core.Import;

public sealed partial class BmsImportService
{
    private const int Resolution = 960;
    private const int MeasureTicks = Resolution * 4;

    private static readonly Dictionary<string, string> NoteChannels = new()
    {
        ["11"] = "key1",
        ["12"] = "key2",
        ["13"] = "key3",
        ["14"] = "key4",
        ["15"] = "key5",
        ["16"] = "scratch",
        ["18"] = "key6",
        ["19"] = "key7",
        ["21"] = "key8",
        ["22"] = "key9",
        ["23"] = "key10",
        ["24"] = "key11",
        ["25"] = "key12",
        ["26"] = "scratch2",
        ["28"] = "key13",
        ["29"] = "key14"
    };

    private static readonly Dictionary<string, string> LongNoteChannels = new()
    {
        ["51"] = "key1",
        ["52"] = "key2",
        ["53"] = "key3",
        ["54"] = "key4",
        ["55"] = "key5",
        ["56"] = "scratch",
        ["58"] = "key6",
        ["59"] = "key7",
        ["61"] = "key8",
        ["62"] = "key9",
        ["63"] = "key10",
        ["64"] = "key11",
        ["65"] = "key12",
        ["66"] = "scratch2",
        ["68"] = "key13",
        ["69"] = "key14"
    };

    private static readonly string[] BackgroundLanes =
    [
        "background1",
        "background2",
        "background3",
        "background4",
        "background5",
        "background6",
        "background7",
        "background8"
    ];

    public async Task<BmsImportResult> ImportAsync(string bmsPath, CancellationToken cancellationToken = default)
    {
        var lines = await File.ReadAllLinesAsync(bmsPath, cancellationToken);
        var doc = new BmsImportDocument { SourcePath = bmsPath };

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || !line.StartsWith('#'))
            {
                continue;
            }

            var channelMatch = ChannelLineRegex().Match(line);
            if (channelMatch.Success)
            {
                doc.ChannelLines.Add(new BmsChannelLine
                {
                    Measure = int.Parse(channelMatch.Groups["measure"].Value, CultureInfo.InvariantCulture),
                    Channel = channelMatch.Groups["channel"].Value.ToUpperInvariant(),
                    Data = channelMatch.Groups["data"].Value.Trim().ToUpperInvariant()
                });
                continue;
            }

            var headerMatch = HeaderLineRegex().Match(line);
            if (!headerMatch.Success)
            {
                continue;
            }

            ApplyHeaderLine(doc, headerMatch.Groups["key"].Value.ToUpperInvariant(), headerMatch.Groups["value"].Value.Trim());
        }

        return BuildResult(doc);
    }

    private static void ApplyHeaderLine(BmsImportDocument doc, string key, string value)
    {
        if (key == "TITLE")
        {
            doc.Title = value;
        }
        else if (key == "ARTIST")
        {
            doc.Artist = value;
        }
        else if (key == "GENRE")
        {
            doc.Genre = value;
        }
        else if (key == "BPM" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm))
        {
            doc.Bpm = bpm;
        }
        else if (key == "PLAYLEVEL" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
        {
            doc.PlayLevel = level;
        }
        else if (key == "LNOBJ" && value.Length >= 2)
        {
            doc.LnObj = value[..2].ToUpperInvariant();
        }
        else if (key.StartsWith("WAV", StringComparison.Ordinal) && key.Length == 5)
        {
            doc.Wav[key[3..]] = value;
        }
        else if (key.StartsWith("BPM", StringComparison.Ordinal) && key.Length == 5 &&
                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var indexedBpm))
        {
            doc.BpmDefinitions[key[3..]] = indexedBpm;
        }
        else if (key.StartsWith("STOP", StringComparison.Ordinal) && key.Length == 6 &&
                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var stopValue))
        {
            doc.StopDefinitions[key[4..]] = stopValue;
        }
    }

    private static BmsImportResult BuildResult(BmsImportDocument doc)
    {
        var measureStarts = BuildMeasureStarts(doc);
        var wavToAudioId = doc.Wav.ToDictionary(pair => pair.Key, pair => CreateAudioId(pair.Key, pair.Value), StringComparer.Ordinal);
        var mode = ResolveMode(doc);
        var chart = CreateBaseChart(doc, mode);
        var backgroundLaneCountsByTick = new Dictionary<int, int>();
        var longNoteStarts = new Dictionary<string, PendingLongNote>(StringComparer.Ordinal);
        var lnObjStarts = new Dictionary<string, PendingLongNote>(StringComparer.Ordinal);

        AddBarLines(chart, measureStarts);

        foreach (var line in doc.ChannelLines.OrderBy(line => line.Measure).ThenBy(line => line.Channel, StringComparer.Ordinal))
        {
            var tokens = SplitTokens(line.Data);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (line.Channel == "02")
            {
                continue;
            }

            for (var index = 0; index < tokens.Count; index++)
            {
                var token = tokens[index];
                if (token == "00")
                {
                    continue;
                }

                var tick = ResolveTick(line.Measure, index, tokens.Count, measureStarts, doc.MeasureLengths);
                AddChannelEvent(doc, chart, line.Channel, token, tick, wavToAudioId, backgroundLaneCountsByTick, longNoteStarts, lnObjStarts);
            }
        }

        foreach (var pending in longNoteStarts.Values.Concat(lnObjStarts.Values))
        {
            chart.Notes.Add(new NoteEvent
            {
                Tick = pending.Tick,
                Lane = pending.Lane,
                Type = "tap",
                AudioId = pending.AudioId
            });
        }

        chart.Timing = chart.Timing
            .OrderBy(timing => timing.Tick)
            .ThenBy(timing => TimingSortOrder(timing.Type))
            .ToList();
        chart.Notes = chart.Notes
            .OrderBy(note => note.Tick)
            .ThenBy(note => note.Lane, StringComparer.Ordinal)
            .ToList();
        chart.BackgroundAudio = chart.BackgroundAudio
            .OrderBy(item => item.Tick)
            .ThenBy(item => item.Lane, StringComparer.Ordinal)
            .ToList();

        var header = new NbmsHeader
        {
            Id = "converted." + Path.GetFileNameWithoutExtension(doc.SourcePath).ToLowerInvariant(),
            Title = doc.Title,
            Artist = doc.Artist,
            Genre = doc.Genre,
            Bpm = new BpmInfo { Initial = doc.Bpm, Min = ResolveMinBpm(doc), Max = ResolveMaxBpm(doc) },
            Audio = new FileReference { File = "audio.nbma" },
            Charts =
            [
                new()
                {
                    Id = "main",
                    File = "score/main.nbmc",
                    Mode = mode,
                    Difficulty = doc.PlayLevel,
                    LevelName = "main"
                }
            ],
            Rights = new RightsInfo
            {
                Music = doc.Artist,
                Chart = doc.Artist,
                SoundSource = "Converted BMS audio",
                License = "Converted from BMS; rights unspecified"
            },
            Security = new SecurityInfo { Signed = false, Encrypted = false, EditPolicy = "open" }
        };

        return new BmsImportResult(header, chart, doc.Wav);
    }

    private static NbmsChart CreateBaseChart(BmsImportDocument doc, string mode)
    {
        return new NbmsChart
        {
            ChartId = "main",
            Mode = mode,
            Resolution = Resolution,
            Lanes = CreateLanes(mode),
            Timing =
            [
                new() { Tick = 0, Type = "bpm", Value = doc.Bpm }
            ]
        };
    }

    private static void AddChannelEvent(
        BmsImportDocument doc,
        NbmsChart chart,
        string channel,
        string token,
        int tick,
        IReadOnlyDictionary<string, string> wavToAudioId,
        Dictionary<int, int> backgroundLaneCountsByTick,
        Dictionary<string, PendingLongNote> longNoteStarts,
        Dictionary<string, PendingLongNote> lnObjStarts)
    {
        if (channel == "01" && wavToAudioId.TryGetValue(token, out var backgroundAudioId))
        {
            var lane = ResolveBackgroundLane(tick, backgroundLaneCountsByTick);
            chart.BackgroundAudio.Add(new BackgroundAudioEvent { Tick = tick, AudioId = backgroundAudioId, Lane = lane });
        }
        else if (channel == "03" && TryParseHex(token, out var directBpm))
        {
            chart.Timing.Add(new TimingEvent { Tick = tick, Type = "bpm", Value = directBpm });
        }
        else if (channel == "08" && doc.BpmDefinitions.TryGetValue(token, out var bpm))
        {
            chart.Timing.Add(new TimingEvent { Tick = tick, Type = "bpm", Value = bpm });
        }
        else if (channel == "09" && doc.StopDefinitions.TryGetValue(token, out var stopValue))
        {
            chart.Timing.Add(new TimingEvent { Tick = tick, Type = "stop", DurationTicks = StopValueToTicks(stopValue) });
        }
        else if (LongNoteChannels.TryGetValue(channel, out var longLane) && wavToAudioId.TryGetValue(token, out var longAudioId))
        {
            AddLongNote(chart, longNoteStarts, longLane, tick, longAudioId);
        }
        else if (NoteChannels.TryGetValue(channel, out var lane) && wavToAudioId.TryGetValue(token, out var audioId))
        {
            AddNormalOrLnObjNote(doc, chart, lnObjStarts, lane, token, tick, audioId);
        }
    }

    private static void AddLongNote(
        NbmsChart chart,
        Dictionary<string, PendingLongNote> starts,
        string lane,
        int tick,
        string audioId)
    {
        if (starts.Remove(lane, out var start))
        {
            chart.Notes.Add(new NoteEvent
            {
                Tick = start.Tick,
                Lane = lane,
                Type = "hold",
                AudioId = start.AudioId,
                DurationTicks = Math.Max(1, tick - start.Tick)
            });
            return;
        }

        starts[lane] = new PendingLongNote(tick, lane, audioId);
    }

    private static void AddNormalOrLnObjNote(
        BmsImportDocument doc,
        NbmsChart chart,
        Dictionary<string, PendingLongNote> starts,
        string lane,
        string token,
        int tick,
        string audioId)
    {
        if (string.Equals(doc.LnObj, token, StringComparison.Ordinal) && starts.Remove(lane, out var start))
        {
            chart.Notes.Add(new NoteEvent
            {
                Tick = start.Tick,
                Lane = lane,
                Type = "hold",
                AudioId = start.AudioId,
                DurationTicks = Math.Max(1, tick - start.Tick)
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(doc.LnObj))
        {
            if (starts.Remove(lane, out var previous))
            {
                chart.Notes.Add(new NoteEvent
                {
                    Tick = previous.Tick,
                    Lane = previous.Lane,
                    Type = "tap",
                    AudioId = previous.AudioId
                });
            }

            starts[lane] = new PendingLongNote(tick, lane, audioId);
            return;
        }

        chart.Notes.Add(new NoteEvent { Tick = tick, Lane = lane, Type = "tap", AudioId = audioId });
    }

    private static List<LaneDefinition> CreateLanes(string mode)
    {
        var lanes = new List<LaneDefinition>();
        if (mode == "beat-14k")
        {
            lanes.Add(new LaneDefinition { Id = "scratch", Type = "scratch", Index = 0 });
            for (var key = 1; key <= 14; key++)
            {
                lanes.Add(new LaneDefinition { Id = $"key{key}", Type = "key", Index = key });
            }

            lanes.Add(new LaneDefinition { Id = "scratch2", Type = "scratch", Index = 15 });
        }
        else
        {
            lanes.Add(new LaneDefinition { Id = "scratch", Type = "scratch", Index = 0 });
            for (var key = 1; key <= 7; key++)
            {
                lanes.Add(new LaneDefinition { Id = $"key{key}", Type = "key", Index = key });
            }
        }

        var backgroundStartIndex = lanes.Count;
        for (var index = 0; index < BackgroundLanes.Length; index++)
        {
            lanes.Add(new LaneDefinition
            {
                Id = BackgroundLanes[index],
                Type = "background-audio",
                Index = backgroundStartIndex + index
            });
        }

        return lanes;
    }

    private static string ResolveMode(BmsImportDocument doc)
    {
        return doc.ChannelLines.Any(line =>
            NoteChannels.TryGetValue(line.Channel, out var lane) && lane is "scratch2" or "key8" or "key9" or "key10" or "key11" or "key12" or "key13" or "key14" ||
            LongNoteChannels.TryGetValue(line.Channel, out var longLane) && longLane is "scratch2" or "key8" or "key9" or "key10" or "key11" or "key12" or "key13" or "key14")
            ? "beat-14k"
            : "beat-7k";
    }

    private static Dictionary<int, int> BuildMeasureStarts(BmsImportDocument doc)
    {
        foreach (var line in doc.ChannelLines.Where(line => line.Channel == "02"))
        {
            if (double.TryParse(line.Data, NumberStyles.Float, CultureInfo.InvariantCulture, out var length) && length > 0)
            {
                doc.MeasureLengths[line.Measure] = length;
            }
        }

        var maxMeasure = doc.ChannelLines.Count == 0 ? 0 : doc.ChannelLines.Max(line => line.Measure);
        var measureStarts = new Dictionary<int, int>();
        var tick = 0;

        for (var measure = 0; measure <= maxMeasure + 1; measure++)
        {
            measureStarts[measure] = tick;
            tick += MeasureLengthToTicks(doc.MeasureLengths.GetValueOrDefault(measure, 1.0));
        }

        return measureStarts;
    }

    private static void AddBarLines(NbmsChart chart, Dictionary<int, int> measureStarts)
    {
        foreach (var pair in measureStarts.OrderBy(pair => pair.Key))
        {
            chart.Timing.Add(new TimingEvent { Tick = pair.Value, Type = "bar" });
        }
    }

    private static int ResolveTick(
        int measure,
        int index,
        int tokenCount,
        IReadOnlyDictionary<int, int> measureStarts,
        IReadOnlyDictionary<int, double> measureLengths)
    {
        var measureStart = measureStarts.GetValueOrDefault(measure, measure * MeasureTicks);
        var measureTicks = MeasureLengthToTicks(measureLengths.GetValueOrDefault(measure, 1.0));
        return measureStart + (int)Math.Round((double)measureTicks * index / tokenCount);
    }

    private static int MeasureLengthToTicks(double length)
    {
        return Math.Max(1, (int)Math.Round(MeasureTicks * length));
    }

    private static int StopValueToTicks(double stopValue)
    {
        return Math.Max(0, (int)Math.Round(stopValue * MeasureTicks / 192.0));
    }

    private static string ResolveBackgroundLane(int tick, Dictionary<int, int> countsByTick)
    {
        var count = countsByTick.GetValueOrDefault(tick);
        countsByTick[tick] = count + 1;
        return BackgroundLanes[count % BackgroundLanes.Length];
    }

    private static double ResolveMinBpm(BmsImportDocument doc)
    {
        return new[] { doc.Bpm }.Concat(doc.BpmDefinitions.Values).Where(value => value > 0).DefaultIfEmpty(doc.Bpm).Min();
    }

    private static double ResolveMaxBpm(BmsImportDocument doc)
    {
        return new[] { doc.Bpm }.Concat(doc.BpmDefinitions.Values).Where(value => value > 0).DefaultIfEmpty(doc.Bpm).Max();
    }

    private static int TimingSortOrder(string type)
    {
        return type switch
        {
            "bar" => 0,
            "bpm" => 1,
            "stop" => 2,
            _ => 9
        };
    }

    private static List<string> SplitTokens(string data)
    {
        if (data.Length % 2 != 0)
        {
            return [];
        }

        var tokens = new List<string>();
        for (var index = 0; index < data.Length; index += 2)
        {
            tokens.Add(data.Substring(index, 2));
        }

        return tokens;
    }

    private static bool TryParseHex(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
    }

    private static string CreateAudioId(string wavKey, string fileName)
    {
        return $"wav_{wavKey.ToLowerInvariant()}_{Path.GetFileNameWithoutExtension(fileName)}";
    }

    [GeneratedRegex("^#(?<measure>[0-9]{3})(?<channel>[0-9A-Z]{2}):(?<data>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelLineRegex();

    [GeneratedRegex("^#(?<key>[A-Za-z][A-Za-z0-9]*)(?:\\s+(?<value>.*))?$")]
    private static partial Regex HeaderLineRegex();

    private sealed record PendingLongNote(int Tick, string Lane, string AudioId);
}

public sealed record BmsImportResult(NbmsHeader Header, NbmsChart Chart, Dictionary<string, string> WavFiles);

internal sealed class BmsImportDocument
{
    public string SourcePath { get; set; } = "";
    public string Title { get; set; } = "Untitled";
    public string Artist { get; set; } = "Unknown Artist";
    public string Genre { get; set; } = "";
    public double Bpm { get; set; } = 130;
    public int PlayLevel { get; set; }
    public string? LnObj { get; set; }
    public Dictionary<string, string> Wav { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> BpmDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> StopDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, double> MeasureLengths { get; } = [];
    public List<BmsChannelLine> ChannelLines { get; } = [];
}

internal sealed class BmsChannelLine
{
    public int Measure { get; set; }
    public string Channel { get; set; } = "";
    public string Data { get; set; } = "";
}
