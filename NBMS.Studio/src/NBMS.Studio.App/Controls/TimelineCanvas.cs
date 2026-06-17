using System.Collections;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using NBMS.Studio.App.ViewModels;

namespace NBMS.Studio.App.Controls;

public sealed class TimelineCanvas : Control
{
    private const double TimelineTopPadding = 36.0;
    private const double TimelineBottomPadding = 12.0;
    private const double PixelsPerTick = 0.125;
    private const double TwoPlayerGapWidth = 18.0;
    private const int TwoPlayerGapBeforeLaneIndex = 16;

    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<TimelineCanvas, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<IEnumerable?> MeasureGridLinesProperty =
        AvaloniaProperty.Register<TimelineCanvas, IEnumerable?>(nameof(MeasureGridLines));

    public static readonly StyledProperty<double> PlayheadTickProperty =
        AvaloniaProperty.Register<TimelineCanvas, double>(nameof(PlayheadTick));

    public static readonly StyledProperty<int> StartTickProperty =
        AvaloniaProperty.Register<TimelineCanvas, int>(nameof(StartTick));

    public static readonly StyledProperty<int> GridTicksProperty =
        AvaloniaProperty.Register<TimelineCanvas, int>(nameof(GridTicks), 120);

    public static readonly StyledProperty<int> SelectedTickProperty =
        AvaloniaProperty.Register<TimelineCanvas, int>(nameof(SelectedTick), -1);

    public static readonly StyledProperty<string> SelectedLaneProperty =
        AvaloniaProperty.Register<TimelineCanvas, string>(nameof(SelectedLane), "");

    public static readonly StyledProperty<IEnumerable?> SelectedObjectKeysProperty =
        AvaloniaProperty.Register<TimelineCanvas, IEnumerable?>(nameof(SelectedObjectKeys));

    private Point? _dragStartPoint;
    private TimelineHitEventArgs? _dragStartHit;
    private TimelineHitEventArgs? _dragCurrentHit;
    private bool _isRangeSelecting;
    private Point? _rangeSelectionStartPoint;
    private Point? _rangeSelectionCurrentPoint;

