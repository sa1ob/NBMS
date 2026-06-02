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

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TimelineCanvas, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<double> PlayheadTickProperty =
        AvaloniaProperty.Register<TimelineCanvas, double>(nameof(PlayheadTick));

    private static readonly string[] DefaultLanes =
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

        DrawBackground(context, bounds);
        DrawLaneBackgrounds(context, bounds);
        DrawMeasureGrid(context, bounds);
        DrawLaneSeparators(context, bounds);
        DrawEvents(context, bounds);
        DrawStartLine(context, bounds);
    }

    private static void DrawLaneBackgrounds(DrawingContext context, Rect bounds)
    {
        var laneWidth = bounds.Width / DefaultLanes.Length;

        for (var i = 0; i < DefaultLanes.Length; i++)
        {
            var lane = DefaultLanes[i];
            var rect = new Rect(bounds.Left + laneWidth * i, bounds.Top, laneWidth, bounds.Height);
            context.DrawRectangle(ResolveLaneBackground(lane), null, rect);
        }
    }

    private static IBrush ResolveLaneBackground(string lane)
    {
        return lane switch
        {
            "scratch" => new SolidColorBrush(Color.Parse("#fee2e2")),
            "key1" or "key3" or "key5" or "key7" => Brushes.White,
            "key2" or "key4" or "key6" => new SolidColorBrush(Color.Parse("#dbeafe")),
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

    private static void DrawLaneSeparators(DrawingContext context, Rect bounds)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#d7dce4")), 1);
        var laneWidth = bounds.Width / DefaultLanes.Length;

        for (var i = 1; i < DefaultLanes.Length; i++)
        {
            var x = bounds.Left + laneWidth * i;
            context.DrawLine(pen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
        }
    }

    private void DrawEvents(DrawingContext context, Rect bounds)
    {
        if (Items is null)
        {
            return;
        }

        var laneWidth = bounds.Width / DefaultLanes.Length;
        var timingBrush = new SolidColorBrush(Color.Parse("#6b7280"));

        foreach (var row in Items.OfType<TimelineRow>())
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

            var laneIndex = ResolveLaneIndex(row.Lane, row.Kind);
            var x = bounds.Left + laneIndex * laneWidth + 4;
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
            "key1" or "key3" or "key5" or "key7" => new Pen(new SolidColorBrush(Color.Parse("#334155")), 1.5),
            "key2" or "key4" or "key6" => new Pen(new SolidColorBrush(Color.Parse("#1e3a8a")), 1.3),
            "scratch" => new Pen(new SolidColorBrush(Color.Parse("#7f1d1d")), 1.5),
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
            "scratch" => new SolidColorBrush(Color.Parse("#ef4444")),
            "key1" or "key3" or "key5" or "key7" => new SolidColorBrush(Color.Parse("#f8fafc")),
            "key2" or "key4" or "key6" => new SolidColorBrush(Color.Parse("#2563eb")),
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

    private static int ResolveLaneIndex(string lane, string kind)
    {
        if (kind.Equals("BGM", StringComparison.OrdinalIgnoreCase))
        {
            var bgmIndex = Array.FindIndex(DefaultLanes, item => item.Equals(lane, StringComparison.OrdinalIgnoreCase));
            return bgmIndex >= 0 ? bgmIndex : Array.IndexOf(DefaultLanes, "background1");
        }

        var index = Array.FindIndex(DefaultLanes, item => item.Equals(lane, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : Math.Min(DefaultLanes.Length - 1, Math.Max(1, DefaultLanes.Length / 2));
    }
}
