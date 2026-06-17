namespace NBMS.Studio.App.Playback;

public sealed class PlaybackTimelineMap
{
    private readonly HashSet<(int Tick, long TimeKey)> _stopPoints;

    public PlaybackTimelineMap(IEnumerable<PlaybackTimelinePoint> points, double tailTicksPerSecond)
    {
        Points = points
            .OrderBy(point => point.TimeSeconds)
            .ThenBy(point => point.Tick)
            .ToList();
        TailTicksPerSecond = tailTicksPerSecond > 0 ? tailTicksPerSecond : 1920.0;
        _stopPoints = Points
            .Where(point =>
                point.Kind.Equals("Timing", StringComparison.OrdinalIgnoreCase) &&
                point.Detail.StartsWith("STOP", StringComparison.OrdinalIgnoreCase))
            .Select(point => (point.Tick, TimeKey: ToTimeKey(point.TimeSeconds)))
            .ToHashSet();
    }

    public IReadOnlyList<PlaybackTimelinePoint> Points { get; }

    public double TailTicksPerSecond { get; }

    public double EstimateTickAt(double elapsedSeconds)
    {
        if (Points.Count == 0)
        {
            return 0;
        }

        if (elapsedSeconds <= Points[0].TimeSeconds)
        {
            return Points[0].Tick;
        }

        var left = 0;
        var right = Points.Count - 1;
        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            if (Points[middle].TimeSeconds <= elapsedSeconds)
            {
                left = middle + 1;
            }
            else
            {
                right = middle - 1;
            }
        }

        var previousIndex = Math.Clamp(right, 0, Points.Count - 1);
        if (previousIndex >= Points.Count - 1)
        {
            var tail = Points[^1];
            var tailSeconds = Math.Max(0, elapsedSeconds - tail.TimeSeconds);
            return tail.Tick + tailSeconds * TailTicksPerSecond;
        }

        var previous = Points[previousIndex];
        var next = Points[previousIndex + 1];
        var span = next.TimeSeconds - previous.TimeSeconds;
        if (span <= 0)
        {
            return next.Tick;
        }

        if (HasStopAt(previous.Tick, previous.TimeSeconds))
        {
            return previous.Tick;
        }

        var ratio = Math.Clamp((elapsedSeconds - previous.TimeSeconds) / span, 0, 1);
        return previous.Tick + (next.Tick - previous.Tick) * ratio;
    }

    public double EstimateSecondsAt(double tick)
    {
        if (Points.Count == 0)
        {
            return 0;
        }

        var orderedByTick = Points
            .OrderBy(point => point.Tick)
            .ThenBy(point => point.TimeSeconds)
            .ToList();
        if (tick <= orderedByTick[0].Tick)
        {
            return orderedByTick[0].TimeSeconds;
        }

        for (var index = 0; index < orderedByTick.Count - 1; index++)
        {
            var previous = orderedByTick[index];
            var next = orderedByTick[index + 1];
            if (tick > next.Tick)
            {
                continue;
            }

            var tickSpan = next.Tick - previous.Tick;
            if (tickSpan <= 0)
            {
                return next.TimeSeconds;
            }

            var ratio = Math.Clamp((tick - previous.Tick) / tickSpan, 0, 1);
            return previous.TimeSeconds + (next.TimeSeconds - previous.TimeSeconds) * ratio;
        }

        var tail = orderedByTick[^1];
        return tail.TimeSeconds + Math.Max(0, tick - tail.Tick) / TailTicksPerSecond;
    }

    private bool HasStopAt(int tick, double timeSeconds)
    {
        return _stopPoints.Contains((tick, ToTimeKey(timeSeconds)));
    }

    private static long ToTimeKey(double timeSeconds)
    {
        return (long)Math.Round(timeSeconds * 10000);
    }
}
