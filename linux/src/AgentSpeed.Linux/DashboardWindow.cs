using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.History;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentSpeed.Linux;

/// <summary>The compact live dashboard; daily analytics have their own full-size history window.</summary>
internal sealed class DashboardWindow : Window
{
    private readonly SpeedController _controller;
    private readonly GlowingGauge _gauge = new() { Width = 156, Height = 156 };
    private readonly LiveSpeedChart _chart = new() { Height = 148 };
    private readonly TextBlock _speed = Ui.Label("—", 48);
    private readonly TextBlock _unit = Ui.Label("tok/s", 13, Ui.Muted);
    private readonly TextBlock _title = Ui.Label("待机", 18, Ui.Accent);
    private readonly TextBlock _readingKind = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _state = Ui.Label("正在读取记录…", 11, Ui.Muted);
    private readonly TextBlock _lastMeasurement = Ui.Label("", 10, Ui.Muted);
    private readonly TextBlock _dataStatus = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _chartNote = Ui.Label("", 10, Ui.Muted);
    private readonly ComboBox _metric = new() { Name = "DashboardMetric", ItemsSource = new[] { "输出速度", "输入＋输出吞吐量" }, MinWidth = 150, FontSize = 12 };
    private readonly StackPanel _sources = new() { Spacing = 8 };
    private readonly TextBlock _traffic = Ui.Label("网络流量尚未启用", 11, Ui.Muted);
    private readonly TextBlock _privacy = Ui.Label("", 10, Ui.Muted);
    private readonly TextBlock _output = Ui.Label("—", 22);
    private readonly TextBlock _input = Ui.Label("—", 22);
    private readonly TextBlock _requests = Ui.Label("—", 22);
    private readonly TextBlock _average = Ui.Label("—", 22);
    private readonly TextBlock _peak = Ui.Label("—", 12, Ui.Peak);
    private readonly TextBlock _cost = Ui.Label("—", 12, Ui.Muted);
    private readonly TextBlock _liveCost = Ui.Label("—", 12, Ui.Muted);
    private readonly TextBlock _executionModes = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _configuredModes = Ui.Label("", 10, Ui.Muted);
    private readonly TextBlock _warning = Ui.Label("", 11, Ui.Warning);
    private readonly TextBlock _agentsCount = Ui.Label("", 11, Ui.Muted);
    private readonly StackPanel _agents = new() { Spacing = 8 };
    private string _agentKey = "";

    public DashboardWindow(SpeedController controller, Action history, Action settings, Action toggleOrb, Action quit)
    {
        _controller = controller;
        Title = "Agent Speed · 实时仪表";
        Width = 500;
        Height = 860;
        MinWidth = 450;
        MinHeight = 660;
        Background = Ui.Background;
        Foreground = Ui.Text;
        FontFamily = new FontFamily("Noto Sans CJK SC");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _speed.FontWeight = FontWeight.Bold;
        var sections = new StackPanel { Spacing = 18, Margin = new Thickness(22, 18, 22, 14) };
        var header = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        var actions = new ContextMenu();
        Ui.Cell(header, Ui.Label("Agent Speed", 18), 0);
        Ui.Cell(header, Ui.Button("用量历史", history), 1);
        Ui.Cell(header, Ui.Button("设置", settings), 2);
        Ui.Cell(header, Ui.Button("⋯", () => ToggleMenu()), 3);
        foreach (var (label, action) in new (string, Action)[] { ("显示 / 隐藏主悬浮窗", toggleOrb), ("工具来源与路径", controller.ShowSources), ("管理悬浮窗与主题", controller.ShowOrbManagement), ("网络流量", controller.ShowTraffic), ("检查应用更新", controller.ShowUpdates), ("上报、头像与设备", controller.ShowReporting), ("用量历史", history), ("设置", settings), ("退出", quit) })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            actions.Items.Add(item);
        }
        header.ContextMenu = actions;
        void ToggleMenu() => actions.Open(header);
        sections.Children.Add(header);
        _metric.SelectedIndex = controller.Settings.Metric == SpeedMetric.Total ? 1 : 0;
        _metric.SelectionChanged += (_, _) =>
        {
            var selected = _metric.SelectedIndex == 1 ? SpeedMetric.Total : SpeedMetric.Output;
            if (selected != controller.Settings.Metric) controller.SetMetric(selected);
        };
        sections.Children.Add(_metric);

