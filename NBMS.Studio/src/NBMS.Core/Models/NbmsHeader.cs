using System.Text.Json.Serialization;

namespace NBMS.Core.Models;

public sealed class NbmsHeader
{
    public string Format { get; set; } = "NBMS";
    public string Version { get; set; } = "0.1.0";
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Genre { get; set; } = "";
    public BpmInfo Bpm { get; set; } = new();
    public PreviewInfo? Preview { get; set; }
    public FileReference Audio { get; set; } = new();
    public FileReference? Media { get; set; }
    public List<ChartReference> Charts { get; set; } = [];
    public RightsInfo Rights { get; set; } = new();
    public SecurityInfo Security { get; set; } = new();
    public List<ExtensionDeclaration> Extensions { get; set; } = [];
}

public sealed class BpmInfo
{
    public double Initial { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
}

public sealed class PreviewInfo
{
    public string AudioId { get; set; } = "";
    public int StartMs { get; set; }
    public int DurationMs { get; set; }
}

public class FileReference
{
    public string File { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool? Optional { get; set; }
}

public sealed class ChartReference
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public string Mode { get; set; } = "";
    public int Difficulty { get; set; }
    public string LevelName { get; set; } = "";
    public string Hash { get; set; } = "";
    public string HashAlgorithm { get; set; } = "sha256-canonical-json";
}

public sealed class RightsInfo
{
    public string Music { get; set; } = "";
    public string Chart { get; set; } = "";
    public string SoundSource { get; set; } = "";
    public string License { get; set; } = "";
    public string Contact { get; set; } = "";
    public List<RightsEntry> Entries { get; set; } = [];
}

public sealed class RightsEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string License { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class SecurityInfo
{
    public bool Signed { get; set; }
    public bool Encrypted { get; set; }
    public string? SignatureAlgorithm { get; set; }
    public string? PublicKeyId { get; set; }
    public string? PublicKey { get; set; }
    public string? Signature { get; set; }
    public string EditPolicy { get; set; } = "open";
}

public sealed class ExtensionDeclaration
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Required { get; set; }
}

