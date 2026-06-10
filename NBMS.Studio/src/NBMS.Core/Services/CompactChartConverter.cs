using System.Text.Json;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class CompactChartConverter
{
    private static readonly Dictionary<string, int> NoteTypeIndexes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tap"] = 0,
        ["hold"] = 1,
        ["mine"] = 2
    };

    private static readonly string[] NoteTypes = ["tap", "hold", "mine"];

    public CompactChart ToCompact(NbmsChart chart)
    {
        var lanes = BuildLaneDictionary(chart);
        var audio = BuildAudioDictionary(chart);
        var media = BuildMediaDictionary(chart);
        var laneIndexes = BuildIndex(lanes);
        var audioIndexes = BuildIndex(audio);
        var mediaIndexes = BuildIndex(media);

        return new CompactChart
        {
            Format = "NBMS-CHART",
            Version = "0.2.0",
            Encoding = "compact-json",
            ChartId = chart.ChartId,
            Mode = chart.Mode,
            Resolution = chart.Resolution,
            Dictionary = new CompactChartDictionary
            {
                Lanes = lanes,
                Audio = audio,
                Media = media
            },
            Timing = chart.Timing
                .OrderBy(timing => timing.Tick)
                .ThenBy(timing => TimingSortOrder(timing.Type))
                .Select(ToCompactTiming)
                .ToList(),
            Notes = chart.Notes
                .OrderBy(note => note.Tick)
                .ThenBy(note => ResolveIndex(laneIndexes, note.Lane))
                .Select(note => ToCompactNote(note, laneIndexes, audioIndexes))
                .ToList(),
            BackgroundAudio = chart.BackgroundAudio
                .OrderBy(item => item.Tick)
                .ThenBy(item => ResolveIndex(audioIndexes, item.AudioId))
                .ThenBy(item => ResolveIndex(laneIndexes, item.Lane))
                .Select(item => ToCompactBackgroundAudio(item, laneIndexes, audioIndexes))
                .ToList(),
            MediaEvents = chart.MediaEvents
                .OrderBy(item => item.Tick)
                .ThenBy(item => item.Layer ?? 0)
                .ThenBy(item => ResolveIndex(mediaIndexes, item.MediaId))
                .Select(item => ToCompactMediaEvent(item, mediaIndexes))
                .ToList(),
            Metadata = chart.Metadata,
            Extensions = chart.Extensions.Count == 0 ? null : chart.Extensions
        };
    }

    public NbmsChart FromCompact(CompactChart compact)
    {
        if (!compact.Encoding.Equals("compact-json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsupported chart encoding: {compact.Encoding}");
        }

        var chart = new NbmsChart
        {
            Format = "NBMS-CHART",
            Version = "0.1.0",
            ChartId = compact.ChartId,
            Mode = compact.Mode,
            Resolution = compact.Resolution,
            Lanes = compact.Dictionary.Lanes
                .Select((lane, index) => new LaneDefinition
                {
                    Id = lane,
                    Type = InferLaneType(lane),
                    Index = index
                })
                .ToList(),
            Timing = compact.Timing.Select(FromCompactTiming).ToList(),
            Notes = compact.Notes.Select(tuple => FromCompactNote(tuple, compact.Dictionary)).ToList(),
            BackgroundAudio = compact.BackgroundAudio.Select(tuple => FromCompactBackgroundAudio(tuple, compact.Dictionary)).ToList(),
            MediaEvents = compact.MediaEvents.Select(tuple => FromCompactMediaEvent(tuple, compact.Dictionary)).ToList(),
            Metadata = compact.Metadata,
            Extensions = compact.Extensions ?? []
        };

        return chart;
    }

    private static List<string> BuildLaneDictionary(NbmsChart chart)
    {
        var lanes = new List<string>();
        foreach (var lane in chart.Lanes.OrderBy(lane => lane.Index).Select(lane => lane.Id))
        {
            AddUnique(lanes, lane);
        }

        foreach (var lane in chart.Notes.Select(note => note.Lane)
                     .Concat(chart.BackgroundAudio.Select(item => item.Lane ?? "")))
        {
            AddUnique(lanes, lane);
        }

        return lanes;
    }

    private static List<string> BuildAudioDictionary(NbmsChart chart)
    {
        var audio = new List<string>();
        foreach (var audioId in chart.Notes.Select(note => note.AudioId ?? "")
                     .Concat(chart.BackgroundAudio.Select(item => item.AudioId)))
        {
            AddUnique(audio, audioId);
        }

        return audio;
    }

    private static List<string> BuildMediaDictionary(NbmsChart chart)
    {
        var media = new List<string>();
        foreach (var mediaId in chart.MediaEvents.Select(item => item.MediaId))
        {
            AddUnique(media, mediaId);
        }

        return media;
    }

    private static object?[] ToCompactTiming(TimingEvent timing)
    {
        object?[] values = timing.Type switch
        {
            "bpm" => [timing.Tick, "bpm", timing.Value],
            "bar" => [timing.Tick, "bar"],
            "stop" => [timing.Tick, "stop", timing.DurationTicks],
            "measureLength" => [timing.Tick, "measureLength", timing.Value],
            "scroll" => [timing.Tick, "scroll", timing.Value],
            "speed" => [timing.Tick, "speed", timing.Value],
            "lnobj" => [timing.Tick, "lnobj", timing.Event],
            _ => [timing.Tick, timing.Type, timing.Value, timing.DurationTicks, timing.Event]
        };

        return TrimTrailingNulls(values);
    }

    private static object?[] ToCompactNote(
        NoteEvent note,
        IReadOnlyDictionary<string, int> laneIndexes,
        IReadOnlyDictionary<string, int> audioIndexes)
    {
        var typeIndex = NoteTypeIndexes.GetValueOrDefault(note.Type, 0);
        int? audioIndex = string.IsNullOrWhiteSpace(note.AudioId)
            ? null
            : ResolveIndex(audioIndexes, note.AudioId);

        object?[] values =
        [
            note.Tick,
            ResolveIndex(laneIndexes, note.Lane),
            typeIndex,
            audioIndex,
            note.DurationTicks is > 0 ? note.DurationTicks : null,
            IsDefaultVolume(note.Volume) ? null : note.Volume,
            IsDefaultPan(note.Pan) ? null : note.Pan
        ];

        return TrimTrailingNulls(values);
    }

    private static object?[] ToCompactBackgroundAudio(
        BackgroundAudioEvent item,
        IReadOnlyDictionary<string, int> laneIndexes,
        IReadOnlyDictionary<string, int> audioIndexes)
    {
        object?[] values =
        [
            item.Tick,
            ResolveIndex(audioIndexes, item.AudioId),
            string.IsNullOrWhiteSpace(item.Lane) ? null : ResolveIndex(laneIndexes, item.Lane),
            IsDefaultVolume(item.Volume) ? null : item.Volume,
            IsDefaultPan(item.Pan) ? null : item.Pan
        ];

        return TrimTrailingNulls(values);
    }

    private static object?[] ToCompactMediaEvent(
        MediaEvent item,
        IReadOnlyDictionary<string, int> mediaIndexes)
    {
        object?[] values =
        [
            item.Tick,
            ResolveIndex(mediaIndexes, item.MediaId),
            item.Type,
            item.Layer is null or 0 ? null : item.Layer
        ];

        return TrimTrailingNulls(values);
    }

    private static TimingEvent FromCompactTiming(object?[] tuple)
    {
        RequireLength(tuple, 2, "timing");
        var tick = ReadInt(tuple[0], "timing.tick");
        var type = ReadString(tuple[1], "timing.type");

        return type switch
        {
            "bpm" => new TimingEvent { Tick = tick, Type = "bpm", Value = ReadOptionalDouble(tuple, 2, "timing.value") },
            "bar" => new TimingEvent { Tick = tick, Type = "bar" },
            "stop" => new TimingEvent { Tick = tick, Type = "stop", DurationTicks = ReadOptionalInt(tuple, 2, "timing.durationTicks") },
            "measureLength" => new TimingEvent { Tick = tick, Type = "measureLength", Value = ReadOptionalDouble(tuple, 2, "timing.value") },
            "scroll" => new TimingEvent { Tick = tick, Type = "scroll", Value = ReadOptionalDouble(tuple, 2, "timing.value") },
            "speed" => new TimingEvent { Tick = tick, Type = "speed", Value = ReadOptionalDouble(tuple, 2, "timing.value") },
            "lnobj" => new TimingEvent { Tick = tick, Type = "lnobj", Event = ReadOptionalString(tuple, 2, "timing.event") },
            _ => new TimingEvent
            {
                Tick = tick,
                Type = type,
                Value = ReadOptionalDouble(tuple, 2, "timing.value"),
                DurationTicks = ReadOptionalInt(tuple, 3, "timing.durationTicks"),
                Event = ReadOptionalString(tuple, 4, "timing.event")
            }
        };
    }

    private static NoteEvent FromCompactNote(object?[] tuple, CompactChartDictionary dictionary)
    {
        RequireLength(tuple, 3, "note");
        var lane = ResolveDictionaryValue(dictionary.Lanes, ReadInt(tuple[1], "note.laneIndex"), "lane");
        var typeIndex = ReadInt(tuple[2], "note.typeIndex");
        var audioIndex = ReadOptionalInt(tuple, 3, "note.audioIndex");
        var durationTicks = ReadOptionalInt(tuple, 4, "note.durationTicks");

        return new NoteEvent
        {
            Tick = ReadInt(tuple[0], "note.tick"),
            Lane = lane,
            Type = typeIndex >= 0 && typeIndex < NoteTypes.Length ? NoteTypes[typeIndex] : "tap",
            AudioId = audioIndex is null ? null : ResolveDictionaryValue(dictionary.Audio, audioIndex.Value, "audio"),
            DurationTicks = durationTicks,
            Volume = ReadOptionalDouble(tuple, 5, "note.volume"),
            Pan = ReadOptionalDouble(tuple, 6, "note.pan")
        };
    }

    private static BackgroundAudioEvent FromCompactBackgroundAudio(object?[] tuple, CompactChartDictionary dictionary)
    {
        RequireLength(tuple, 2, "backgroundAudio");
        var laneIndex = ReadOptionalInt(tuple, 2, "backgroundAudio.laneIndex");

        return new BackgroundAudioEvent
        {
            Tick = ReadInt(tuple[0], "backgroundAudio.tick"),
            AudioId = ResolveDictionaryValue(dictionary.Audio, ReadInt(tuple[1], "backgroundAudio.audioIndex"), "audio"),
            Lane = laneIndex is null ? null : ResolveDictionaryValue(dictionary.Lanes, laneIndex.Value, "lane"),
            Volume = ReadOptionalDouble(tuple, 3, "backgroundAudio.volume"),
            Pan = ReadOptionalDouble(tuple, 4, "backgroundAudio.pan")
        };
    }

    private static MediaEvent FromCompactMediaEvent(object?[] tuple, CompactChartDictionary dictionary)
    {
        RequireLength(tuple, 3, "mediaEvent");
        return new MediaEvent
        {
            Tick = ReadInt(tuple[0], "mediaEvent.tick"),
            MediaId = ResolveDictionaryValue(dictionary.Media, ReadInt(tuple[1], "mediaEvent.mediaIndex"), "media"),
            Type = ReadString(tuple[2], "mediaEvent.type"),
            Layer = ReadOptionalInt(tuple, 3, "mediaEvent.layer")
        };
    }

    private static Dictionary<string, int> BuildIndex(IReadOnlyList<string> values)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            index[values[i]] = i;
        }

        return index;
    }

    private static int ResolveIndex(IReadOnlyDictionary<string, int> index, string? value)
    {
        if (value is not null && index.TryGetValue(value, out var result))
        {
            return result;
        }

        return -1;
    }

    private static void AddUnique(List<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || values.Contains(value, StringComparer.Ordinal))
        {
            return;
        }

        values.Add(value);
    }

    private static object?[] TrimTrailingNulls(object?[] values)
    {
        var length = values.Length;
        while (length > 0 && values[length - 1] is null)
        {
            length--;
        }

        if (length == values.Length)
        {
            return values;
        }

        var trimmed = new object?[length];
        Array.Copy(values, trimmed, length);
        return trimmed;
    }

    private static bool IsDefaultVolume(double? volume)
    {
        return volume is null || Math.Abs(volume.Value - 1.0) < double.Epsilon;
    }

    private static bool IsDefaultPan(double? pan)
    {
        return pan is null || Math.Abs(pan.Value) < double.Epsilon;
    }

    private static int TimingSortOrder(string type)
    {
        return type switch
        {
            "bar" => 0,
            "bpm" => 1,
            "stop" => 2,
            "measureLength" => 3,
            _ => 9
        };
    }

    private static string InferLaneType(string laneId)
    {
        if (laneId.StartsWith("background", StringComparison.OrdinalIgnoreCase))
        {
            return "background-audio";
        }

        if (laneId.Contains("scratch", StringComparison.OrdinalIgnoreCase))
        {
            return "scratch";
        }

        return "key";
    }

    private static void RequireLength(object?[] tuple, int length, string name)
    {
        if (tuple.Length < length)
        {
            throw new InvalidDataException($"{name} tuple requires at least {length} values.");
        }
    }

    private static string ResolveDictionaryValue(IReadOnlyList<string> values, int index, string dictionaryName)
    {
        if (index < 0 || index >= values.Count)
        {
            throw new InvalidDataException($"{dictionaryName} dictionary index is out of range: {index}");
        }

        return values[index];
    }

    private static int? ReadOptionalInt(object?[] tuple, int index, string name)
    {
        return index >= tuple.Length || IsNull(tuple[index]) ? null : ReadInt(tuple[index], name);
    }

    private static double? ReadOptionalDouble(object?[] tuple, int index, string name)
    {
        return index >= tuple.Length || IsNull(tuple[index]) ? null : ReadDouble(tuple[index], name);
    }

    private static string? ReadOptionalString(object?[] tuple, int index, string name)
    {
        return index >= tuple.Length || IsNull(tuple[index]) ? null : ReadString(tuple[index], name);
    }

    private static int ReadInt(object? value, string name)
    {
        if (value is int intValue)
        {
            return intValue;
        }

        if (value is long longValue)
        {
            return checked((int)longValue);
        }

        if (value is double doubleValue)
        {
            return checked((int)doubleValue);
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var jsonInt))
        {
            return jsonInt;
        }

        throw new InvalidDataException($"{name} must be an integer.");
    }

    private static double ReadDouble(object? value, string name)
    {
        if (value is double doubleValue)
        {
            return doubleValue;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        if (value is long longValue)
        {
            return longValue;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var jsonDouble))
        {
            return jsonDouble;
        }

        throw new InvalidDataException($"{name} must be a number.");
    }

    private static string ReadString(object? value, string name)
    {
        if (value is string stringValue)
        {
            return stringValue;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? "";
        }

        throw new InvalidDataException($"{name} must be a string.");
    }

    private static bool IsNull(object? value)
    {
        return value is null ||
               value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };
    }
}
