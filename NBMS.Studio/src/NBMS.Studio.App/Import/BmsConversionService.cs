using System.IO.Compression;
using NBMS.Core.Import;
using NBMS.Core.Models;
using NBMS.Core.Services;
using NBMS.Studio.App.Services;

namespace NBMS.Studio.App.Import;

public sealed class BmsConversionService
{
    private readonly BmsImportService _importService = new();
    private readonly HashService _hashService = new();
    private static readonly string[] BmsPatterns = ["*.bms", "*.bme", "*.bml", "*.pms", "*.oct", "*.fp", "*.ibmsc"];

    public async Task<BmsConversionResult> ConvertAsync(
        string bmsPath,
        string outputDirectory,
        BmsConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var importResult = await _importService.ImportAsync(bmsPath, options?.EncodingName, cancellationToken);
        var outputRoot = Path.GetFullPath(outputDirectory);
        var scoreDirectory = Path.Combine(outputRoot, "score");
        Directory.CreateDirectory(scoreDirectory);

        var chartPath = Path.Combine(scoreDirectory, "main.nbmc");
        var audioPath = Path.Combine(outputRoot, "audio.nbma");
        var mediaPath = Path.Combine(outputRoot, "media.nbmg");
        var headerPath = Path.Combine(outputRoot, "song.nbmh");

        importResult.Header.Audio.File = "audio.nbma";
        importResult.Header.Charts[0].File = "score/main.nbmc";

        var imported = new ImportedBms(
            bmsPath,
            importResult,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            []);
        var audioSources = BuildMergedAudioSources([imported]);
        var mediaSources = BuildMergedMediaSources([imported]);
        RemapChartAudioIds(importResult.Chart, imported.AudioIdMap);
        RemapChartMediaIds(importResult.Chart, imported.MediaIdMap);
        await WriteChartAsync(chartPath, importResult.Chart, options?.ReadableScoreJson == true, cancellationToken);
        await CreateAudioArchiveAsync(audioSources, audioPath, cancellationToken);
        if (mediaSources.Count > 0)
        {
            importResult.Header.Media = new FileReference { File = "media.nbmg", Optional = true };
            await CreateMediaArchiveAsync(mediaSources, mediaPath, cancellationToken);
            importResult.Header.Media.Hash = await _hashService.ComputeFileSha256Async(mediaPath, cancellationToken);
        }

        importResult.Header.Charts[0].Hash = _hashService.ComputeCanonicalJsonHash(importResult.Chart);
        importResult.Header.Charts[0].HashAlgorithm = "sha256-compact-canonical-json";
        importResult.Header.Audio.Hash = await _hashService.ComputeFileSha256Async(audioPath, cancellationToken);
        await NbmsJson.WriteAsync(headerPath, importResult.Header, cancellationToken);

        return new BmsConversionResult(headerPath, chartPath, audioPath)
        {
            ImportReport = imported.ImportReport.ToList()
        };
    }

