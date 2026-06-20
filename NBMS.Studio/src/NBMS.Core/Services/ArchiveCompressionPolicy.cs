using System.IO.Compression;

namespace NBMS.Core.Services;

public static class ArchiveCompressionPolicy
{
    private static readonly HashSet<string> AlreadyCompressedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg",
        ".oga",
        ".flac",
        ".mp3",
        ".mp4",
        ".m4v",
        ".mpg",
        ".mpeg",
        ".avi",
        ".webm",
        ".mov",
        ".mkv",
        ".wmv",
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".webp"
    };

    public static CompressionLevel ForAssetPath(string path)
    {
        var extension = Path.GetExtension(path);
        if (AlreadyCompressedExtensions.Contains(extension))
        {
            return CompressionLevel.NoCompression;
        }

        return CompressionLevel.Fastest;
    }
}
