using NBMS.Core.Models;

namespace NBMS.Core.Services;

public sealed class MeasureMap
{
    private readonly IReadOnlyList<MeasureLengthChange> _lengthChanges;

    private MeasureMap(int resolution, IReadOnlyList<MeasureLengthChange> lengthChanges)
    {
        Resolution = resolution > 0 ? resolution : 960;
        DefaultMeasureTicks = Resolution * 4;
        _lengthChanges = lengthChanges;
    }

    public int Resolution { get; }

    public int DefaultMeasureTicks { get; }

    public static MeasureMap FromChart(NbmsChart chart)
    {
        var resolution = chart.Resolution > 0 ? chart.Resolution : 960;
        var changes = chart.Timing
            .Where(timing => timing.Type.Equals("measureLength", StringComparison.OrdinalIgnoreCase) &&
                             timing.Value is > 0)
            .Select(timing => new MeasureLengthChange(
                Math.Max(0, timing.Tick),
                Math.Max(0.0001, timing.Value!.Value)))
            .OrderBy(change => change.Tick)
            .ToList();

        return new MeasureMap(resolution, changes);
    }

    public IReadOnlyList<MeasureGridLine> BuildGridLines(int startTick, int endTick, int gridDivision)
    {
        var result = new List<MeasureGridLine>();
        var normalizedStart = Math.Min(startTick, endTick);
        var normalizedEnd = Math.Max(startTick, endTick);
        var division = Math.Max(1, gridDivision);
        var measureNumber = 0;
        var measureStart = 0;
        var lengthChangeIndex = 0;
        var currentLengthRatio = 1.0;

        while (lengthChangeIndex < _lengthChanges.Count && _lengthChanges[lengthChangeIndex].Tick <= 0)
        {
            currentLengthRatio = _lengthChanges[lengthChangeIndex].Ratio;
            lengthChangeIndex++;
        }

        while (measureStart <= normalizedEnd + DefaultMeasureTicks)
        {
            while (lengthChangeIndex < _lengthChanges.Count && _lengthChanges[lengthChangeIndex].Tick <= measureStart)
            {
                currentLengthRatio = _lengthChanges[lengthChangeIndex].Ratio;
                lengthChangeIndex++;
            }

            var measureTicks = ResolveMeasureTicks(currentLengthRatio);
            if (measureStart + measureTicks >= normalizedStart && measureStart <= normalizedEnd)
            {
                result.Add(new MeasureGridLine(measureStart, measureNumber, 0, true));
                var gridTicks = Math.Max(1, measureTicks / division);
                for (var index = 1; index < division; index++)
                {
                    var tick = measureStart + gridTicks * index;
                    if (tick >= normalizedStart && tick <= normalizedEnd)
                    {
                        result.Add(new MeasureGridLine(tick, measureNumber, index, false));
                    }
                }
            }

            measureStart += measureTicks;
            measureNumber++;
        }

        return result
            .Where(line => line.Tick >= normalizedStart && line.Tick <= normalizedEnd)
            .OrderBy(line => line.Tick)
            .ThenBy(line => line.IsMeasureStart ? 0 : 1)
            .ToList();
    }

    public int ResolveStartTickWithMeasurePadding(int contentTick, int beforeMeasures)
    {
        var paddingTicks = DefaultMeasureTicks * Math.Max(0, beforeMeasures);
        return contentTick - paddingTicks;
    }

    private int ResolveMeasureTicks(double ratio)
    {
        return Math.Max(1, (int)Math.Round(DefaultMeasureTicks * ratio));
    }

    private readonly record struct MeasureLengthChange(int Tick, double Ratio);
}

public sealed record MeasureGridLine(int Tick, int MeasureNumber, int DivisionIndex, bool IsMeasureStart);
