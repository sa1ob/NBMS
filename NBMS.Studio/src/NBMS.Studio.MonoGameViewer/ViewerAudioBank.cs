using System.IO.Compression;
using System.Text.Json;
using NBMS.Core.Models;
using NBMS.Core.Services;

namespace NBMS.Studio.MonoGameViewer;

public sealed class ViewerAudioBank : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Dictionary<string, string> _filesByAudioId;

    private ViewerAudioBank(string tempDirectory, Dictionary<string, string> filesByAudioId)
    {
        _tempDirectory = tempDirectory;
        _filesByAudioId = filesByAudioId;
    }

    public int Count => _filesByAudioId.Count;

    public IEnumerable<ViewerAudioFile> Files => _filesByAudioId
        .Select(pair => new ViewerAudioFile(pair.Key, pair.Value));

    public static ViewerAudioBank Load(string audioArchivePath, IEnumerable<string> requiredAudioIds)
    {
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

        var filesByAudioId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            if (required.Count > 0 && !required.Contains(entry.AudioId))
            {
                continue;
            }

            var archiveEntry = archive.GetEntry(entry.Path.Replace('\\', '/'));
            if (archiveEntry is null)
            {
                continue;
            }

            var extension = Path.GetExtension(entry.Path);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ResolveExtension(entry.Codec);
            }

            var outputPath = Path.Combine(tempDirectory, $"{SanitizeFileName(entry.AudioId)}{extension}");
            archiveEntry.ExtractToFile(outputPath, overwrite: true);
            filesByAudioId[entry.AudioId] = outputPath;
        }

        return new ViewerAudioBank(tempDirectory, filesByAudioId);
    }

    public bool TryGetFilePath(string audioId, out string filePath)
    {
        return _filesByAudioId.TryGetValue(audioId, out filePath!);
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

public sealed record ViewerAudioFile(string AudioId, string FilePath);
