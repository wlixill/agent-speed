using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.History;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AgentSpeed.Linux;

internal sealed class TrafficWindow : Window
{
    private readonly LinuxTrafficService _service;
    private readonly Func<IReadOnlySet<SourceKind>>? _enabledSources;
    private readonly Func<IReadOnlyDictionary<string, SourceKind>>? _agentSources;
    private readonly TextBlock _state = Ui.Label("", 12, Ui.Muted);
    private readonly TextBlock _coverage = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _error = Ui.Label("", 11, Ui.Warning);
    private readonly TextBlock[] _values = Enumerable.Range(0, 4).Select(_ => Ui.Label("—", 23)).ToArray();
    private readonly TextBlock[] _totals = Enumerable.Range(0, 4).Select(_ => Ui.Label("", 11, Ui.Muted)).ToArray();
    private readonly ComboBox _chartScope = new() { Name = "TrafficChartScope", ItemsSource = new[] { "本机接口", "Agent 应用字节（含工具）" }, SelectedIndex = 0, Width = 190 };
    private readonly ComboBox _span = new() { ItemsSource = new[] { "1 分钟", "2 分钟", "5 分钟" }, SelectedIndex = 1, Width = 110 };
    private readonly ComboBox _range = new() { Name = "TrafficHistoryRange", ItemsSource = new[] { "最近 7 天", "最近 30 天", "最近 90 天", "全部观测历史" }, SelectedIndex = 1, Width = 160 };
    private readonly CheckBox _includeTools = new() { Content = "明细包含工具子进程", IsChecked = true, FontSize = 11 };
    private readonly LinuxTrafficChart _chart = new() { Height = 175 };
    private readonly StackPanel _sources = new() { Spacing = 5 };
    private readonly StackPanel _agents = new() { Spacing = 5 };
    private readonly StackPanel _days = new() { Spacing = 4 };
    private IReadOnlyList<TrafficDay>? _lastDays;
    private string? _lastEnabledKey;
    private DateTime _lastHistoryDate;
    private IReadOnlyList<TrafficDay> _visibleDays = [];
    private string? _exportStatus;

    public TrafficWindow(LinuxTrafficService service) : this(service, null, null) { }

