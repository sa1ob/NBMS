using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NBMS.Core.Models;

namespace NBMS.Core.Import;

public sealed partial class BmsImportService
{
    private const int Resolution = 960;
    private const int MeasureTicks = Resolution * 4;

    static BmsImportService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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

    public async Task<BmsImportResult> ImportAsync(
        string bmsPath,
        string? encodingName = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(bmsPath, cancellationToken);
        var decoded = DecodeBmsText(bytes, encodingName);
        var lines = SplitBmsLines(decoded.Text);
        var doc = new BmsImportDocument
        {
            SourcePath = bmsPath,
            SourceExtension = Path.GetExtension(bmsPath).ToLowerInvariant(),
            SourceEncoding = decoded.EncodingName,
            CharsetDirective = decoded.CharsetDirective,
            EncodingDetection = decoded.Detection,
            EncodingWarnings = decoded.Warnings
        };

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
                    Data = channelMatch.Groups["data"].Value.Trim()
                });
                continue;
            }

            var headerMatch = HeaderLineRegex().Match(line);
            if (!headerMatch.Success)
            {
                continue;
            }

            ApplyHeaderLine(doc, headerMatch.Groups["key"].Value, headerMatch.Groups["value"].Value.Trim());
        }

        return BuildResult(doc);
    }

    private static BmsDecodedText DecodeBmsText(byte[] bytes, string? requestedEncodingName)
    {
        var charsetDirective = FindCharsetDirective(bytes);
        var warnings = new List<string>();

        if (TryDecodeBom(bytes, out var bomDecoded))
        {
            AddCharsetConflictWarning(warnings, charsetDirective, bomDecoded.EncodingName);
            return bomDecoded with { CharsetDirective = charsetDirective, Warnings = warnings };
        }

        if (!string.IsNullOrWhiteSpace(requestedEncodingName) &&
            !requestedEncodingName.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = DecodeWithEncoding(bytes, requestedEncodingName, "user");
            AddCharsetConflictWarning(warnings, charsetDirective, decoded.EncodingName);
            return decoded with { CharsetDirective = charsetDirective, Warnings = warnings };
        }

        if (!string.IsNullOrWhiteSpace(charsetDirective))
        {
            var decoded = DecodeWithEncoding(bytes, charsetDirective, "charset");
            return decoded with { CharsetDirective = charsetDirective, Warnings = warnings };
        }

        if (TryDecodeStrictUtf8(bytes, out var utf8Text))
        {
            return new BmsDecodedText(utf8Text, "utf-8", "utf8Strict", charsetDirective, warnings);
        }

        var fallback = DecodeWithEncoding(bytes, "shift_jis", "fallback");
        return fallback with { CharsetDirective = charsetDirective, Warnings = warnings };
    }

    private static string[] SplitBmsLines(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static bool TryDecodeBom(byte[] bytes, out BmsDecodedText decoded)
    {
        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF)
        {
            decoded = new BmsDecodedText(new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3), "utf-8", "bom", null, []);
            return true;
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE)
        {
            decoded = new BmsDecodedText(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16le", "bom", null, []);
            return true;
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF)
        {
            decoded = new BmsDecodedText(Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be", "bom", null, []);
            return true;
        }

        decoded = new BmsDecodedText("", "", "", null, []);
        return false;
    }

    private static bool TryDecodeStrictUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    private static BmsDecodedText DecodeWithEncoding(byte[] bytes, string encodingName, string detection)
    {
        var encoding = ResolveEncoding(encodingName);
        return new BmsDecodedText(encoding.GetString(bytes), NormalizeEncodingName(encoding), detection, null, []);
    }

    private static Encoding ResolveEncoding(string? encodingName)
    {
        var normalized = NormalizeRequestedEncodingName(encodingName);
        if (normalized == "utf-8")
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        }

        if (normalized == "utf-16le")
        {
            return Encoding.Unicode;
        }

        if (normalized == "utf-16be")
        {
            return Encoding.BigEndianUnicode;
        }

        if (normalized == "system-default")
        {
            return Encoding.Default;
        }

        try
        {
            return Encoding.GetEncoding(normalized);
        }
        catch
        {
            return Encoding.GetEncoding(932);
        }
    }

    private static string? FindCharsetDirective(byte[] bytes)
    {
        var ascii = Encoding.ASCII.GetString(bytes);
        foreach (var rawLine in SplitBmsLines(ascii))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("#CHARSET", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return parts[1].Trim();
            }
        }

        return null;
    }

    private static string NormalizeRequestedEncodingName(string? encodingName)
    {
        if (string.IsNullOrWhiteSpace(encodingName))
        {
            return "utf-8";
        }

        var normalized = encodingName.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal);
        return normalized switch
        {
            "auto" => "utf-8",
            "utf8" or "utf-8-bom" => "utf-8",
            "utf16" or "utf-16" or "utf-16-le" => "utf-16le",
            "utf-16-be" => "utf-16be",
            "shift-jis" or "shift_jis" or "sjis" or "cp932" or "windows-31j" => "shift_jis",
            "euc-kr" or "euckr" or "ks-c-5601-1987" => "euc-kr",
            _ => normalized
        };
    }

    private static string NormalizeEncodingName(Encoding encoding)
    {
        return encoding.CodePage switch
        {
            65001 => "utf-8",
            1200 => "utf-16le",
            1201 => "utf-16be",
            932 => "shift_jis",
            949 => "euc-kr",
            _ => encoding.WebName
        };
    }

    private static void AddCharsetConflictWarning(List<string> warnings, string? charsetDirective, string actualEncoding)
    {
        if (string.IsNullOrWhiteSpace(charsetDirective))
        {
            return;
        }

        var declared = NormalizeRequestedEncodingName(charsetDirective);
        if (!declared.Equals(actualEncoding, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"#CHARSET {charsetDirective} ignored; decoded as {actualEncoding}.");
        }
    }

    private static void ApplyHeaderLine(BmsImportDocument doc, string key, string value)
    {
        var upperKey = key.ToUpperInvariant();
        if (upperKey == "TITLE")
        {
            doc.Title = value;
        }
        else if (upperKey == "ARTIST")
        {
            doc.Artist = value;
        }
        else if (upperKey == "GENRE")
        {
            doc.Genre = value;
        }
        else if (upperKey == "BPM" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bpm))
        {
            doc.Bpm = bpm;
        }
        else if (upperKey == "PLAYLEVEL" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level))
        {
            doc.PlayLevel = level;
        }
        else if (upperKey == "LNOBJ" && value.Length >= 2)
        {
            doc.LnObj = value[..2];
        }
        else if (upperKey.StartsWith("WAV", StringComparison.Ordinal) && key.Length == 5)
        {
            doc.Wav[key[3..]] = value;
        }
        else if (upperKey.StartsWith("BMP", StringComparison.Ordinal) && key.Length == 5)
        {
            doc.Bmp[key[3..]] = value;
        }
        else if (upperKey.StartsWith("BGA", StringComparison.Ordinal) && key.Length == 5)
        {
            doc.BgaDefinitions[key[3..]] = value;
        }
        else if (upperKey.StartsWith("LAYER", StringComparison.Ordinal) && key.Length == 7)
        {
            doc.LayerDefinitions[key[5..]] = value;
        }
        else if (upperKey.StartsWith("POOR", StringComparison.Ordinal) && key.Length == 6)
        {
            doc.PoorDefinitions[key[4..]] = value;
        }
        else if (upperKey == "STAGEFILE")
        {
            doc.StageFile = value;
        }
        else if (upperKey == "BANNER")
        {
            doc.Banner = value;
        }
        else if (upperKey == "BACKBMP")
        {
            doc.BackBmp = value;
        }
        else if (upperKey.StartsWith("BPM", StringComparison.Ordinal) && key.Length == 5 &&
                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var indexedBpm))
        {
            doc.BpmDefinitions[key[3..]] = indexedBpm;
        }
        else if (upperKey.StartsWith("STOP", StringComparison.Ordinal) && key.Length == 6 &&
                 double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var stopValue))
        {
            doc.StopDefinitions[key[4..]] = stopValue;
        }
        else if (IsRandomDirective(upperKey))
        {
            doc.RandomDirectives.Add(string.IsNullOrWhiteSpace(value) ? key : $"{key} {value}");
        }
    }

    private static BmsImportResult BuildResult(BmsImportDocument doc)
    {
        var measureStarts = BuildMeasureStarts(doc);
        var wavToAudioId = doc.Wav.ToDictionary(pair => pair.Key, pair => CreateAudioId(pair.Key, pair.Value), StringComparer.Ordinal);
        var bmpToMediaId = doc.Bmp.ToDictionary(pair => pair.Key, pair => CreateMediaId("bmp", pair.Key, pair.Value), StringComparer.Ordinal);
        var bgaToMediaId = BuildBgaMediaIdMap(doc, bmpToMediaId);
        var layerToMediaId = BuildVisualDefinitionMediaIdMap(doc.LayerDefinitions, "layer", bmpToMediaId, bgaToMediaId);
        var poorToMediaId = BuildVisualDefinitionMediaIdMap(doc.PoorDefinitions, "poor", bmpToMediaId, bgaToMediaId);
        var mode = ResolveMode(doc);
        var chart = CreateBaseChart(doc, mode);
        var backgroundLaneCountsByTick = new Dictionary<int, int>();
        var longNoteStarts = new Dictionary<string, PendingLongNote>(StringComparer.Ordinal);
        var lnObjStarts = new Dictionary<string, PendingLongNote>(StringComparer.Ordinal);

        AddBarLines(chart, measureStarts, doc.MeasureLengths);
        AddInitialMediaEvents(doc, chart);

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
                AddChannelEvent(doc, chart, line.Channel, token, tick, wavToAudioId, bmpToMediaId, bgaToMediaId, layerToMediaId, poorToMediaId, backgroundLaneCountsByTick, longNoteStarts, lnObjStarts);
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
            Bpm = ResolveBpmInfo(doc, chart),
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

        return new BmsImportResult(header, chart, doc.Wav, BuildMediaFiles(doc));
    }

    private static NbmsChart CreateBaseChart(BmsImportDocument doc, string mode)
    {
        var chart = new NbmsChart
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

        if (!string.IsNullOrWhiteSpace(doc.LnObj))
        {
            chart.Timing.Add(new TimingEvent { Tick = 0, Type = "lnobj", Event = doc.LnObj });
        }

        if (doc.RandomDirectives.Count > 0 ||
            ResolveSourceFormat(doc) != "bms" ||
            !string.IsNullOrWhiteSpace(doc.SourceEncoding))
        {
            chart.Metadata = JsonSerializer.SerializeToElement(new
            {
                bmsCompat = new
                {
                    sourceFormat = ResolveSourceFormat(doc),
                    sourceExtension = doc.SourceExtension,
                    sourceEncoding = doc.SourceEncoding,
                    charsetDirective = doc.CharsetDirective,
                    encodingDetection = doc.EncodingDetection,
                    encodingWarnings = doc.EncodingWarnings,
                    randomDirectives = doc.RandomDirectives
                }
            });
        }

        return chart;
    }

    private static bool IsRandomDirective(string upperKey)
    {
        return upperKey is "RANDOM" or "SETRANDOM" or "IF" or "ELSEIF" or "ELSE" or "ENDIF" or "ENDRANDOM";
    }

    private static void AddChannelEvent(
        BmsImportDocument doc,
        NbmsChart chart,
        string channel,
        string token,
        int tick,
        IReadOnlyDictionary<string, string> wavToAudioId,
        IReadOnlyDictionary<string, string> bmpToMediaId,
        IReadOnlyDictionary<string, string> bgaToMediaId,
        IReadOnlyDictionary<string, string> layerToMediaId,
        IReadOnlyDictionary<string, string> poorToMediaId,
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
        else if ((channel == "04" || channel == "07" || channel == "06") &&
                 TryResolveMediaId(channel, token, bmpToMediaId, bgaToMediaId, layerToMediaId, poorToMediaId, out var mediaId))
        {
            var eventType = ResolveMediaEventType(doc, channel, token, mediaId);
            chart.MediaEvents.Add(new MediaEvent
            {
                Tick = tick,
                MediaId = mediaId,
                Type = eventType,
                Layer = channel == "07" ? 1 : 0
            });
        }
        else if (LongNoteChannels.TryGetValue(channel, out var longLane) && wavToAudioId.TryGetValue(token, out var longAudioId))
        {
            AddLongNote(chart, longNoteStarts, longLane, tick, longAudioId);
        }
        else if (NoteChannels.TryGetValue(channel, out var lane) &&
                 (wavToAudioId.TryGetValue(token, out var audioId) ||
                  string.Equals(doc.LnObj, token, StringComparison.Ordinal)))
        {
            AddNormalOrLnObjNote(doc, chart, lnObjStarts, lane, token, tick, audioId ?? "");
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

    private static Dictionary<string, string> BuildBgaMediaIdMap(
        BmsImportDocument doc,
        IReadOnlyDictionary<string, string> bmpToMediaId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in doc.BgaDefinitions)
        {
            var referencedBmpKey = pair.Value
                .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (referencedBmpKey is not null && bmpToMediaId.TryGetValue(referencedBmpKey, out var mediaId))
            {
                result[pair.Key] = mediaId;
            }
        }

        return result;
    }

    private static Dictionary<string, string> BuildVisualDefinitionMediaIdMap(
        IReadOnlyDictionary<string, string> definitions,
        string kind,
        IReadOnlyDictionary<string, string> bmpToMediaId,
        IReadOnlyDictionary<string, string> bgaToMediaId)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in definitions)
        {
            var firstToken = pair.Value
                .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (firstToken is null)
            {
                continue;
            }

            if (bmpToMediaId.TryGetValue(firstToken, out var bmpMediaId) ||
                bgaToMediaId.TryGetValue(firstToken, out bmpMediaId!))
            {
                result[pair.Key] = bmpMediaId;
                continue;
            }

            result[pair.Key] = CreateMediaId(kind, pair.Key, firstToken);
        }

        return result;
    }

    private static bool TryResolveMediaId(
        string channel,
        string token,
        IReadOnlyDictionary<string, string> bmpToMediaId,
        IReadOnlyDictionary<string, string> bgaToMediaId,
        IReadOnlyDictionary<string, string> layerToMediaId,
        IReadOnlyDictionary<string, string> poorToMediaId,
        out string mediaId)
    {
        if (channel == "07" && layerToMediaId.TryGetValue(token, out mediaId!))
        {
            return true;
        }

        if (channel == "06" && poorToMediaId.TryGetValue(token, out mediaId!))
        {
            return true;
        }

        return bmpToMediaId.TryGetValue(token, out mediaId!) ||
               bgaToMediaId.TryGetValue(token, out mediaId!) ||
               layerToMediaId.TryGetValue(token, out mediaId!) ||
               poorToMediaId.TryGetValue(token, out mediaId!);
    }

    private static string ResolveMediaEventType(BmsImportDocument doc, string channel, string token, string mediaId)
    {
        if (channel == "06")
        {
            return "poor";
        }

        if (channel == "07")
        {
            return "layer";
        }

        return IsLikelyVideoMedia(ResolveMediaFileName(doc, token, mediaId)) ? "video" : "bga";
    }

    private static string ResolveMediaFileName(BmsImportDocument doc, string token, string mediaId)
    {
        if (doc.Bmp.TryGetValue(token, out var bmp))
        {
            return bmp;
        }

        if (doc.BgaDefinitions.TryGetValue(token, out var bga))
        {
            var firstToken = bga.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstToken is not null && doc.Bmp.TryGetValue(firstToken, out var referencedBmp))
            {
                return referencedBmp;
            }
        }

        var directLayer = doc.LayerDefinitions
            .Select(pair => new
            {
                pair.Key,
                FileName = pair.Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""
            })
            .FirstOrDefault(pair => CreateMediaId("layer", pair.Key, pair.FileName) == mediaId)
            ?.FileName;
        if (!string.IsNullOrWhiteSpace(directLayer))
        {
            return directLayer;
        }

        var directPoor = doc.PoorDefinitions
            .Select(pair => new
            {
                pair.Key,
                FileName = pair.Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? ""
            })
            .FirstOrDefault(pair => CreateMediaId("poor", pair.Key, pair.FileName) == mediaId)
            ?.FileName;
        return directPoor ?? "";
    }

    private static bool IsLikelyVideoMedia(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() is ".mp4" or ".avi" or ".webm" or ".mov" or ".mkv" or ".wmv" or ".mpg" or ".mpeg";
    }

    private static void AddInitialMediaEvents(BmsImportDocument doc, NbmsChart chart)
    {
        if (!string.IsNullOrWhiteSpace(doc.BackBmp))
        {
            chart.MediaEvents.Add(new MediaEvent
            {
                Tick = 0,
                MediaId = CreateMediaId("backbmp", "00", doc.BackBmp),
                Type = "image",
                Layer = 0
            });
        }

        if (!string.IsNullOrWhiteSpace(doc.StageFile))
        {
            chart.MediaEvents.Add(new MediaEvent
            {
                Tick = 0,
                MediaId = CreateMediaId("stagefile", "00", doc.StageFile),
                Type = "stagefile",
                Layer = 0
            });
        }
    }

    private static Dictionary<string, string> BuildMediaFiles(BmsImportDocument doc)
    {
        var mediaFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in doc.Bmp)
        {
            mediaFiles[CreateMediaId("bmp", pair.Key, pair.Value)] = pair.Value;
        }

        AddVisualDefinitionMediaFiles(mediaFiles, "layer", doc.LayerDefinitions, doc);
        AddVisualDefinitionMediaFiles(mediaFiles, "poor", doc.PoorDefinitions, doc);

        AddHeaderMediaFile(mediaFiles, "stagefile", doc.StageFile);
        AddHeaderMediaFile(mediaFiles, "banner", doc.Banner);
        AddHeaderMediaFile(mediaFiles, "backbmp", doc.BackBmp);
        return mediaFiles;
    }

    private static void AddVisualDefinitionMediaFiles(
        Dictionary<string, string> mediaFiles,
        string kind,
        IReadOnlyDictionary<string, string> definitions,
        BmsImportDocument doc)
    {
        foreach (var pair in definitions)
        {
            var firstToken = pair.Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstToken) ||
                doc.Bmp.ContainsKey(firstToken) ||
                doc.BgaDefinitions.ContainsKey(firstToken))
            {
                continue;
            }

            mediaFiles[CreateMediaId(kind, pair.Key, firstToken)] = firstToken;
        }
    }

    private static void AddHeaderMediaFile(Dictionary<string, string> mediaFiles, string kind, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            mediaFiles[CreateMediaId(kind, "00", fileName)] = fileName;
        }
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
        var sourceFormat = ResolveSourceFormat(doc);
        if (sourceFormat == "pms")
        {
            return "pms-9k";
        }

        if (sourceFormat == "oct")
        {
            return "oct";
        }

        if (sourceFormat == "fp")
        {
            return "foot-pedal";
        }

        if (sourceFormat == "ibmsc")
        {
            return "ibmsc";
        }

        return doc.ChannelLines.Any(line =>
            NoteChannels.TryGetValue(line.Channel, out var lane) && lane is "scratch2" or "key8" or "key9" or "key10" or "key11" or "key12" or "key13" or "key14" ||
            LongNoteChannels.TryGetValue(line.Channel, out var longLane) && longLane is "scratch2" or "key8" or "key9" or "key10" or "key11" or "key12" or "key13" or "key14")
            ? "beat-14k"
            : "beat-7k";
    }

    private static string ResolveSourceFormat(BmsImportDocument doc)
    {
        return Path.GetExtension(doc.SourcePath).ToLowerInvariant() switch
        {
            ".pms" => "pms",
            ".oct" => "oct",
            ".fp" => "fp",
            ".bme" => "bme",
            ".bml" => "bml",
            ".ibmsc" => "ibmsc",
            _ => "bms"
        };
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

    private static void AddBarLines(
        NbmsChart chart,
        Dictionary<int, int> measureStarts,
        IReadOnlyDictionary<int, double> measureLengths)
    {
        foreach (var pair in measureStarts.OrderBy(pair => pair.Key))
        {
            chart.Timing.Add(new TimingEvent { Tick = pair.Value, Type = "bar" });
            if (measureLengths.TryGetValue(pair.Key, out var length))
            {
                chart.Timing.Add(new TimingEvent
                {
                    Tick = pair.Value,
                    Type = "measureLength",
                    Value = length,
                    ExtensionId = "nbms.bmsCompat",
                    Event = "measureLength"
                });
            }
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

    private static BpmInfo ResolveBpmInfo(BmsImportDocument doc, NbmsChart chart)
    {
        var bpmValues = chart.Timing
            .Where(timing => timing.Type == "bpm" && timing.Value is not null && timing.Value.Value > 0)
            .Select(timing => timing.Value!.Value)
            .DefaultIfEmpty(doc.Bpm)
            .ToList();

        return new BpmInfo
        {
            Initial = doc.Bpm,
            Min = bpmValues.Min(),
            Max = bpmValues.Max()
        };
    }

    private static int TimingSortOrder(string type)
    {
        return type switch
        {
            "bar" => 0,
            "bpm" => 1,
            "stop" => 2,
            "lnobj" => 3,
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
        return $"wav_{wavKey}_{Path.GetFileNameWithoutExtension(fileName)}";
    }

    private static string CreateMediaId(string kind, string key, string fileName)
    {
        return $"{kind}_{key}_{Path.GetFileNameWithoutExtension(fileName)}";
    }

    [GeneratedRegex("^#(?<measure>[0-9]{3})(?<channel>[0-9A-Z]{2}):(?<data>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelLineRegex();

    [GeneratedRegex("^#(?<key>[A-Za-z][A-Za-z0-9]*)(?:\\s+(?<value>.*))?$")]
    private static partial Regex HeaderLineRegex();

    private sealed record PendingLongNote(int Tick, string Lane, string AudioId);

    private sealed record BmsDecodedText(
        string Text,
        string EncodingName,
        string Detection,
        string? CharsetDirective,
        List<string> Warnings);
}

public sealed record BmsImportResult(
    NbmsHeader Header,
    NbmsChart Chart,
    Dictionary<string, string> WavFiles,
    Dictionary<string, string> MediaFiles);

internal sealed class BmsImportDocument
{
    public string SourcePath { get; set; } = "";
    public string SourceExtension { get; set; } = "";
    public string SourceEncoding { get; set; } = "";
    public string EncodingDetection { get; set; } = "";
    public string? CharsetDirective { get; set; }
    public List<string> EncodingWarnings { get; set; } = [];
    public string Title { get; set; } = "Untitled";
    public string Artist { get; set; } = "Unknown Artist";
    public string Genre { get; set; } = "";
    public double Bpm { get; set; } = 130;
    public int PlayLevel { get; set; }
    public string? LnObj { get; set; }
    public string? StageFile { get; set; }
    public string? Banner { get; set; }
    public string? BackBmp { get; set; }
    public Dictionary<string, string> Wav { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Bmp { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BgaDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LayerDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PoorDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> BpmDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> StopDefinitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<int, double> MeasureLengths { get; } = [];
    public List<string> RandomDirectives { get; } = [];
    public List<BmsChannelLine> ChannelLines { get; } = [];
}

internal sealed class BmsChannelLine
{
    public int Measure { get; set; }
    public string Channel { get; set; } = "";
    public string Data { get; set; } = "";
}
