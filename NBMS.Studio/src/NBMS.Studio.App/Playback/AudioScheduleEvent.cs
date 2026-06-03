namespace NBMS.Studio.App.Playback;

public sealed record AudioScheduleEvent(
    int Sequence,
    double TimeSeconds,
    int Tick,
    string Kind,
    string Lane,
    string AudioId,
    double DurationSeconds);
