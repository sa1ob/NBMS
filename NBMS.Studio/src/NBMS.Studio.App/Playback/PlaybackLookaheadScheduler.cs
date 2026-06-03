namespace NBMS.Studio.App.Playback;

public sealed class PlaybackLookaheadScheduler
{
    private PlaybackSession? _session;
    private int _nextEventIndex;

    public int NextEventIndex => _nextEventIndex;

    public void Reset(PlaybackSession session, double startSeconds)
    {
        _session = session;
        _nextEventIndex = session.FindNextEventIndex(startSeconds);
    }

    public void Clear()
    {
        _session = null;
        _nextEventIndex = 0;
    }

    public IReadOnlyList<AudioScheduleEvent> Poll(double currentSeconds, double lookaheadSeconds)
    {
        if (_session is null)
        {
            return [];
        }

        var scheduleUntil = currentSeconds + Math.Max(0, lookaheadSeconds);
        return _session.TakeDueEvents(scheduleUntil, _nextEventIndex, out _nextEventIndex);
    }
}
