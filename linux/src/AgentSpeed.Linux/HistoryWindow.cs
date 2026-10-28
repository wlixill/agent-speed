using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.History;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AgentSpeed.Linux;

internal sealed class HistoryWindow : Window
{
    private static readonly HistoryRangeKind[] RangeKinds = [HistoryRangeKind.Today, HistoryRangeKind.Yesterday,
        HistoryRangeKind.Days7, HistoryRangeKind.Days30, HistoryRangeKind.Days90, HistoryRangeKind.Year,
        HistoryRangeKind.All, HistoryRangeKind.Custom];
    private readonly SpeedController _controller;
    private readonly ComboBox _range = new() { ItemsSource = new[] { "今天", "昨天", "最近 7 天", "最近 30 天", "最近 90 天", "今年", "全部历史", "自定义日期" }, SelectedIndex = 3, Width = 145 };
    private readonly ComboBox _metric = new() { Name = "HistoryMetric", ItemsSource = new[] { "输出 token", "输入 token", "请求次数", "API 等值费用", "网络流量（观测量）" }, SelectedIndex = 0, Width = 190 };
    private readonly ComboBox _source = new() { Name = "HistorySource", Width = 180 };
    private sealed record SourceChoice(SourceKind? Kind, string Name) { public override string ToString() => Name; }
    private SourceKind? SelectedSource => (_source.SelectedItem as SourceChoice)?.Kind;
    private string SourceLabel => SelectedSource is { } kind ? LinuxSources.Name(kind) : "全部来源";
    private readonly CheckBox _includeSubscriptions = new() { Name = "HistoryIncludeSubscriptions", Content = "计入包月订阅的 API 等值费用", FontSize = 11 };
    private readonly ComboBox _dailySort = new() { Name = "HistoryDailySort", ItemsSource = new[] { "按日期", "按输出 token", "按输入 token", "按请求数", "按输出均速", "按输出峰值", "按费用", "按流量" }, SelectedIndex = 0, Width = 160 };
    private readonly CheckBox _dailyAscending = new() { Name = "HistoryDailyAscending", Content = "升序", FontSize = 11 };
    private readonly TabControl _tabs = new() { Name = "HistoryDetailTabs", Height = 340 };
    private readonly WrapPanel _legend = new() { ItemSpacing = 10, LineSpacing = 5 };
    private readonly StackPanel _sourceShares = new() { Spacing = 7 };
    private readonly StackPanel _modelShares = new() { Spacing = 7 };
    private readonly TextBlock _shareHint = Ui.Label("", 10, Ui.Muted);
    private readonly TextBlock _trafficNote = Ui.Label("", 10, Ui.Muted);
    private IReadOnlyList<DaySummary> _visibleDays = [];
    private IReadOnlyList<TrafficDay>? _lastTrafficDays;
    private bool _syncSubscriptions;
    private readonly CheckBox _speedLine = new() { Content = "输出平均速度", IsChecked = true, FontSize = 12 };
    private readonly CalendarDatePicker _from = new() { SelectedDate = DateTime.Today.AddDays(-29), Width = 150 };
    private readonly CalendarDatePicker _to = new() { SelectedDate = DateTime.Today, Width = 150 };
    private readonly StackPanel _customRange = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly TextBlock _subtitle = Ui.Label("", 12, Ui.Muted);
    private readonly TextBlock _footer = Ui.Label("正在加载记录…", 11, Ui.Muted);
    private readonly TextBlock _chartHint = Ui.Label("", 11, Ui.Muted);
    private readonly HistoryDailyChart _chart = new() { Height = 245 };
    private readonly TextBlock[] _values = Enumerable.Range(0, 5).Select(_ => Ui.Label("—", 25)).ToArray();
    private readonly TextBlock[] _trends = Enumerable.Range(0, 5).Select(_ => Ui.Label("", 10, Ui.Muted)).ToArray();
    private readonly HistorySparkLine[] _sparks = Enumerable.Range(0, 5).Select(_ => new HistorySparkLine { Height = 29 }).ToArray();
    private readonly StackPanel _dailyRows = new() { Spacing = 3 };
    private readonly StackPanel _modelRows = new() { Spacing = 3 };
    private readonly TextBox _modelFilter = new() { Name = "ModelFilter", PlaceholderText = "筛选模型名称…", Width = 210 };
    private readonly ComboBox _modelSort = new() { Name = "ModelSort", ItemsSource = new[] { "按输出 token", "按输入 token", "按缓存读取", "按请求次数", "按输出平均速度", "按 API 等值费用", "按模型名称" }, SelectedIndex = 0, Width = 175 };
    private readonly CheckBox _modelAscending = new() { Name = "ModelSortAscending", Content = "升序", FontSize = 11 };
    private readonly TextBlock _modelCount = Ui.Label("", 10, Ui.Muted);
    private readonly ComboBox _modeKind = new() { Name = "HistoryModeKind", ItemsSource = ExecutionModePresentation.Kinds.Select(ExecutionModePresentation.Kind).ToArray(), SelectedIndex = 0, Width = 160 };
    private readonly TextBlock _modeCount = Ui.Label("", 10, Ui.Muted);
    private readonly TextBlock _modeConfiguration = Ui.Label("", 10, Ui.Muted);
    private readonly StackPanel _modeRows = new() { Spacing = 3 };
    private IReadOnlyList<DaySummary> _modeDays = [];
    private IReadOnlyList<ExecutionModeUsage> _visibleModes = [];
    private IReadOnlyList<ModelStatistic> _models = [];
    private IReadOnlyList<ModelStatistic> _visibleModels = [];
    private IReadOnlyList<DailyUsage>? _lastUsage;
    private DateOnly _lastDate;
    private long _lastMoneyRevision = -1;
    private HistoryPeriod? _period;
    private bool _changingRange;
    private string? _exportStatus;