    private static readonly string[] SevenKeyLanes =
    [
        "speed",
        "scroll",
        "bpm",
        "stop",
        "measure",
        "bga",
        "layer",
        "poor",
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
        "speed",
        "scroll",
        "bpm",
        "stop",
        "measure",
        "bga",
        "layer",
        "poor",
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

    public IEnumerable? MeasureGridLines
    {
        get => GetValue(MeasureGridLinesProperty);
        set => SetValue(MeasureGridLinesProperty, value);
    }

    public double PlayheadTick
    {
        get => GetValue(PlayheadTickProperty);
        set => SetValue(PlayheadTickProperty, value);
    }

    public int StartTick
    {
        get => GetValue(StartTickProperty);
        set => SetValue(StartTickProperty, value);
    }

    public int GridTicks
    {
        get => GetValue(GridTicksProperty);
        set => SetValue(GridTicksProperty, value);
    }

    public int SelectedTick
    {
        get => GetValue(SelectedTickProperty);
        set => SetValue(SelectedTickProperty, value);
    }

    public string SelectedLane
    {
        get => GetValue(SelectedLaneProperty);
        set => SetValue(SelectedLaneProperty, value);
    }

    public IEnumerable? SelectedObjectKeys
    {
        get => GetValue(SelectedObjectKeysProperty);
        set => SetValue(SelectedObjectKeysProperty, value);
    }

    public event EventHandler<TimelineHitEventArgs>? TimelineHit;
    public event EventHandler<TimelineDragEventArgs>? TimelineDragCompleted;
    public event EventHandler<TimelineRangeSelectionEventArgs>? TimelineRangeSelected;

    static TimelineCanvas()
    {
        AffectsRender<TimelineCanvas>(ItemsProperty);
        AffectsRender<TimelineCanvas>(MeasureGridLinesProperty);
        AffectsRender<TimelineCanvas>(PlayheadTickProperty);
        AffectsRender<TimelineCanvas>(StartTickProperty);
        AffectsRender<TimelineCanvas>(GridTicksProperty);
        AffectsRender<TimelineCanvas>(SelectedTickProperty);
        AffectsRender<TimelineCanvas>(SelectedLaneProperty);
        AffectsRender<TimelineCanvas>(SelectedObjectKeysProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Renderはコントロール内のローカル座標で描画するため、親座標を含むBoundsではなく0起点にそろえる。
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var rows = (Items ?? Array.Empty<object>()).OfType<TimelineRow>().ToList();
        var lanes = ResolveLanes(rows);

        DrawBackground(context, bounds);
        DrawLaneBackgrounds(context, bounds, lanes);
        DrawMeasureGrid(context, bounds, StartTick, GridTicks, (MeasureGridLines ?? Array.Empty<object>()).OfType<MeasureGridLineRow>().ToList());
        DrawLaneSeparators(context, bounds, lanes);
        DrawLaneHeaders(context, bounds, lanes);
        var selectedObjectKeys = ResolveSelectedObjectKeySet();
        DrawEvents(context, bounds, rows, lanes, StartTick, SelectedTick, SelectedLane, selectedObjectKeys);
        DrawDragPreview(context, bounds, rows, lanes, selectedObjectKeys);
        DrawStartLine(context, bounds, StartTick);
        DrawRangeSelection(context);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var pointer = e.GetCurrentPoint(this);
        var button = pointer.Properties.IsLeftButtonPressed
            ? TimelineHitButton.Left
            : pointer.Properties.IsRightButtonPressed
                ? TimelineHitButton.Right
                : TimelineHitButton.Other;
        if (button == TimelineHitButton.Other)
        {
            return;
        }

        var bounds = new Rect(Bounds.Size);
        var rows = (Items ?? Array.Empty<object>()).OfType<TimelineRow>().ToList();
        var lanes = ResolveLanes(rows);
        var hit = HitTestTimeline(bounds, lanes, e.GetPosition(this), StartTick, e.ClickCount, button);
        if (hit is null)
        {
            return;
        }

        if (button == TimelineHitButton.Left && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _isRangeSelecting = true;
            _rangeSelectionStartPoint = e.GetPosition(this);
            _rangeSelectionCurrentPoint = _rangeSelectionStartPoint;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        TimelineHit?.Invoke(this, hit);
        if (button == TimelineHitButton.Left && e.ClickCount == 1)
        {
            _dragStartPoint = e.GetPosition(this);
            _dragStartHit = hit;
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isRangeSelecting)
        {
            if (_dragStartPoint is not null && _dragStartHit is not null)
            {
                var bounds = new Rect(Bounds.Size);
                var rows = (Items ?? Array.Empty<object>()).OfType<TimelineRow>().ToList();
                var lanes = ResolveLanes(rows);
                _dragCurrentHit = HitTestTimeline(bounds, lanes, e.GetPosition(this), StartTick, 1, TimelineHitButton.Left);
                InvalidateVisual();
                e.Handled = true;
            }

            return;
        }

        _rangeSelectionCurrentPoint = e.GetPosition(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isRangeSelecting)
        {
            var selectionStartPoint = _rangeSelectionStartPoint;
            var selectionEndPoint = e.GetPosition(this);
            _isRangeSelecting = false;
            _rangeSelectionStartPoint = null;
            _rangeSelectionCurrentPoint = null;
            e.Pointer.Capture(null);

            if (selectionStartPoint is not null)
            {
                RaiseRangeSelected(selectionStartPoint.Value, selectionEndPoint);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_dragStartPoint is null || _dragStartHit is null)
        {
            return;
        }

        var startPoint = _dragStartPoint.Value;
        var startHit = _dragStartHit;
        _dragStartPoint = null;
        _dragStartHit = null;
        _dragCurrentHit = null;
        e.Pointer.Capture(null);
        InvalidateVisual();

        var endPoint = e.GetPosition(this);
        if (Math.Abs(endPoint.X - startPoint.X) < 4 && Math.Abs(endPoint.Y - startPoint.Y) < 4)
        {
            return;
        }

        var bounds = new Rect(Bounds.Size);
        var rows = (Items ?? Array.Empty<object>()).OfType<TimelineRow>().ToList();
        var lanes = ResolveLanes(rows);
        var endHit = HitTestTimeline(bounds, lanes, endPoint, StartTick, 1, TimelineHitButton.Left);
        if (endHit is null)
        {
            return;
        }

        TimelineDragCompleted?.Invoke(this, new TimelineDragEventArgs(
            startHit.Tick,
            startHit.Lane,
            endHit.Tick,
            endHit.Lane));
        e.Handled = true;
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
            "speed" or "scroll" or "bpm" or "stop" or "measure" => new SolidColorBrush(Color.Parse("#f7f7d5")),
            "bga" or "layer" or "poor" => new SolidColorBrush(Color.Parse("#dcfce7")),
            var value when value.StartsWith("background", StringComparison.OrdinalIgnoreCase) => new SolidColorBrush(Color.Parse("#fff7ed")),
            _ => Brushes.White
        };
    }

    private static void DrawBackground(DrawingContext context, Rect bounds)
    {
        context.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.Parse("#9aa4b2")), 1), bounds);
    }

