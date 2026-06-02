using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBMS.Core.Models;

public sealed class NbmsChart
{
    public string Format { get; set; } = "NBMS-CHART";
    public string Version { get; set; } = "0.1.0";
    public string ChartId { get; set; } = "";
    public string Mode { get; set; } = "";
    public int Resolution { get; set; } = 960;
    public List<LaneDefinition> Lanes { get; set; } = [];
    public List<TimingEvent> Timing { get; set; } = [];
    public List<NoteEvent> Notes { get; set; } = [];
    public List<BackgroundAudioEvent> BackgroundAudio { get; set; } = [];
    public List<MediaEvent> MediaEvents { get; set; } = [];
    public JsonElement? Metadata { get; set; }
    public List<ExtensionDeclaration> Extensions { get; set; } = [];
}

public sealed class LaneDefinition
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public int Index { get; set; }
}

public sealed class TimingEvent
{
    public int Tick { get; set; }
    public string Type { get; set; } = "";
    public double? Value { get; set; }
    public int? DurationTicks { get; set; }
    public string? ExtensionId { get; set; }
    public string? Event { get; set; }
    public JsonElement? Payload { get; set; }
}

public sealed class NoteEvent
{
    public int Tick { get; set; }
    public string Lane { get; set; } = "";
    public string Type { get; set; } = "tap";
    public string? AudioId { get; set; }
    public int? DurationTicks { get; set; }
    public double? Volume { get; set; }
    public double? Pan { get; set; }
}

public sealed class BackgroundAudioEvent
{
    public int Tick { get; set; }
    public string AudioId { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lane { get; set; }
    public double? Volume { get; set; }
    public double? Pan { get; set; }
}

public sealed class MediaEvent
{
    public int Tick { get; set; }
    public string MediaId { get; set; } = "";
    public string Type { get; set; } = "";
    public int? Layer { get; set; }
}
