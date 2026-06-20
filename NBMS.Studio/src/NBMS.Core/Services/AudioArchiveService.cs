using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class AudioArchiveService
{
    private readonly HashService _hashService = new();

    public async Task<AudioManifest> ReadManifestAsync(string audioArchivePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(audioArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException(".nbma に manifest.json がありません。");

        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<AudioManifest>(manifestStream, NbmsJson.SerializerOptions, cancellationToken);
        return manifest ?? throw new InvalidDataException("audio manifestを読み込めませんでした。");
    }

    public List<string> ListArchiveEntries(string audioArchivePath)
    {
        using var stream = File.OpenRead(audioArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToList();
    }

    public async Task WriteManifestAsync(
        string audioArchivePath,
        AudioManifest manifest,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.Open(audioArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false);
        archive.GetEntry("manifest.json")?.Delete();
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(
            manifestStream,
            manifest,
            NbmsJson.SerializerOptions,
            cancellationToken);
        await manifestStream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    public async Task<AudioEntry> AddAudioFileAsync(
        string audioArchivePath,
        AudioManifest manifest,
        string sourceFilePath,
        string audioId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("追加する音声ファイルが見つかりません。", sourceFilePath);
        }

        var normalizedAudioId = SanitizeAudioId(audioId);
        if (string.IsNullOrWhiteSpace(normalizedAudioId))
        {
            normalizedAudioId = SanitizeAudioId(Path.GetFileNameWithoutExtension(sourceFilePath));
        }

        normalizedAudioId = EnsureUniqueAudioId(manifest, normalizedAudioId);
        var archivePath = EnsureUniqueArchivePath(
            audioArchivePath,
            $"audio/{normalizedAudioId}{Path.GetExtension(sourceFilePath).ToLowerInvariant()}");

        await using (var stream = File.Open(audioArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false))
        {
            var entry = archive.CreateEntry(archivePath, ArchiveCompressionPolicy.ForAssetPath(archivePath));
            await using var entryStream = entry.Open();
            await using var sourceStream = File.OpenRead(sourceFilePath);
            await sourceStream.CopyToAsync(entryStream, cancellationToken);
        }

        var audioEntry = new AudioEntry
        {
            AudioId = normalizedAudioId,
            Path = archivePath,
            Codec = ResolveCodec(sourceFilePath),
            Hash = await _hashService.ComputeFileSha256Async(sourceFilePath, cancellationToken)
        };

        manifest.Entries.Add(audioEntry);
        RefreshCodecRequired(manifest);
        await WriteManifestAsync(audioArchivePath, manifest, cancellationToken);
        return audioEntry;
    }

    public async Task RemoveAudioEntriesAsync(
        string audioArchivePath,
        AudioManifest manifest,
        IEnumerable<string> audioIds,
        CancellationToken cancellationToken = default)
    {
        var idSet = audioIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (idSet.Count == 0)
        {
            return;
        }

        var removePaths = manifest.Entries
            .Where(entry => idSet.Contains(entry.AudioId))
            .Select(entry => entry.Path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        manifest.Entries = manifest.Entries
            .Where(entry => !idSet.Contains(entry.AudioId))
            .ToList();
        RefreshCodecRequired(manifest);

        await using (var stream = File.Open(audioArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false))
        {
            foreach (var path in removePaths)
            {
                archive.GetEntry(path)?.Delete();
            }
        }

        await WriteManifestAsync(audioArchivePath, manifest, cancellationToken);
    }

    public async Task RenameAudioEntryArchivePathAsync(
        string audioArchivePath,
        AudioManifest manifest,
        AudioEntry entry,
        string newAudioId,
        CancellationToken cancellationToken = default)
    {
        var oldPath = entry.Path.Replace('\\', '/');
        var extension = Path.GetExtension(oldPath);
        var newPath = EnsureUniqueArchivePath(audioArchivePath, $"audio/{SanitizeAudioId(newAudioId)}{extension}");
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            await MoveArchiveEntryAsync(audioArchivePath, oldPath, newPath, cancellationToken);
            entry.Path = newPath;
        }

        await WriteManifestAsync(audioArchivePath, manifest, cancellationToken);
    }

    public async Task<List<ArchiveIntegrityIssue>> ValidateArchiveEntriesAsync(
        string audioArchivePath,
        AudioManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<ArchiveIntegrityIssue>();
        await using var stream = File.OpenRead(audioArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in manifest.Entries)
        {
            var path = entry.Path.Replace('\\', '/');
            var archiveEntry = archive.GetEntry(path);
            if (archiveEntry is null)
            {
                issues.Add(new ArchiveIntegrityIssue("Error", entry.AudioId, path, $"missing archive entry: {path}"));
                continue;
            }

            try
            {
                var actualHash = await ComputeArchiveEntrySha256Async(archiveEntry, cancellationToken);
                if (!string.IsNullOrWhiteSpace(entry.Hash) &&
                    !string.Equals(entry.Hash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ArchiveIntegrityIssue("Error", entry.AudioId, path, $"hash mismatch: {entry.Hash} != {actualHash}"));
                }
            }
            catch (Exception ex)
            {
                issues.Add(new ArchiveIntegrityIssue("Error", entry.AudioId, path, $"broken archive entry: {ex.Message}"));
            }
        }

        return issues;
    }

    public async Task ExtractAudioFilesAsync(
        string audioArchivePath,
        AudioManifest manifest,
        string destinationDirectory,
        IReadOnlyDictionary<string, string> outputFileNames,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        await using var stream = File.OpenRead(audioArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in manifest.Entries)
        {
            if (!outputFileNames.TryGetValue(entry.AudioId, out var outputFileName) ||
                string.IsNullOrWhiteSpace(outputFileName))
            {
                continue;
            }

            var archivePath = entry.Path.Replace('\\', '/');
            var archiveEntry = archive.GetEntry(archivePath);
            if (archiveEntry is null)
            {
                continue;
            }

            var safeFileName = Path.GetFileName(outputFileName);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                continue;
            }

            var outputPath = Path.Combine(destinationDirectory, safeFileName);
            await using var input = archiveEntry.Open();
            await using var output = File.Create(outputPath);
            await input.CopyToAsync(output, cancellationToken);
        }
    }

    private string EnsureUniqueArchivePath(string audioArchivePath, string preferredPath)
    {
        var existing = File.Exists(audioArchivePath)
            ? ListArchiveEntries(audioArchivePath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(preferredPath))
        {
            return preferredPath;
        }

        var directory = Path.GetDirectoryName(preferredPath)?.Replace('\\', '/') ?? "audio";
        var fileName = Path.GetFileNameWithoutExtension(preferredPath);
        var extension = Path.GetExtension(preferredPath);
        for (var index = 2; ; index++)
        {
            var candidate = $"{directory}/{fileName}_{index}{extension}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static async Task MoveArchiveEntryAsync(
        string archivePath,
        string oldPath,
        string newPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Open(archivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false);
        var oldEntry = archive.GetEntry(oldPath);
        if (oldEntry is null)
        {
            return;
        }

        archive.GetEntry(newPath)?.Delete();
        var newEntry = archive.CreateEntry(newPath, ArchiveCompressionPolicy.ForAssetPath(newPath));
        await using (var oldStream = oldEntry.Open())
        await using (var newStream = newEntry.Open())
        {
            await oldStream.CopyToAsync(newStream, cancellationToken);
        }

        oldEntry.Delete();
    }

    private static async Task<string> ComputeArchiveEntrySha256Async(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return "sha256-" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string EnsureUniqueAudioId(AudioManifest manifest, string preferredId)
    {
        var existing = manifest.Entries
            .Select(entry => entry.AudioId)
            .ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(preferredId))
        {
            return preferredId;
        }

        for (var index = 2; ; index++)
        {
            var candidate = $"{preferredId}_{index}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeAudioId(string value)
    {
        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private static string ResolveCodec(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".wav" => "wav",
            ".flac" => "flac",
            ".ogg" or ".oga" => "ogg",
            ".mp3" => "mp3",
            _ => "unknown"
        };
    }

    private static void RefreshCodecRequired(AudioManifest manifest)
    {
        manifest.CodecRequired = manifest.Entries
            .Select(entry => entry.Codec)
            .Where(codec => !string.IsNullOrWhiteSpace(codec) && codec != "unknown")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(codec => codec, StringComparer.Ordinal)
            .ToList();
    }
}
