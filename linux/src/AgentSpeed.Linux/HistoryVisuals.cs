using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Colors;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace AgentSpeed.Linux;

internal sealed class HistorySparkLine : Control
{
    private IReadOnlyList<double?> _values = [];
    public void Update(IReadOnlyList<double?> values) { _values = values; InvalidateVisual(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); Ui.PaletteChanged += InvalidateVisual; }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { Ui.PaletteChanged -= InvalidateVisual; base.OnDetachedFromVisualTree(e); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_values.Count == 0) return;
        var valid = _values.Where(value => value is { } number && double.IsFinite(number)).Select(value => value!.Value).ToArray();
        if (valid.Length == 0) return;
        var top = Math.Max(1, valid.Max());
        Point? previous = null;
        for (var i = 0; i < _values.Count; i++)
        {
            if (_values[i] is not { } value || !double.IsFinite(value)) { previous = null; continue; }
            var point = new Point(_values.Count == 1 ? Bounds.Width / 2 : i * Bounds.Width / (_values.Count - 1),
                Bounds.Height - 3 - value / top * Math.Max(1, Bounds.Height - 7));
            if (previous is { } last) context.DrawLine(new Pen(Ui.Accent, 1.6, lineCap: PenLineCap.Round), last, point);
            else context.DrawEllipse(Ui.Accent, null, point, 1.8, 1.8);
            previous = point;
        }
    }
}

internal static class HistoryPalette
{
    public static Color Color(SourceKind source)
    {
        var color = HeatPalette.SourceColor(source);
        return Avalonia.Media.Color.FromArgb((byte)Math.Round(color.A * 255), (byte)Math.Round(color.R * 255),
            (byte)Math.Round(color.G * 255), (byte)Math.Round(color.B * 255));
    }
    public static IBrush Brush(SourceKind source) => new SolidColorBrush(Color(source));
}