        var hero = new Grid { ColumnDefinitions = new("156,*"), ColumnSpacing = 18, MinHeight = 164 };
        Ui.Cell(hero, _gauge, 0);
        var reading = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var value = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        value.Children.Add(_speed);
        var unit = _unit;
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        unit.Margin = new Thickness(0, 0, 0, 8);
        value.Children.Add(unit);
        reading.Children.Add(value);
        reading.Children.Add(_title);
        reading.Children.Add(_readingKind);
        _state.TextWrapping = TextWrapping.Wrap;
        reading.Children.Add(_state);
        _lastMeasurement.TextWrapping = TextWrapping.Wrap;
        reading.Children.Add(_lastMeasurement);
        Ui.Cell(hero, reading, 1);
        sections.Children.Add(hero);

        var chartHeader = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        Ui.Cell(chartHeader, Ui.Label("实时速度", 12, Ui.Muted), 0);
        var span = new ComboBox { ItemsSource = new[] { "1 分钟", "2 分钟", "5 分钟" }, SelectedIndex = 1, MinWidth = 96, FontSize = 11 };
        span.SelectionChanged += (_, _) => { _chart.WindowSeconds = span.SelectedIndex switch { 0 => 60, 2 => 300, _ => 120 }; _chart.Update(controller.DisplaySnapshot); };
        Ui.Cell(chartHeader, span, 1);
        var chartSection = new StackPanel { Spacing = 6 };
        chartSection.Children.Add(chartHeader);
        chartSection.Children.Add(_chart);
        _chartNote.TextWrapping = TextWrapping.Wrap;
        chartSection.Children.Add(_chartNote);
        sections.Children.Add(chartSection);
        sections.Children.Add(Divider());

        sections.Children.Add(_sources);
        _executionModes.TextWrapping = _configuredModes.TextWrapping = TextWrapping.Wrap;
        sections.Children.Add(_executionModes);
        sections.Children.Add(_configuredModes);
        var agentHeader = new Grid { ColumnDefinitions = new("*,Auto") };
        Ui.Cell(agentHeader, Ui.Label("AGENT · 请求速率（含估算）", 11, Ui.Muted), 0);
        Ui.Cell(agentHeader, _agentsCount, 1);
        sections.Children.Add(agentHeader);
        sections.Children.Add(_agents);
        sections.Children.Add(Divider());

        sections.Children.Add(Ui.Label("今天", 13));
        var today = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,Auto"), RowSpacing = 14, ColumnSpacing = 20 };
        var labels = new[] { "输出 token", "输入 token（含缓存）", "请求数", "平均输出 · tok/s" };
        var values = new[] { _output, _input, _requests, _average };
        for (var i = 0; i < values.Length; i++)
        {
            var card = new StackPanel { Spacing = 3 };
            card.Children.Add(Ui.Label(labels[i], 10, Ui.Muted));
            card.Children.Add(values[i]);
            Ui.Cell(today, card, i % 2, i / 2);
        }
        sections.Children.Add(today);
        sections.Children.Add(_peak);
        sections.Children.Add(_liveCost);
        sections.Children.Add(_cost);
        _traffic.TextWrapping = TextWrapping.Wrap;
        sections.Children.Add(_traffic);
        sections.Children.Add(Ui.Button("网络流量与历史…", controller.ShowTraffic));

