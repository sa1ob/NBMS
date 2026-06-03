namespace NBMS.Studio.App.Playback;

public sealed record PlaybackTimelinePoint(
    int Tick,
    double TimeSeconds,
    string Kind,
    string Lane,
    string Detail);
