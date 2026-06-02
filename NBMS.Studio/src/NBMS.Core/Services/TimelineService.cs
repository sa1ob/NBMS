using NBMS.Core.Extensions;
using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class TimelineService
{
    private readonly ExtensionRegistry _extensions;

    public TimelineService(ExtensionRegistry extensions)
    {
        _extensions = extensions;
    }

    public List<TimelineItem> BuildTimeline(NbmsChart chart)
    {
        var segments = BuildBpmSegments(chart);
        var stopDurations = BuildStopDurations(chart, segments);
        var items = new List<TimelineItem>();

        foreach (var timing in chart.Timing)
        {
            var handled = _extensions.TryDescribeTimingEvent(timing, out var extensionDetail);
            items.Add(new TimelineItem
            {
                Tick = timing.Tick,
                TimeSeconds = TickToSeconds(timing.Tick, segments, stopDurations),
                Kind = "Timing",
                Detail = handled ? extensionDetail : DescribeCoreTiming(timing)
            });
        }

        foreach (var note in chart.Notes)
        {
            items.Add(new TimelineItem
            {
                Tick = note.Tick,
                TimeSeconds = TickToSeconds(note.Tick, segments, stopDurations),
                Kind = "Note",
                Lane = note.Lane,
                Detail = $"{note.Type} {note.AudioId}"
            });
        }

        foreach (var backgroundAudio in chart.BackgroundAudio)
        {
            items.Add(new TimelineItem
            {
                Tick = backgroundAudio.Tick,
                TimeSeconds = TickToSeconds(backgroundAudio.Tick, segments, stopDurations),
                Kind = "BGM",
                Lane = string.IsNullOrWhiteSpace(backgroundAudio.Lane) ? "background1" : backgroundAudio.Lane,
                Detail = backgroundAudio.AudioId
            });
        }

        return items.OrderBy(item => item.TimeSeconds).ThenBy(item => item.Tick).ToList();
    }

    private static List<BpmSegment> BuildBpmSegments(NbmsChart chart)
    {
        var bpmEvents = chart.Timing
            .Where(timing => timing.Type == "bpm" && timing.Value is not null)
            .OrderBy(timing => timing.Tick)
            .ToList();

        if (bpmEvents.Count == 0)
        {
            bpmEvents.Add(new TimingEvent { Tick = 0, Type = "bpm", Value = 120 });
        }

        var segments = new List<BpmSegment>();
        var currentTick = 0;
        var currentSeconds = 0.0;
        var currentBpm = bpmEvents[0].Value!.Value;
        var currentSecondsPerTick = 60.0 / (currentBpm * chart.Resolution);
        segments.Add(new BpmSegment(0, 0.0, currentSecondsPerTick));

        foreach (var bpmEvent in bpmEvents)
        {
            if (bpmEvent.Tick < currentTick)
            {
                continue;
            }

            currentSeconds += (bpmEvent.Tick - currentTick) * currentSecondsPerTick;
            currentTick = bpmEvent.Tick;
            currentBpm = bpmEvent.Value!.Value;
            currentSecondsPerTick = 60.0 / (currentBpm * chart.Resolution);

            if (segments.Last().Tick == currentTick)
            {
                segments[^1] = new BpmSegment(currentTick, currentSeconds, currentSecondsPerTick);
            }
            else
            {
                segments.Add(new BpmSegment(currentTick, currentSeconds, currentSecondsPerTick));
            }
        }

        return segments;
    }

    private static Dictionary<int, double> BuildStopDurations(NbmsChart chart, List<BpmSegment> segments)
    {
        var result = new Dictionary<int, double>();

        foreach (var timing in chart.Timing.Where(timing => timing.Type == "stop" && timing.DurationTicks is not null))
        {
            var segment = FindSegment(timing.Tick, segments);
            result[timing.Tick] = result.GetValueOrDefault(timing.Tick) + timing.DurationTicks!.Value * segment.SecondsPerTick;
        }

        return result;
    }

    private static double TickToSeconds(int tick, List<BpmSegment> segments, Dictionary<int, double> stopDurations)
    {
        var segment = FindSegment(tick, segments);
        var baseSeconds = segment.StartSeconds + (tick - segment.Tick) * segment.SecondsPerTick;
        var stopSeconds = stopDurations.Where(pair => pair.Key < tick).Sum(pair => pair.Value);
        return baseSeconds + stopSeconds;
    }

    private static BpmSegment FindSegment(int tick, List<BpmSegment> segments)
    {
        return segments.Last(segment => segment.Tick <= tick);
    }

    private static string DescribeCoreTiming(TimingEvent timing)
    {
        return timing.Type switch
        {
            "bpm" => $"BPM {timing.Value}",
            "bar" => "Bar",
            "stop" => $"STOP {timing.DurationTicks} ticks",
            _ => timing.Type
        };
    }

    private readonly record struct BpmSegment(int Tick, double StartSeconds, double SecondsPerTick);
}