    private static void DrawMeasureGrid(
        DrawingContext context,
        Rect bounds,
        int startTick,
        int gridTicks,
        IReadOnlyList<MeasureGridLineRow> measureGridLines)
    {
        var measurePen = new Pen(new SolidColorBrush(Color.Parse("#c7cdd6")), 1);
        var beatPen = new Pen(new SolidColorBrush(Color.Parse("#edf0f4")), 1);

        if (measureGridLines.Count > 0)
        {
            foreach (var line in measureGridLines)
            {
                var y = EventToY(line.Tick, bounds, startTick);
                if (y < bounds.Top + TimelineTopPadding || y > bounds.Bottom)
                {
                    continue;
                }

                context.DrawLine(
                    line.IsMeasureStart ? measurePen : beatPen,
                    new Point(bounds.Left, SnapLineY(y)),
                    new Point(bounds.Right, SnapLineY(y)));
            }

            return;
        }

        var tickStep = Math.Max(1.0, gridTicks);
        var firstTick = Math.Floor(startTick / tickStep) * tickStep;
        for (var tick = firstTick; EventToY(tick, bounds, startTick) >= bounds.Top + TimelineTopPadding; tick += tickStep)
        {
            var y = SnapLineY(EventToY(tick, bounds, startTick));
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

    private static void DrawLaneHeaders(DrawingContext context, Rect bounds, IReadOnlyList<string> lanes)
    {
        var laneWidth = ResolveLaneWidth(bounds, lanes);
        for (var i = 0; i < lanes.Count; i++)
        {
            var label = ResolveLaneLabel(lanes[i]);
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var text = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                9,
                new SolidColorBrush(Color.Parse("#166534")));
            var x = ResolveLaneX(bounds, lanes, laneWidth, i) + Math.Max(1, (laneWidth - text.Width) / 2);
            context.DrawText(text, new Point(x, bounds.Top + 2));
        }
    }

    private static string ResolveLaneLabel(string lane)
    {
        return lane switch
        {
            "speed" => "SPD",
            "scroll" => "SCRL",
            "bpm" => "BPM",
            "stop" => "STOP",
            "measure" => "LEN",
            "bga" => "BGA",
            "layer" => "LAYER",
            "poor" => "POOR",
            "scratch" => "S",
            "scratch2" => "S2",
            var key when TryGetKeyNumber(key, out var number) => number.ToString(CultureInfo.InvariantCulture),
            var value when value.Equals("background1", StringComparison.OrdinalIgnoreCase) => "BGM",
            _ => ""
        };
    }

    private static void DrawEvents(
        DrawingContext context,
        Rect bounds,
        IReadOnlyList<TimelineRow> rows,
        IReadOnlyList<string> lanes,
        int startTick,
        int selectedTick,
        string selectedLane,
        IReadOnlySet<string> selectedObjectKeys)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var laneWidth = ResolveLaneWidth(bounds, lanes);
        foreach (var row in rows)
        {
            var y = EventToY(row.Tick, bounds, startTick);
            if (y < bounds.Top - 40 || y > bounds.Bottom + 40)
            {
                continue;
            }

            var isSelected = IsSelected(row, selectedTick, selectedLane) || selectedObjectKeys.Contains(CreateObjectKey(row));
            if (row.Kind.Equals("Timing", StringComparison.OrdinalIgnoreCase))
            {
                if (!row.Detail.Equals("Bar", StringComparison.OrdinalIgnoreCase))
                {
                    DrawTimingEvent(context, bounds, lanes, laneWidth, row, y, isSelected);
                }

                continue;
            }

            if (row.Kind.Equals("Visual", StringComparison.OrdinalIgnoreCase))
            {
                DrawVisualEvent(context, bounds, lanes, laneWidth, row, y, isSelected);
                continue;
            }

            var laneIndex = ResolveLaneIndex(row.Lane, row.Kind, lanes);
            var x = ResolveLaneX(bounds, lanes, laneWidth, laneIndex) + 4;
            var noteType = ResolveNoteType(row.Detail);
            var isLongNote = row.DurationTicks is > 0 || noteType is "hold" or "cn" or "hcn";
            var height = isLongNote ? 14 : 12;
            var brush = ResolveObjectBrush(row.Lane, row.Kind, isLongNote, noteType);
            var pen = ResolveObjectPen(row.Lane, row.Kind, noteType);

            // tick座標をオブジェクトの開始端として小節線・グリッド線へ揃える。
            var rect = new Rect(x, Math.Round(y - height), Math.Max(8, laneWidth - 8), height);
            if (isLongNote && row.DurationTicks.GetValueOrDefault() > 0)
            {
                var durationTicks = row.DurationTicks.GetValueOrDefault();
                DrawLongNote(context, x, laneWidth, y, EventToY(row.Tick + durationTicks, bounds, startTick), brush, pen);
            }
            else
            {
                context.DrawRectangle(brush, pen, rect);
            }

            if (isSelected)
            {
                context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#facc15")), 2), rect.Inflate(2));
            }
        }
    }

