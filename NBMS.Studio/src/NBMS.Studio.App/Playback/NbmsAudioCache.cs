using System.IO.Compression;
using NAudio.Vorbis;
using NAudio.Wave;
using NBMS.Core.Models;

namespace NBMS.Studio.App.Playback;

public sealed class NbmsAudioCache : IDisposable
{
    private readonly Dictionary<string, string> _filesByAudioId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _durationsByAudioId = new(StringComparer.Ordinal);
    private readonly string _cacheDirectory;

    private NbmsAudioCache(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
    }

    public static NbmsAudioCache Create(string audioArchivePath, AudioManifest manifest)
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "NBMS.Studio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);

        var cache = new NbmsAudioCache(cacheDirectory);
        cache.ExtractAudioEntries(audioArchivePath, manifest);
        return cache;
    }

    public bool TryGetFilePath(string audioId, out string path)
    {
        return _filesByAudioId.TryGetValue(audioId, out path!);
    }

    public IReadOnlyDictionary<string, double> DurationsByAudioId => _durationsByAudioId;

    private void ExtractAudioEntries(string audioArchivePath, AudioManifest manifest)
    {
        using var stream = File.OpenRead(audioArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in manifest.Entries)
        {
            if (entry.Encrypted)
            {
                continue;
            }

            var archiveEntry = archive.GetEntry(entry.Path.Replace('\\', '/'));
            if (archiveEntry is null)
            {
                continue;
            }

            var safeFileName = $"{SanitizeFileName(entry.AudioId)}{Path.GetExtension(entry.Path)}";
            var outputPath = Path.Combine(_cacheDirectory, safeFileName);
            archiveEntry.ExtractToFile(outputPath, overwrite: true);
            var playbackPath = PreparePlaybackFile(outputPath, entry.AudioId);
            _filesByAudioId[entry.AudioId] = playbackPath;
            var durationSeconds = entry.DurationMs > 0
                ? entry.DurationMs / 1000.0
                : TryReadDurationSeconds(playbackPath);
            if (durationSeconds > 0)
            {
                _durationsByAudioId[entry.AudioId] = durationSeconds;
            }
        }
    }

    private string PreparePlaybackFile(string filePath, string audioId)
    {
        if (!IsOggVorbisFile(filePath))
        {
            return filePath;
        }

        try
        {
            var outputPath = Path.Combine(_cacheDirectory, $"{SanitizeFileName(audioId)}.decoded.wav");
            using var reader = new VorbisWaveReader(filePath);
            WaveFileWriter.CreateWaveFile(outputPath, reader);
            return outputPath;
        }
        catch
        {
            return filePath;
        }
    }

    private static double TryReadDurationSeconds(string filePath)
    {
        try
        {
            using var reader = CreateReader(filePath);
            return reader.TotalTime.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    private static WaveStream CreateReader(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".ogg" or ".oga" => new VorbisWaveReader(filePath),
            _ => new MediaFoundationReader(filePath)
        };
    }

    private static bool IsOggVorbisFile(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() is ".ogg" or ".oga";
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }
        catch
        {
            // 一時ファイル削除に失敗しても、再生処理自体は失敗扱いにしない。
        }
    }
}