    public async Task<BmsFolderConversionResult> ConvertFolderAsync(
        string bmsDirectory,
        string outputDirectory,
        BmsFolderConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.GetFullPath(bmsDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory);
        var scoreDirectory = Path.Combine(outputRoot, "score");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(scoreDirectory);

        var bmsFiles = EnumerateBmsFiles(sourceRoot);

        if (bmsFiles.Count == 0)
        {
            throw new InvalidDataException("指定フォルダにBMS譜面ファイルが見つかりません。");
        }

        var importedFiles = new List<ImportedBms>();
        foreach (var bmsPath in bmsFiles)
        {
            importedFiles.Add(new ImportedBms(
                bmsPath,
                await _importService.ImportAsync(bmsPath, options?.EncodingName, cancellationToken),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                []));
        }

        var header = BuildMergedHeader(sourceRoot, importedFiles);
        ApplyFolderConversionOptions(header, options);
        var audioPath = Path.Combine(outputRoot, "audio.nbma");
        var mediaPath = Path.Combine(outputRoot, "media.nbmg");
        var headerPath = Path.Combine(outputRoot, "song.nbmh");
        var audioSources = BuildMergedAudioSources(importedFiles);
        var mediaSources = BuildMergedMediaSources(importedFiles);
        var results = new List<BmsConversionResult>();
        var usedChartFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await CreateAudioArchiveAsync(audioSources, audioPath, cancellationToken);
        if (mediaSources.Count > 0)
        {
            header.Media = new FileReference { File = "media.nbmg", Optional = true };
            await CreateMediaArchiveAsync(mediaSources, mediaPath, cancellationToken);
        }

        foreach (var imported in importedFiles)
        {
            var chartId = CreateChartId(imported.BmsPath);
            var chartFileName = CreateUniqueFileName($"{chartId}.nbmc", usedChartFileNames);
            var chartRelativePath = $"score/{chartFileName}";
            var chartPath = Path.Combine(scoreDirectory, chartFileName);

            imported.ImportResult.Chart.ChartId = chartId;
            RemapChartAudioIds(imported.ImportResult.Chart, imported.AudioIdMap);
            RemapChartMediaIds(imported.ImportResult.Chart, imported.MediaIdMap);
            await WriteChartAsync(chartPath, imported.ImportResult.Chart, options?.ReadableScoreJson == true, cancellationToken);

            var sourceReference = imported.ImportResult.Header.Charts.FirstOrDefault() ?? new ChartReference();
            header.Charts.Add(new ChartReference
            {
                Id = chartId,
                File = chartRelativePath,
                Mode = string.IsNullOrWhiteSpace(sourceReference.Mode) ? imported.ImportResult.Chart.Mode : sourceReference.Mode,
                Difficulty = sourceReference.Difficulty,
                LevelName = ResolveLevelName(imported.BmsPath, sourceReference, options),
                Hash = _hashService.ComputeCanonicalJsonHash(imported.ImportResult.Chart),
                HashAlgorithm = "sha256-compact-canonical-json"
            });

            results.Add(new BmsConversionResult(headerPath, chartPath, audioPath)
            {
                ImportReport = imported.ImportReport.ToList()
            });
        }

        header.Audio.Hash = await _hashService.ComputeFileSha256Async(audioPath, cancellationToken);
        if (header.Media is not null && File.Exists(mediaPath))
        {
            header.Media.Hash = await _hashService.ComputeFileSha256Async(mediaPath, cancellationToken);
        }
        await NbmsJson.WriteAsync(headerPath, header, cancellationToken);

        return new BmsFolderConversionResult(results);
    }

    public async Task<BmsFolderConversionPreview> PreviewFolderAsync(
        string bmsDirectory,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.GetFullPath(bmsDirectory);
        var bmsFiles = EnumerateBmsFiles(sourceRoot);

        if (bmsFiles.Count == 0)
        {
            throw new InvalidDataException("No BMS chart files were found in the selected folder.");
        }

        var importedFiles = new List<ImportedBms>();
        foreach (var bmsPath in bmsFiles)
        {
            importedFiles.Add(new ImportedBms(
                bmsPath,
                await _importService.ImportAsync(bmsPath, cancellationToken: cancellationToken),
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                []));
        }

        var commonTitle = ResolveCommonTitle(importedFiles);
        var charts = importedFiles
            .Select(imported =>
            {
                var chart = imported.ImportResult.Chart;
                var sourceReference = imported.ImportResult.Header.Charts.FirstOrDefault() ?? new ChartReference();
                var originalTitle = imported.ImportResult.Header.Title;
                var fallbackName = ResolveBaseLevelName(imported.BmsPath, sourceReference);
                return new BmsChartConversionPreview(
                    Path.GetFullPath(imported.BmsPath),
                    originalTitle,
                    ResolveSuggestedLevelName(originalTitle, commonTitle, fallbackName),
                    string.IsNullOrWhiteSpace(sourceReference.Mode) ? chart.Mode : sourceReference.Mode,
                    sourceReference.Difficulty,
                    chart.Timing.Count(timing => timing.Type == "bpm"),
                    chart.Timing.Count(timing => timing.Type == "stop"),
                    chart.Notes.Count(note => note.Type == "hold"),
                    chart.Timing.FirstOrDefault(timing => timing.Type == "lnobj")?.Event ?? "");
            })
            .ToList();

        return new BmsFolderConversionPreview(sourceRoot, commonTitle, charts);
    }