    public int VisibleDayCount => _period?.Days.Count ?? 0;
    public int VisibleModelCount => _visibleModels.Count;
    public int VisibleModeRowCount => _visibleModes.Count;
    public string ExecutionModesSummary => string.Join("; ", _visibleModes.Select(row => $"{ExecutionModes.Name(row.Kind)}/{row.Mode}/{row.Evidence}: requests={row.Requests}, output={row.Output}"));
    public string ModelStatisticsSummary => string.Join("; ", _visibleModels.Select(model => $"{model.Model}: input={model.Input?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, cache={model.CachedInput?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}, output={model.Output}, requests={model.Requests}"));
    public string StatisticsSummary => _period is { } period
        ? $"{period.Range.Start:yyyy-MM-dd}..{period.Range.End:yyyy-MM-dd}; {SourceLabel}; output={period.Totals.Output}; input={period.Totals.Input}; requests={period.Totals.Requests}; weighted tok/s={UsageProjection.Rate(period.Totals.AverageSpeed)}; API={_controller.FormatHistoryMoney(period.Totals.Cost, period.Totals.HasData)}"
        : "未加载";

    public HistoryWindow(SpeedController controller)
    {
        _controller = controller;
        Title = "Agent Speed · Codex 用量历史";
        Width = 1080;
        Height = 820;
        MinWidth = 900;
        MinHeight = 690;
        Background = Ui.Background;
        FontFamily = new FontFamily("Noto Sans CJK SC");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new Grid { RowDefinitions = new("Auto,*,Auto"), RowSpacing = 16, Margin = new Thickness(24) };
        var header = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 10 };
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(Ui.Label("用量历史", 27));
        title.Children.Add(_subtitle);
        Ui.Cell(header, title, 0);
        Ui.Cell(header, Ui.Button("刷新记录", () => controller.History.Refresh()), 1);
        Ui.Cell(header, Ui.Button("导出 CSV…", async () => await ExportAsync()), 2);
        Ui.Cell(root, header, 0, 0);