    private static void DrawVisualEvent(
        DrawingContext context,
        Rect bounds,
        IReadOnlyList<string> lanes,
        double laneWidth,
        TimelineRow row,
        double y,
        bool isSelected)
    {
        var laneIndex = FindLaneIndex(lanes, row.Lane);
        if (laneIndex < 0)
        {
            return;
        }

        var x = ResolveLaneX(bounds, lanes, laneWidth, laneIndex) + 3;
        var rect = new Rect(x, Math.Round(y - 14), Math.Max(10, laneWidth - 6), 14);
        var brush = row.Lane switch
        {
            "layer" => new SolidColorBrush(Color.Parse("#22c55e")),
            "poor" => new SolidColorBrush(Color.Parse("#f87171")),
            _ => new SolidColorBrush(Color.Parse("#16a34a"))
        };
        var pen = new Pen(new SolidColorBrush(Color.Parse("#14532d")), 1.2);
        context.DrawRectangle(brush, pen, rect);

        if (isSelected)
        {
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#facc15")), 2), rect.Inflate(2));
        }
    }

    private static void DrawTimingEvent(
        DrawingContext context,
        Rect bounds,
        IReadOnlyList<string> lanes,
        double laneWidth,
        TimelineRow row,
        double y,
        bool isSelected)
    {
        var laneIndex = FindLaneIndex(lanes, ResolveTimingLane(row.Detail));
        if (laneIndex < 0)
        {
            return;
        }

        var x = ResolveLaneX(bounds, lanes, laneWidth, laneIndex) + 3;
        var rect = new Rect(x, Math.Round(y - 16 ), Math.Max(10, laneWidth - 6), 16);
        var brush = ResolveTimingBrush(row.Detail);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#312e81")), 1.2);
        context.DrawRectangle(brush, pen, rect);

        if (isSelected)
        {
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#facc15")), 2), rect.Inflate(2));
        }
    }