    public TrafficWindow(LinuxTrafficService service, Func<IReadOnlySet<SourceKind>>? enabledSources,
        Func<IReadOnlyDictionary<string, SourceKind>>? agentSources = null)
    {
        _service = service;
        _enabledSources = enabledSources;
        _agentSources = agentSources;
        Title = "Agent Speed · 网络流量";
        Width = 1040; Height = 800; MinWidth = 780; MinHeight = 600;
        Background = Ui.Background; Foreground = Ui.Text;
        FontFamily = new FontFamily("Noto Sans CJK SC");
        var root = new Grid { Margin = new Thickness(24), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 14 };
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        var heading = new StackPanel { Spacing = 4 };
        heading.Children.Add(Ui.Label("网络流量", 26)); heading.Children.Add(_state);
        Ui.Cell(header, heading, 0); Ui.Cell(header, Ui.Button("导出观测 CSV…", async () => await ExportAsync()), 1);
        Ui.Cell(root, header, 0, 0);
        var body = new StackPanel { Spacing = 16 };
        var cards = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 10 };
        string[] labels = ["本机接口下载", "本机接口上传", "Agent 下载观测下限", "Agent 上传观测下限"];
        for (var i = 0; i < 4; i++)
        {
            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(Ui.Label(labels[i], 11, Ui.Muted)); card.Children.Add(_values[i]); card.Children.Add(_totals[i]);
            Ui.Cell(cards, Ui.Card(card, new Thickness(13)), i);
        }
        body.Children.Add(cards);
        var chart = new StackPanel { Spacing = 10 };
        var chartOptions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        chartOptions.Children.Add(_chartScope); chartOptions.Children.Add(_span);
        chartOptions.Children.Add(Ui.Label("↓ 下载   ↑ 上传 · 缺失采样留空", 11, Ui.Muted));
        chart.Children.Add(chartOptions); chart.Children.Add(_chart);
        body.Children.Add(Ui.Card(chart));
        var tabs = new TabControl { Name = "TrafficDetailTabs", Height = 300 };
        var sources = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        Ui.Cell(sources, _includeTools, 0, 0);
        Ui.Cell(sources, Row(["来源", "下载下限", "上传下限", "今日下载下限", "今日上传下限", "未对应会话"], "1.2*,*,*,*,*,1.1*", true), 0, 1);
        Ui.Cell(sources, new ScrollViewer { Content = _sources }, 0, 2);
        tabs.Items.Add(new TabItem { Header = "来源", Content = sources });
        var agents = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        Ui.Cell(agents, Row(["会话（仅有打开日志证明时归属）", "下载下限", "上传下限", "今日下载下限", "今日上传下限"], "2*,*,*,*,*", true), 0, 0);
        Ui.Cell(agents, new ScrollViewer { Content = _agents }, 0, 1);
        tabs.Items.Add(new TabItem { Header = "已匹配会话", Content = agents });
        var days = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
        Ui.Cell(days, _range, 0, 0);
        Ui.Cell(days, Row(["日期", "范围", "下载观测量", "上传观测量", "已采样时长", "统计口径"], "112,110,*,*,105,1.7*", true), 0, 1);
        Ui.Cell(days, new ScrollViewer { Content = _days }, 0, 2);
        tabs.Items.Add(new TabItem { Header = "每日历史", Content = days });
        body.Children.Add(Ui.Card(tabs, new Thickness(15, 5, 15, 12)));
        _coverage.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_coverage);
        Ui.Cell(root, new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0, 1);
        _error.TextWrapping = TextWrapping.Wrap; Ui.Cell(root, _error, 0, 2);
        Content = root;
        _chartScope.SelectionChanged += (_, _) => Update();
        _span.SelectionChanged += (_, _) => Update();
        _includeTools.IsCheckedChanged += (_, _) => Update();
        _range.SelectionChanged += (_, _) => UpdateDays(true);
        service.Changed += Changed;
        Opened += (_, _) => Update();
        Closed += (_, _) => service.Changed -= Changed;
        Update();
    }

    private void Changed()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Changed); return; }
        if (IsVisible) Update();
    }
    public void Refresh() => Changed();
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _service is not null) Update();
    }
    private void Update()
    {
        var snapshot = TrafficWindowProjection.Snapshot(_service.Snapshot, _enabledSources?.Invoke(), _agentSources?.Invoke());
        var tools = _includeTools.IsChecked == true;
        _state.Text = !snapshot.Enabled ? "监测未启用 · 请在设置中开启，不追溯开启前的流量"
            : snapshot.ProcessState.Kind == ProcessTrafficKind.Unavailable ? "本机网卡继续观测；进程字节当前不可用"
            : _service.EnhancedActive ? "增强 TCP＋UDP 观测 · 应用字节，不含绕过采集点的路径" : "基础 TCP 采样 · 进程为已观测下限";
        var sourceRate = snapshot.Enabled && snapshot.Sources.Count > 0 ? snapshot.Live(null, tools) : null;
        var sourceTotal = snapshot.Today(null, tools);
        _values[0].Text = Rate(snapshot.MachineLive?.Rx);
        _values[1].Text = Rate(snapshot.MachineLive?.Tx);
        _values[2].Text = Rate(sourceRate?.Rx, true);
        _values[3].Text = Rate(sourceRate?.Tx, true);
        _totals[0].Text = snapshot.MachineSampledMs > 0 ? "今日观测 " + TrafficFormat.Total(snapshot.MachineToday.Rx) : "尚无成对采样";
        _totals[1].Text = snapshot.MachineSampledMs > 0 ? "今日观测 " + TrafficFormat.Total(snapshot.MachineToday.Tx) : "尚无成对采样";
        _totals[2].Text = snapshot.ProcessSampledMs > 0 ? "今日 ≥" + TrafficFormat.Total(sourceTotal.Rx) : "尚无成对采样";
        _totals[3].Text = snapshot.ProcessSampledMs > 0 ? "今日 ≥" + TrafficFormat.Total(sourceTotal.Tx) : "尚无成对采样";
        _sources.Children.Clear();
        foreach (var source in snapshot.Sources.Values.OrderBy(source => source.Source))
        {
            var rate = source.Live(tools); var total = source.Today.Total(tools);
            _sources.Children.Add(Row([source.Source.ToString(), Rate(rate?.Rx, true), Rate(rate?.Tx, true),
                "≥" + TrafficFormat.Total(total.Rx), "≥" + TrafficFormat.Total(total.Tx), "≥" + TrafficFormat.Total(source.Unassigned.Sum)], "1.2*,*,*,*,*,1.1*"));
        }
        if (_sources.Children.Count == 0) _sources.Children.Add(Ui.Label("所选来源尚无可识别的 Agent 应用字节观测；未提供的数据不会表示为精确零。", 11, Ui.Muted));
        _agents.Children.Clear();
        foreach (var agent in snapshot.Agents.Values.OrderBy(agent => agent.AgentId, StringComparer.Ordinal))
        {
            var rate = agent.Live(tools); var total = agent.Today.Total(tools);
            var name = Path.GetFileNameWithoutExtension(agent.AgentId);
            _agents.Children.Add(Row([string.IsNullOrEmpty(name) ? "已匹配会话" : name, Rate(rate?.Rx, true), Rate(rate?.Tx, true),
                "≥" + TrafficFormat.Total(total.Rx), "≥" + TrafficFormat.Total(total.Tx)], "2*,*,*,*,*"));
        }
        if (_agents.Children.Count == 0) _agents.Children.Add(Ui.Label("所选来源中尚无可验证归属的会话；未匹配字节保留在来源汇总。", 11, Ui.Muted));
        IReadOnlyList<TrafficPoint> points = _chartScope.SelectedIndex == 0 ? snapshot.MachineHistory
            : snapshot.SourceHistory.Values.SelectMany(history => history).GroupBy(point => point.Time)
                .Select(group => new TrafficPoint(group.Key, group.Aggregate(TrafficRate.Zero, (sum, point) => sum + point.Rate)))
                .OrderBy(point => point.Time).ToArray();
        _chart.Update(points, snapshot.Timestamp, _span.SelectedIndex switch { 0 => 60, 2 => 300, _ => 120 });
        _coverage.Text = _service.Coverage + "。仅统计启用监测的时段；来源曲线始终包含工具。会话归属只在内存中保存。";
        _error.Text = _exportStatus ?? _service.Error ?? "";
        UpdateDays();
    }
    private void UpdateDays(bool force = false)
    {
        var enabled = _enabledSources?.Invoke();
        var key = enabled is null ? "all" : string.Join(',', enabled.Order());
        if (!force && ReferenceEquals(_lastDays, _service.Days) && key == _lastEnabledKey && _lastHistoryDate == DateTime.Today) return;
        _lastDays = _service.Days;
        _lastEnabledKey = key;
        _lastHistoryDate = DateTime.Today;
        var count = _range.SelectedIndex switch { 0 => 7, 2 => 90, 3 => int.MaxValue, _ => 30 };
        var start = count == int.MaxValue ? DateOnly.MinValue : DateOnly.FromDateTime(DateTime.Today).AddDays(1 - count);
        _visibleDays = TrafficWindowProjection.Days(_service.Days, enabled).Where(row => DateOnly.TryParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date >= start)
            .OrderByDescending(row => row.Day, StringComparer.Ordinal).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
        _days.Children.Clear();
        foreach (var day in _visibleDays)
        {
            var total = day.Total(true);
            var prefix = day.IsMachine ? "" : "≥";
            _days.Children.Add(Row([day.Day, day.IsMachine ? "本机接口" : day.Source?.ToString() ?? day.Key,
                prefix + TrafficFormat.Total(total.Rx), prefix + TrafficFormat.Total(total.Tx), Duration(day.SampledMs),
                day.IsMachine ? "已观测网卡区间" : "应用字节观测（含工具）"], "112,110,*,*,105,1.7*"));
        }
        if (_days.Children.Count == 0) _days.Children.Add(Ui.Label("所选日期没有流量观测历史；不会填补未监测的日期。", 11, Ui.Muted));
    }
    private async Task ExportAsync()
    {
        if (!StorageProvider.CanSave) return;
        UpdateDays(true);
        var rows = _visibleDays;
        try
        {
            var result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
            {
                Title = "导出网络流量观测", SuggestedFileName = "agent-speed-traffic-observed-" + DateTime.Today.ToString("yyyy-MM-dd") + ".csv",
                DefaultExtension = "csv", ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("CSV 文件") { Patterns = ["*.csv"] }],
            });
            if (result.File is not { } file) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await stream.WriteAsync(HistoryCsv.Encode(LinuxTrafficStore.Csv(rows)));
            _exportStatus = "已导出观测历史 · " + file.Name;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _exportStatus = "无法导出流量历史：" + error.Message; }
        Update();
    }
    private static string Rate(double? value, bool lower = false) => value is { } known && double.IsFinite(known)
        ? (lower ? "≥" : "") + TrafficFormat.Rate(known) : "—";
    private static string Duration(long milliseconds)
    {
        var seconds = milliseconds / 1000;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }
    private static Grid Row(string[] values, string columns, bool heading = false)
    {
        var row = new Grid { ColumnDefinitions = new(columns), ColumnSpacing = 10, Margin = new Thickness(0, heading ? 0 : 6) };
        for (var i = 0; i < values.Length; i++)
        {
            var text = Ui.Label(values[i], heading ? 10 : 11, heading ? Ui.Muted : Ui.Text);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            if (i > 1) text.HorizontalAlignment = HorizontalAlignment.Right;
            ToolTip.SetTip(text, values[i]); Ui.Cell(row, text, i);
        }
        return row;
    }
}

