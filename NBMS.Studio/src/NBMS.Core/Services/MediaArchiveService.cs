using System.IO.Compression;
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
            var entry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
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