internal sealed class HistoryDailyChart : Control
{
    public static readonly IBrush CodexColor = Ui.Codex;
    private static readonly IBrush SpeedColor = Ui.HistorySpeed;
    private IReadOnlyList<HistoryChartBucket> _buckets = [];
    private readonly List<(Rect Area, int Index, SourceKind Source)> _segments = [];
    private int _metric;
    private bool _speed = true;
    private int _hover = -1;
    private SourceKind? _hoverSource;
    public double MoneyFactor { get; set; } = 1;
    public string MoneyCurrency { get; set; } = "USD";
    public Func<HistoryCost, string>? MoneyFormatter { get; set; }
    public int VisibleSegmentCount => _segments.Count;
    private double? ChartValue(HistoryChartBucket bucket) => bucket.Value(_metric) is { } value
        ? _metric == 3 ? value * MoneyFactor : value : null;
    public event Action<HistoryChartBucket>? DaySelected;
    public event Action<HistoryChartBucket, SourceKind?>? SourceDaySelected;
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); Ui.PaletteChanged += InvalidateVisual; }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { Ui.PaletteChanged -= InvalidateVisual; base.OnDetachedFromVisualTree(e); }

    public HistoryDailyChart()
    {
        ClipToBounds = true; Focusable = true;
        AutomationProperties.SetName(this, "按来源堆叠的每日用量图表");
        PointerMoved += Move;
        PointerExited += (_, _) => { _hover = -1; _hoverSource = null; ToolTip.SetTip(this, null); InvalidateVisual(); };
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus(); SelectHovered();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right && _buckets.Count > 0)
            {
                _hover = _hover < 0 ? e.Key == Key.Left ? _buckets.Count - 1 : 0
                    : Math.Clamp(_hover + (e.Key == Key.Left ? -1 : 1), 0, _buckets.Count - 1);
                _hoverSource = null; UpdateTooltip(); InvalidateVisual(); e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Space) { SelectHovered(); e.Handled = true; }
        };
    }

    private void SelectHovered()
    {
        if (_hover < 0 || _hover >= _buckets.Count) return;
        var bucket = _buckets[_hover]; var source = _hoverSource;
        DaySelected?.Invoke(bucket);
        SourceDaySelected?.Invoke(bucket, source);
    }

    public void Update(HistoryPeriod period, int metric, bool showSpeed)
    {
        _buckets = HistoryStatistics.Buckets(period); _metric = metric; _speed = showSpeed;
        _hover = -1; _hoverSource = null; _segments.Clear();
        AutomationProperties.SetHelpText(this, "左右键选择日期，回车打开该日来源与模型明细。颜色代表工具来源；未知日期留空。" + (metric == 4 ? "流量向上为下载、向下为上传，外框为本机接口。" : ""));
        InvalidateVisual();
    }

    private Rect Plot => new(62, 23, Math.Max(1, Bounds.Width - 120), Math.Max(1, Bounds.Height - 60));

    private void Move(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var index = Plot.Contains(p) && _buckets.Count > 0
            ? Math.Clamp((int)((p.X - Plot.X) / Plot.Width * _buckets.Count), 0, _buckets.Count - 1) : -1;
        var source = _segments.LastOrDefault(segment => segment.Area.Contains(p));
        SourceKind? hovered = source.Area.Width > 0 ? source.Source : null;
        if (index == _hover && hovered == _hoverSource) return;
        _hover = index; _hoverSource = hovered; UpdateTooltip(); InvalidateVisual();
    }

    private void UpdateTooltip()
    {
        if (_hover < 0 || _hover >= _buckets.Count) { ToolTip.SetTip(this, null); return; }
        var bucket = _buckets[_hover];
        var date = bucket.Start == bucket.End ? bucket.Start.ToString("yyyy-MM-dd") : $"{bucket.Start:yyyy-MM-dd} — {bucket.End:yyyy-MM-dd}";
        var sources = string.Join("\n", bucket.Sources.Where(pair => pair.Value.Count > 0).Select(pair =>
            LinuxSources.Name(pair.Key) + "：" + (_metric == 4 ? TrafficFormat.Total((long)HistoryStatistics.SourceValue(pair.Value, 4))
                : _metric == 3 ? MoneyFormatter?.Invoke(HistoryAggregator.Totals(pair.Value).Cost) ?? HistoryStatistics.Money(HistoryAggregator.Totals(pair.Value).Cost)
                : UsageProjection.Number((long)HistoryStatistics.SourceValue(pair.Value, _metric)))));
        var text = _metric == 4 ? bucket.Traffic is { } traffic
            ? $"{date}\nAgent ↓ {TrafficFormat.Total(traffic.Shown.Rx)} · ↑ {TrafficFormat.Total(traffic.Shown.Tx)}"
                + (traffic.Machine is { } machine ? $"\n本机 ↓ {TrafficFormat.Total(machine.Rx)} · ↑ {TrafficFormat.Total(machine.Tx)}" : "")
                + (traffic.Partial ? "\n仅已观测时段，未覆盖全天。" : "")
            : date + "\n未采集流量，未知值不会显示为零。"
            : bucket.Totals.HasData ? $"{date}\n输出 {UsageProjection.Number(bucket.Totals.Output)} · 输入 {UsageProjection.Number(bucket.Totals.Input)}\n"
                + $"请求 {UsageProjection.Number(bucket.Totals.Requests)} · 平均 {UsageProjection.Rate(bucket.Totals.AverageSpeed)} tok/s\n"
                + $"API 等值 {MoneyFormatter?.Invoke(bucket.Totals.Cost) ?? HistoryStatistics.Money(bucket.Totals.Cost)}"
            : date + "\n无用量记录";
        ToolTip.SetTip(this, text + (sources.Length > 0 ? "\n" + sources : "") + "\n点击颜色查看该来源模型；点击空白查看日期范围。");
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _segments.Clear();
        if (Bounds.Width < 100 || Bounds.Height < 60) return;
        var plot = Plot;
        var trafficMode = _metric == 4;
        var top = trafficMode
            ? Math.Max(1, _buckets.Select(bucket => Math.Max(Math.Max(bucket.Traffic?.Shown.Rx ?? 0, bucket.Traffic?.Shown.Tx ?? 0), Math.Max(bucket.Traffic?.Machine?.Rx ?? 0, bucket.Traffic?.Machine?.Tx ?? 0))).DefaultIfEmpty(0).Max() * 1.15)
            : Math.Max(_metric == 3 ? .000001 : 1, _buckets.Select(bucket => ChartValue(bucket) ?? 0).DefaultIfEmpty(0).Max() * 1.15);
        var speedTop = Math.Max(1, _buckets.Select(bucket => bucket.Totals.AverageSpeed ?? 0).DefaultIfEmpty(0).Max() * 1.15);
        Text(context, _metric switch { 1 => "输入 token", 2 => "请求数", 3 => "API 等值 / " + MoneyCurrency, 4 => "流量 ↓下载／↑上传（观测量）", _ => "输出 token" }, 10, Ui.Muted, new(0, 0));
        if (_speed && !trafficMode) Text(context, "平均 tok/s", 10, Ui.Muted, new(Bounds.Width - 65, 0));
        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Bottom - plot.Height * i / 4;
            context.DrawLine(new Pen(Ui.Line, .6), new(plot.Left, y), new(plot.Right, y));
            var value = trafficMode ? Math.Abs((i - 2) / 2d) * top : top * i / 4;
            var label = _metric == 3 ? value.ToString(top < 1 ? "0.######" : "0.##", CultureInfo.InvariantCulture)
                : trafficMode ? TrafficFormat.Total((long)value) : UsageProjection.Compact((long)value);
            Text(context, label, 10, Ui.Muted, new(0, y - 7));
            if (_speed && !trafficMode) Text(context, (speedTop * i / 4).ToString("0", CultureInfo.InvariantCulture), 10, Ui.Muted, new(plot.Right + 9, y - 7));
        }
        if (_buckets.Count == 0) return;
        var step = plot.Width / _buckets.Count; Point? previous = null;
        for (var i = 0; i < _buckets.Count; i++)
        {
            var bucket = _buckets[i]; var x = plot.X + step * (i + .5); var width = Math.Min(46, Math.Max(1, step * .62));
            if (_hover == i) context.DrawRectangle(Brush.Parse("#128299FF"), null, new(plot.X + step * i, plot.Y, step, plot.Height));
            if (trafficMode) DrawTraffic(context, bucket, plot, top, x, width, i);
            else if (ChartValue(bucket) is { } value)
            {
                var bottom = plot.Bottom;
                foreach (var source in bucket.Sources.OrderBy(pair => pair.Key))
                {
                    var contribution = HistoryStatistics.SourceValue(source.Value, _metric) * (_metric == 3 ? MoneyFactor : 1);
                    if (contribution <= 0) continue;
                    var height = contribution / top * plot.Height;
                    var area = new Rect(x - width / 2, bottom - height, width, height);
                    context.DrawRectangle(HistoryPalette.Brush(source.Key), null, area, 1, 1);
                    _segments.Add((area, i, source.Key)); bottom -= height;
                }
                if (bucket.Sources.Count == 0)
                {
                    var height = Math.Max(value > 0 ? 2 : 0, value / top * plot.Height);
                    context.DrawRectangle(Ui.Codex, null, new(x - width / 2, plot.Bottom - height, width, height), 2, 2);
                }
            }
            if (_speed && !trafficMode && bucket.Totals.AverageSpeed is { } speed)
            {
                var point = new Point(x, plot.Bottom - speed / speedTop * plot.Height);
                if (previous is { } last) context.DrawLine(new Pen(SpeedColor, 1.6), last, point);
                context.DrawEllipse(Ui.Surface, new Pen(SpeedColor, 1.4), point, 2.4, 2.4); previous = point;
            }
            else previous = null;
            if (i % Math.Max(1, (int)Math.Ceiling(_buckets.Count / 11d)) == 0 || i == _buckets.Count - 1)
            {
                var text = Format(bucket.Start.ToString("M/d", CultureInfo.InvariantCulture), 10, Ui.Muted);
                context.DrawText(text, new(Math.Clamp(x - text.Width / 2, 0, Math.Max(0, Bounds.Width - text.Width)), plot.Bottom + 13));
            }
        }
    }

    private void DrawTraffic(DrawingContext context, HistoryChartBucket bucket, Rect plot, double top, double x, double width, int index)
    {
        if (bucket.Traffic is not { } traffic) return;
        var axis = plot.Y + plot.Height / 2; var up = axis; var down = axis;
        foreach (var pair in traffic.BySource.OrderBy(pair => pair.Key))
        {
            var rx = pair.Value.Rx / top * plot.Height / 2; var tx = pair.Value.Tx / top * plot.Height / 2;
            if (rx > 0) { var area = new Rect(x - width / 2, up - rx, width, rx); context.DrawRectangle(HistoryPalette.Brush(pair.Key), null, area); _segments.Add((area, index, pair.Key)); up -= rx; }
            if (tx > 0) { var area = new Rect(x - width / 2, down, width, tx); context.DrawRectangle(HistoryPalette.Brush(pair.Key), null, area); _segments.Add((area, index, pair.Key)); down += tx; }
        }
        if (traffic.Machine is { } machine)
        {
            var rx = machine.Rx / top * plot.Height / 2; var tx = machine.Tx / top * plot.Height / 2;
            context.DrawRectangle(null, new Pen(Ui.Muted, 1), new(x - width / 2 - 2, axis - rx, width + 4, rx + tx));
        }
    }

    private static FormattedText Format(string text, double size, IBrush brush) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Noto Sans CJK SC"), size, brush);
    private static void Text(DrawingContext context, string text, double size, IBrush brush, Point at) => context.DrawText(Format(text, size, brush), at);
}
