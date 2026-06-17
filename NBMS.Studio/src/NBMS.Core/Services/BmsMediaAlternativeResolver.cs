namespace NBMS.Core.Services;

public static class BmsMediaAlternativeResolver
{
    private static readonly string[] ImageExtensions = [".bmp", ".png", ".jpg", ".jpeg", ".gif", ".webp"];
    private static readonly string[] VideoExtensions = [".avi", ".mpg", ".mpeg", ".mp4", ".webm", ".mov", ".mkv", ".wmv"];

    public static BmsResolvedMediaSource Resolve(string bmsDirectory, string declaredFileName)
    {
        var exactPath = Path.GetFullPath(Path.Combine(bmsDirectory, declaredFileName));
        if (File.Exists(exactPath))
        {
            return new BmsResolvedMediaSource(exactPath, declaredFileName, false);
        }

        foreach (var candidate in EnumerateCandidates(bmsDirectory, declaredFileName))
        {
            if (File.Exists(candidate.SourcePath))
            {
                return candidate;
            }
        }

        return new BmsResolvedMediaSource(exactPath, declaredFileName, false);
    }

    public static IReadOnlyList<BmsResolvedMediaSource> EnumerateCandidates(string bmsDirectory, string declaredFileName)
    {
        var result = new List<BmsResolvedMediaSource>();
        var directoryPart = Path.GetDirectoryName(declaredFileName);
        var fileName = Path.GetFileName(declaredFileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return result;
        }

        var relativeDirectory = string.IsNullOrWhiteSpace(directoryPart) ? "" : directoryPart;
        var searchDirectory = Path.GetFullPath(Path.Combine(bmsDirectory, relativeDirectory));
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var preferredExtensions = IsVideoExtension(extension) ? VideoExtensions : ImageExtensions;
        var fallbackExtensions = IsVideoExtension(extension) ? ImageExtensions : VideoExtensions;

        AddCandidates(result, searchDirectory, relativeDirectory, stem, preferredExtensions);
        AddCandidates(result, searchDirectory, relativeDirectory, stem, fallbackExtensions);
        return result;
    }

    private static void AddCandidates(
        List<BmsResolvedMediaSource> result,
        string searchDirectory,
        string relativeDirectory,
        string stem,
        IReadOnlyList<string> extensions)
    {
        foreach (var extension in extensions)
        {
            var fileName = stem + extension;
            var relativePath = string.IsNullOrWhiteSpace(relativeDirectory)
                ? fileName
                : Path.Combine(relativeDirectory, fileName);
            result.Add(new BmsResolvedMediaSource(
                Path.GetFullPath(Path.Combine(searchDirectory, fileName)),
                relativePath,
                true));
        }
    }

    private static bool IsVideoExtension(string extension)
    {
        return VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record BmsResolvedMediaSource(string SourcePath, string RelativePath, bool IsAlternative);

public static class BmsAudioAlternativeResolver
{
    private static readonly string[] AudioExtensions = [".wav", ".ogg", ".oga", ".flac", ".mp3"];

    public static BmsResolvedAudioSource Resolve(string bmsDirectory, string declaredFileName)
    {
        var exactPath = Path.GetFullPath(Path.Combine(bmsDirectory, declaredFileName));
        if (File.Exists(exactPath))
        {
            return new BmsResolvedAudioSource(exactPath, declaredFileName, false);
        }

        foreach (var candidate in EnumerateCandidates(bmsDirectory, declaredFileName))
        {
            if (File.Exists(candidate.SourcePath))
            {
                return candidate;
            }
        }

        return new BmsResolvedAudioSource(exactPath, declaredFileName, false);
    }

    public static IReadOnlyList<BmsResolvedAudioSource> EnumerateCandidates(string bmsDirectory, string declaredFileName)
    {
        var result = new List<BmsResolvedAudioSource>();
        var directoryPart = Path.GetDirectoryName(declaredFileName);
        var fileName = Path.GetFileName(declaredFileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return result;
        }

        var relativeDirectory = string.IsNullOrWhiteSpace(directoryPart) ? "" : directoryPart;
        var searchDirectory = Path.GetFullPath(Path.Combine(bmsDirectory, relativeDirectory));
        var declaredExtension = Path.GetExtension(fileName);

        foreach (var extension in AudioExtensions)
        {
            if (extension.Equals(declaredExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidateFileName = stem + extension;
            var relativePath = string.IsNullOrWhiteSpace(relativeDirectory)
                ? candidateFileName
                : Path.Combine(relativeDirectory, candidateFileName);
            result.Add(new BmsResolvedAudioSource(
                Path.GetFullPath(Path.Combine(searchDirectory, candidateFileName)),
                relativePath,
                true));
        }

        return result;
    }
}

public sealed record BmsResolvedAudioSource(string SourcePath, string RelativePath, bool IsAlternative);