/// <summary>Display/export filtering only. Original observed counters and persisted days are never removed.</summary>
public static class TrafficWindowProjection
{
    public static TrafficSnapshot Snapshot(TrafficSnapshot snapshot, IReadOnlySet<SourceKind>? enabled,
        IReadOnlyDictionary<string, SourceKind>? agentSources = null)
    {
        if (enabled is null) return snapshot;
        return snapshot with
        {
            Sources = snapshot.Sources.Where(item => enabled.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value),
            SourceHistory = snapshot.SourceHistory.Where(item => enabled.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value),
            Agents = snapshot.Agents.Where(item => agentSources is not null && agentSources.TryGetValue(item.Key, out var source) && enabled.Contains(source))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            ReportTotals = snapshot.ReportTotals with
            { Sources = snapshot.ReportTotals.Sources.Where(item => enabled.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value) },
        };
    }

    public static IReadOnlyList<TrafficDay> Days(IReadOnlyList<TrafficDay> days, IReadOnlySet<SourceKind>? enabled) => enabled is null ? days
        : days.Where(row => row.IsMachine || row.Source is not { } source || enabled.Contains(source)).ToArray();
}

internal sealed class LinuxTrafficChart : Control
{
    private IReadOnlyList<TrafficPoint> _points = [];
    private DateTimeOffset _end;
    private int _seconds = 120;
    public void Update(IReadOnlyList<TrafficPoint> points, DateTimeOffset end, int seconds)
    {
        _seconds = seconds; _end = end;
        _points = points.Where(point => point.Time >= end.AddSeconds(1 - seconds) && point.Time <= end
            && double.IsFinite(point.Rate.Rx) && double.IsFinite(point.Rate.Tx) && point.Rate.Rx >= 0 && point.Rate.Tx >= 0).ToArray();
        InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 30 || Bounds.Height < 30) return;
        var area = new Rect(0, 22, Bounds.Width, Bounds.Height - 42);
        var maximum = Math.Max(1, _points.Select(point => Math.Max(point.Rate.Rx, point.Rate.Tx)).DefaultIfEmpty().Max() * 1.1);
        for (var i = 0; i < 3; i++)
            context.DrawLine(new Pen(Ui.Line, 1), new(area.Left, area.Top + i * area.Height / 2), new(area.Right, area.Top + i * area.Height / 2));
        Label(context, TrafficFormat.Rate(maximum), new(0, 0));
        Label(context, _points.Count == 0 ? "暂无成对采样" : "↓ 下载", new(0, Bounds.Height - 17));
        Label(context, _points.Count == 0 ? "" : "↑ 上传", new(92, Bounds.Height - 17));
        Draw(context, area, maximum, false, Ui.Accent);
        Draw(context, area, maximum, true, Ui.Codex);
    }
    private void Draw(DrawingContext context, Rect area, double maximum, bool tx, IBrush color)
    {
        TrafficPoint? previous = null; Point last = default;
        foreach (var point in _points)
        {
            var x = area.Left + Math.Clamp((point.Time - _end.AddSeconds(1 - _seconds)).TotalSeconds / (_seconds - 1), 0, 1) * area.Width;
            var y = area.Bottom - Math.Clamp((tx ? point.Rate.Tx : point.Rate.Rx) / maximum, 0, 1) * area.Height;
            var next = new Point(x, y);
            if (previous is { } before && (point.Time - before.Time).TotalSeconds <= 2.5) context.DrawLine(new Pen(color, 1.7), last, next);
            else context.DrawEllipse(color, null, next, 1.8, 1.8);
            previous = point; last = next;
        }
    }
    private static void Label(DrawingContext context, string value, Point point) => context.DrawText(new FormattedText(value,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Noto Sans CJK SC"), 10, Ui.Muted), point);
}