        var content = new StackPanel { Spacing = 17 };
        var filter = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 10 };
        _source.ItemsSource = new[] { new SourceChoice(null, "全部来源") }.Concat(Enum.GetValues<SourceKind>().Select(kind => new SourceChoice(kind, LinuxSources.Name(kind)))).ToArray();
        var enabled = LinuxSources.Enabled(controller.Settings);
        _source.SelectedIndex = enabled.Length == 1 ? Array.IndexOf(Enum.GetValues<SourceKind>(), enabled[0]) + 1 : 0;
        filter.Children.Add(_source);
        filter.Children.Add(_range);
        _customRange.Children.Add(_from);
        _customRange.Children.Add(Ui.Label("至", 11, Ui.Muted));
        _customRange.Children.Add(_to);
        filter.Children.Add(_customRange);
        content.Children.Add(filter);
        content.Children.Add(_includeSubscriptions);

        var cards = new Grid { ColumnDefinitions = new("*,*,*,*,*"), ColumnSpacing = 10 };
        string[] names = ["输出 token", "输入 token（含缓存）", "请求次数", "输出平均 · tok/s", "API 等值费用"];
        for (var i = 0; i < 5; i++)
        {
            var card = new StackPanel { Spacing = 8 };
            if (i == 4) _values[i].FontSize = 20;
            _values[i].TextTrimming = TextTrimming.CharacterEllipsis;
            card.Children.Add(Ui.Label(names[i], 11, Ui.Muted));
            card.Children.Add(_values[i]);
            card.Children.Add(_sparks[i]);
            card.Children.Add(_trends[i]);
            var border = Ui.Card(card, new Thickness(15));
            if (i == 4) ToolTip.SetTip(border, "按记录中的模型与本地价格表估算 API 等值费用，不代表订阅实际账单。\n未知价格显示 —，部分已知显示 ≥。");
            Ui.Cell(cards, border, i);
        }
        content.Children.Add(cards);

        var chart = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto"), RowSpacing = 14 };
        var chartHeader = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 18 };
        Ui.Cell(chartHeader, Ui.Label("每日用量", 16), 0);
        Ui.Cell(chartHeader, _metric, 1);
        Ui.Cell(chartHeader, _speedLine, 2);
        Ui.Cell(chart, chartHeader, 0, 0);
        Ui.Cell(chart, _legend, 0, 1);
        Ui.Cell(chart, _chart, 0, 2);
        Ui.Cell(chart, _chartHint, 0, 3);
        content.Children.Add(Ui.Card(chart));

        var daily = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var dailyControls = new WrapPanel { ItemSpacing = 10, LineSpacing = 7 };
        dailyControls.Children.Add(_dailySort); dailyControls.Children.Add(_dailyAscending);
        var copy = Ui.Button("复制当前明细", async () => await CopyDailyAsync()); copy.Name = "HistoryCopyDaily"; dailyControls.Children.Add(copy);
        dailyControls.Children.Add(Ui.Label("点击日期查看来源／模型；右键可复制该日", 10, Ui.Muted));
        Ui.Cell(daily, dailyControls, 0, 0);
        Ui.Cell(daily, TableRow(["日期", "输出 token", "输入 token", "请求", "输出均速", "输出峰值", "API 等值", "流量 ↓／↑"], true), 0, 1);
        Ui.Cell(daily, new ScrollViewer { Content = _dailyRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0, 2);
        var models = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 10, Margin = new Thickness(0, 12, 0, 0) };
        var modelControls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 7 };
        modelControls.Children.Add(_modelFilter);
        modelControls.Children.Add(_modelSort);
        modelControls.Children.Add(_modelAscending);
        modelControls.Children.Add(Ui.Button("导出模型 CSV…", async () => await ExportAsync(models: true)));
        modelControls.Children.Add(_modelCount);
        Ui.Cell(models, modelControls, 0, 0);
        var modelHint = Ui.Label("输入包含缓存读取；旧缓存缺少模型输入且原日志已移除时显示 —。均速为实测输出 token ÷ 实测时长。", 10, Ui.Muted);
        modelHint.TextWrapping = TextWrapping.Wrap;
        Ui.Cell(models, modelHint, 0, 1);
        var modelTable = new Grid { MinWidth = 820, RowDefinitions = new("Auto,*"), RowSpacing = 7 };
        Ui.Cell(modelTable, ModelRow(["模型", "输入 token", "缓存读取", "输出 token", "请求", "输出均速 tok/s", "API 等值"], true), 0, 0);
        Ui.Cell(modelTable, new ScrollViewer { Content = _modelRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0, 1);
        Ui.Cell(models, new ScrollViewer { Content = modelTable, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0, 2);
        _tabs.Items.Add(new TabItem { Header = "每日明细", Content = daily });
        _tabs.Items.Add(new TabItem { Header = "模型统计", Content = models });
        var modes = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 9, Margin = new Thickness(0, 12, 0, 0) };
        var modeControls = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 7 };
        modeControls.Children.Add(_modeKind);
        modeControls.Children.Add(Ui.Button("导出当前维度 CSV…", async () => await ExportAsync(modes: true)));
        modeControls.Children.Add(_modeCount);
        Ui.Cell(modes, modeControls, 0, 0);
        _modeConfiguration.TextWrapping = TextWrapping.Wrap;
        ToolTip.SetTip(_modeConfiguration, "配置默认与任务选择、Agent 记录、实际响应是不同证据。配置 Fast 不等于每次请求实际以 Fast 服务；未提供的历史仍归入未知。");
        Ui.Cell(modes, _modeConfiguration, 0, 1);
        var modeTable = new Grid { MinWidth = 820, RowDefinitions = new("Auto,*"), RowSpacing = 7 };
        Ui.Cell(modeTable, ModeRow(["模型", "模式", "证据", "提供方", "请求", "输出 token", "输出均速 tok/s"], true), 0, 0);
        Ui.Cell(modeTable, new ScrollViewer { Content = _modeRows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0, 1);
        Ui.Cell(modes, new ScrollViewer { Content = modeTable, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0, 2);
        _tabs.Items.Add(new TabItem { Header = "执行模式", Content = modes });
        var shares = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,*"), ColumnSpacing = 20, RowSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        _shareHint.TextWrapping = TextWrapping.Wrap;
        Ui.Cell(shares, _shareHint, 0, 0); Grid.SetColumnSpan(_shareHint, 2);
        Ui.Cell(shares, new ScrollViewer { Content = _sourceShares }, 0, 1);
        Ui.Cell(shares, new ScrollViewer { Content = _modelShares }, 1, 1);
        _tabs.Items.Add(new TabItem { Header = "来源与占比", Content = shares });
        content.Children.Add(Ui.Card(_tabs, new Thickness(17, 7, 17, 12)));
        _trafficNote.TextWrapping = TextWrapping.Wrap; content.Children.Add(_trafficNote);
        Ui.Cell(root, new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, 0, 1);
        _footer.TextWrapping = TextWrapping.Wrap;
        Ui.Cell(root, _footer, 0, 2);
        Content = root;

        _range.SelectionChanged += (_, _) => { _customRange.IsVisible = _range.SelectedIndex == 7; if (!_changingRange) Update(true); };
        _source.SelectionChanged += (_, _) => { _exportStatus = null; Update(true); };
        _from.SelectedDateChanged += (_, _) => { if (!_changingRange) Update(true); };
        _to.SelectedDateChanged += (_, _) => { if (!_changingRange) Update(true); };
        _metric.SelectionChanged += (_, _) => { UpdateChart(); UpdateShares(); };
        _dailySort.SelectionChanged += (_, _) => UpdateDaily();
        _dailyAscending.IsCheckedChanged += (_, _) => UpdateDaily();
        _includeSubscriptions.IsCheckedChanged += (_, _) => { if (!_syncSubscriptions) controller.SetHistoryIncludeSubscriptions(_includeSubscriptions.IsChecked == true); };
        _speedLine.IsCheckedChanged += (_, _) => UpdateChart();
        _modelFilter.TextChanged += (_, _) => UpdateModels();
        _modelSort.SelectionChanged += (_, _) => UpdateModels();
        _modelAscending.IsCheckedChanged += (_, _) => UpdateModels();
        _modeKind.SelectionChanged += (_, _) => UpdateModes();
        _chart.DaySelected += bucket =>
        {
            _changingRange = true;
            _from.SelectedDate = bucket.Start.ToDateTime(TimeOnly.MinValue);
            _to.SelectedDate = bucket.End.ToDateTime(TimeOnly.MinValue);
            _range.SelectedIndex = 7;
            _changingRange = false;
            Update(true);
        };
        _chart.SourceDaySelected += (_, source) => { if (source is { } kind) OpenSourceModels(kind); else _tabs.SelectedIndex = 3; };
        controller.Changed += Changed;
        Opened += (_, _) => { Update(true); controller.History.Refresh(); };
        Closed += (_, _) => controller.Changed -= Changed;
        Update(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible && _controller is not null) Update(true);
    }

    private void Changed() { if (IsVisible) Update(); }

    private void Update(bool force = false)
    {
        var state = _controller.History.State;
        _footer.Text = _exportStatus ?? state.Kind switch
        {
            HistoryStateKind.Analyzing => $"正在分析本地记录 {_controller.History.State.FilesDone:N0} / {_controller.History.State.FilesTotal:N0} · 当前显示已完成的汇总",
            HistoryStateKind.Ready => $"{SourceLabel} · 按本机日期汇总 · 今日记录每 30 秒更新 · 更新于 {state.UpdatedAt?.ToLocalTime():HH:mm:ss}",
            HistoryStateKind.Failed => "历史分析失败：" + state.Error,
            _ => "正在加载本地历史记录…",
        };
        _modeConfiguration.Text = "当前" + ExecutionModePresentation.ConfiguredSource(_controller.Speed.Snapshot, SelectedSource)
            + " · 配置不代表实际响应；未知会单独列出。";
        var costSettings = _controller.Settings.CostSettings;
        _syncSubscriptions = true;
        _includeSubscriptions.IsChecked = costSettings.HistoryIncludeSubscriptions ?? costSettings.IncludeSubscriptions;
        _includeSubscriptions.IsVisible = !costSettings.Subscriptions.IsEmpty && SelectedSource is null;
        _syncSubscriptions = false;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (!force && ReferenceEquals(_lastUsage, _controller.History.Days) && today == _lastDate
            && _lastMoneyRevision == _controller.MoneyRevision && ReferenceEquals(_lastTrafficDays, _controller.Traffic.Days)) return;
        _lastUsage = _controller.History.Days;
        _lastDate = today;
        _lastMoneyRevision = _controller.MoneyRevision;
        _lastTrafficDays = _controller.Traffic.Days;
        DayRange? custom = null;
        if (_range.SelectedIndex == 7)
        {
            if (_from.SelectedDate is not { } from || _to.SelectedDate is not { } to || from.Date > to.Date)
            {
                _period = null;
                _models = []; _modeDays = [];
                UpdateModels(); UpdateModes();
                _dailyRows.Children.Clear(); _visibleDays = []; _sourceShares.Children.Clear(); _modelShares.Children.Clear(); _legend.Children.Clear();
                foreach (var value in _values) value.Text = "—";
                foreach (var trend in _trends) trend.Text = "";
                foreach (var spark in _sparks) spark.Update([]);
                _chart.IsVisible = false;
                _subtitle.Text = SourceLabel + " · 日期范围无效";
                _shareHint.Text = "请选择有效日期范围后查看来源与模型占比。";
                _chartHint.Text = "请先选择有效日期范围。";
                _footer.Text = "请选择有效的起止日期，开始日期不能晚于结束日期。";
                return;
            }
            custom = new(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to));
        }
        var excluded = new HistoryCostOptions(costSettings.Subscriptions, _includeSubscriptions.IsChecked == true).Excluded(SelectedSource);
        var trafficRows = TrafficWindowProjection.Days(_controller.Traffic.Days, LinuxSources.Enabled(_controller.Settings).ToHashSet());
        _period = HistoryStatistics.Build(_controller.History.Days, today, RangeKinds[Math.Clamp(_range.SelectedIndex, 0, RangeKinds.Length - 1)],
            custom, SelectedSource, excluded, trafficRows, _controller.Traffic.Since, _controller.Settings.TrafficIncludeTools);
        _chart.IsVisible = true;
        var period = _period;
        Title = "Agent Speed · " + SourceLabel + " 用量历史";
        _subtitle.Text = $"{SourceLabel} · {period.Range.Start:yyyy/MM/dd} — {period.Range.End:yyyy/MM/dd} · {period.Range.DayCount:N0} 天";
        var total = period.Totals;
        var previous = period.Previous;
        double?[] current = [total.HasData ? total.Output : null, total.HasData ? total.Input : null, total.HasData ? total.Requests : null,
            total.AverageSpeed, total.HasData ? total.Cost.SortValue : null];
        double?[] before = [previous.HasData ? previous.Output : null, previous.HasData ? previous.Input : null, previous.HasData ? previous.Requests : null,
            previous.AverageSpeed, previous.HasData ? previous.Cost.SortValue : null];
        _values[0].Text = total.HasData ? UsageProjection.Compact(total.Output) : "—";
        _values[1].Text = total.HasData ? UsageProjection.Compact(total.Input) : "—";
        _values[2].Text = total.HasData ? UsageProjection.Compact(total.Requests) : "—";
        _values[3].Text = UsageProjection.Rate(total.AverageSpeed);
        _values[4].Text = _controller.FormatHistoryMoney(total.Cost, total.HasData);
        ToolTip.SetTip(_values[4], _values[4].Text);
        var buckets = HistoryStatistics.Buckets(period, 90);
        for (var i = 0; i < 5; i++)
        {
            _trends[i].Text = HistoryStatistics.Trend(current[i], before[i]);
            var metric = i;
            _sparks[i].Update(buckets.Select(bucket => metric switch
            {
                0 => bucket.Value(0), 1 => bucket.Value(1), 2 => bucket.Value(2),
                3 => bucket.Totals.AverageSpeed, _ => bucket.Value(3),
            }).ToArray());
        }
        UpdateDaily();
        _models = ModelStatistics.Build(_controller.History.Days, period.Range, SelectedSource);
        UpdateModels();
        _modeDays = ExecutionModePresentation.HistoryDays(_controller.History.Days, period.Range, SelectedSource);
        UpdateModes();
        UpdateChart(); UpdateShares();
        _trafficNote.Text = period.TrafficMeasured ? "流量只代表实际采样的时段，未采集日期保留为空。" + _controller.Traffic.Coverage : "网络流量尚无已采集历史，可在设置中启用。";
    }

    private void UpdateChart()
    {
        if (_period is not { } period) return;
        _speedLine.IsVisible = _metric.SelectedIndex != 4;
        var conversion = MoneyPresentation.ConversionRate(_controller.Cost.Currency, _controller.Cost.Rates, _controller.Cost.CurrencyTable);
        _chart.MoneyFactor = (double)(conversion ?? 1m);
        _chart.MoneyCurrency = conversion is null ? "USD" : _controller.Settings.Currency;
        _chart.MoneyFormatter = cost => _controller.FormatHistoryMoney(cost);
        _chart.Update(period, _metric.SelectedIndex, _speedLine.IsChecked == true);
        _legend.Children.Clear();
        foreach (var source in period.SourceDays.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key))
        {
            var button = Ui.Button("● " + LinuxSources.Name(source), () => OpenSourceModels(source));
            button.Foreground = HistoryPalette.Brush(source); ToolTip.SetTip(button, "查看该来源在当前日期范围的模型"); _legend.Children.Add(button);
        }
        _chartHint.Text = period.Days.Count == 0 ? "所选范围暂无记录，未知值不会显示为零。"
            : _metric.SelectedIndex == 4 ? "颜色为 Agent 来源；上方下载、下方上传，外框为本机接口。点击查看该日来源。"
            : $"{SourceLabel} · {period.Totals.ActiveDays:N0} 个有用量日期 · 点击颜色查看该日来源模型 · 峰值仅来自实际监测";
    }

    private void OpenSourceModels(SourceKind source)
    {
        _source.SelectedIndex = Array.IndexOf(Enum.GetValues<SourceKind>(), source) + 1;
        _tabs.SelectedIndex = 1;
    }

    private void SelectDay(DateOnly day)
    {
        _changingRange = true;
        _from.SelectedDate = day.ToDateTime(TimeOnly.MinValue); _to.SelectedDate = day.ToDateTime(TimeOnly.MinValue);
        _range.SelectedIndex = 7; _changingRange = false;
        Update(true); _tabs.SelectedIndex = 3;
    }

    private void UpdateDaily()
    {
        _dailyRows.Children.Clear();
        _visibleDays = _period is { } period ? HistoryStatistics.SortDays(period.Days,
            (HistoryDaySort)Math.Clamp(_dailySort.SelectedIndex, 0, 7), _dailyAscending.IsChecked == true) : [];
        foreach (var day in _visibleDays)
        {
            var row = TableRow([day.Day.ToString("yyyy-MM-dd"), UsageProjection.Number(day.Output), UsageProjection.Number(day.Input),
                UsageProjection.Number(day.Requests), UsageProjection.Rate(day.AverageSpeed), UsageProjection.Rate(day.Peak),
                _controller.FormatHistoryMoney(day.Cost), day.Traffic is { } traffic
                    ? $"↓{TrafficFormat.Total(traffic.Shown.Rx)} ↑{TrafficFormat.Total(traffic.Shown.Tx)}" : "—"]);
            row.Children.RemoveAt(0);
            var button = Ui.Button(day.Day.ToString("yyyy-MM-dd"), () => SelectDay(day.Day));
            button.Padding = new Thickness(2); ToolTip.SetTip(button, "查看该日来源占比，再选择来源查看模型"); Ui.Cell(row, button, 0);
            var menu = new ContextMenu();
            var detail = new MenuItem { Header = "查看该日来源与模型" }; detail.Click += (_, _) => SelectDay(day.Day);
            var copy = new MenuItem { Header = "复制该日明细" }; copy.Click += async (_, _) => await CopyDailyAsync(day);
            menu.Items.Add(detail); menu.Items.Add(copy); row.ContextMenu = menu;
            if (day.Traffic is { } observed) ToolTip.SetTip(row, $"已采样 {observed.RecordedMs / 1000:N0} 秒" + (observed.Partial ? " · 非全天覆盖" : ""));
            _dailyRows.Children.Add(row);
        }
        if (_visibleDays.Count == 0) _dailyRows.Children.Add(Ui.Label(_controller.History.State.Kind == HistoryStateKind.NotLoaded
            ? "正在加载记录…" : "所选日期范围内没有用量或已采集流量。", 12, Ui.Muted));
    }

    private HistoryMoney ExportMoney() => new(new CostDisplay(new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans"),
        _controller.Cost.CurrencyTable, _controller.Cost.Rates) { Settings = _controller.Settings.CostSettings });

    private async Task CopyDailyAsync(DaySummary? selected = null)
    {
        if (_period is not { } period) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new NotSupportedException("当前桌面没有可用剪贴板。");
            var csv = HistoryExports.Daily(selected is { } day ? [day] : _visibleDays, SelectedSource, ExportMoney(), period.TrafficMeasured);
            await clipboard.SetTextAsync(csv);
            _exportStatus = selected is { } one ? $"已复制 {one.Day:yyyy-MM-dd} 明细。" : $"已复制 {_visibleDays.Count:N0} 日明细。";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or NotSupportedException)
        { _exportStatus = "复制失败：" + error.Message; }
        Update();
    }

    private string FormatModelCost(ModelStatistic model)
    {
        var settings = _controller.Settings.CostSettings;
        return SelectedSource is null && _includeSubscriptions.IsChecked != true && model.Source is { } source && settings.Subscriptions.Contains(source)
            ? "订阅（已排除）" : _controller.FormatHistoryMoney(model.Cost);
    }

    private void UpdateShares()
    {
        _sourceShares.Children.Clear(); _modelShares.Children.Clear();
        if (_period is not { } period) return;
        var metric = Math.Clamp(_metric.SelectedIndex, 0, 4);
        var name = metric switch { 1 => "输入 token", 2 => "请求数", 3 => "已计入的 API 等值费用", 4 => "Agent 已观测流量", _ => "输出 token" };
        _shareHint.Text = $"{period.Range.Start:yyyy-MM-dd} — {period.Range.End:yyyy-MM-dd} · {name}占比。输入／费用未知的部分不参与分母，原始用量仍保留。";
        _sourceShares.Children.Add(Ui.Label("来源 · 点击查看模型", 12));
        string Value(double value) => metric == 4 ? TrafficFormat.Total((long)value)
            : metric == 3 ? _controller.Money.AmountText((long)Math.Round(value * 1_000_000)) : UsageProjection.Number((long)value);
        foreach (var row in HistoryStatistics.Shares(period, metric))
        {
            var button = Ui.Button($"● {LinuxSources.Name(row.Source)}  {Value(row.Value)}  ({row.Fraction:P1})", () => OpenSourceModels(row.Source));
            button.Foreground = HistoryPalette.Brush(row.Source); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            _sourceShares.Children.Add(button);
        }
        if (_sourceShares.Children.Count == 1) _sourceShares.Children.Add(Ui.Label("当前指标没有可计算的来源占比。", 11, Ui.Muted));
        _modelShares.Children.Add(Ui.Label("模型 · 已知部分占比", 12));
        if (metric == 4) { _modelShares.Children.Add(Ui.Label("网络流量不能按模型可靠归属。", 11, Ui.Muted)); return; }
        var excluded = new HistoryCostOptions(_controller.Settings.CostSettings.Subscriptions, _includeSubscriptions.IsChecked == true).Excluded(SelectedSource);
        var modelShares = HistoryStatistics.ModelShares(_models.Where(model => metric != 3 || model.Source is not { } source || !excluded.Contains(source)), metric);
        foreach (var row in modelShares)
        {
            var card = new StackPanel { Spacing = 3 };
            var label = Ui.Label($"{(string.IsNullOrWhiteSpace(row.Model) ? "未知模型" : row.Model)} · {LinuxSources.Name(row.Source)}  {Value(row.Value)} ({row.Fraction:P1})", 10);
            label.TextTrimming = TextTrimming.CharacterEllipsis; ToolTip.SetTip(label, label.Text); card.Children.Add(label);
            card.Children.Add(new ProgressBar { Minimum = 0, Maximum = 1, Value = row.Fraction, Height = 4, Foreground = HistoryPalette.Brush(row.Source) });
            _modelShares.Children.Add(card);
        }
        if (modelShares.Count == 0) _modelShares.Children.Add(Ui.Label("暂无可计算的模型占比。", 11, Ui.Muted));
    }

    private void UpdateModels()
    {
        _modelRows.Children.Clear();
        _visibleModels = ModelStatistics.FilterAndSort(_models, _modelFilter.Text,
            (ModelStatisticSort)Math.Clamp(_modelSort.SelectedIndex, 0, 6), _modelAscending.IsChecked == true);
        _modelCount.Text = $"{_visibleModels.Count:N0} / {_models.Count:N0} 个模型";
        foreach (var model in _visibleModels)
        {
            _modelRows.Children.Add(ModelRow([(string.IsNullOrWhiteSpace(model.Model) ? "未知模型" : model.Model) + (SelectedSource is null && model.Source is { } kind ? " · " + LinuxSources.Name(kind) : ""),
                model.Input is { } input ? UsageProjection.Number(input) : "—",
                model.CachedInput is { } cached ? UsageProjection.Number(cached) : "—",
                UsageProjection.Number(model.Output), UsageProjection.Number(model.Requests),
                UsageProjection.Rate(model.AverageSpeed), FormatModelCost(model)]));
        }
        if (_modelRows.Children.Count == 0) _modelRows.Children.Add(Ui.Label(_models.Count == 0 ? "所选范围暂无模型记录。" : "没有匹配的模型。", 12, Ui.Muted));
    }

    private void UpdateModes()
    {
        _modeRows.Children.Clear();
        var kind = ExecutionModePresentation.Kinds[Math.Clamp(_modeKind.SelectedIndex, 0, ExecutionModePresentation.Kinds.Count - 1)];
        _visibleModes = ExecutionModePresentation.Rows(_modeDays, kind, SelectedSource);
        var range = _period is { } period ? $"{period.Range.Start:MM-dd} — {period.Range.End:MM-dd} · " : "";
        _modeCount.Text = range + $"{_visibleModes.Sum(row => row.Requests):N0} 请求 · 各维度独立统计";
        foreach (var row in _visibleModes)
            _modeRows.Children.Add(ModeRow([(string.IsNullOrWhiteSpace(row.Model) ? "未知模型" : row.Model) + (SelectedSource is null ? " · " + LinuxSources.Name(row.Source) : ""),
                ExecutionModePresentation.Value(row.Mode), ExecutionModePresentation.Evidence(row.Evidence), row.Provider ?? "—",
                UsageProjection.Number(row.Requests), UsageProjection.Number(row.Output), UsageProjection.Rate(row.AverageSpeed)]));
        if (_visibleModes.Count == 0) _modeRows.Children.Add(Ui.Label("所选范围没有可归属的请求；配置默认不作为历史实际模式。", 11, Ui.Muted));
    }

    private static Grid TableRow(string[] values, bool heading = false) => Row(values, "112,*,*,60,80,80,110,150", heading);
    private static Grid ModelRow(string[] values, bool heading = false) => Row(values, "2*,1.1*,1.1*,1.1*,.7*,1.2*,1.4*", heading);
    private static Grid ModeRow(string[] values, bool heading = false) => Row(values, "1.8*,1.1*,1.5*,1*,.7*,1.2*,1.3*", heading);
    private static Grid Row(string[] values, string columns, bool heading)
    {
        var grid = new Grid { ColumnDefinitions = new(columns), ColumnSpacing = 10, Margin = new Thickness(0, heading ? 0 : 7) };
        for (var i = 0; i < values.Length; i++)
        {
            var value = Ui.Label(values[i], heading ? 10 : 11, heading ? Ui.Muted : Ui.Text);
            value.TextTrimming = TextTrimming.CharacterEllipsis;
            if (i > 0) value.HorizontalAlignment = HorizontalAlignment.Right;
            ToolTip.SetTip(value, values[i]);
            Ui.Cell(grid, value, i);
        }
        return grid;
    }

    private async Task ExportAsync(bool models = false, bool modes = false)
    {
        if (_period is not { } period || !StorageProvider.CanSave) return;
        // Capture the same range/filter snapshot as the rows visible when export was clicked.
        var modelRows = _visibleModels;
        var dailyRows = _visibleDays;
        var money = ExportMoney();
        var traffic = period.TrafficMeasured;
        var exportSource = SelectedSource;
        var modeDays = _modeDays;
        var modeKind = ExecutionModePresentation.Kinds[Math.Clamp(_modeKind.SelectedIndex, 0, ExecutionModePresentation.Kinds.Count - 1)];
        try
        {
            var result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
            {
                Title = modes ? "导出 " + SourceLabel + " " + ExecutionModePresentation.Kind(modeKind) : models ? "导出 " + SourceLabel + " 模型统计" : "导出 " + SourceLabel + " 用量历史",
                SuggestedFileName = modes ? ExecutionModeCsv.FileName(period.Range, exportSource) : models ? ModelStatistics.FileName(period.Range, exportSource) : HistoryCsv.FileName(period.Range, exportSource),
                DefaultExtension = "csv", ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("CSV 文件") { Patterns = ["*.csv"], MimeTypes = ["text/csv"] }],
            });
            if (result.File is not { } file) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            await stream.WriteAsync(HistoryCsv.Encode(modes ? ExecutionModePresentation.Csv(modeDays, modeKind, exportSource)
                : models ? ModelStatistics.Csv(modelRows, period.Range, exportSource, money) : HistoryExports.Daily(dailyRows, exportSource, money, traffic)));
            _exportStatus = (modes ? "模式 CSV 已导出（当前维度） · " : models ? "模型 CSV 已导出（当前筛选） · " : "CSV 已导出 · ") + file.Name;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _exportStatus = "CSV 导出失败：" + exception.Message;
        }
        Update();
    }
}
