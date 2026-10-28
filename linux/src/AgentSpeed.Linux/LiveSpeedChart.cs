using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Chart;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AgentSpeed.Linux;

/// <summary>Actual measured output, with in-flight estimates drawn separately, over the selected recent time window.</summary>
internal sealed class LiveSpeedChart : Control
{
    private static readonly Typeface LabelFace = new("Noto Sans CJK SC");
    private static readonly IBrush Grid = Ui.ChartGrid;
    private SpeedSnapshot _snapshot = SpeedSnapshot.Empty;
    private IReadOnlyList<LiveChartBucket> _points = [];
    private int _windowSeconds = 120;
    private double _redline = 400;
    private SpeedMetric _metric;
    public SpeedMetric Metric
    {
        get => _metric;
        set { if (_metric == value) return; _metric = value; UpdateSeries(force: true); }
    }
    private DateTimeOffset _end = DateTimeOffset.MinValue;
    private Point? _hover;
    private bool _dirty = true;
    private Size _geometrySize;
    private Rect _plot;
    private ChartScale _scale;
    private Point[] _measuredPoints = [];
    private Point[] _totalPoints = [];
    private StreamGeometry? _measuredLine;
    private StreamGeometry? _measuredArea;
    private StreamGeometry? _estimatedLine;
    private StreamGeometry? _estimatedArea;
    private List<int> _isolated = [];

