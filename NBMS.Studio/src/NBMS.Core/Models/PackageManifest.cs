namespace NBMS.Core.Models;

public sealed class PackageManifest
{
    public string Format { get; set; } = "NBMS-PACKAGE";
    public string Version { get; set; } = "0.1.0";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Header { get; set; } = "song.nbmh";
    public List<string> Files { get; set; } = [];
}

