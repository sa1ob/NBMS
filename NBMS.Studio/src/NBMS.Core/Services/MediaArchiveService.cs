using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class MediaArchiveService
{
    private readonly HashService _hashService = new();

    public async Task<MediaManifest> ReadManifestAsync(string mediaArchivePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(mediaArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException(".nbmg に manifest.json がありません。");

        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<MediaManifest>(manifestStream, NbmsJson.SerializerOptions, cancellationToken);
        return manifest ?? throw new InvalidDataException("media manifestを読み込めませんでした。");
    }

    public async Task WriteManifestAsync(
        string mediaArchivePath,
        MediaManifest manifest,
        CancellationToken cancellationToken = default)
    {
        EnsureArchive(mediaArchivePath);
        await using var stream = File.Open(mediaArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false);
        archive.GetEntry("manifest.json")?.Delete();
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(manifestStream, manifest, NbmsJson.SerializerOptions, cancellationToken);
        await manifestStream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    public async Task<MediaAssetEntry> AddMediaFileAsync(
        string mediaArchivePath,
        MediaManifest manifest,
        string sourceFilePath,
        string mediaId,
        string type,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("追加するメディアファイルが見つかりません。", sourceFilePath);
        }

        EnsureArchive(mediaArchivePath);
        var normalizedMediaId = SanitizeId(string.IsNullOrWhiteSpace(mediaId)
            ? Path.GetFileNameWithoutExtension(sourceFilePath)
            : mediaId);
        normalizedMediaId = EnsureUniqueMediaId(manifest, normalizedMediaId);
        var archivePath = EnsureUniqueArchivePath(mediaArchivePath, $"media/{normalizedMediaId}{Path.GetExtension(sourceFilePath).ToLowerInvariant()}");

        await using (var stream = File.Open(mediaArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false))
        {
            var entry = archive.CreateEntry(archivePath, ArchiveCompressionPolicy.ForAssetPath(archivePath));
            await using var entryStream = entry.Open();
            await using var sourceStream = File.OpenRead(sourceFilePath);
            await sourceStream.CopyToAsync(entryStream, cancellationToken);
        }

        var mediaEntry = new MediaAssetEntry
        {
            MediaId = normalizedMediaId,
            Path = archivePath,
            Type = string.IsNullOrWhiteSpace(type) ? ResolveMediaType(sourceFilePath) : type.Trim(),
            MimeType = ResolveMimeType(sourceFilePath),
            Hash = await _hashService.ComputeFileSha256Async(sourceFilePath, cancellationToken)
        };

        manifest.Entries.Add(mediaEntry);
        await WriteManifestAsync(mediaArchivePath, manifest, cancellationToken);
        return mediaEntry;
    }

    public async Task RemoveMediaEntriesAsync(
        string mediaArchivePath,
        MediaManifest manifest,
        IEnumerable<string> mediaIds,
        CancellationToken cancellationToken = default)
    {
        var idSet = mediaIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (idSet.Count == 0)
        {
            return;
        }

        var removePaths = manifest.Entries
            .Where(entry => idSet.Contains(entry.MediaId))
            .Select(entry => entry.Path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        manifest.Entries = manifest.Entries
            .Where(entry => !idSet.Contains(entry.MediaId))
            .ToList();

        await using (var stream = File.Open(mediaArchivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: false))
        {
            foreach (var path in removePaths)
            {
                archive.GetEntry(path)?.Delete();
            }
        }

        await WriteManifestAsync(mediaArchivePath, manifest, cancellationToken);
    }

    public async Task RenameMediaEntryArchivePathAsync(
        string mediaArchivePath,
        MediaManifest manifest,
        MediaAssetEntry entry,
        string newMediaId,
        CancellationToken cancellationToken = default)
    {
        EnsureArchive(mediaArchivePath);
        var oldPath = entry.Path.Replace('\\', '/');
        var extension = Path.GetExtension(oldPath);
        var newPath = EnsureUniqueArchivePath(mediaArchivePath, $"media/{SanitizeId(newMediaId)}{extension}");
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
        {
            await MoveArchiveEntryAsync(mediaArchivePath, oldPath, newPath, cancellationToken);
            entry.Path = newPath;
        }

        await WriteManifestAsync(mediaArchivePath, manifest, cancellationToken);
    }

    public async Task<List<ArchiveIntegrityIssue>> ValidateArchiveEntriesAsync(
        string mediaArchivePath,
        MediaManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<ArchiveIntegrityIssue>();
        await using var stream = File.OpenRead(mediaArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in manifest.Entries)
        {
            var path = entry.Path.Replace('\\', '/');
            var archiveEntry = archive.GetEntry(path);
            if (archiveEntry is null)
            {
                issues.Add(new ArchiveIntegrityIssue("Error", entry.MediaId, path, $"missing archive entry: {path}"));
                continue;
            }

            try
            {
                var actualHash = await ComputeArchiveEntrySha256Async(archiveEntry, cancellationToken);
                if (!string.IsNullOrWhiteSpace(entry.Hash) &&
                    !string.Equals(entry.Hash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ArchiveIntegrityIssue("Error", entry.MediaId, path, $"hash mismatch: {entry.Hash} != {actualHash}"));
                }
            }
            catch (Exception ex)
            {
                issues.Add(new ArchiveIntegrityIssue("Error", entry.MediaId, path, $"broken archive entry: {ex.Message}"));
            }
        }

        return issues;
    }

    public async Task ExtractMediaFilesAsync(
        string mediaArchivePath,
        MediaManifest manifest,
        string destinationDirectory,
        IReadOnlyDictionary<string, string> outputFileNames,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        await using var stream = File.OpenRead(mediaArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in manifest.Entries)
        {
            if (!outputFileNames.TryGetValue(entry.MediaId, out var outputFileName) ||
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

    private static void EnsureArchive(string mediaArchivePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(mediaArchivePath))!);
        if (File.Exists(mediaArchivePath))
        {
            return;
        }

        using var stream = File.Create(mediaArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using var manifestStream = manifestEntry.Open();
        JsonSerializer.Serialize(manifestStream, new MediaManifest(), NbmsJson.SerializerOptions);
    }

    private static string EnsureUniqueArchivePath(string mediaArchivePath, string preferredPath)
    {
        using var stream = File.OpenRead(mediaArchivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var existing = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(preferredPath))
        {
            return preferredPath;
        }

        var directory = Path.GetDirectoryName(preferredPath)?.Replace('\\', '/') ?? "media";
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

    private static string EnsureUniqueMediaId(MediaManifest manifest, string preferredId)
    {
        var existing = manifest.Entries.Select(entry => entry.MediaId).ToHashSet(StringComparer.Ordinal);
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

    private static string SanitizeId(string value)
    {
        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private static string ResolveMediaType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".avi" or ".webm" or ".mov" or ".mkv" or ".wmv" => "video",
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" => "image",
            _ => "other"
        };
    }

    private static string ResolveMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".wmv" => "video/x-ms-wmv",
            _ => "application/octet-stream"
        };
    }
}
