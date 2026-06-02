using System.IO.Compression;
using NBMS.Core.Models;

namespace NBMS.Studio.App.Playback;

public sealed class NbmsAudioCache : IDisposable
{
    private readonly Dictionary<string, string> _filesByAudioId = new(StringComparer.Ordinal);
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
            _filesByAudioId[entry.AudioId] = outputPath;
        }
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