    public LiveSpeedChart()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = false;
        MinHeight = 104;
        AutomationProperties.SetName(this, "Codex 实时输出速度曲线");
        ToolTip.SetTip(this, "实线为实测速度，虚线为生成中的估算；悬停可查看每秒的明细。");
    }

    public int WindowSeconds
    {
        get => _windowSeconds;
        set
        {
            var normalized = LiveChartSeries.NormalizeWindow(value);
            if (_windowSeconds == normalized) return;
            _windowSeconds = normalized;
            UpdateSeries(force: true);
        }
    }

    public double Redline
    {
        get => _redline;
        set
        {
            var next = double.IsFinite(value) && value > 0 ? value : 400;
            if (_redline == next) return;
            _redline = next;
            InvalidateVisual();
        }
    }

    public int VisiblePointCount => _points.Count;
    public double LatestRate => _points.Count > 0 ? _points[^1].Total : 0;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); Ui.PaletteChanged += InvalidateVisual; }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { Ui.PaletteChanged -= InvalidateVisual; base.OnDetachedFromVisualTree(e); }

    public void Update(SpeedSnapshot snapshot)
    {
        var oldState = _snapshot.State.Kind;
        var oldPeak = _snapshot.PeakToday.Output;
        _snapshot = snapshot;
        UpdateSeries(oldState != snapshot.State.Kind || oldPeak != snapshot.PeakToday.Output);
    }

    private void UpdateSeries(bool force)
    {
        var end = LiveChartSeries.EndTime(_snapshot.History, _snapshot.Timestamp);
        var next = _snapshot.State.Kind == MonitorStateKind.Running
            ? LiveChartSeries.Window(_snapshot.History, _snapshot.Timestamp, _windowSeconds) : [];
        if (!force && _end == end && next.SequenceEqual(_points)) return;
        _points = next;
        _end = end;
        _dirty = true;
        AutomationProperties.SetName(this, "Codex 实时" + MetricPresentation.Name(Metric) + "曲线");
        AutomationProperties.SetHelpText(this, _points.Count == 0 ? "尚无实时采样。"
            : $"最近 {_windowSeconds} 秒，最新{MetricPresentation.Name(Metric)} {SpeedFormat.Speed(LatestRate)} tok/s；实线实测，虚线估算。");
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 24 || Bounds.Height < 45) return;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (_dirty || _geometrySize != Bounds.Size) RebuildGeometry();
        for (var i = 0; i <= 2; i++)
        {
            var y = _plot.Top + _plot.Height * i / 2;
            context.DrawLine(new Pen(Grid, 1, new DashStyle([2, 4], 0)), new(_plot.Left, y), new(_plot.Right, y));
        }

        Text(context, _points.Count == 0 ? (Metric == SpeedMetric.Total ? "输入＋输出 tok/s" : "输出 tok/s") : SpeedFormat.Speed(_scale.MaxValue) + " tok/s", new(0, 0), Ui.Muted);
        if (_points.Count == 0)
        {
            var message = _snapshot.State.Kind switch
            {
                MonitorStateKind.Starting => "正在载入实时曲线…",
                MonitorStateKind.NoSources => "尚未发现 Codex 会话记录",
                _ => "等待实时采样",
            };
            var text = Format(message, Ui.Muted, 11);
            context.DrawText(text, new((_plot.Width - text.Width) / 2, _plot.Top + (_plot.Height - text.Height) / 2));
            return;
        }

        var line = HeatBrush(1);
        using (context.PushClip(_plot.Inflate(2)))
        {
            context.DrawGeometry(HeatBrush(.13), null, _estimatedArea!);
            context.DrawGeometry(HeatBrush(.32), null, _measuredArea!);
            context.DrawGeometry(null, new Pen(HeatBrush(.12), 5, lineCap: PenLineCap.Round), _measuredLine!);
            context.DrawGeometry(null, new Pen(line, 1.6, lineCap: PenLineCap.Round), _measuredLine!);
            context.DrawGeometry(null, new Pen(HeatBrush(.73), 1.5, new DashStyle([3, 3], 0), PenLineCap.Round), _estimatedLine!);
            foreach (var index in _isolated)
            {
                context.DrawEllipse(new SolidColorBrush(ToColor(HeroPalette.Stroke(HeatMath.Heat(_points[index].Measured, Redline), Ui.IsDark))),
                    null, _measuredPoints[index], 2, 2);
                if (_points[index].Estimated > 0)
                    context.DrawEllipse(null, new Pen(new SolidColorBrush(ToColor(HeroPalette.Stroke(HeatMath.Heat(_points[index].Total, Redline), Ui.IsDark).MultiplyAlpha(.7))), 1.4),
                        _totalPoints[index], 2.5, 2.5);
            }
        }

        if (_snapshot.PeakToday.Output > 0 && _scale.PeakInRange)
        {
            var y = Y(_snapshot.PeakToday.Output);
            context.DrawLine(new Pen(Ui.ChartReference, .8, new DashStyle([3, 4], 0)), new(0, y), new(_plot.Right, y));
            var peakText = Format("今日峰值 " + SpeedFormat.Speed(_snapshot.PeakToday.Output), Ui.Muted, 9);
            context.DrawText(peakText, new(Math.Max(0, _plot.Right - peakText.Width), Math.Max(_plot.Top, y - peakText.Height - 2)));
        }

        var left = _end.AddSeconds(1 - WindowSeconds).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var right = _end.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        Text(context, left, new(0, _plot.Bottom + 5), Ui.Muted, 9);
        var rightText = Format(right, Ui.Muted, 9);
        context.DrawText(rightText, new(_plot.Right - rightText.Width, _plot.Bottom + 5));
        DrawHover(context);
    }

    private void RebuildGeometry()
    {
        _geometrySize = Bounds.Size;
        _plot = new(0, 20, Math.Max(1, Bounds.Width - 1), Math.Max(1, Bounds.Height - 40));
        _scale = ChartMath.ComputeScale(_points.Count == 0 ? 0 : _points.Max(point => point.Total), _snapshot.PeakToday.Output);
        _measuredPoints = _points.Select(point => new Point(LiveChartSeries.X(point.Time, _end, WindowSeconds, _plot.Width), Y(point.Measured))).ToArray();
        _totalPoints = _points.Select(point => new Point(LiveChartSeries.X(point.Time, _end, WindowSeconds, _plot.Width), Y(point.Total))).ToArray();
        _isolated = [];
        var runs = new List<(int Start, int End)>();
        for (var start = 0; start < _points.Count;)
        {
            var end = start;
            while (end + 1 < _points.Count && !_points[end + 1].StartsNewSegment) end++;
            runs.Add((start, end));
            if (start == end) _isolated.Add(start);
            start = end + 1;
        }
        _measuredLine = Geometry(_measuredPoints, runs, area: false);
        _measuredArea = Geometry(_measuredPoints, runs, area: true);
        var estimates = new List<(int Start, int End)>();
        foreach (var run in runs)
        {
            var cursor = run.Start;
            while (cursor <= run.End)
            {
                if (_points[cursor].Estimated <= 0) { cursor++; continue; }
                var first = Math.Max(run.Start, cursor - 1);
                while (cursor + 1 <= run.End && _points[cursor + 1].Estimated > 0) cursor++;
                var last = Math.Min(run.End, cursor + 1);
                estimates.Add((first, last));
                cursor += 2;
            }
        }
        _estimatedLine = Geometry(_totalPoints, estimates, area: false);
        _estimatedArea = Geometry(_totalPoints, estimates, area: true);
        _dirty = false;
    }

    private StreamGeometry Geometry(Point[] points, IEnumerable<(int Start, int End)> runs, bool area)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        foreach (var (start, end) in runs)
        {
            if (end <= start) continue;
            var slice = points[start..(end + 1)].Select(point => new ChartPoint(point.X, point.Y)).ToArray();
            context.BeginFigure(area ? new(points[start].X, _plot.Bottom) : points[start], area);
            if (area) context.LineTo(points[start], false);
            foreach (var segment in ChartMath.CatmullRom(slice))
                context.CubicBezierTo(new(segment.Control1.X, segment.Control1.Y), new(segment.Control2.X, segment.Control2.Y),
                    new(segment.End.X, segment.End.Y), true);
            if (area) context.LineTo(new(points[end].X, _plot.Bottom), false);
            context.EndFigure(area);
        }
        return geometry;
    }

    private LinearGradientBrush HeatBrush(double alpha)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, _plot.Top, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(0, _plot.Bottom, RelativeUnit.Absolute),
        };
        foreach (var (offset, heat) in ChartMath.HeatGradient(_scale.MaxValue, Redline))
            brush.GradientStops.Add(new(ToColor(HeroPalette.Stroke(heat, Ui.IsDark).MultiplyAlpha(alpha < 1 ? alpha * (1 - offset * .8) : alpha)), offset));
        return brush;
    }

    private void DrawHover(DrawingContext context)
    {
        if (_hover is not { } hover || !_plot.Contains(hover)) return;
        var index = LiveChartSeries.IndexAt(_points, _end, WindowSeconds, hover.X, _plot.Width);
        if (index < 0)
        {
            var missing = Format("此时段暂无采样", Ui.Muted, 10);
            context.DrawText(missing, new(Math.Clamp(hover.X - missing.Width / 2, 0, Math.Max(0, Bounds.Width - missing.Width)), 0));
            return;
        }
        var point = _points[index];
        var position = _totalPoints[index];
        context.DrawLine(new Pen(Ui.ChartReference, 1, new DashStyle([2, 3], 0)), new(position.X, _plot.Top), new(position.X, _plot.Bottom));
        var color = new SolidColorBrush(ToColor(HeroPalette.Stroke(HeatMath.Heat(point.Total, Redline), Ui.IsDark)));
        context.DrawEllipse(color, new Pen(Ui.Surface, 1.5), position, 3, 3);
        var value = point.Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " · " + SpeedFormat.Speed(point.Total) + " tok/s";
        if (point.Estimated > 0) value += "（估算 " + SpeedFormat.Speed(point.Estimated) + "）";
        var text = Format(value, Ui.Text, 10);
        var width = Math.Min(Bounds.Width, text.Width + 14);
        var x = Math.Clamp(position.X - width / 2, 0, Math.Max(0, Bounds.Width - width));
        context.DrawRectangle(Ui.Surface, new Pen(Grid, 1), new Rect(x, 0, width, 19), 5, 5);
        context.DrawText(text, new(x + 7, 1));
    }

    private double Y(double value) => _plot.Bottom - Math.Clamp(value / _scale.MaxValue, 0, 1) * _plot.Height;
    private static Color ToColor(Rgba color) => Color.FromArgb(color.A8, color.R8, color.G8, color.B8);
    private static FormattedText Format(string value, IBrush brush, double size = 10) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, brush);
    private static void Text(DrawingContext context, string value, Point origin, IBrush brush, double size = 10) =>
        context.DrawText(Format(value, brush, size), origin);
}