    private NbmsHeader BuildMergedHeader(string sourceRoot, IReadOnlyList<ImportedBms> importedFiles)
    {
        var firstHeader = importedFiles[0].ImportResult.Header;
        var folderName = Path.GetFileName(sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return new NbmsHeader
        {
            Id = "converted." + SanitizeId(string.IsNullOrWhiteSpace(folderName) ? "bms-folder" : folderName),
            Title = firstHeader.Title,
            Subtitle = firstHeader.Subtitle,
            Artist = firstHeader.Artist,
            Genre = firstHeader.Genre,
            Bpm = firstHeader.Bpm,
            Preview = firstHeader.Preview,
            Audio = new FileReference { File = "audio.nbma" },
            Media = null,
            Charts = [],
            Rights = firstHeader.Rights,
            Security = new SecurityInfo { Signed = false, Encrypted = false, EditPolicy = "open" },
            Extensions = firstHeader.Extensions
        };
    }

    private static List<AudioSource> BuildMergedAudioSources(IReadOnlyList<ImportedBms> importedFiles)
    {
        var sources = new List<AudioSource>();
        var usedAudioIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var audioIdsBySourcePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var imported in importedFiles)
        {
            var bmsDirectory = Path.GetDirectoryName(Path.GetFullPath(imported.BmsPath))
                ?? throw new InvalidDataException("BMSファイルの親ディレクトリを解決できません。");

            foreach (var pair in imported.ImportResult.WavFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var originalAudioId = CreateAudioId(pair.Key, pair.Value);
                var sourcePath = Path.GetFullPath(Path.Combine(bmsDirectory, pair.Value));
                var audioId = ResolveMergedAudioId(originalAudioId, sourcePath, usedAudioIds, audioIdsBySourcePath);

                imported.AudioIdMap[originalAudioId] = audioId;
                if (sources.Any(source => source.AudioId.Equals(audioId, StringComparison.Ordinal)))
                {
                    continue;
                }

                sources.Add(new AudioSource(audioId, sourcePath, pair.Value));
            }
        }

        return sources;
    }

