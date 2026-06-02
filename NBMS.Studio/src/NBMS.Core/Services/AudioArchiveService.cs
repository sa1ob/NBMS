using System.IO.Compression;
using System.Text.Json;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class AudioArchiveService
{
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
}

