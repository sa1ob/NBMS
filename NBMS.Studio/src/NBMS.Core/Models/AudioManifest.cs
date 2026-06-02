namespace NBMS.Core.Models;

public sealed class AudioManifest
{
    public string Format { get; set; } = "NBMS-AUDIO";
    public string Version { get; set; } = "0.1.0";
    public List<string> CodecRequired { get; set; } = [];
    public bool Encrypted { get; set; }
    public List<AudioEntry> Entries { get; set; } = [];
    public List<RightsEntry> Rights { get; set; } = [];
    public EncryptionInfo? Encryption { get; set; }
}

public sealed class AudioEntry
{
    public string AudioId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Codec { get; set; } = "";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int DurationMs { get; set; }
    public string Hash { get; set; } = "";
    public string RightsId { get; set; } = "";
    public bool Encrypted { get; set; }
    public EncryptionInfo? Encryption { get; set; }
}

public sealed class EncryptionInfo
{
    public string Algorithm { get; set; } = "";
    public string? Kdf { get; set; }
    public string? Salt { get; set; }
    public int? Iterations { get; set; }
    public string? Nonce { get; set; }
    public string? Tag { get; set; }
    public string? Aad { get; set; }
    public string? OriginalPath { get; set; }
    public string? OriginalHash { get; set; }
}

