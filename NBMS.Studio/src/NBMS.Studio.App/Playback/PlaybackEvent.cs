namespace NBMS.Studio.App.Playback;

public sealed class PlaybackEvent
{
    public double TimeSeconds { get; set; }
    public int Tick { get; set; }
    public string Kind { get; set; } = "";
    public string Lane { get; set; } = "";
    public string AudioId { get; set; } = "";
    public double DurationSeconds { get; set; }
}