        var diagnostics = new Expander { Header = "数据读取状态", HorizontalAlignment = HorizontalAlignment.Stretch };
        var options = new StackPanel { Spacing = 9 };
        _dataStatus.TextWrapping = TextWrapping.Wrap;
        options.Children.Add(_dataStatus);
        options.Children.Add(Ui.Button("设置与诊断…", settings));
        diagnostics.Content = options;
        sections.Children.Add(diagnostics);
        _warning.TextWrapping = TextWrapping.Wrap;
        sections.Children.Add(_warning);
        _privacy.TextWrapping = TextWrapping.Wrap;
        sections.Children.Add(_privacy);
        Content = new ScrollViewer { Content = sections, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _gauge.VisualStateChanged += UpdateTitle;
        controller.Changed += Update;
        Update();
    }

    public string CurrentVehicleKey => _gauge.CurrentVehicleKey;
    public long AnimationFrameCount => _gauge.AnimationFrameCount;
    public int CurvePointCount => _chart.VisiblePointCount;
    public int DisplayedAgentCount { get; private set; }

    private static Control Divider() => new Border { Height = 1, Background = Ui.Line, Opacity = 0.65 };

    private void UpdateTitle()
    {
        var reading = SpeedReadingPresentation.From(_controller.Speed.Snapshot, _controller.Settings.Metric);
        _title.Text = reading.IsAvailable ? reading.VehiclePrefix + _gauge.SpeedTitleText : reading.KindLabel;
        _title.Foreground = new SolidColorBrush(_gauge.HeatColor);
    }

