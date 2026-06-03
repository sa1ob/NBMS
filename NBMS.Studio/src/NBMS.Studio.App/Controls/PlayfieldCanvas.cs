using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NBMS.Studio.App.ViewModels;

namespace NBMS.Studio.App.Controls;

public sealed class PlayfieldCanvas : Control
{
    private const double SevenKeyPlayfieldWidth = 280.0;
    private const double MinimumPlayfieldWidth = 220.0;
    private const double TwoPlayerGapWidth = 20.0;
    private const int TwoPlayerGapBeforeLaneIndex = 8;

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<PlayfieldCanvas, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<double> PlayheadTickProperty =
        AvaloniaProperty.Register<PlayfieldCanvas, double>(nameof(PlayheadTick));

    public static readonly StyledProperty<double> HiSpeedProperty =
        AvaloniaProperty.Register<PlayfieldCanvas, double>(nameof(HiSpeed), 1.0);

    private static readonly string[] SevenKeyLanes =
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
        "scratch2"
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

        var rows = GetCachedRows();
        var lanes = ResolvePlayLanes(rows);
        var playfieldWidth = ResolvePlayfieldWidth(bounds, lanes);
        var playfieldLeft = bounds.Left + (bounds.Width - playfieldWidth) / 2 - 18;
        var playfield = new Rect(playfieldLeft, bounds.Top + 8, playfieldWidth, bounds.Height - 58);
        var bgmStrip = new Rect(playfield.Right + 6, playfield.Top, 14, playfield.Height);
        var pixelsPerTick = ResolvePixelsPerTick();

        DrawCabinet(context, bounds, playfield, bgmStrip, lanes);
        DrawMeasureLines(context, playfield, pixelsPerTick);
        DrawEvents(context, playfield, bgmStrip, pixelsPerTick, rows, lanes);
        DrawReceptors(context, bounds, playfield, lanes);
    }

    private static void DrawCabinet(DrawingContext context, Rect bounds, Rect playfield, Rect bgmStrip, IReadOnlyList<string> lanes)
    {
        context.DrawRectangle(BlackBrush, null, bounds);
        context.DrawRectangle(PlayfieldBrush, PlayfieldPen, playfield);
        context.DrawRectangle(BgmStripBrush, ThinLanePen, bgmStrip);

        var laneWidth = ResolveLaneWidth(playfield, lanes);
        DrawLaneGap(context, playfield, lanes, laneWidth);
        for (var i = 0; i < lanes.Count; i++)
        {
            var lane = lanes[i];
            var x = ResolveLaneX(playfield, lanes, laneWidth, i);
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
            "scratch" or "scratch2" => ScratchLaneBrush,
            var key when IsWhiteKey(key) => WhiteLaneBrush,
            var key when IsBlueKey(key) => BlueLaneBrush,
            _ => Brushes.Black
        };
    }

    private void DrawEvents(
        DrawingContext context,
        Rect playfield,
        Rect bgmStrip,
        double pixelsPerTick,
        IReadOnlyList<TimelineRow> rows,
        IReadOnlyList<string> lanes)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var laneWidth = ResolveLaneWidth(playfield, lanes);
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

            var laneIndex = FindLaneIndex(lanes, row.Lane);
            if (laneIndex < 0)
            {
                continue;
            }

            var y = EventToY(row.Tick, playfield, pixelsPerTick);
            var x = ResolveLaneX(playfield, lanes, laneWidth, laneIndex) + 4;
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
            "scratch" or "scratch2" => StartLineBrush,
            var key when IsWhiteKey(key) => WhiteObjectBrush,
            var key when IsBlueKey(key) => BlueObjectBrush,
            _ => BgmObjectBrush
        };
    }

    private static void DrawReceptors(DrawingContext context, Rect bounds, Rect playfield, IReadOnlyList<string> lanes)
    {
        var receptorTop = playfield.Bottom + 8;
        var laneWidth = ResolveLaneWidth(playfield, lanes);

        context.DrawRectangle(PanelBrush, ReceptorPen, new Rect(playfield.Left - 8, receptorTop - 4, playfield.Width + 16, 44));

        for (var i = 0; i < lanes.Count; i++)
        {
            var x = ResolveLaneX(playfield, lanes, laneWidth, i) + 3;
            var rect = new Rect(x, receptorTop, laneWidth - 6, 34);
            var brush = lanes[i].StartsWith("scratch", StringComparison.OrdinalIgnoreCase) ? BlueObjectBrush : ResolveLaneBrush(lanes[i]);
            context.DrawRectangle(brush, ReceptorPen, rect);
        }
    }

    private static IReadOnlyList<string> ResolvePlayLanes(IEnumerable<TimelineRow> rows)
    {
        return rows.Any(row => row.Lane is "scratch2" or "key8" or "key9" or "key10" or "key11" or "key12" or "key13" or "key14")
            ? FourteenKeyLanes
            : SevenKeyLanes;
    }

    private static double ResolvePlayfieldWidth(Rect bounds, IReadOnlyList<string> lanes)
    {
        var sevenLaneWidth = SevenKeyPlayfieldWidth / SevenKeyLanes.Length;
        var desiredWidth = lanes.Count > SevenKeyLanes.Length
            ? sevenLaneWidth * lanes.Count + TwoPlayerGapWidth
            : SevenKeyPlayfieldWidth;
        return Math.Min(Math.Max(MinimumPlayfieldWidth, desiredWidth), Math.Max(MinimumPlayfieldWidth, bounds.Width - 120));
    }

    private static double ResolveLaneWidth(Rect playfield, IReadOnlyList<string> lanes)
    {
        var gap = ResolveLaneGap(lanes);
        return Math.Max(8, (playfield.Width - gap) / lanes.Count);
    }

    private static double ResolveLaneX(Rect playfield, IReadOnlyList<string> lanes, double laneWidth, int index)
    {
        var gapOffset = lanes.Count > SevenKeyLanes.Length && index >= TwoPlayerGapBeforeLaneIndex
            ? TwoPlayerGapWidth
            : 0;
        return playfield.Left + laneWidth * index + gapOffset;
    }

    private static double ResolveLaneGap(IReadOnlyList<string> lanes)
    {
        return lanes.Count > SevenKeyLanes.Length ? TwoPlayerGapWidth : 0;
    }

    private static void DrawLaneGap(DrawingContext context, Rect playfield, IReadOnlyList<string> lanes, double laneWidth)
    {
        if (lanes.Count <= SevenKeyLanes.Length)
        {
            return;
        }

        var x = playfield.Left + laneWidth * TwoPlayerGapBeforeLaneIndex;
        context.DrawRectangle(BlackBrush, null, new Rect(x, playfield.Top, TwoPlayerGapWidth, playfield.Height));
        context.DrawLine(PlayfieldPen, new Point(SnapLine(x), playfield.Top), new Point(SnapLine(x), playfield.Bottom));
        context.DrawLine(PlayfieldPen, new Point(SnapLine(x + TwoPlayerGapWidth), playfield.Top), new Point(SnapLine(x + TwoPlayerGapWidth), playfield.Bottom));
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
