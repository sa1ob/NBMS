using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NBMS.Studio.App.ViewModels;

namespace NBMS.Studio.App.Controls;

public sealed class PlayfieldCanvas : Control
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<PlayfieldCanvas, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<double> PlayheadTickProperty =
        AvaloniaProperty.Register<PlayfieldCanvas, double>(nameof(PlayheadTick));

    public static readonly StyledProperty<double> HiSpeedProperty =
        AvaloniaProperty.Register<PlayfieldCanvas, double>(nameof(HiSpeed), 1.0);

    private static readonly string[] PlayLanes =
    [
        "scratch",
        "key1",
        "key2",
        "key3",
        "key4",
        "key5",
        "key6",
        "key7"
    ];

    private static readonly SolidColorBrush BlackBrush = new(Color.Parse("#050505"));
    private static readonly SolidColorBrush PlayfieldBrush = new(Color.Parse("#101010"));
    private static readonly SolidColorBrush BgmStripBrush = new(Color.Parse("#111827"));
    private static readonly SolidColorBrush ScratchLaneBrush = new(Color.Parse("#1f0f12"));
    private static readonly SolidColorBrush WhiteLaneBrush = new(Color.Parse("#070707"));
    private static readonly SolidColorBrush BlueLaneBrush = new(Color.Parse("#08111e"));
    private static readonly SolidColorBrush PanelBrush = new(Color.Parse("#1f2937"));
    private static readonly SolidColorBrush JudgeBrush = new(Color.Parse("#2dd4bf"));
    private static readonly SolidColorBrush StartLineBrush = new(Color.Parse("#ef4444"));
    private static readonly SolidColorBrush WhiteObjectBrush = new(Color.Parse("#f3f4f6"));
    private static readonly SolidColorBrush BlueObjectBrush = new(Color.Parse("#1d4ed8"));
    private static readonly SolidColorBrush BgmObjectBrush = new(Color.Parse("#f59e0b"));
    private static readonly Pen PlayfieldPen = new(new SolidColorBrush(Color.Parse("#d1d5db")), 2);
    private static readonly Pen ThinLanePen = new(new SolidColorBrush(Color.Parse("#9ca3af")), 1);
    private static readonly Pen MeasurePen = new(new SolidColorBrush(Color.Parse("#6b7280")), 1);
    private static readonly Pen TimingPen = new(new SolidColorBrush(Color.Parse("#374151")), 1);
    private static readonly Pen ObjectPen = new(new SolidColorBrush(Color.Parse("#d1d5db")), 1);
    private static readonly Pen ReceptorPen = new(new SolidColorBrush(Color.Parse("#e5e7eb")), 2);
    private static readonly Pen JudgePen = new(JudgeBrush, 4);
    private static readonly Pen StartLinePen = new(StartLineBrush, 2);

    private IEnumerable? _cachedItemsSource;
    private INotifyCollectionChanged? _cachedCollectionNotifier;
    private bool _cacheDirty = true;
    private List<TimelineRow> _cachedRows = [];

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

    public double HiSpeed
    {
        get => GetValue(HiSpeedProperty);
        set => SetValue(HiSpeedProperty, value);
    }

    static PlayfieldCanvas()
    {
        AffectsRender<PlayfieldCanvas>(ItemsProperty);
        AffectsRender<PlayfieldCanvas>(PlayheadTickProperty);
        AffectsRender<PlayfieldCanvas>(HiSpeedProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var playfieldWidth = Math.Min(280, Math.Max(220, bounds.Width - 160));
        var playfieldLeft = bounds.Left + (bounds.Width - playfieldWidth) / 2 - 18;
        var playfield = new Rect(playfieldLeft, bounds.Top + 8, playfieldWidth, bounds.Height - 58);
        var bgmStrip = new Rect(playfield.Right + 6, playfield.Top, 14, playfield.Height);
        var pixelsPerTick = ResolvePixelsPerTick();

        DrawCabinet(context, bounds, playfield, bgmStrip);
        DrawMeasureLines(context, playfield, pixelsPerTick);
        DrawEvents(context, playfield, bgmStrip, pixelsPerTick);
        DrawReceptors(context, bounds, playfield);
    }

    private static void DrawCabinet(DrawingContext context, Rect bounds, Rect playfield, Rect bgmStrip)
    {
        context.DrawRectangle(BlackBrush, null, bounds);
        context.DrawRectangle(PlayfieldBrush, PlayfieldPen, playfield);
        context.DrawRectangle(BgmStripBrush, ThinLanePen, bgmStrip);

        var laneWidth = playfield.Width / PlayLanes.Length;
        for (var i = 0; i < PlayLanes.Length; i++)
        {
            var lane = PlayLanes[i];
            var x = playfield.Left + laneWidth * i;
            var rect = new Rect(x, playfield.Top, laneWidth, playfield.Height);
            context.DrawRectangle(ResolveLaneBrush(lane), null, rect);
            context.DrawLine(ThinLanePen, new Point(SnapLine(x), playfield.Top), new Point(SnapLine(x), playfield.Bottom));
        }

        context.DrawLine(ThinLanePen, new Point(SnapLine(playfield.Right), playfield.Top), new Point(SnapLine(playfield.Right), playfield.Bottom));
        var judgeY = ResolveJudgeY(playfield);
        context.DrawLine(JudgePen, new Point(playfield.Left, judgeY), new Point(playfield.Right, judgeY));
        context.DrawLine(StartLinePen, new Point(playfield.Left, judgeY - 5), new Point(playfield.Right, judgeY - 5));
    }

    private static IBrush ResolveLaneBrush(string lane)
    {
        return lane switch
        {
            "scratch" => ScratchLaneBrush,
            "key1" or "key3" or "key5" or "key7" => WhiteLaneBrush,
            "key2" or "key4" or "key6" => BlueLaneBrush,
            _ => Brushes.Black
        };
    }

    private void DrawEvents(DrawingContext context, Rect playfield, Rect bgmStrip, double pixelsPerTick)
    {
        var rows = GetCachedRows();
        if (rows.Count == 0)
        {
            return;
        }

        var laneWidth = playfield.Width / PlayLanes.Length;
        var judgeY = ResolveJudgeY(playfield);
        var visibleMinTick = PlayheadTick + (judgeY - playfield.Bottom - 32) / pixelsPerTick;
        var visibleMaxTick = PlayheadTick + (judgeY - playfield.Top + 32) / pixelsPerTick;

        foreach (var row in rows)
        {
            if (row.Tick < visibleMinTick || row.Tick > visibleMaxTick)
            {
                continue;
            }

            if (row.Kind.Equals("Timing", StringComparison.OrdinalIgnoreCase))
            {
                if (!row.Detail.Contains("Bar", StringComparison.OrdinalIgnoreCase))
                {
                    DrawTimingLine(context, row.Tick, playfield, pixelsPerTick);
                }

                continue;
            }

            if (row.Kind.Equals("BGM", StringComparison.OrdinalIgnoreCase))
            {
                DrawBgmEvent(context, row.Tick, bgmStrip, pixelsPerTick);
                continue;
            }

            var laneIndex = Array.FindIndex(PlayLanes, lane => lane.Equals(row.Lane, StringComparison.OrdinalIgnoreCase));
            if (laneIndex < 0)
            {
                continue;
            }

            var y = EventToY(row.Tick, playfield, pixelsPerTick);
            var x = playfield.Left + laneWidth * laneIndex + 4;
            var rect = new Rect(x, Math.Round(y - 8), Math.Max(8, laneWidth - 8), 14);
            if (rect.Bottom < playfield.Top || rect.Top > playfield.Bottom)
            {
                continue;
            }

            using (context.PushClip(playfield))
            {
                context.DrawRectangle(ResolveObjectBrush(row.Lane), ObjectPen, rect);
            }
        }
    }

    private void DrawMeasureLines(DrawingContext context, Rect playfield, double pixelsPerTick)
    {
        const int measureTicks = 3840;
        var judgeY = ResolveJudgeY(playfield);
        var visibleMinTick = PlayheadTick + (judgeY - playfield.Bottom) / pixelsPerTick;
        var visibleMaxTick = PlayheadTick + (judgeY - playfield.Top) / pixelsPerTick;
        var firstMeasure = Math.Max(0, (int)Math.Floor(visibleMinTick / measureTicks));
        var lastMeasure = (int)Math.Ceiling(visibleMaxTick / measureTicks);

        for (var measure = firstMeasure; measure <= lastMeasure; measure++)
        {
            var y = SnapLine(EventToY(measure * measureTicks, playfield, pixelsPerTick));
            if (y < playfield.Top || y > playfield.Bottom)
            {
                continue;
            }

            using (context.PushClip(playfield))
            {
                context.DrawLine(MeasurePen, new Point(playfield.Left, y), new Point(playfield.Right, y));
            }
        }
    }

    private void DrawTimingLine(DrawingContext context, int tick, Rect playfield, double pixelsPerTick)
    {
        var y = SnapLine(EventToY(tick, playfield, pixelsPerTick));
        if (y < playfield.Top || y > playfield.Bottom)
        {
            return;
        }

        using (context.PushClip(playfield))
        {
            context.DrawLine(TimingPen, new Point(playfield.Left, y), new Point(playfield.Right, y));
        }
    }

    private void DrawBgmEvent(DrawingContext context, int tick, Rect bgmStrip, double pixelsPerTick)
    {
        var y = EventToY(tick, bgmStrip, pixelsPerTick);
        var rect = new Rect(bgmStrip.Left + 2, Math.Round(y - 5), bgmStrip.Width - 4, 10);
        if (rect.Bottom < bgmStrip.Top || rect.Top > bgmStrip.Bottom)
        {
            return;
        }

        using (context.PushClip(bgmStrip))
        {
            context.DrawRectangle(BgmObjectBrush, null, rect);
        }
    }

    private static IBrush ResolveObjectBrush(string lane)
    {
        return lane switch
        {
            "scratch" => StartLineBrush,
            "key1" or "key3" or "key5" or "key7" => WhiteObjectBrush,
            "key2" or "key4" or "key6" => BlueObjectBrush,
            _ => BgmObjectBrush
        };
    }

    private static void DrawReceptors(DrawingContext context, Rect bounds, Rect playfield)
    {
        var receptorTop = playfield.Bottom + 8;
        var laneWidth = playfield.Width / PlayLanes.Length;

        context.DrawRectangle(PanelBrush, ReceptorPen, new Rect(playfield.Left - 8, receptorTop - 4, playfield.Width + 16, 44));

        for (var i = 0; i < PlayLanes.Length; i++)
        {
            var x = playfield.Left + laneWidth * i + 3;
            var rect = new Rect(x, receptorTop, laneWidth - 6, 34);
            var brush = PlayLanes[i] == "scratch" ? BlueObjectBrush : ResolveLaneBrush(PlayLanes[i]);
            context.DrawRectangle(brush, ReceptorPen, rect);
        }
    }

    private List<TimelineRow> GetCachedRows()
    {
        if (Items is null)
        {
            DetachCollectionNotifier();
            _cachedItemsSource = null;
            _cacheDirty = false;
            _cachedRows = [];
            return _cachedRows;
        }

        if (!ReferenceEquals(Items, _cachedItemsSource))
        {
            DetachCollectionNotifier();
            _cachedItemsSource = Items;
            _cachedCollectionNotifier = Items as INotifyCollectionChanged;
            if (_cachedCollectionNotifier is not null)
            {
                _cachedCollectionNotifier.CollectionChanged += OnItemsCollectionChanged;
            }

            _cacheDirty = true;
        }

        if (!_cacheDirty)
        {
            return _cachedRows;
        }

        _cachedRows = Items
            .OfType<TimelineRow>()
            .OrderBy(row => row.Tick)
            .ToList();
        _cacheDirty = false;
        return _cachedRows;
    }

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _cacheDirty = true;
        InvalidateVisual();
    }

    private void DetachCollectionNotifier()
    {
        if (_cachedCollectionNotifier is not null)
        {
            _cachedCollectionNotifier.CollectionChanged -= OnItemsCollectionChanged;
            _cachedCollectionNotifier = null;
        }
    }

    private double EventToY(double tick, Rect playfield, double pixelsPerTick)
    {
        return ResolveJudgeY(playfield) - (tick - PlayheadTick) * pixelsPerTick;
    }

    private double ResolvePixelsPerTick()
    {
        return 0.09 * Math.Clamp(HiSpeed, 1.0, 4.0);
    }

    private static double ResolveJudgeY(Rect playfield)
    {
        return playfield.Bottom - 18;
    }

    private static double SnapLine(double value)
    {
        return Math.Round(value) + 0.5;
    }
}