    private static bool IsSelected(TimelineRow row, int selectedTick, string selectedLane)
    {
        return selectedTick >= 0 &&
               row.Tick == selectedTick &&
               string.Equals(row.Lane, selectedLane, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlySet<string> ResolveSelectedObjectKeySet()
    {
        return (SelectedObjectKeys ?? Array.Empty<object>())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private void DrawDragPreview(
        DrawingContext context,
        Rect bounds,
        IReadOnlyList<TimelineRow> rows,
        IReadOnlyList<string> lanes,
        IReadOnlySet<string> selectedObjectKeys)
    {
        if (_dragStartHit is not { } startHit || _dragCurrentHit is not { } currentHit)
        {
            return;
        }

        var tickDelta = currentHit.Tick - startHit.Tick;
        var startLaneIndex = FindLaneIndex(lanes, startHit.Lane);
        var currentLaneIndex = FindLaneIndex(lanes, currentHit.Lane);
        if (startLaneIndex < 0 || currentLaneIndex < 0)
        {
            return;
        }

        var laneDelta = currentLaneIndex - startLaneIndex;
        var targets = rows
            .Where(row => selectedObjectKeys.Contains(CreateObjectKey(row)))
            .ToList();
        if (targets.Count == 0)
        {
            targets = rows
                .Where(row =>
                    string.Equals(row.Lane, startHit.Lane, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(row.Tick - startHit.Tick) <= Math.Max(1, GridTicks / 2))
                .OrderBy(row => Math.Abs(row.Tick - startHit.Tick))
                .Take(1)
                .ToList();
        }

        if (targets.Count == 0)
        {
            return;
        }

        var laneWidth = ResolveLaneWidth(bounds, lanes);
        var fill = new SolidColorBrush(Color.Parse("#fde047"), 0.36);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#a16207")), 2);
        foreach (var row in targets)
        {
            var targetTick = Math.Max(0, row.Tick + tickDelta);
            var targetLane = ResolvePreviewLane(row, lanes, laneDelta, currentHit.Lane);
            var y = EventToY(targetTick, bounds, StartTick);
            if (y < bounds.Top - 48 || y > bounds.Bottom + 48)
            {
                continue;
            }

            var laneIndex = ResolveLaneIndex(targetLane, row.Kind, lanes);
            if (laneIndex < 0)
            {
                continue;
            }

            var x = ResolveLaneX(bounds, lanes, laneWidth, laneIndex) + 3;
            var height = row.DurationTicks is > 0 ? 16 : 14;
            var rect = new Rect(x, Math.Round(y - height), Math.Max(10, laneWidth - 6), height);
            context.DrawRectangle(fill, pen, rect);
        }
    }

    private static string ResolvePreviewLane(TimelineRow row, IReadOnlyList<string> lanes, int laneDelta, string currentLane)
    {
        if (row.Kind.Equals("Timing", StringComparison.OrdinalIgnoreCase))
        {
            return row.Lane;
        }

        if (row.Kind.Equals("Visual", StringComparison.OrdinalIgnoreCase))
        {
            return IsMediaLane(currentLane) ? currentLane : row.Lane;
        }

        if ((row.Kind.Equals("Note", StringComparison.OrdinalIgnoreCase) ||
             row.Kind.Equals("BGM", StringComparison.OrdinalIgnoreCase)) &&
            !IsAudioLane(currentLane))
        {
            return row.Lane;
        }

        var rowLaneIndex = FindLaneIndex(lanes, row.Lane);
        if (rowLaneIndex < 0)
        {
            return row.Lane;
        }

        var targetIndex = Math.Clamp(rowLaneIndex + laneDelta, 0, lanes.Count - 1);
        return lanes[targetIndex];
    }

    private static bool IsMediaLane(string lane)
    {
        return lane.Equals("bga", StringComparison.OrdinalIgnoreCase) ||
               lane.Equals("layer", StringComparison.OrdinalIgnoreCase) ||
               lane.Equals("poor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAudioLane(string lane)
    {
        return lane.StartsWith("background", StringComparison.OrdinalIgnoreCase) ||
               lane.Equals("scratch", StringComparison.OrdinalIgnoreCase) ||
               lane.Equals("scratch2", StringComparison.OrdinalIgnoreCase) ||
               TryGetKeyNumber(lane, out _);
    }

    private static string CreateObjectKey(TimelineRow row)
    {
        return $"{row.Kind}|{row.Tick}|{row.Lane}|{row.Detail}";
    }

    private void DrawRangeSelection(DrawingContext context)
    {
        if (!_isRangeSelecting ||
            _rangeSelectionStartPoint is not { } start ||
            _rangeSelectionCurrentPoint is not { } current)
        {
            return;
        }

        var rect = new Rect(start, current).Normalize();
        var fill = new SolidColorBrush(Color.Parse("#60a5fa"), 0.16);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#2563eb")), 1.2);
        context.DrawRectangle(fill, pen, rect);
    }

    private static void DrawLongNote(
        DrawingContext context,
        double x,
        double laneWidth,
        double startY,
        double endY,
        IBrush brush,
        Pen pen)
    {
        var top = Math.Min(startY, endY);
        var height = Math.Max(8, Math.Abs(endY - startY));
        var width = Math.Max(8, laneWidth - 8);
        var body = new Rect(x, Math.Round(top), width, height);
        var startCap = new Rect(x, Math.Round(startY - 7), width, 14);
        var endCap = new Rect(x, Math.Round(endY - 7), width, 14);
        var bodyBrush = brush is SolidColorBrush solidBrush
            ? new SolidColorBrush(solidBrush.Color, 0.45)
            : brush;

        context.DrawRectangle(bodyBrush, pen, body);
        context.DrawRectangle(brush, pen, startCap);
        context.DrawRectangle(bodyBrush, pen, endCap);
    }

    private static IBrush ResolveTimingBrush(string detail)
    {
        if (detail.StartsWith("BPM ", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.Parse("#818cf8"));
        }

        if (detail.StartsWith("STOP ", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.Parse("#f59e0b"));
        }

        if (detail.StartsWith("MEASURE ", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.Parse("#84cc16"));
        }

        if (detail.StartsWith("LNOBJ ", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.Parse("#a78bfa"));
        }

        return new SolidColorBrush(Color.Parse("#94a3b8"));
    }

    private static string ResolveTimingLane(string detail)
    {
        if (detail.StartsWith("BPM ", StringComparison.OrdinalIgnoreCase))
        {
            return "bpm";
        }

        if (detail.StartsWith("STOP ", StringComparison.OrdinalIgnoreCase))
        {
            return "stop";
        }

        if (detail.StartsWith("MEASURE ", StringComparison.OrdinalIgnoreCase))
        {
            return "measure";
        }

        if (detail.StartsWith("SCROLL ", StringComparison.OrdinalIgnoreCase))
        {
            return "scroll";
        }

        if (detail.StartsWith("SPEED ", StringComparison.OrdinalIgnoreCase))
        {
            return "speed";
        }

        return "speed";
    }

    private static string ResolveNoteType(string detail)
    {
        var separator = detail.IndexOf(' ', StringComparison.Ordinal);
        return separator <= 0
            ? detail.ToLowerInvariant()
            : detail[..separator].ToLowerInvariant();
    }

    private static Pen ResolveObjectPen(string lane, string kind, string noteType)
    {
        if (kind.Equals("BGM", StringComparison.OrdinalIgnoreCase))
        {
            return new Pen(new SolidColorBrush(Color.Parse("#9a3412")), 1.5);
        }

        if (noteType == "mine")
        {
            return new Pen(new SolidColorBrush(Color.Parse("#713f12")), 1.6);
        }

        if (noteType == "invisible")
        {
            return new Pen(new SolidColorBrush(Color.Parse("#64748b")), 1.3);
        }

        return lane switch
        {
            var key when IsWhiteKey(key) => new Pen(new SolidColorBrush(Color.Parse("#334155")), 1.5),
            var key when IsBlueKey(key) => new Pen(new SolidColorBrush(Color.Parse("#1e3a8a")), 1.3),
            "scratch" or "scratch2" => new Pen(new SolidColorBrush(Color.Parse("#7f1d1d")), 1.5),
            _ => new Pen(new SolidColorBrush(Color.Parse("#334155")), 1)
        };
    }

    private static IBrush ResolveObjectBrush(string lane, string kind, bool isLongNote, string noteType)
    {
        if (kind.Equals("BGM", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(Color.Parse("#f97316"));
        }

        if (noteType == "mine")
        {
            return new SolidColorBrush(Color.Parse("#facc15"));
        }

        if (noteType == "invisible")
        {
            return new SolidColorBrush(Color.Parse("#94a3b8"), 0.35);
        }

        return lane switch
        {
            "scratch" or "scratch2" => new SolidColorBrush(Color.Parse("#ef4444")),
            var key when IsWhiteKey(key) => new SolidColorBrush(Color.Parse("#f8fafc")),
            var key when IsBlueKey(key) => new SolidColorBrush(Color.Parse("#2563eb")),
            _ => new SolidColorBrush(Color.Parse("#64748b"))
        };
    }

    private static void DrawStartLine(DrawingContext context, Rect bounds, int startTick)
    {
        var y = SnapLineY(EventToY(0, bounds, startTick));
        if (y < bounds.Top || y > bounds.Bottom)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.Parse("#ef4444")), 2);
        context.DrawLine(pen, new Point(bounds.Left, y), new Point(bounds.Right, y));
    }

    private static double EventToY(double tick, Rect bounds, int startTick)
    {
        return bounds.Bottom - TimelineBottomPadding - (tick - startTick) * PixelsPerTick;
    }

    private static double YToTick(double y, Rect bounds, int startTick)
    {
        return startTick + (bounds.Bottom - TimelineBottomPadding - y) / PixelsPerTick;
    }

    private static TimelineHitEventArgs? HitTestTimeline(
        Rect bounds,
        IReadOnlyList<string> lanes,
        Point point,
        int startTick,
        int clickCount,
        TimelineHitButton button)
    {
        if (point.X < bounds.Left ||
            point.X > bounds.Right ||
            point.Y < bounds.Top + TimelineTopPadding ||
            point.Y > bounds.Bottom)
        {
            return null;
        }

        var laneWidth = ResolveLaneWidth(bounds, lanes);
        var laneIndex = ResolveLaneIndexAtX(bounds, lanes, laneWidth, point.X);
        if (laneIndex < 0)
        {
            return null;
        }

        var tick = Math.Max(0, (int)Math.Round(YToTick(point.Y, bounds, startTick)));
        return new TimelineHitEventArgs(tick, lanes[laneIndex], clickCount, button);
    }

    private void RaiseRangeSelected(Point startPoint, Point endPoint)
    {
        var bounds = new Rect(Bounds.Size);
        var rows = (Items ?? Array.Empty<object>()).OfType<TimelineRow>().ToList();
        var lanes = ResolveLanes(rows);
        var laneWidth = ResolveLaneWidth(bounds, lanes);
        var startLaneIndex = ResolveLaneIndexAtX(bounds, lanes, laneWidth, startPoint.X);
        var endLaneIndex = ResolveLaneIndexAtX(bounds, lanes, laneWidth, endPoint.X);
        if (startLaneIndex < 0 || endLaneIndex < 0)
        {
            return;
        }

        var tickA = Math.Max(0, (int)Math.Round(YToTick(startPoint.Y, bounds, StartTick)));
        var tickB = Math.Max(0, (int)Math.Round(YToTick(endPoint.Y, bounds, StartTick)));
        var minLaneIndex = Math.Min(startLaneIndex, endLaneIndex);
        var maxLaneIndex = Math.Max(startLaneIndex, endLaneIndex);
        TimelineRangeSelected?.Invoke(this, new TimelineRangeSelectionEventArgs(
            Math.Min(tickA, tickB),
            Math.Max(tickA, tickB),
            lanes[minLaneIndex],
            lanes[maxLaneIndex],
            lanes.Skip(minLaneIndex).Take(maxLaneIndex - minLaneIndex + 1).ToArray()));
    }

    private static int ResolveLaneIndexAtX(
        Rect bounds,
        IReadOnlyList<string> lanes,
        double laneWidth,
        double x)
    {
        for (var i = 0; i < lanes.Count; i++)
        {
            var laneX = ResolveLaneX(bounds, lanes, laneWidth, i);
            if (x >= laneX && x < laneX + laneWidth)
            {
                return i;
            }
        }

        return -1;
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

        if (kind.Equals("Timing", StringComparison.OrdinalIgnoreCase))
        {
            var eventIndex = FindLaneIndex(lanes, "speed");
            return eventIndex >= 0 ? eventIndex : Math.Min(lanes.Count - 1, Math.Max(1, lanes.Count / 2));
        }

        if (kind.Equals("Visual", StringComparison.OrdinalIgnoreCase))
        {
            var visualIndex = FindLaneIndex(lanes, lane);
            return visualIndex >= 0 ? visualIndex : FindLaneIndex(lanes, "bga");
        }

        var index = FindLaneIndex(lanes, lane);
        return index >= 0 ? index : Math.Min(lanes.Count - 1, Math.Max(1, lanes.Count / 2));
    }

    private static IReadOnlyList<string> ResolveLanes(IEnumerable<TimelineRow> rows)
    {
        var rowList = rows as IReadOnlyCollection<TimelineRow> ?? rows.ToList();
        var baseLanes = rowList.Any(row => row.Lane is "scratch2" or "key8" or "key9" or "key10" or "key11" or "key12" or "key13" or "key14")
            ? FourteenKeyLanes
            : SevenKeyLanes;
        var maxBackgroundLane = rowList
            .Select(row => ResolveBackgroundLaneNumber(row.Lane))
            .DefaultIfEmpty(8)
            .Max();
        if (maxBackgroundLane <= 8)
        {
            return baseLanes;
        }

        var lanes = baseLanes
            .Where(lane => !lane.StartsWith("background", StringComparison.OrdinalIgnoreCase))
            .ToList();
        for (var index = 1; index <= maxBackgroundLane; index++)
        {
            lanes.Add($"background{index}");
        }

        return lanes;
    }

    private static int ResolveBackgroundLaneNumber(string lane)
    {
        const string prefix = "background";
        return lane.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(lane[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? Math.Max(1, number)
            : 0;
    }

    private static double ResolveLaneWidth(Rect bounds, IReadOnlyList<string> lanes)
    {
        var gap = IsFourteenKeyLaneSet(lanes) ? TwoPlayerGapWidth : 0;
        return Math.Max(8, (bounds.Width - gap) / lanes.Count);
    }

    private static double ResolveLaneX(Rect bounds, IReadOnlyList<string> lanes, double laneWidth, int index)
    {
        var gapOffset = IsFourteenKeyLaneSet(lanes) && index >= TwoPlayerGapBeforeLaneIndex
            ? TwoPlayerGapWidth
            : 0;
        return bounds.Left + laneWidth * index + gapOffset;
    }

    private static void DrawLaneGap(DrawingContext context, Rect bounds, IReadOnlyList<string> lanes, double laneWidth)
    {
        if (!IsFourteenKeyLaneSet(lanes))
        {
            return;
        }

        var x = bounds.Left + laneWidth * TwoPlayerGapBeforeLaneIndex;
        var brush = new SolidColorBrush(Color.Parse("#e5e7eb"));
        var pen = new Pen(new SolidColorBrush(Color.Parse("#94a3b8")), 1);
        context.DrawRectangle(brush, pen, new Rect(x, bounds.Top, TwoPlayerGapWidth, bounds.Height));
    }

    private static bool IsFourteenKeyLaneSet(IReadOnlyList<string> lanes)
    {
        return lanes.Contains("scratch2") || lanes.Contains("key8");
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

public sealed class TimelineHitEventArgs(int tick, string lane, int clickCount, TimelineHitButton button) : EventArgs
{
    public int Tick { get; } = tick;
    public string Lane { get; } = lane;
    public int ClickCount { get; } = clickCount;
    public TimelineHitButton Button { get; } = button;
}

public sealed class TimelineDragEventArgs(int fromTick, string fromLane, int toTick, string toLane) : EventArgs
{
    public int FromTick { get; } = fromTick;
    public string FromLane { get; } = fromLane;
    public int ToTick { get; } = toTick;
    public string ToLane { get; } = toLane;
}

public sealed class TimelineRangeSelectionEventArgs(
    int startTick,
    int endTick,
    string startLane,
    string endLane,
    IReadOnlyList<string> lanes) : EventArgs
{
    public int StartTick { get; } = startTick;
    public int EndTick { get; } = endTick;
    public string StartLane { get; } = startLane;
    public string EndLane { get; } = endLane;
    public IReadOnlyList<string> Lanes { get; } = lanes;
}

public enum TimelineHitButton
{
    Other,
    Left,
    Right
}
