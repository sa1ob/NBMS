using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NBMS.Studio.App.ViewModels;

namespace NBMS.Studio.App.Controls;

public sealed class TimelineCanvas : Control
{
    private const double TimelineTopPadding = 36.0;
    private const double PixelsPerTick = 0.125;
    private const double TwoPlayerGapWidth = 18.0;
    private const int TwoPlayerGapBeforeLaneIndex = 8;

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TimelineCanvas, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<double> PlayheadTickProperty =
        AvaloniaProperty.Register<TimelineCanvas, double>(nameof(PlayheadTick));

    private static readonly string[] SevenKeyLanes =
    [
        "scratch",
        "key1",
        "key2",
        "key3",
        "key4",
        "key5",
        "key6",
        "key7",
        "background1",
        "background2",
        "background3",
        "background4",
        "background5",
        "background6",
        "background7",
        "background8"
    ];

    private static readonly string[] FourteenKeyLanes =
    [
        "scratch",
        "key1",
        "key2",
        "key3",
        "key4",
        "key5",
        "key6",
        "key7",
        "key8",
        "key9",
        "key10",
        "key11",
        "key12",
        "key13",
        "key14",
        "scratch2",
        "background1",
        "background2",
        "background3",
        "background4",
        "background5",
        "background6",
        "background7",
        "background8"
    ];

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double PlayheadTick
    {
        get => GetValue(PlayheadTickProperty);
        set => SetValue(PlayheadTickProperty, value);
    }

    static TimelineCanvas()
    {
        AffectsRender<TimelineCanvas>(ItemsProperty);
        AffectsRender<TimelineCanvas>(PlayheadTickProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var rows = (Items ?? Array.Empty<object>()).OfType<TimelineRow>().ToList();
        var lanes = ResolveLanes(rows);

        DrawBackground(context, bounds);
        DrawLaneBackgrounds(context, bounds, lanes);
        DrawMeasureGrid(context, bounds);
        DrawLaneSeparators(context, bounds, lanes);
        DrawEvents(context, bounds, rows, lanes);
        DrawStartLine(context, bounds);
    }

    private static void DrawLaneBackgrounds(DrawingContext context, Rect bounds, IReadOnlyList<string> lanes)
    {
        var laneWidth = ResolveLaneWidth(bounds, lanes);

        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            var rect = new Rect(ResolveLaneX(bounds, lanes, laneWidth, i), bounds.Top, laneWidth, bounds.Height);
            context.DrawRectangle(ResolveLaneBackground(lane), null, rect);
        }

        DrawLaneGap(context, bounds, lanes, laneWidth);
    }

    private static IBrush ResolveLaneBackground(string lane)
    {
        return lane switch
        {
            "scratch" or "scratch2" => new SolidColorBrush(Color.Parse("#fee2e2")),
            var key when IsWhiteKey(key) => Brushes.White,
            var key when IsBlueKey(key) => new SolidColorBrush(Color.Parse("#dbeafe")),
            var value when value.StartsWith("background", StringComparison.OrdinalIgnoreCase) => new SolidColorBrush(Color.Parse("#fff7ed")),
            _ => Brushes.White
        };
    }

