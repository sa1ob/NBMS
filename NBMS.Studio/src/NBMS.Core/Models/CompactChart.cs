using System.Text.Json;
using System.Text.Json.Serialization;

namespace NBMS.Core.Models;

public sealed class CompactChart
{
    public string Format { get; set; } = "NBMS-CHART";
    public string Version { get; set; } = "0.2.0";
    public string Encoding { get; set; } = "compact-json";
    public string ChartId { get; set; } = "";
    public string Mode { get; set; } = "";
    public int Resolution { get; set; } = 960;
    public CompactChartDictionary Dictionary { get; set; } = new();

    // timing tuple: [tick, type, valueOrDuration?, extra?]
    // type は v0.2 初期では "bpm" / "bar" / "stop" などの短い文字列を保持する。
    public List<object?[]> Timing { get; set; } = [];

    // note tuple: [tick, laneIndex, typeIndex, audioIndex?, durationTicks?, volume?, pan?]
    // typeIndex: 0=tap, 1=hold, 2=mine。末尾の既定値は保存時に省略する。
    public List<object?[]> Notes { get; set; } = [];

    // background audio tuple: [tick, audioIndex, backgroundLaneIndex?, volume?, pan?]
    // audioIndex は dictionary.audio、backgroundLaneIndex は dictionary.lanes を参照する。
    public List<object?[]> BackgroundAudio { get; set; } = [];

    // media event tuple: [tick, mediaIndex, type, layer?]
    // type は v0.2 初期では "image" / "video" などの短い文字列を保持する。
    public List<object?[]> MediaEvents { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Metadata { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ExtensionDeclaration>? Extensions { get; set; }
}

public sealed class CompactChartDictionary
{
    public List<string> Lanes { get; set; } = [];
    public List<string> Audio { get; set; } = [];
    public List<string> Media { get; set; } = [];
}
