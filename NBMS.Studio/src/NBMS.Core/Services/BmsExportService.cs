using System.Globalization;
using System.Text;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class BmsExportService
{
    private const int BmsSlotsPerMeasure = 192;
    private static readonly string[] BmsIds = BuildBmsIds();

    private static readonly IReadOnlyDictionary<string, string> TapChannels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["key1"] = "11",
        ["key2"] = "12",
        ["key3"] = "13",
        ["key4"] = "14",
        ["key5"] = "15",
        ["scratch"] = "16",
        ["key6"] = "18",
        ["key7"] = "19",
        ["key8"] = "21",
        ["key9"] = "22",
        ["key10"] = "23",
        ["key11"] = "24",
        ["key12"] = "25",
        ["scratch2"] = "26",
        ["key13"] = "28",
        ["key14"] = "29"
    };

    private static readonly IReadOnlyDictionary<string, string> LongNoteChannels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["key1"] = "51",
        ["key2"] = "52",
        ["key3"] = "53",
        ["key4"] = "54",
        ["key5"] = "55",
        ["scratch"] = "56",
        ["key6"] = "58",
        ["key7"] = "59",
        ["key8"] = "61",
        ["key9"] = "62",
        ["key10"] = "63",
        ["key11"] = "64",
        ["key12"] = "65",
        ["scratch2"] = "66",
        ["key13"] = "68",
        ["key14"] = "69"
    };

    public BmsExportResult Export(
        NbmsHeader header,
        NbmsChart chart,
        IReadOnlyDictionary<string, string>? audioFileNames = null,
        IReadOnlyDictionary<string, string>? mediaFileNames = null)
    {
        var issues = new List<BmsExportLossIssue>();
        var measureMap = BuildMeasureMap(chart);
        var audioIds = chart.Notes
            .Select(note => note.AudioId)
            .Concat(chart.BackgroundAudio.Select(item => item.AudioId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var audioIdMap = AssignDefinitionIds(audioIds, "audio", issues);
        var exportedAudioFileNames = BuildAudioFileNames(audioIds, audioFileNames);
        var mediaIds = chart.MediaEvents
            .Where(item => !string.IsNullOrWhiteSpace(item.MediaId) && IsBmsExportableMediaEvent(item))
            .Select(item => item.MediaId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        var mediaIdMap = AssignDefinitionIds(mediaIds, "media", issues);
        var exportedMediaFileNames = BuildAssetFileNames(mediaIds, mediaFileNames);
        var bpmMap = AssignBpmDefinitions(chart, issues);
        var stopMap = AssignStopDefinitions(chart, issues);
        var channels = new SortedDictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var note in chart.Notes.OrderBy(note => note.Tick).ThenBy(note => note.Lane, StringComparer.Ordinal))
        {
            ExportNote(note, measureMap, audioIdMap, channels, issues);
        }

        foreach (var background in chart.BackgroundAudio.OrderBy(item => item.Tick))
        {
            if (!audioIdMap.TryGetValue(background.AudioId, out var bmsId))
            {
                issues.Add(new BmsExportLossIssue("Warning", "BMS_EXPORT_MISSING_AUDIO", $"backgroundAudio:{background.Tick}", $"AudioId is not exported: {background.AudioId}"));
                continue;
            }

            PutToken(channels, measureMap, background.Tick, "01", bmsId, issues);
        }

        foreach (var timing in chart.Timing.OrderBy(item => item.Tick))
        {
            ExportTiming(timing, measureMap, bpmMap, stopMap, channels, issues);
        }

        foreach (var mediaEvent in chart.MediaEvents.OrderBy(item => item.Tick))
        {
            ExportMediaEvent(mediaEvent, measureMap, mediaIdMap, channels, issues);
        }

        AddUnsupportedLosses(chart, issues);

        var builder = new StringBuilder();
        builder.AppendLine("#PLAYER " + (chart.Mode.Contains("14", StringComparison.OrdinalIgnoreCase) || chart.Mode.Contains("10", StringComparison.OrdinalIgnoreCase) ? "3" : "1"));
        builder.AppendLine("#TITLE " + EscapeHeaderValue(header.Title));
        if (!string.IsNullOrWhiteSpace(header.Artist))
        {
            builder.AppendLine("#ARTIST " + EscapeHeaderValue(header.Artist));
        }

        builder.AppendLine("#BPM " + ResolveInitialBpm(header, chart).ToString("0.######", CultureInfo.InvariantCulture));
        builder.AppendLine("#LNTYPE 1");

        foreach (var pair in audioIdMap.OrderBy(pair => pair.Value, StringComparer.Ordinal))
        {
            var fileName = exportedAudioFileNames.GetValueOrDefault(pair.Key, pair.Key);
            builder.AppendLine($"#WAV{pair.Value} {fileName}");
        }

        foreach (var pair in bpmMap.OrderBy(pair => pair.Value.Id, StringComparer.Ordinal))
        {
            builder.AppendLine($"#BPM{pair.Value.Id} {pair.Key.ToString("0.######", CultureInfo.InvariantCulture)}");
        }

        foreach (var pair in mediaIdMap.OrderBy(pair => pair.Value, StringComparer.Ordinal))
        {
            var fileName = exportedMediaFileNames.GetValueOrDefault(pair.Key, pair.Key);
            builder.AppendLine($"#BMP{pair.Value} {fileName}");
        }

        foreach (var pair in stopMap.OrderBy(pair => pair.Value.Id, StringComparer.Ordinal))
        {
            builder.AppendLine($"#STOP{pair.Value.Id} {pair.Key.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (var measure in measureMap.Measures.Where(item => Math.Abs(item.LengthRatio - 1.0) > 0.000001))
        {
            builder.AppendLine($"#{measure.Index:000}02:{measure.LengthRatio.ToString("0.######", CultureInfo.InvariantCulture)}");
        }

        foreach (var pair in channels)
        {
            if (pair.Value.Any(token => token != "00"))
            {
                builder.AppendLine($"#{pair.Key}:{string.Concat(pair.Value)}");
            }
        }

        return new BmsExportResult(builder.ToString(), BuildLossReport(issues), issues, exportedAudioFileNames, exportedMediaFileNames);
    }

    private static void ExportNote(
        NoteEvent note,
        BmsMeasureMap measureMap,
        IReadOnlyDictionary<string, string> audioIdMap,
        SortedDictionary<string, string[]> channels,
        List<BmsExportLossIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(note.AudioId) || !audioIdMap.TryGetValue(note.AudioId, out var bmsId))
        {
            issues.Add(new BmsExportLossIssue("Warning", "BMS_EXPORT_MISSING_AUDIO", $"note:{note.Tick}:{note.Lane}", "Note without exported audioId."));
            return;
        }

        var isLong = note.Type.Equals("hold", StringComparison.OrdinalIgnoreCase) && note.DurationTicks is > 0;
        var channelMap = isLong ? LongNoteChannels : TapChannels;
        if (!channelMap.TryGetValue(note.Lane, out var channel))
        {
            issues.Add(new BmsExportLossIssue("Warning", "BMS_EXPORT_UNSUPPORTED_LANE", $"note:{note.Tick}:{note.Lane}", $"Unsupported lane for BMS export: {note.Lane}"));
            return;
        }

        if (note.Volume is not null || note.Pan is not null)
        {
            issues.Add(new BmsExportLossIssue("Info", "BMS_EXPORT_NOTE_PROPERTY_LOSS", $"note:{note.Tick}:{note.Lane}", "Volume/Pan cannot be represented in standard BMS."));
        }

        PutToken(channels, measureMap, note.Tick, channel, bmsId, issues);
        if (isLong)
        {
            PutToken(channels, measureMap, note.Tick + note.DurationTicks!.Value, channel, bmsId, issues);
        }
    }

    private static void ExportTiming(
        TimingEvent timing,
        BmsMeasureMap measureMap,
        IReadOnlyDictionary<double, BmsDefinition> bpmMap,
        IReadOnlyDictionary<int, BmsDefinition> stopMap,
        SortedDictionary<string, string[]> channels,
        List<BmsExportLossIssue> issues)
    {
        if (timing.Type.Equals("bpm", StringComparison.OrdinalIgnoreCase) && timing.Value is { } bpm)
        {
            if (timing.Tick == 0)
            {
                return;
            }

            if (bpmMap.TryGetValue(bpm, out var definition))
            {
                PutToken(channels, measureMap, timing.Tick, "08", definition.Id, issues);
            }

            return;
        }

        if (timing.Type.Equals("stop", StringComparison.OrdinalIgnoreCase) && timing.DurationTicks is { } durationTicks)
        {
            if (stopMap.TryGetValue(durationTicks, out var definition))
            {
                PutToken(channels, measureMap, timing.Tick, "09", definition.Id, issues);
            }

            return;
        }

        if (timing.Type is "bar" or "measureLength")
        {
            return;
        }

        issues.Add(new BmsExportLossIssue("Warning", "BMS_EXPORT_TIMING_LOSS", $"timing:{timing.Tick}:{timing.Type}", $"Timing event cannot be represented in minimal BMS export: {timing.Type}"));
    }

    private static void ExportMediaEvent(
        MediaEvent mediaEvent,
        BmsMeasureMap measureMap,
        IReadOnlyDictionary<string, string> mediaIdMap,
        SortedDictionary<string, string[]> channels,
        List<BmsExportLossIssue> issues)
    {
        if (!IsBmsExportableMediaEvent(mediaEvent))
        {
            issues.Add(new BmsExportLossIssue("Warning", "BMS_EXPORT_MEDIA_EVENT_LOSS", $"media:{mediaEvent.Tick}:{mediaEvent.MediaId}", $"mediaEvent cannot be represented in minimal BMS export: {mediaEvent.Type}"));
            return;
        }

        if (!mediaIdMap.TryGetValue(mediaEvent.MediaId, out var bmsId))
        {
            issues.Add(new BmsExportLossIssue("Warning", "BMS_EXPORT_MISSING_MEDIA", $"media:{mediaEvent.Tick}:{mediaEvent.MediaId}", $"MediaId is not exported: {mediaEvent.MediaId}"));
            return;
        }

        var channel = mediaEvent.Type.ToLowerInvariant() switch
        {
            "layer" => "07",
            "poor" => "06",
            _ => "04"
        };
        PutToken(channels, measureMap, mediaEvent.Tick, channel, bmsId, issues);
    }

    private static void PutToken(
        SortedDictionary<string, string[]> channels,
        BmsMeasureMap measureMap,
        int tick,
        string channel,
        string token,
        List<BmsExportLossIssue> issues)
    {
        var location = measureMap.Resolve(tick);
        var key = $"{location.MeasureIndex:000}{channel}";
        if (!channels.TryGetValue(key, out var data))
        {
            data = Enumerable.Repeat("00", BmsSlotsPerMeasure).ToArray();
            channels[key] = data;
        }

        if (data[location.Slot] != "00" && data[location.Slot] != token)
        {
            issues.Add(new BmsExportLossIssue("Warning", "BMS_EXPORT_SLOT_COLLISION", key, $"Multiple objects collided at slot {location.Slot}."));
        }

        data[location.Slot] = token;
    }

    private static BmsMeasureMap BuildMeasureMap(NbmsChart chart)
    {
        var resolution = Math.Max(1, chart.Resolution);
        var defaultMeasureTicks = resolution * 4;
        var maxTick = new[]
        {
            chart.Notes.Select(note => note.Tick + Math.Max(0, note.DurationTicks ?? 0)).DefaultIfEmpty(0).Max(),
            chart.BackgroundAudio.Select(item => item.Tick).DefaultIfEmpty(0).Max(),
            chart.Timing.Select(item => item.Tick).DefaultIfEmpty(0).Max(),
            chart.MediaEvents.Select(item => item.Tick).DefaultIfEmpty(0).Max()
        }.Max();
        var measureLengthByTick = chart.Timing
            .Where(item => item.Type.Equals("measureLength", StringComparison.OrdinalIgnoreCase) && item.Value is > 0)
            .GroupBy(item => item.Tick)
            .ToDictionary(group => group.Key, group => group.Last().Value!.Value);
        var measures = new List<BmsMeasureInfo>();
        var tick = 0;
        var index = 0;

        while (tick <= maxTick + defaultMeasureTicks)
        {
            var ratio = measureLengthByTick.GetValueOrDefault(tick, 1.0);
            var lengthTicks = Math.Max(1, (int)Math.Round(defaultMeasureTicks * ratio));
            measures.Add(new BmsMeasureInfo(index, tick, lengthTicks, ratio));
            tick += lengthTicks;
            index++;
        }

        return new BmsMeasureMap(measures);
    }

    private static Dictionary<string, string> AssignDefinitionIds(
        IReadOnlyList<string> values,
        string kind,
        List<BmsExportLossIssue> issues)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            if (index >= BmsIds.Length)
            {
                issues.Add(new BmsExportLossIssue("Error", "BMS_EXPORT_TOO_MANY_DEFINITIONS", kind, $"Too many {kind} definitions for BMS ID space."));
                break;
            }

            result[values[index]] = BmsIds[index];
        }

        return result;
    }

    private static Dictionary<double, BmsDefinition> AssignBpmDefinitions(NbmsChart chart, List<BmsExportLossIssue> issues)
    {
        var values = chart.Timing
            .Where(item => item.Type.Equals("bpm", StringComparison.OrdinalIgnoreCase) && item.Tick != 0 && item.Value is > 0)
            .Select(item => item.Value!.Value)
            .Distinct()
            .Order()
            .ToList();
        return AssignNumberDefinitions(values, "bpm", issues);
    }

    private static Dictionary<int, BmsDefinition> AssignStopDefinitions(NbmsChart chart, List<BmsExportLossIssue> issues)
    {
        var values = chart.Timing
            .Where(item => item.Type.Equals("stop", StringComparison.OrdinalIgnoreCase) && item.DurationTicks is > 0)
            .Select(item => item.DurationTicks!.Value)
            .Distinct()
            .Order()
            .ToList();
        return AssignNumberDefinitions(values, "stop", issues);
    }

    private static Dictionary<T, BmsDefinition> AssignNumberDefinitions<T>(IReadOnlyList<T> values, string kind, List<BmsExportLossIssue> issues)
        where T : notnull
    {
        var result = new Dictionary<T, BmsDefinition>();
        for (var index = 0; index < values.Count; index++)
        {
            if (index >= BmsIds.Length)
            {
                issues.Add(new BmsExportLossIssue("Error", "BMS_EXPORT_TOO_MANY_DEFINITIONS", kind, $"Too many {kind} definitions for BMS ID space."));
                break;
            }

            result[values[index]] = new BmsDefinition(BmsIds[index]);
        }

        return result;
    }

    private static void AddUnsupportedLosses(NbmsChart chart, List<BmsExportLossIssue> issues)
    {
        foreach (var extension in chart.Extensions.Where(extension => extension.Id is not "nbms.editor" and not "nbms.longNote"))
        {
            issues.Add(new BmsExportLossIssue("Warning", "BMS_EXPORT_EXTENSION_LOSS", $"extension:{extension.Id}", $"Extension is not represented in standard BMS: {extension.Id}"));
        }
    }

    private static bool IsBmsExportableMediaEvent(MediaEvent mediaEvent)
    {
        if (string.IsNullOrWhiteSpace(mediaEvent.MediaId))
        {
            return false;
        }

        return mediaEvent.Type.Equals("bga", StringComparison.OrdinalIgnoreCase) ||
               mediaEvent.Type.Equals("video", StringComparison.OrdinalIgnoreCase) ||
               mediaEvent.Type.Equals("layer", StringComparison.OrdinalIgnoreCase) ||
               mediaEvent.Type.Equals("poor", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildAudioFileNames(
        IReadOnlyList<string> audioIds,
        IReadOnlyDictionary<string, string>? audioFileNames)
    {
        return BuildAssetFileNames(audioIds, audioFileNames);
    }

    private static Dictionary<string, string> BuildAssetFileNames(
        IReadOnlyList<string> assetIds,
        IReadOnlyDictionary<string, string>? fileNames)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assetId in assetIds)
        {
            if (fileNames is not null &&
                fileNames.TryGetValue(assetId, out var fileName) &&
                !string.IsNullOrWhiteSpace(fileName))
            {
                result[assetId] = fileName;
                continue;
            }

            result[assetId] = assetId;
        }

        return result;
    }

    private static double ResolveInitialBpm(NbmsHeader header, NbmsChart chart)
    {
        return chart.Timing
            .Where(item => item.Type.Equals("bpm", StringComparison.OrdinalIgnoreCase) && item.Tick == 0 && item.Value is > 0)
            .Select(item => item.Value!.Value)
            .DefaultIfEmpty(header.Bpm.Initial > 0 ? header.Bpm.Initial : 120)
            .Last();
    }

    private static string BuildLossReport(IReadOnlyList<BmsExportLossIssue> issues)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# BMS Export Loss Report");
        builder.AppendLine();
        if (issues.Count == 0)
        {
            builder.AppendLine("No loss issues were detected.");
            return builder.ToString();
        }

        foreach (var issue in issues)
        {
            builder.AppendLine($"- `{issue.Severity}` `{issue.Code}` `{issue.TargetReference}`: {issue.Message}");
        }

        return builder.ToString();
    }

    private static string EscapeHeaderValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Untitled" : value.ReplaceLineEndings(" ").Trim();
    }

    private static string[] BuildBmsIds()
    {
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        return chars
            .SelectMany(left => chars.Select(right => $"{left}{right}"))
            .Where(id => id != "00")
            .ToArray();
    }

    private sealed record BmsDefinition(string Id);

    private sealed record BmsMeasureInfo(int Index, int StartTick, int LengthTicks, double LengthRatio);

    private sealed record BmsLocation(int MeasureIndex, int Slot);

    private sealed class BmsMeasureMap(IReadOnlyList<BmsMeasureInfo> measures)
    {
        public IReadOnlyList<BmsMeasureInfo> Measures => measures;

        public BmsLocation Resolve(int tick)
        {
            var measure = measures[0];
            foreach (var candidate in measures)
            {
                if (candidate.StartTick > tick)
                {
                    break;
                }

                measure = candidate;
            }

            var offset = Math.Clamp(tick - measure.StartTick, 0, Math.Max(0, measure.LengthTicks - 1));
            var slot = Math.Clamp((int)Math.Round((double)offset * BmsSlotsPerMeasure / measure.LengthTicks), 0, BmsSlotsPerMeasure - 1);
            return new BmsLocation(measure.Index, slot);
        }
    }
}

public sealed record BmsExportResult(
    string BmsText,
    string LossReportMarkdown,
    IReadOnlyList<BmsExportLossIssue> Issues,
    IReadOnlyDictionary<string, string> AudioFileNames,
    IReadOnlyDictionary<string, string> MediaFileNames);

public sealed record BmsExportLossIssue(
    string Severity,
    string Code,
    string TargetReference,
    string Message);
