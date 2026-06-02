using System.IO.Compression;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class PackageService
{
    public async Task CreatePackageAsync(NbmsProject project, string outputPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var files = CollectPackageFiles(project);
        var manifest = new PackageManifest
        {
            Id = project.Header.Id,
            Title = project.Header.Title,
            Header = "song.nbmh",
            Files = files.Select(file => file.ArchiveName).ToList()
        };

        await using var stream = File.Create(outputPath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        await WriteJsonEntryAsync(archive, "package_manifest.json", manifest, cancellationToken);

        foreach (var file in files)
        {
            archive.CreateEntryFromFile(file.SourcePath, file.ArchiveName, CompressionLevel.Optimal);
        }
    }

    private static List<PackageFile> CollectPackageFiles(NbmsProject project)
    {
        var files = new List<PackageFile>
        {
            new(project.HeaderPath, "song.nbmh"),
            new(Path.Combine(project.RootDirectory, project.Header.Audio.File), NormalizeArchivePath(project.Header.Audio.File))
        };

        if (project.Header.Media is { File.Length: > 0 } media)
        {
            var mediaPath = Path.Combine(project.RootDirectory, media.File);
            if (File.Exists(mediaPath))
            {
                files.Add(new PackageFile(mediaPath, NormalizeArchivePath(media.File)));
            }
        }

        files.AddRange(project.Charts.Select(chart => new PackageFile(chart.Path, NormalizeArchivePath(chart.Reference.File))));
        return files
            .GroupBy(file => file.ArchiveName, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive archive, string name, T value, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await System.Text.Json.JsonSerializer.SerializeAsync(entryStream, value, NbmsJson.SerializerOptions, cancellationToken);
        await entryStream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }

    private static string NormalizeArchivePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private sealed record PackageFile(string SourcePath, string ArchiveName);
}