    private static void DrawBackground(DrawingContext context, Rect bounds)
    {
        context.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.Parse("#9aa4b2")), 1), bounds);
    }

    private static void DrawMeasureGrid(DrawingContext context, Rect bounds)
    {
        var measurePen = new Pen(new SolidColorBrush(Color.Parse("#c7cdd6")), 1);
        var beatPen = new Pen(new SolidColorBrush(Color.Parse("#edf0f4")), 1);
        const double tickStep = 120.0;

        for (var tick = 0.0; TimelineTopPadding + tick * PixelsPerTick < bounds.Height; tick += tickStep)
        {
            var y = SnapLineY(TimelineTopPadding + tick * PixelsPerTick);
            var isMeasure = Math.Abs(tick % 3840.0) < 0.01;
            context.DrawLine(isMeasure ? measurePen : beatPen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
    }

    private static void DrawLaneSeparators(DrawingContext context, Rect bounds, IReadOnlyList<string> lanes)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#d7dce4")), 1);
        var laneWidth = ResolveLaneWidth(bounds, lanes);

        for (var i = 1; i < lanes.Count; i++)
        {
            var x = ResolveLaneX(bounds, lanes, laneWidth, i);
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
    }

    private static void DrawEvents(
        DrawingContext context,
        Rect bounds,
        IReadOnlyList<TimelineRow> rows,
        IReadOnlyList<string> lanes)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var laneWidth = ResolveLaneWidth(bounds, lanes);
        var timingBrush = new SolidColorBrush(Color.Parse("#6b7280"));

        foreach (var row in rows)
        {
            var y = EventToY(row.Tick);
            if (y < bounds.Top - 40 || y > bounds.Bottom + 40)
            {
                continue;
            }

            if (row.Kind.Equals("Timing", StringComparison.OrdinalIgnoreCase))
            {
                var timingY = SnapLineY(y);
                context.DrawLine(new Pen(timingBrush, 2), new Point(bounds.Left, timingY), new Point(bounds.Right, timingY));
                continue;
            }

            var laneIndex = ResolveLaneIndex(row.Lane, row.Kind, lanes);
            var x = ResolveLaneX(bounds, lanes, laneWidth, laneIndex) + 4;
            var height = row.Detail.Contains("hold", StringComparison.OrdinalIgnoreCase) ? 24 : 12;
            var brush = ResolveObjectBrush(row.Lane, row.Kind, height > 12);
            var pen = ResolveObjectPen(row.Lane, row.Kind);

            // オブジェクト中央をtick座標に合わせ、矩形位置は整数pxへ丸める。
            var rect = new Rect(x, Math.Round(y - height / 2), Math.Max(8, laneWidth - 8), height);
            context.DrawRectangle(brush, pen, rect);
        }
    }

    private static Pen ResolveObjectPen(string lane, string kind)
    {
        if (kind.Equals("BGM", StringComparison.OrdinalIgnoreCase))
        {
            return new Pen(new SolidColorBrush(Color.Parse("#9a3412")), 1.5);
        }

        return lane switch
        {
            var key when IsWhiteKey(key) => new Pen(new SolidColorBrush(Color.Parse("#334155")), 1.5),
            var key when IsBlueKey(key) => new Pen(new SolidColorBrush(Color.Parse("#1e3a8a")), 1.3),
            "scratch" or "scratch2" => new Pen(new SolidColorBrush(Color.Parse("#7f1d1d")), 1.5),
            _ => new Pen(new SolidColorBrush(Color.Parse("#334155")), 1)
        };
    }

    private static IBrush ResolveObjectBrush(string lane, string kind, bool isLongNote)
    {
        if (kind.Equals("BGM", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.Parse("#f97316"));
        }

        if (isLongNote)
        {
            return new SolidColorBrush(Color.Parse("#22c55e"));
        }

        return lane switch
        {
            "scratch" or "scratch2" => new SolidColorBrush(Color.Parse("#ef4444")),
            var key when IsWhiteKey(key) => new SolidColorBrush(Color.Parse("#f8fafc")),
            var key when IsBlueKey(key) => new SolidColorBrush(Color.Parse("#2563eb")),
            _ => new SolidColorBrush(Color.Parse("#64748b"))
        };
    }

    private static void DrawStartLine(DrawingContext context, Rect bounds)
    {
        var y = SnapLineY(EventToY(0));
        var pen = new Pen(new SolidColorBrush(Color.Parse("#ef4444")), 2);
        context.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
    }

    private static double EventToY(double tick)
    {
        return TimelineTopPadding + tick * PixelsPerTick;
    }

    private static double SnapLineY(double y)
    {
        return Math.Round(y) + 0.5;
    }

    private static int ResolveLaneIndex(string lane, string kind, IReadOnlyList<string> lanes)
    {
        if (kind.Equals("BGM", StringComparison.OrdinalIgnoreCase))
        {
            var bgmIndex = FindLaneIndex(lanes, lane);
            return bgmIndex >= 0 ? bgmIndex : FindLaneIndex(lanes, "background1");
        }

        var index = FindLaneIndex(lanes, lane);
        return index >= 0 ? index : Math.Min(lanes.Count - 1, Math.Max(1, lanes.Count / 2));
    }

    private static IReadOnlyList<string> ResolveLanes(IEnumerable<TimelineRow> rows)
    {
        return rows.Any(row => row.Lane is "scratch2" or "key8" or "key9" or "key10" or "key11" or "key12" or "key13" or "key14")
            ? FourteenKeyLanes
            : SevenKeyLanes;
    }

    private static double ResolveLaneWidth(Rect bounds, IReadOnlyList<string> lanes)
    {
        var gap = lanes.Count > SevenKeyLanes.Length ? TwoPlayerGapWidth : 0;
        return Math.Max(8, (bounds.Width - gap) / lanes.Count);
    }

    private static double ResolveLaneX(Rect bounds, IReadOnlyList<string> lanes, double laneWidth, int index)
    {
        var gapOffset = lanes.Count > SevenKeyLanes.Length && index >= TwoPlayerGapBeforeLaneIndex
            ? TwoPlayerGapWidth
            : 0;
        return bounds.Left + laneWidth * index + gapOffset;
    }

    private static void DrawLaneGap(DrawingContext context, Rect bounds, IReadOnlyList<string> lanes, double laneWidth)
    {
        if (lanes.Count <= SevenKeyLanes.Length)
        {
            return;
        }

        var x = bounds.Left + laneWidth * TwoPlayerGapBeforeLaneIndex;
        var brush = new SolidColorBrush(Color.Parse("#e5e7eb"));
        var pen = new Pen(new SolidColorBrush(Color.Parse("#94a3b8")), 1);
        context.DrawRectangle(brush, pen, new Rect(x, bounds.Top, TwoPlayerGapWidth, bounds.Height));
    }

    private static int FindLaneIndex(IReadOnlyList<string> lanes, string lane)
    {
        for (var index = 0; index < lanes.Count; index++)
        {
            if (lanes[index].Equals(lane, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsWhiteKey(string lane)
    {
        return TryGetKeyNumber(lane, out var key) && key % 2 == 1;
    }

    private static bool IsBlueKey(string lane)
    {
        return TryGetKeyNumber(lane, out var key) && key % 2 == 0;
    }

    private static bool TryGetKeyNumber(string lane, out int key)
    {
        key = 0;
        return lane.StartsWith("key", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(lane[3..], out key);
    }
}
