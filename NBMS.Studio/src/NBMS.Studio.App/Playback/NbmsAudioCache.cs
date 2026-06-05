using System.IO.Compression;
using NAudio.Vorbis;
using NAudio.Wave;
using NBMS.Core.Models;

namespace NBMS.Studio.App.Playback;

public sealed class NbmsAudioCache : IDisposable
{
    private readonly Dictionary<string, string> _filesByAudioId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _durationsByAudioId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _preparedAudioIds = new(StringComparer.Ordinal);
    private readonly string _audioArchivePath;
    private readonly AudioManifest _manifest;
    private readonly string _cacheDirectory;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _prepareGate = new(1, 1);
    private bool _disposed;

    private NbmsAudioCache(string audioArchivePath, AudioManifest manifest, string cacheDirectory)
    {
        _audioArchivePath = audioArchivePath;
        _manifest = manifest;
        _cacheDirectory = cacheDirectory;
    }

    public static NbmsAudioCache CreateEmpty(string audioArchivePath, AudioManifest manifest)
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "NBMS.Studio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);
        return new NbmsAudioCache(audioArchivePath, manifest, cacheDirectory);
    }

    public static NbmsAudioCache Create(
        string audioArchivePath,
        AudioManifest manifest,
        IEnumerable<string>? requiredAudioIds = null,
        IProgress<AudioCacheProgress>? progress = null)
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "NBMS.Studio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);

        var cache = new NbmsAudioCache(audioArchivePath, manifest, cacheDirectory);
        cache.PrepareAudioIds(requiredAudioIds, progress);
        return cache;
    }

    public bool TryGetFilePath(string audioId, out string path)
    {
        lock (_gate)
        {
            return _filesByAudioId.TryGetValue(audioId, out path!);
        }
    }

    public IReadOnlyDictionary<string, double> DurationsByAudioId
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, double>(_durationsByAudioId, StringComparer.Ordinal);
            }
        }
    }

    public bool Covers(IEnumerable<string> audioIds)
    {
        lock (_gate)
        {
            return audioIds
                .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
                .All(audioId => _preparedAudioIds.Contains(audioId));
        }
    }

    public void PrepareAudioIds(
        IEnumerable<string>? requiredAudioIds,
        IProgress<AudioCacheProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _prepareGate.Wait(cancellationToken);
        try
        {
            ExtractAudioEntries(requiredAudioIds, progress, cancellationToken);
        }
        finally
        {
            _prepareGate.Release();
        }
    }

    private void ExtractAudioEntries(
        IEnumerable<string>? requiredAudioIds,
        IProgress<AudioCacheProgress>? progress,
        CancellationToken cancellationToken)
    {
        var requiredList = requiredAudioIds?
            .Where(audioId => !string.IsNullOrWhiteSpace(audioId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var required = requiredList?.ToHashSet(StringComparer.Ordinal);
        List<AudioEntry> targetEntries;
        lock (_gate)
        {
            ThrowIfDisposed();
            targetEntries = _manifest.Entries
                .Where(entry => required is null || required.Contains(entry.AudioId))
                .Where(entry => !_preparedAudioIds.Contains(entry.AudioId))
                .ToList();
        }

        if (requiredList is not null)
        {
            var order = requiredList
                .Select((audioId, index) => new { audioId, index })
                .ToDictionary(item => item.audioId, item => item.index, StringComparer.Ordinal);
            targetEntries = targetEntries
                .OrderBy(entry => order.GetValueOrDefault(entry.AudioId, int.MaxValue))
                .ToList();
        }

        var total = targetEntries.Count;
        var processed = 0;

        using var stream = File.OpenRead(_audioArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in targetEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            processed++;
            progress?.Report(new AudioCacheProgress(processed, total, entry.AudioId, "extract"));

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
            progress?.Report(new AudioCacheProgress(processed, total, entry.AudioId, "prepare"));
            // OGG/Vorbisは一時WAVへ全展開せず、抽出したファイルを直接Readerへ渡す。
            // 大量音源の譜面では全展開が再生開始待ちの主因になるため、decodeは再生側へ寄せる。
            var playbackPath = outputPath;
            var durationSeconds = entry.DurationMs > 0
                ? entry.DurationMs / 1000.0
                : TryReadDurationSeconds(playbackPath);

            lock (_gate)
            {
                ThrowIfDisposed();
                _filesByAudioId[entry.AudioId] = playbackPath;
                _preparedAudioIds.Add(entry.AudioId);
                if (durationSeconds > 0)
                {
                    _durationsByAudioId[entry.AudioId] = durationSeconds;
                }
            }
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

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NbmsAudioCache));
        }
    }
}

public sealed record AudioCacheProgress(
    int Processed,
    int Total,
    string AudioId,
    string Stage);
