namespace NBMS.Core.Models;

public sealed class MediaManifest
{
    public string Format { get; set; } = "NBMS-MEDIA";
    public string Version { get; set; } = "0.1.0";
    public List<MediaAssetEntry> Entries { get; set; } = [];
}

public sealed class MediaAssetEntry
{
    public string MediaId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public string MimeType { get; set; } = "";
    public string Hash { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? DurationMs { get; set; }
    public string RightsId { get; set; } = "";
}
