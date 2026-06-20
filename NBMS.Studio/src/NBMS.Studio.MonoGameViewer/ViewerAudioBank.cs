using System.IO.Compression;
using System.Text.Json;
using NBMS.Core.Models;
using NBMS.Core.Services;

namespace NBMS.Studio.MonoGameViewer;

public sealed class ViewerAudioBank : IDisposable
{
    private readonly string _audioArchivePath;
    private readonly string _tempDirectory;
    private readonly Dictionary<string, ViewerAudioEntry> _entriesByAudioId;
    private readonly Dictionary<string, ViewerAudioFile> _filesByAudioId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _extractLocks = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private ViewerAudioBank(
        string audioArchivePath,
        string tempDirectory,
        Dictionary<string, ViewerAudioEntry> entriesByAudioId)
    {
        _audioArchivePath = audioArchivePath;
        _tempDirectory = tempDirectory;
        _entriesByAudioId = entriesByAudioId;
    }

    public int Count => _entriesByAudioId.Count;

    public int ExtractedCount
    {
        get
        {
            lock (_gate)
            {
                return _filesByAudioId.Count;
            }
        }
    }

    public static ViewerAudioBank Load(string audioArchivePath, IEnumerable<string> requiredAudioIds)
    {
        using var measure = PerformanceLog.Measure($"ViewerAudioBank.LoadManifest file={Path.GetFileName(audioArchivePath)}");
        var required = requiredAudioIds
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .ToHashSet(StringComparer.Ordinal);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "NBMS.Studio.MonoGameViewer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        using var stream = File.OpenRead(audioArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("audio.nbma manifest.json was not found.");
        using var manifestStream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<AudioManifest>(manifestStream, NbmsJson.SerializerOptions)
            ?? throw new InvalidDataException("audio manifest was empty.");

        var entriesByAudioId = new Dictionary<string, ViewerAudioEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (required.Count > 0 && !required.Contains(entry.AudioId))
            {
                continue;
            }

            entriesByAudioId[entry.AudioId] = new ViewerAudioEntry(
                entry.AudioId,
                entry.Path.Replace('\\', '/'),
                entry.Codec,
                Math.Max(0, entry.DurationMs) / 1000.0);
        }

        PerformanceLog.Mark($"ViewerAudioBank.LoadManifest entries={entriesByAudioId.Count}");
        return new ViewerAudioBank(audioArchivePath, tempDirectory, entriesByAudioId);
    }

    public IReadOnlyList<ViewerAudioFile> ExtractFiles(IEnumerable<string> audioIds, bool writePerformanceLog = true)
    {
        var result = new List<ViewerAudioFile>();
        foreach (var audioId in audioIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            if (TryGetFile(audioId, out var file, writePerformanceLog))
            {
                result.Add(file);
            }
        }

        return result;
    }

    public bool TryGetFilePath(string audioId, out string filePath)
    {
        if (TryGetFile(audioId, out var file))
        {
            filePath = file.FilePath;
            return true;
        }

        filePath = "";
        return false;
    }

    public bool TryGetAudioFile(string audioId, out ViewerAudioFile file)
    {
        return TryGetFile(audioId, out file);
    }

    public double ResolveDurationSeconds(string audioId)
    {
        if (_entriesByAudioId.TryGetValue(audioId, out var entry) && entry.DurationSeconds > 0)
        {
            return entry.DurationSeconds;
        }

        return 0.25;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private bool TryGetFile(string audioId, out ViewerAudioFile file, bool writePerformanceLog = true)
    {
        lock (_gate)
        {
            if (_filesByAudioId.TryGetValue(audioId, out file!))
            {
                return true;
            }
        }

        if (!_entriesByAudioId.TryGetValue(audioId, out var entry))
        {
            file = default!;
            return false;
        }

        object extractLock;
        lock (_gate)
        {
            if (_filesByAudioId.TryGetValue(audioId, out file!))
            {
                return true;
            }

            if (!_extractLocks.TryGetValue(audioId, out extractLock!))
            {
                extractLock = new object();
                _extractLocks[audioId] = extractLock;
            }
        }

        lock (extractLock)
        {
            lock (_gate)
            {
                if (_filesByAudioId.TryGetValue(audioId, out file!))
                {
                    return true;
                }
            }

            var extracted = ExtractEntry(entry, writePerformanceLog);
            lock (_gate)
            {
                _filesByAudioId[audioId] = extracted;
            }

            file = extracted;
            return true;
        }
    }

    public bool IsExtracted(string audioId)
    {
        lock (_gate)
        {
            return _filesByAudioId.ContainsKey(audioId);
        }
    }

    public bool HasEntry(string audioId)
    {
        return _entriesByAudioId.ContainsKey(audioId);
    }

    public IReadOnlyList<string> AudioIds => _entriesByAudioId.Keys.ToList();

    private ViewerAudioFile ExtractEntry(ViewerAudioEntry entry, bool writePerformanceLog)
    {
        using var measure = writePerformanceLog
            ? PerformanceLog.Measure($"ViewerAudioBank.Extract id={entry.AudioId}", minimumElapsedMs: 10)
            : null;
        using var stream = File.OpenRead(_audioArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var archiveEntry = archive.GetEntry(entry.ArchivePath);
        if (archiveEntry is null)
        {
            throw new FileNotFoundException($"audio archive entry was not found: {entry.ArchivePath}");
        }

        var extension = Path.GetExtension(entry.ArchivePath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ResolveExtension(entry.Codec);
        }

        var outputPath = Path.Combine(_tempDirectory, $"{SanitizeFileName(entry.AudioId)}{extension}");
        if (!File.Exists(outputPath))
        {
            archiveEntry.ExtractToFile(outputPath, overwrite: true);
        }

        return new ViewerAudioFile(entry.AudioId, outputPath, entry.DurationSeconds);
    }

    private static string ResolveExtension(string codec)
    {
        return codec.ToLowerInvariant() switch
        {
            "ogg" or "vorbis" or "ogg-vorbis" => ".ogg",
            "flac" => ".flac",
            _ => ".wav"
        };
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }
}

internal sealed record ViewerAudioEntry(string AudioId, string ArchivePath, string Codec, double DurationSeconds);

public sealed record ViewerAudioFile(string AudioId, string FilePath, double DurationSeconds);
