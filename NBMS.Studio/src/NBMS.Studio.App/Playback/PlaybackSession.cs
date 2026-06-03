using NBMS.Core.Models;
using NBMS.Core.Services;

namespace NBMS.Studio.App.Playback;

public sealed class PlaybackSession
{
    private const double UnknownAudioTailSeconds = 120.0;

    private PlaybackSession(
        PlaybackTimelineMap timelineMap,
        IReadOnlyList<AudioScheduleEvent> events,
        IReadOnlyList<PlaybackAssetPlan> assetPlan,
        double endSeconds)
    {
        TimelineMap = timelineMap;
        Events = events;
        AssetPlan = assetPlan;
        EndSeconds = endSeconds;
    }

    public PlaybackTimelineMap TimelineMap { get; }

    public IReadOnlyList<AudioScheduleEvent> Events { get; }

    public IReadOnlyList<PlaybackAssetPlan> AssetPlan { get; }

    public double EndSeconds { get; }

    public static PlaybackSession Create(
        NbmsChart chart,
        AudioManifest? audioManifest,
        TimelineService timelineService,
        IReadOnlyDictionary<string, double>? durationOverrides = null)
    {
        var timelineItems = timelineService.BuildTimeline(chart);
        var timelinePoints = timelineItems
            .Select(item => new PlaybackTimelinePoint(
                item.Tick,
                item.TimeSeconds,
                item.Kind,
                item.Lane,
                item.Detail))
            .ToList();

        var durations = (audioManifest?.Entries ?? [])
            .ToDictionary(entry => entry.AudioId, entry => ResolveDurationSeconds(entry, durationOverrides), StringComparer.Ordinal);

        var events = new List<AudioScheduleEvent>();
        foreach (var item in timelineItems.Where(item => item.Kind == "Note" || item.Kind == "BGM"))
        {
            var audioId = ResolveAudioId(item);
            if (string.IsNullOrWhiteSpace(audioId))
            {
                continue;
            }

            events.Add(new AudioScheduleEvent(
                events.Count,
                item.TimeSeconds,
                item.Tick,
                item.Kind,
                item.Lane,
                audioId,
                durations.GetValueOrDefault(audioId)));
        }

        events = events
            .OrderBy(item => item.TimeSeconds)
            .ThenBy(item => item.Tick)
            .ThenBy(item => item.Sequence)
            .ToList();

        var timelineMap = new PlaybackTimelineMap(timelinePoints, ResolveInitialTicksPerSecond(chart));
        var lastTimelineSeconds = timelineMap.Points.Count == 0 ? 0 : timelineMap.Points.Max(item => item.TimeSeconds);
        var lastAudioSeconds = events.Count == 0
            ? 0
            : events.Max(item => item.TimeSeconds + ResolvePlaybackTailSeconds(item));
        var assetPlan = BuildAssetPlan(audioManifest, events, durationOverrides);

        return new PlaybackSession(
            timelineMap,
            events,
            assetPlan,
            Math.Max(lastTimelineSeconds, lastAudioSeconds));
    }

    public int FindNextEventIndex(double elapsedSeconds)
    {
        var left = 0;
        var right = Events.Count - 1;
        var result = Events.Count;

        while (left <= right)
        {
            var middle = left + (right - left) / 2;
            if (Events[middle].TimeSeconds >= elapsedSeconds)
            {
                result = middle;
                right = middle - 1;
            }
            else
            {
                left = middle + 1;
            }
        }

        return result;
    }

    public IReadOnlyList<AudioScheduleEvent> TakeDueEvents(
        double elapsedSeconds,
        int nextEventIndex,
        out int updatedNextEventIndex)
    {
        var result = new List<AudioScheduleEvent>();
        while (nextEventIndex < Events.Count && Events[nextEventIndex].TimeSeconds <= elapsedSeconds)
        {
            result.Add(Events[nextEventIndex]);
            nextEventIndex++;
        }

        updatedNextEventIndex = nextEventIndex;
        return result;
    }

    public double EstimateTickAt(double elapsedSeconds)
    {
        return TimelineMap.EstimateTickAt(elapsedSeconds);
    }

    private static List<PlaybackAssetPlan> BuildAssetPlan(
        AudioManifest? audioManifest,
        IEnumerable<AudioScheduleEvent> events,
        IReadOnlyDictionary<string, double>? durationOverrides)
    {
        var eventList = events.ToList();
        var usedAudioIds = events
            .Select(item => item.AudioId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var playableAudioIds = eventList
            .Where(item => item.Kind.Equals("Note", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.AudioId)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var entries = audioManifest?.Entries ?? [];
        var result = new List<PlaybackAssetPlan>();

        foreach (var entry in entries.Where(entry => usedAudioIds.Contains(entry.AudioId)))
        {
            var durationSeconds = ResolveDurationSeconds(entry, durationOverrides);
            var mode = ShouldPreload(entry.Codec, durationSeconds, playableAudioIds.Contains(entry.AudioId))
                ? PlaybackAssetLoadMode.Preload
                : PlaybackAssetLoadMode.Stream;

            result.Add(new PlaybackAssetPlan(
                entry.AudioId,
                entry.Path,
                entry.Codec,
                durationSeconds,
                mode));
        }

        return result
            .OrderBy(item => item.Mode)
            .ThenBy(item => item.AudioId, StringComparer.Ordinal)
            .ToList();
    }

    private static bool ShouldPreload(string codec, double durationSeconds, bool isPlayableAudio)
    {
        if (durationSeconds > 0 && durationSeconds <= 8.0)
        {
            return true;
        }

        if (!isPlayableAudio)
        {
            return false;
        }

        return IsOggVorbis(codec) && durationSeconds <= 0;
    }

    private static bool IsOggVorbis(string codec)
    {
        return codec.Equals("ogg", StringComparison.OrdinalIgnoreCase) ||
               codec.Equals("vorbis", StringComparison.OrdinalIgnoreCase) ||
               codec.Equals("ogg-vorbis", StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolveDurationSeconds(
        AudioEntry entry,
        IReadOnlyDictionary<string, double>? durationOverrides)
    {
        if (entry.DurationMs > 0)
        {
            return entry.DurationMs / 1000.0;
        }

        if (durationOverrides is not null &&
            durationOverrides.TryGetValue(entry.AudioId, out var durationSeconds) &&
            durationSeconds > 0)
        {
            return durationSeconds;
        }

        return 0;
    }

    private static double ResolvePlaybackTailSeconds(AudioScheduleEvent scheduleEvent)
    {
        return scheduleEvent.DurationSeconds > 0
            ? Math.Max(2.0, scheduleEvent.DurationSeconds)
            : UnknownAudioTailSeconds;
    }

    private static double ResolveInitialTicksPerSecond(NbmsChart chart)
    {
        var bpm = chart.Timing
            .Where(timing => timing.Type == "bpm" && timing.Value is not null)
            .OrderBy(timing => timing.Tick)
            .Select(timing => timing.Value!.Value)
            .FirstOrDefault();

        if (bpm <= 0)
        {
            bpm = 120;
        }

        return chart.Resolution * bpm / 60.0;
    }

    private static string ResolveAudioId(TimelineItem item)
    {
        if (item.Kind == "BGM")
        {
            return item.Detail.Trim();
        }

        var parts = item.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^1] : "";
    }
}

public sealed record PlaybackAssetPlan(
    string AudioId,
    string Path,
    string Codec,
    double DurationSeconds,
    PlaybackAssetLoadMode Mode);

public enum PlaybackAssetLoadMode
{
    Preload,
    Stream
}