    private void Update()
    {
        var snapshot = _controller.Speed.Snapshot;
        var running = snapshot.State.Kind == MonitorStateKind.Running;
        var reading = SpeedReadingPresentation.From(snapshot, _controller.Settings.Metric);
        var moneyMode = _controller.Settings.CostSettings.LiveUnit == LiveUnit.Cost;
        var money = _controller.GetLiveMoney();
        _metric.SelectedIndex = _controller.Settings.Metric == SpeedMetric.Total ? 1 : 0;
        _gauge.Redline = _controller.Redline;
        _gauge.Effects = _controller.Settings.Effects;
        _gauge.ReduceMotion = _controller.EffectiveReduceMotion;
        _gauge.TitleTheme = _controller.Settings.TitleThemes.General;
        _gauge.Update(_controller.DisplaySnapshot);
        _chart.Redline = _controller.Redline;
        _chart.Metric = _controller.Settings.Metric;
        _chart.Update(_controller.DisplaySnapshot);
        UpdateTitle();
        _speed.Text = moneyMode ? reading.IsAvailable ? money.NumberText : "—" : reading.Value;
        _speed.FontSize = _speed.Text.Length > 8 ? 30 : _speed.Text.Length > 6 ? 38 : 48;
        _unit.Text = moneyMode ? money.Unit : "tok/s";
        _gauge.ReadoutOverride = moneyMode ? _speed.Text : null;
        _gauge.ReadoutUnitOverride = moneyMode ? money.Unit : null;
        _readingKind.Text = (moneyMode ? "API 等值费用 · " : "") + reading.KindLabel + (reading.IsEstimated ? " · 非当前出字速度" : "");
        _lastMeasurement.Text = reading.LastMeasurement;
        ToolTip.SetTip(_speed, moneyMode ? money.HoverLine + "\n" + reading.Explanation : reading.Explanation);
        ToolTip.SetTip(_gauge, reading.Explanation);
        _chartNote.Text = (_controller.Settings.Metric == SpeedMetric.Total ? "输入含缓存 · " : "输出 tok/s · ") + "含请求等待 · 虚线为历史参考估算";
        _dataStatus.Text = DataStatusPresentation.Describe(snapshot, _controller.CodexDirectory);
        _executionModes.Text = ExecutionModePresentation.Current(snapshot, null);
        _configuredModes.Text = ExecutionModePresentation.ConfiguredSource(snapshot, null);
        ToolTip.SetTip(_configuredModes, ExecutionModePresentation.ConfiguredDetails(snapshot, null));
        _state.Text = UsageProjection.State(snapshot) + $"  ·  生成中 {snapshot.GeneratingCount} / 工具 {snapshot.ToolsCount}";
        UpdateSources(snapshot);
        _output.Text = running ? UsageProjection.Compact(snapshot.Today.Output) : "—";
        _input.Text = running ? UsageProjection.Compact(snapshot.Today.Input) : "—";
        _requests.Text = running ? UsageProjection.Number(snapshot.Today.Requests) : "—";
        _average.Text = running ? UsageProjection.Rate(snapshot.AverageToday) : "—";
        var peak = snapshot.PeakToday.ValueFor(_controller.Settings.Metric);
        _peak.Text = "⚑  今日" + MetricPresentation.Name(_controller.Settings.Metric) + "峰值  " + (running && peak > 0 ? MetricPresentation.Rate(peak) + " tok/s" : "—");
        var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var usage = _controller.History.Days.Where(item => item.Day == day).ToArray();
        var excluded = _controller.Settings.CostSettings.IncludeSubscriptions ? SourceSet.Empty : _controller.Settings.CostSettings.Subscriptions;
        var cost = HistoryAggregator.Totals(HistoryAggregator.Daily(usage, null, excluded: excluded)).Cost;
        _liveCost.Text = "当前 API 等值费用  " + (reading.IsAvailable ? money.InlineText + "/秒" : "—");
        _cost.Text = "今日 API 等值费用  " + (usage.Length == 0 ? "—" : _controller.FormatHistoryMoney(cost));
        ToolTip.SetTip(_cost, "按已汇总记录和对应价格版本估算，每 30 秒更新。显示币种：" + _controller.Settings.Currency
            + (excluded.IsEmpty ? "" : "\n显示费用排除订阅来源：" + string.Join("、", excluded.Select(LinuxSources.Name))));
        UpdateAgents(snapshot);
        var traffic = _controller.Traffic.Snapshot;
        _traffic.Text = !traffic.Enabled ? "网络流量尚未启用，可在设置中开启。"
            : traffic.MachineLive is { } net ? $"网卡 ↓ {TrafficFormat.Rate(net.Rx, _controller.Settings.TrafficUnit == TrafficUnit.Bits)}  ↑ {TrafficFormat.Rate(net.Tx, _controller.Settings.TrafficUnit == TrafficUnit.Bits)} · 进程为观测计数"
            : "网络流量采样中，尚无有效速率。";
        ToolTip.SetTip(_traffic, _controller.Traffic.Coverage);
        _privacy.Text = "本地记录 · " + (_controller.Reporting.Enabled ? _controller.Reporting.Paused ? "上报已暂停" : "已启用上报（以所选内容为准）" : "上报未启用");
        _warning.Text = _controller.Error ?? string.Join(" · ", snapshot.Warnings.Select(warning => warning switch
        {
            MonitorWarning.UnreadableFiles files => $"{files.Count} 个文件无法读取",
            MonitorWarning.MalformedLines lines => $"{lines.Count} 条记录无法识别",
            MonitorWarning.PartialHistory => "实时视图已载入近期记录，完整历史正在后台汇总",
            _ => null,
        }).Where(message => message is not null));
        _warning.IsVisible = !string.IsNullOrWhiteSpace(_warning.Text);
    }