    private static string ResolveMergedAudioId(
        string originalAudioId,
        string sourcePath,
        Dictionary<string, string> usedAudioIds,
        Dictionary<string, string> audioIdsBySourcePath)
    {
        if (audioIdsBySourcePath.TryGetValue(sourcePath, out var existingAudioId))
        {
            return existingAudioId;
        }

        if (!usedAudioIds.TryGetValue(originalAudioId, out var existingPath))
        {
            usedAudioIds[originalAudioId] = sourcePath;
            audioIdsBySourcePath[sourcePath] = originalAudioId;
            return originalAudioId;
        }

        if (string.Equals(existingPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            audioIdsBySourcePath[sourcePath] = originalAudioId;
            return originalAudioId;
        }

        var suffix = StableShortHash(sourcePath);
        var candidate = $"{originalAudioId}_{suffix}";
        var index = 2;
        while (usedAudioIds.TryGetValue(candidate, out var collisionPath) &&
               !string.Equals(collisionPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"{originalAudioId}_{suffix}_{index}";
            index++;
        }

        usedAudioIds[candidate] = sourcePath;
        audioIdsBySourcePath[sourcePath] = candidate;
        return candidate;
    }

    private static string ResolveMergedAudioId(string originalAudioId, string sourcePath, Dictionary<string, string> usedAudioIds)
    {
        if (!usedAudioIds.TryGetValue(originalAudioId, out var existingPath))
        {
            usedAudioIds[originalAudioId] = sourcePath;
            return originalAudioId;
        }

        if (string.Equals(existingPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return originalAudioId;
        }

        var suffix = StableShortHash(sourcePath);
        var candidate = $"{originalAudioId}_{suffix}";
        var index = 2;
        while (usedAudioIds.TryGetValue(candidate, out var collisionPath) &&
               !string.Equals(collisionPath, sourcePath, StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"{originalAudioId}_{suffix}_{index}";
            index++;
        }

        usedAudioIds[candidate] = sourcePath;
        return candidate;
    }

    private static List<MediaSource> BuildMergedMediaSources(IReadOnlyList<ImportedBms> importedFiles)
    {
        var sources = new List<MediaSource>();
        var usedMediaIds = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var imported in importedFiles)
        {
            var bmsDirectory = Path.GetDirectoryName(Path.GetFullPath(imported.BmsPath))
                ?? throw new InvalidDataException("BMSファイルの親ディレクトリを解決できません。");

            foreach (var pair in imported.ImportResult.MediaFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var originalMediaId = pair.Key;
                var resolved = BmsMediaAlternativeResolver.Resolve(bmsDirectory, pair.Value);
                var sourcePath = resolved.SourcePath;
                var mediaId = ResolveMergedAudioId(originalMediaId, sourcePath, usedMediaIds);

                imported.MediaIdMap[originalMediaId] = mediaId;
                if (sources.Any(source => source.MediaId.Equals(mediaId, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (resolved.IsAlternative)
                {
                    imported.ImportReport.Add($"media alternative resolved: {pair.Value} -> {resolved.RelativePath}");
                }

                sources.Add(new MediaSource(mediaId, sourcePath, resolved.RelativePath));
            }
        }

        return sources;
    }

    private static void RemapChartAudioIds(NbmsChart chart, IReadOnlyDictionary<string, string> audioIdMap)
    {
        foreach (var note in chart.Notes)
        {
            if (note.AudioId is not null && audioIdMap.TryGetValue(note.AudioId, out var audioId))
            {
                note.AudioId = audioId;
            }
        }

        foreach (var backgroundAudio in chart.BackgroundAudio)
        {
            if (audioIdMap.TryGetValue(backgroundAudio.AudioId, out var audioId))
            {
                backgroundAudio.AudioId = audioId;
            }
        }
    }

    private static void RemapChartMediaIds(NbmsChart chart, IReadOnlyDictionary<string, string> mediaIdMap)
    {
        foreach (var mediaEvent in chart.MediaEvents)
        {
            if (mediaIdMap.TryGetValue(mediaEvent.MediaId, out var mediaId))
            {
                mediaEvent.MediaId = mediaId;
            }
        }
    }

    private static async Task CreateAudioArchiveAsync(
        IReadOnlyList<AudioSource> audioSources,
        string audioPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(audioPath)!);
        await using var stream = File.Create(audioPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var manifest = new AudioManifest
        {
            Format = "NBMS-AUDIO",
            Version = "0.1.0",
            CodecRequired = [],
            Encrypted = false,
            Rights =
            [
                new RightsEntry
                {
                    Id = "converted-bms-audio",
                    Name = "Converted BMS audio",
                    Role = "sound-source",
                    License = "Rights unspecified; check original BMS package"
                }
            ]
        };

        foreach (var source in audioSources.OrderBy(source => source.AudioId, StringComparer.Ordinal))
        {
            var archivePath = $"audio/{source.AudioId}{Path.GetExtension(source.OriginalFileName).ToLowerInvariant()}";
            var codec = DetectCodec(source.OriginalFileName);
            var metadata = AudioMetadataReader.TryRead(source.SourcePath);

            if (File.Exists(source.SourcePath))
            {
                archive.CreateEntryFromFile(source.SourcePath, archivePath, CompressionLevel.Optimal);
            }

            manifest.Entries.Add(new AudioEntry
            {
                AudioId = source.AudioId,
                Path = archivePath,
                Codec = codec,
                Hash = File.Exists(source.SourcePath) ? await ComputeFileSha256Async(source.SourcePath, cancellationToken) : "",
                RightsId = "converted-bms-audio",
                SampleRate = metadata?.SampleRate ?? 0,
                Channels = metadata?.Channels ?? 0,
                DurationMs = metadata?.DurationMs ?? 0,
                Encrypted = false
            });
        }

        manifest.CodecRequired = manifest.Entries
            .Select(entry => entry.Codec)
            .Where(codec => !string.IsNullOrWhiteSpace(codec) && codec != "unknown")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(codec => codec, StringComparer.Ordinal)
            .ToList();

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await System.Text.Json.JsonSerializer.SerializeAsync(
            manifestStream,
            manifest,
            NbmsJson.SerializerOptions,
            cancellationToken);
        await manifestStream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    private static Task WriteChartAsync(
        string chartPath,
        NbmsChart chart,
        bool readableScoreJson,
        CancellationToken cancellationToken)
    {
        return readableScoreJson
            ? NbmsJson.WritePrettyCompactChartAsync(chartPath, chart, cancellationToken)
            : NbmsJson.WriteCompactChartAsync(chartPath, chart, cancellationToken);
    }

    private static async Task CreateMediaArchiveAsync(
        IReadOnlyList<MediaSource> mediaSources,
        string mediaPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(mediaPath)!);
        await using var stream = File.Create(mediaPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        var manifest = new MediaManifest
        {
            Format = "NBMS-MEDIA",
            Version = "0.1.0"
        };

        foreach (var source in mediaSources.OrderBy(source => source.MediaId, StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(source.OriginalFileName).ToLowerInvariant();
            var archivePath = $"media/{source.MediaId}{extension}";
            var metadata = MediaMetadataReader.TryRead(source.SourcePath);

            if (File.Exists(source.SourcePath))
            {
                archive.CreateEntryFromFile(source.SourcePath, archivePath, CompressionLevel.Optimal);
            }

            manifest.Entries.Add(new MediaAssetEntry
            {
                MediaId = source.MediaId,
                Path = archivePath,
                Type = DetectMediaType(source.OriginalFileName),
                MimeType = DetectMediaMimeType(source.OriginalFileName),
                Hash = File.Exists(source.SourcePath) ? await ComputeFileSha256Async(source.SourcePath, cancellationToken) : "",
                Width = metadata.Width,
                Height = metadata.Height,
                DurationMs = metadata.DurationMs,
                RightsId = "converted-bms-media"
            });
        }

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await System.Text.Json.JsonSerializer.SerializeAsync(
            manifestStream,
            manifest,
            NbmsJson.SerializerOptions,
            cancellationToken);
        await manifestStream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    private static string CreateAudioId(string wavKey, string fileName)
    {
        return $"wav_{wavKey}_{Path.GetFileNameWithoutExtension(fileName)}";
    }

    private static string DetectCodec(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".flac" => "flac",
            ".wav" => "pcm-wav",
            ".ogg" => "ogg-vorbis",
            ".oga" => "ogg-vorbis",
            ".mp3" => "mp3",
            _ => "unknown"
        };
    }

    private static string DetectMediaType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".bmp" or ".png" or ".jpg" or ".jpeg" or ".gif" => "image",
            ".mp4" or ".webm" or ".avi" or ".mpg" or ".mpeg" or ".mov" or ".mkv" or ".wmv" => "video",
            _ => "unknown"
        };
    }

    private static string DetectMediaMimeType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".bmp" => "image/bmp",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".mpg" or ".mpeg" => "video/mpeg",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".wmv" => "video/x-ms-wmv",
            _ => "application/octet-stream"
        };
    }

    private static string CreateChartId(string bmsPath)
    {
        return SanitizeId(Path.GetFileNameWithoutExtension(bmsPath));
    }

    private static string CreateUniqueFileName(string fileName, HashSet<string> usedFileNames)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = fileName;
        var index = 2;

        while (!usedFileNames.Add(candidate))
        {
            candidate = $"{baseName}_{index}{extension}";
            index++;
        }

        return candidate;
    }

    private static List<string> EnumerateBmsFiles(string sourceRoot)
    {
        return BmsPatterns
            .SelectMany(pattern => Directory.EnumerateFiles(sourceRoot, pattern, SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ApplyFolderConversionOptions(NbmsHeader header, BmsFolderConversionOptions? options)
    {
        if (!string.IsNullOrWhiteSpace(options?.Title))
        {
            header.Title = options.Title.Trim();
        }
    }

    private static string ResolveLevelName(
        string bmsPath,
        ChartReference sourceReference,
        BmsFolderConversionOptions? options)
    {
        if (options is not null &&
            options.LevelNames.TryGetValue(Path.GetFullPath(bmsPath), out var levelName) &&
            !string.IsNullOrWhiteSpace(levelName))
        {
            return levelName.Trim();
        }

        return ResolveBaseLevelName(bmsPath, sourceReference);
    }

    private static string ResolveBaseLevelName(string bmsPath, ChartReference sourceReference)
    {
        return string.IsNullOrWhiteSpace(sourceReference.LevelName)
            ? Path.GetFileNameWithoutExtension(bmsPath)
            : sourceReference.LevelName;
    }

    private static string ResolveCommonTitle(IReadOnlyList<ImportedBms> importedFiles)
    {
        var titles = importedFiles
            .Select(imported => imported.ImportResult.Header.Title)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToList();
        if (titles.Count == 0)
        {
            return "Untitled";
        }

        var prefix = titles[0];
        foreach (var title in titles.Skip(1))
        {
            prefix = CommonPrefix(prefix, title);
            if (prefix.Length == 0)
            {
                break;
            }
        }

        prefix = TrimTitlePart(TrimToLastSeparator(prefix));
        return string.IsNullOrWhiteSpace(prefix) ? titles[0] : prefix;
    }

    private static string ResolveSuggestedLevelName(string originalTitle, string commonTitle, string fallback)
    {
        var candidate = originalTitle;
        if (!string.IsNullOrWhiteSpace(commonTitle) &&
            candidate.StartsWith(commonTitle, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[commonTitle.Length..];
        }

        candidate = TrimTitlePart(candidate);
        return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
    }

    private static string CommonPrefix(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && char.ToUpperInvariant(left[index]) == char.ToUpperInvariant(right[index]))
        {
            index++;
        }

        return left[..index];
    }

    private static string TrimToLastSeparator(string value)
    {
        var separators = new[] { ' ', '-', '_', '~', ':', '[', '(' };
        var lastSeparator = value.LastIndexOfAny(separators);
        return lastSeparator > 0 ? value[..lastSeparator] : value;
    }

    private static string TrimTitlePart(string value)
    {
        return value.Trim().Trim('-', '_', '~', ':', '[', ']', '(', ')').Trim();
    }

    private static string SanitizeId(string value)
    {
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var result = new string(chars).Trim('_', '.');
        return string.IsNullOrWhiteSpace(result) ? "converted" : result;
    }

    private static string StableShortHash(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        return "sha256-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record ImportedBms(
        string BmsPath,
        BmsImportResult ImportResult,
        Dictionary<string, string> AudioIdMap,
        Dictionary<string, string> MediaIdMap,
        List<string> ImportReport);

    private sealed record AudioSource(string AudioId, string SourcePath, string OriginalFileName);

    private sealed record MediaSource(string MediaId, string SourcePath, string OriginalFileName);

}

public sealed record BmsConversionResult(string HeaderPath, string ChartPath, string AudioPath)
{
    public IReadOnlyList<string> ImportReport { get; init; } = [];
}

public sealed record BmsFolderConversionResult(IReadOnlyList<BmsConversionResult> Results);

public sealed record BmsFolderConversionOptions(
    string Title,
    IReadOnlyDictionary<string, string> LevelNames,
    string EncodingName,
    bool ReadableScoreJson = false);

public sealed record BmsConversionOptions(bool ReadableScoreJson = false, string EncodingName = "auto");

public sealed record BmsFolderConversionPreview(
    string SourceDirectory,
    string SuggestedTitle,
    IReadOnlyList<BmsChartConversionPreview> Charts);

public sealed record BmsChartConversionPreview(
    string BmsPath,
    string OriginalTitle,
    string SuggestedLevelName,
    string Mode,
    int Difficulty,
    int BpmEventCount,
    int StopEventCount,
    int HoldNoteCount,
    string LnObj);