    private void UpdateSources(SpeedSnapshot snapshot)
    {
        _sources.Children.Clear();
        foreach (var source in snapshot.Sources.Where(source => source.Enabled))
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 14 };
            var available = !OrbSubset.IsUnavailable(source);
            var selected = LinuxSources.Subset(snapshot, SourceSet.Of(source.Kind));
            var reading = SpeedReadingPresentation.From(selected, _controller.Settings.Metric);
            var moneyMode = _controller.Settings.CostSettings.LiveUnit == LiveUnit.Cost;
            var money = _controller.GetSourceMoney(source.Kind);
            Ui.Cell(row, Ui.Label("●  " + LinuxSources.Name(source.Kind), 13, available ? Ui.Accent : Ui.Muted), 0);
            var rate = Ui.Label(reading.IsAvailable ? moneyMode ? money.InlineText + "/秒" : reading.Value + " tok/s" : "—", 14);
            ToolTip.SetTip(rate, moneyMode ? money.HoverLine + (_controller.Settings.CostSettings.IsSubscription(source.Kind)
                ? "\n订阅来源：此行显示自身 API 等值费用，汇总金额按订阅设置计算。" : "") : reading.Explanation);
            Ui.Cell(row, rate, 1);
            Ui.Cell(row, Ui.Label(available ? UsageProjection.Compact(source.Today.Output) + " 输出/今日" : "目录不可用", 12, Ui.Muted), 2);
            _sources.Children.Add(row);
        }
        if (_sources.Children.Count == 0) _sources.Children.Add(Ui.Label("未启用工具来源，可在设置中选择。", 12, Ui.Muted));
    }

    private void UpdateAgents(SpeedSnapshot snapshot)
    {
        _agentsCount.Text = $"{snapshot.Agents.Count} 项 · {snapshot.GeneratingCount} 生成 / {snapshot.ToolsCount} 工具";
        var agents = snapshot.Agents.OrderBy(agent => agent.Phase == AgentPhase.Idle ? 1 : 0)
            .ThenByDescending(agent => agent.Live.ValueFor(_controller.Settings.Metric)).ToArray();
        DisplayedAgentCount = agents.Length;
        var key = _controller.Settings.Metric + string.Join('|', agents.Select(agent => $"{agent.Id}/{agent.Model}/{agent.Phase}/{agent.Live.ValueFor(_controller.Settings.Metric):F1}/{agent.RecentSpeed:F1}/{agent.Modes.GetHashCode()}"));
        if (key == _agentKey && _agents.Children.Count > 0) return;
        _agentKey = key;
        _agents.Children.Clear();
        if (agents.Length == 0)
        {
            _agents.Children.Add(Ui.Label(snapshot.State.Kind == MonitorStateKind.Starting ? "正在发现会话…" : "暂无活跃会话", 12, Ui.Muted));
            return;
        }
        foreach (var agent in agents)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(agent.IsSubagent ? 12 : 0, 0, 0, 0) };
            var details = new StackPanel { Spacing = 2 };
            var name = string.IsNullOrWhiteSpace(agent.Project) ? LinuxSources.Name(agent.Source) : agent.Project;
            var top = Ui.Label((agent.IsSubagent ? "↳ " : "● ") + name, 12, agent.Phase == AgentPhase.Generating ? Ui.Text : Ui.Muted);
            top.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Children.Add(top);
            var phase = agent.Phase switch { AgentPhase.Generating => "生成中", AgentPhase.Tools => "工具调用", _ => "空闲" };
            var model = Ui.Label(LinuxSources.Name(agent.Source) + " · " + (agent.Model ?? "模型未知") + " · " + phase + " · " + ExecutionModePresentation.Agent(agent, true), 10, Ui.Muted);
            ToolTip.SetTip(model, ExecutionModePresentation.AgentDetails(agent, true));
            model.TextTrimming = TextTrimming.CharacterEllipsis;
            details.Children.Add(model);
            Ui.Cell(row, details, 0);
            var liveRate = agent.Live.ValueFor(_controller.Settings.Metric);
            var rateText = agent.Phase == AgentPhase.Generating ? liveRate > 0 ? MetricPresentation.Rate(liveRate) : "—"
                : _controller.Settings.Metric == SpeedMetric.Output ? "最近 " + UsageProjection.Rate(agent.RecentSpeed) : "—";
            Ui.Cell(row, Ui.Label(rateText, 12,
                agent.Phase == AgentPhase.Generating ? Ui.Accent : Ui.Muted), 1);
            _agents.Children.Add(row);
        }
    }
}
