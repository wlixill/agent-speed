using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Presentation.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace AgentSpeed.Linux;

internal sealed class SettingsWindow : Window
{
    private readonly SpeedController _controller;
    private readonly CheckBox _login = new() { Name = "SettingsLogin", Content = "登录桌面时自动启动" };
    private readonly CheckBox _background = new() { Name = "SettingsBackground", Content = "启动时只显示悬浮窗／托盘，保持仪表在后台" };
    private readonly CheckBox _clickThrough = new() { Name = "SettingsClickThrough", Content = "鼠标穿透悬浮窗" };
    private readonly CheckBox _snap = new() { Name = "SettingsSnap", Content = "靠近屏幕工作区边缘时自动吸附" };
    private readonly CheckBox _priceUpdates = new() { Name = "SettingsPriceUpdates", Content = "自动更新公开的模型价格表与汇率" };
    private readonly ComboBox _currency = new() { Name = "SettingsCurrency", ItemsSource = CurrencyTable.Bundled.Currencies.OrderBy(item => item.Code != "USD").ThenBy(item => item.Code != "CNY").ThenBy(item => item.Code).Select(item => item.Code).ToArray() };
    private readonly TextBlock _priceStatus = Ui.Label("", 11, Ui.Muted);
    private readonly Button _syncPrices;
    private readonly Button _rateAttribution;
    private readonly CheckBox _traySpeed = new() { Name = "SettingsTraySpeed", Content = "在系统顶栏的托盘图标中显示速度数字" };
    private readonly ComboBox _appearance = new() { Name = "SettingsAppearance", ItemsSource = new[] { "跟随系统", "浅色", "深色" } };
    private readonly ComboBox _effects = new() { Name = "SettingsEffects", ItemsSource = new[] { "关闭特效", "柔和", "标准", "全开" } };
    private readonly CheckBox _reduceMotion = new() { Name = "SettingsReduceMotion", Content = "减弱动态效果（停止载具运动与持续动画）" };
    private readonly TextBlock _appearanceStatus = Ui.Label("", 11, Ui.Muted);
    private readonly ComboBox _metric = new() { Name = "SettingsMetric", ItemsSource = new[] { "输出速度", "输入＋输出吞吐量（输入含缓存）" } };
    private readonly ComboBox _redline = new() { Name = "SettingsRedline" };
    private readonly Slider _size = new() { Name = "SettingsOrbSize", Minimum = 96, Maximum = 300, TickFrequency = 4, IsSnapToTickEnabled = true };
    private readonly Slider _opacity = new() { Name = "SettingsOrbOpacity", Minimum = .4, Maximum = 1, TickFrequency = .05, IsSnapToTickEnabled = true };
    private readonly TextBlock _sizeValue = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _opacityValue = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _status = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _error = Ui.Label("", 11, Ui.Warning);
    private readonly TextBox _directory = new() { FontSize = 12 };
    private readonly CheckBox _trafficEnabled = new() { Name = "SettingsTrafficEnabled", Content = "采集本机与 agent 网络流量" };
    private readonly CheckBox _autoUpdates = new() { Name = "SettingsAutoUpdates", Content = "定时检查已配置的 Ubuntu 更新源" };
    private readonly TextBox _updateFeed = new() { Name = "SettingsUpdateFeed", PlaceholderText = "https://你的发布站点/agent-speed-linux-release.json" };
    private readonly TextBlock _trafficStatus = Ui.Label("", 11, Ui.Muted);
    private readonly ComboBox _liveUnit = new() { Name = "SettingsLiveUnit", ItemsSource = new[] { "token 速度", "API 等值费用／秒" } };
    private readonly CheckBox _includeSubscriptions = new() { Name = "SettingsIncludeSubscriptions", Content = "将包月订阅来源也计入显示费用" };
    private readonly Dictionary<SourceKind, CheckBox> _subscriptions = [];
    private readonly CheckBox _trafficTools = new() { Name = "SettingsTrafficTools", Content = "流量包含工具子进程" };
    private readonly ComboBox _trafficUnit = new() { Name = "SettingsTrafficUnit", ItemsSource = new[] { "字节／秒（B/s）", "比特／秒（b/s）" } };
    private readonly ComboBox _trafficBar = new() { Name = "SettingsTrafficBar", ItemsSource = new[] { "顶栏不显示流量", "顶栏显示整机上传／下载", "顶栏显示已启用来源的进程观测流量" } };
    private UserSettings? _bound;
    private bool _syncing;

    public SettingsWindow(SpeedController controller)
    {
        _controller = controller;
        Title = "Agent Speed · 设置";
        Width = 650;
        Height = 790;
        MinWidth = 530;
        MinHeight = 570;
        Background = Ui.Background;
        Foreground = Ui.Text;
        FontFamily = new FontFamily("Noto Sans CJK SC");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var content = new StackPanel { Spacing = 18, Margin = new Thickness(24) };
        content.Children.Add(Ui.Label("设置", 26));
        content.Children.Add(Ui.Label("Agent Speed · " + BuildInformation.Version, 11, Ui.Muted));

        var tools = Section(content, "工具来源与更多功能");
        tools.Children.Add(Ui.Button("工具来源与记录路径…", controller.ShowSources));
        tools.Children.Add(Ui.Button("管理悬浮窗、样式与称号主题…", controller.ShowOrbManagement));
        tools.Children.Add(_trafficEnabled);
        tools.Children.Add(_trafficTools); tools.Children.Add(_trafficUnit); tools.Children.Add(_trafficBar);
        _trafficStatus.TextWrapping = TextWrapping.Wrap; tools.Children.Add(_trafficStatus);
        tools.Children.Add(Ui.Button("网络流量与每日历史…", controller.ShowTraffic));
        tools.Children.Add(Ui.Button("启用增强流量采集（需要系统认证）…", async () =>
        {
            if (!controller.Settings.TrafficEnabled) controller.SetTrafficEnabled(true);
            await controller.Traffic.StartEnhancedAsync();
        }));
        tools.Children.Add(Ui.Button("停止增强采集", controller.Traffic.StopEnhanced));
        Note(tools, "增强采集统计当前用户的 TCP／UDP 收发字节，不读取内容或连接地址。应用退出或停止后结束；未启用时使用无特权 TCP 观测。采集范围和未覆盖路径见流量窗口。");
        tools.Children.Add(Ui.Button("可选上报、头像与设备配对…", controller.ShowReporting));

        var updates = Section(content, "Ubuntu 应用更新");
        updates.Children.Add(_updateFeed); updates.Children.Add(_autoUpdates);
        var applyUpdates = Ui.Button("保存更新源", () => controller.SetUpdateOptions(_autoUpdates.IsChecked == true, _updateFeed.Text ?? ""));
        applyUpdates.Name = "ApplyUpdateFeed"; updates.Children.Add(applyUpdates);
        updates.Children.Add(Ui.Button("检查、下载或安装本地 deb…", controller.ShowUpdates));
        Note(updates, "填写发布者提供的 HTTPS 更新清单地址后可检查新版；也可以直接选择本地 deb。安装前校验包名、架构、版本与 SHA-256，安装需要系统认证。");

        var startup = Section(content, "启动");
        startup.Children.Add(_login);
        startup.Children.Add(_background);
        Note(startup, "从应用菜单启动始终打开仪表；可用 agent-speed --background 显式后台启动。登录启动默认关闭。");

        var metric = Section(content, "显示指标");
        metric.Children.Add(_metric);
        Note(metric, "输出速度用于查看输出 token 的请求速率；输入＋输出表示吞吐量，包含缓存输入。两者均含请求等待时间。");
        metric.Children.Add(Ui.Label("当前指标的表盘满量程 · tok/s", 12, Ui.Muted));
        metric.Children.Add(_redline);
        Note(metric, "两种指标分别保存刻度。刻度只影响灯光和载具升级门槛，不改变实际用量。");
        metric.Children.Add(_traySpeed);
        Note(metric, "顶栏数字使用紧凑格式；完整数值、指标和估算状态可在悬停提示中查看。显示由 Ubuntu AppIndicators 扩展承载。");

        var appearance = Section(content, "外观与动画");
        appearance.Children.Add(_appearance);
        appearance.Children.Add(_effects);
        appearance.Children.Add(_reduceMotion);
        _appearanceStatus.TextWrapping = TextWrapping.Wrap;
        appearance.Children.Add(_appearanceStatus);
        Note(appearance, "系统关闭动画时也会停止持续运动；隐藏或最小化窗口继续停止动画更新。");

        var money = Section(content, "API 等值费用与价格");
        money.Children.Add(Ui.Label("实时仪表与顶栏主读数", 12, Ui.Muted));
        money.Children.Add(_liveUnit);
        var subscriptions = new StackPanel { Spacing = 5 };
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var box = new CheckBox { Name = "Subscription" + kind, Content = LinuxSources.Name(kind) };
            _subscriptions.Add(kind, box); subscriptions.Children.Add(box);
        }
        money.Children.Add(new Expander { Header = "按包月订阅使用的来源", Content = subscriptions });
        money.Children.Add(_includeSubscriptions);
        var applyCost = Ui.Button("应用费用口径与主读数", () => controller.SetCostDisplay(
            _liveUnit.SelectedIndex == 1 ? LiveUnit.Cost : LiveUnit.Tokens,
            SourceSet.From(_subscriptions.Where(item => item.Value.IsChecked == true).Select(item => item.Key)),
            _includeSubscriptions.IsChecked == true));
        applyCost.Name = "ApplyCostDisplay"; money.Children.Add(applyCost);
        Note(money, "标为订阅的来源默认不计入汇总费用，token 与请求数继续保留。历史窗口可单独选择是否计入；CSV 保留完整 API 等值费用。每个悬浮窗可独立选择 token 或费用主读数。");
        money.Children.Add(Ui.Label("显示币种", 12, Ui.Muted));
        money.Children.Add(_currency);
        money.Children.Add(_priceUpdates);
        _syncPrices = Ui.Button("立即更新价格与汇率", async () => await controller.RefreshPricesAsync());
        _syncPrices.Name = "SettingsSyncPrices";
        money.Children.Add(_syncPrices);
        _priceStatus.TextWrapping = TextWrapping.Wrap;
        money.Children.Add(_priceStatus);
        _rateAttribution = Ui.Button("汇率来源：ExchangeRate-API", () =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.exchangerate-api.com") { UseShellExecute = true }); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            { controller.ReportError("无法打开汇率来源：" + exception.Message); }
        });
        money.Children.Add(_rateAttribution);
        Note(money, "仅下载公开价格与汇率，不上传本机用量。默认离线；非美元币种可先使用内置或缓存汇率，日期与来源会在这里显示。费用不代表订阅实际账单。");

        var orb = Section(content, "悬浮窗");
        orb.Children.Add(_sizeValue);
        orb.Children.Add(_size);
        orb.Children.Add(_opacityValue);
        orb.Children.Add(_opacity);
        orb.Children.Add(_clickThrough);
        Note(orb, "启用穿透后，可从托盘菜单“恢复悬浮窗鼠标操作”关闭。每种显示器布局分别记住位置。");
        orb.Children.Add(_snap);
        var applyOrb = Ui.Button("应用悬浮窗设置", () => controller.SetOrbOptions(_size.Value, _opacity.Value, _clickThrough.IsChecked == true, _snap.IsChecked == true));
        applyOrb.Name = "ApplyOrbSettings";
        orb.Children.Add(applyOrb);

        var source = Section(content, "Codex 数据源与诊断");
        source.Children.Add(_directory);
        source.Children.Add(Ui.Button("应用会话目录", () => controller.ChangeDirectory(_directory.Text ?? "")));
        _status.TextWrapping = TextWrapping.Wrap;
        source.Children.Add(_status);
        source.Children.Add(Ui.Button("导出诊断信息…", async () => await ExportDiagnostics()));
        Note(source, "诊断包含目录、事件时间、模型与数字用量，不含对话正文、工具命令、会话名或项目名。");
        _error.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(_error);
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        _login.IsCheckedChanged += (_, _) => { if (!_syncing) controller.SetLoginStartup(_login.IsChecked == true); };
        _trafficEnabled.IsCheckedChanged += (_, _) => { if (!_syncing) controller.SetTrafficEnabled(_trafficEnabled.IsChecked == true); };
        void ApplyTraffic() { if (!_syncing) controller.SetTrafficDisplay(_trafficTools.IsChecked == true,
            _trafficUnit.SelectedIndex == 1 ? TrafficUnit.Bits : TrafficUnit.Bytes, (BarTraffic)Math.Clamp(_trafficBar.SelectedIndex, 0, 2)); }
        _trafficTools.IsCheckedChanged += (_, _) => ApplyTraffic();
        _trafficUnit.SelectionChanged += (_, _) => ApplyTraffic();
        _trafficBar.SelectionChanged += (_, _) => ApplyTraffic();
        _background.IsCheckedChanged += (_, _) => { if (!_syncing) controller.SetStartInBackground(_background.IsChecked == true); };
        _metric.SelectionChanged += (_, _) => { if (!_syncing) controller.SetMetric(_metric.SelectedIndex == 1 ? SpeedMetric.Total : SpeedMetric.Output); };
        _priceUpdates.IsCheckedChanged += (_, _) => { if (!_syncing) controller.SetCostOptions(_priceUpdates.IsChecked == true, _currency.SelectedItem as string ?? "USD"); };
        _currency.SelectionChanged += (_, _) => { if (!_syncing) controller.SetCostOptions(_priceUpdates.IsChecked == true, _currency.SelectedItem as string ?? "USD"); };
        _traySpeed.IsCheckedChanged += (_, _) => { if (!_syncing) controller.SetTrayShowSpeed(_traySpeed.IsChecked == true); };
        _appearance.SelectionChanged += (_, _) => ApplyAppearance();
        _effects.SelectionChanged += (_, _) => ApplyAppearance();
        _reduceMotion.IsCheckedChanged += (_, _) => ApplyAppearance();
        _redline.SelectionChanged += (_, _) => { if (!_syncing && _redline.SelectedItem is double value) controller.SetRedline(value); };
        _size.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) UpdateSliderLabels(); };
        _opacity.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) UpdateSliderLabels(); };
        controller.Changed += Changed;
        Opened += (_, _) => Update();
        Closed += (_, _) => controller.Changed -= Changed;
        Update();
    }

    private static StackPanel Section(Panel root, string title)
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(Ui.Label(title, 16));
        root.Children.Add(Ui.Card(section));
        return section;
    }

    private static void Note(Panel root, string text)
    {
        var label = Ui.Label(text, 11, Ui.Muted);
        label.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(label);
    }

    private void UpdateSliderLabels()
    {
        _sizeValue.Text = $"表盘尺寸：{_size.Value:N0} px";
        _opacityValue.Text = $"不透明度：{_opacity.Value:P0}";
    }

    private void Changed() { if (IsVisible) Update(); }

    private void ApplyAppearance()
    {
        if (!_syncing) _controller.SetAppearance((AppearanceMode)Math.Max(0, _appearance.SelectedIndex),
            (EffectsIntensity)Math.Max(0, _effects.SelectedIndex), _reduceMotion.IsChecked == true);
    }

    private void Update()
    {
        var settings = _controller.Settings;
        _syncing = true;
        try
        {
            _login.IsChecked = _controller.LoginStartupEnabled;
            _trafficEnabled.IsChecked = settings.TrafficEnabled;
            _trafficTools.IsChecked = settings.TrafficIncludeTools;
            _trafficUnit.SelectedIndex = settings.TrafficUnit == TrafficUnit.Bits ? 1 : 0;
            _trafficBar.SelectedIndex = (int)settings.TrafficBar;
            if (_bound is null || _bound.CostSettings.LiveUnit != settings.CostSettings.LiveUnit
                || _bound.CostSettings.Subscriptions != settings.CostSettings.Subscriptions
                || _bound.CostSettings.IncludeSubscriptions != settings.CostSettings.IncludeSubscriptions)
            {
                _liveUnit.SelectedIndex = settings.CostSettings.LiveUnit == LiveUnit.Cost ? 1 : 0;
                _includeSubscriptions.IsChecked = settings.CostSettings.IncludeSubscriptions;
                foreach (var (kind, box) in _subscriptions) box.IsChecked = settings.CostSettings.IsSubscription(kind);
            }
            if (_bound is null || _bound.UpdateFeedUrl != settings.UpdateFeedUrl) _updateFeed.Text = settings.UpdateFeedUrl;
            if (_bound is null || _bound.AutoCheckUpdates != settings.AutoCheckUpdates) _autoUpdates.IsChecked = settings.AutoCheckUpdates;
            _background.IsChecked = settings.StartInBackground;
            _metric.SelectedIndex = settings.Metric == SpeedMetric.Total ? 1 : 0;
            _priceUpdates.IsChecked = settings.EnablePriceUpdates;
            _currency.SelectedItem = settings.Currency;
            _traySpeed.IsChecked = settings.TrayShowSpeed;
            _appearance.SelectedIndex = (int)settings.Appearance;
            _effects.SelectedIndex = (int)settings.Effects;
            _reduceMotion.IsChecked = settings.ReduceMotion;
            if (_bound is null || _bound.Metric != settings.Metric || _bound.Redline != settings.Redline || _bound.RedlineTotal != settings.RedlineTotal)
            {
                var choices = settings.Metric == SpeedMetric.Total ? new[] { 20_000d, 100_000d, 200_000d, 500_000d, 2_000_000d }
                    : new[] { 200d, 400d, 800d, 2000d };
                _redline.ItemsSource = choices.Append(_controller.Redline).Distinct().Order().ToArray();
                _redline.SelectedItem = _controller.Redline;
            }
            if (_bound is null || _bound.OrbSize != settings.OrbSize) _size.Value = settings.OrbSize;
            if (_bound is null || _bound.OrbOpacity != settings.OrbOpacity) _opacity.Value = settings.OrbOpacity;
            if (_bound is null || _bound.OrbClickThrough != settings.OrbClickThrough) _clickThrough.IsChecked = settings.OrbClickThrough;
            if (_bound is null || _bound.OrbSnapToEdges != settings.OrbSnapToEdges) _snap.IsChecked = settings.OrbSnapToEdges;
            if (_bound is null || _bound.CodexDirectory != settings.CodexDirectory) _directory.Text = _controller.CodexDirectory;
            _bound = settings;
        }
        finally { _syncing = false; }
        UpdateSliderLabels();
        _status.Text = DataStatusPresentation.Describe(_controller.Speed.Snapshot, _controller.CodexDirectory);
        _priceStatus.Text = _controller.Cost.Status.ToString();
        _syncPrices.IsEnabled = settings.EnablePriceUpdates && !_controller.Cost.Status.IsBusy;
        _rateAttribution.IsVisible = _controller.Cost.Status.NeedsAttribution;
        _appearanceStatus.Text = _controller.AppearanceStatus;
        _trafficStatus.Text = _controller.Traffic.EnhancedStatus + "\n" + _controller.Traffic.Coverage;
        _error.Text = _controller.Error ?? "";
        _error.IsVisible = !string.IsNullOrEmpty(_error.Text);
    }

    private async Task ExportDiagnostics()
    {
        try
        {
            var result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
            {
                Title = "导出 Agent Speed 诊断", SuggestedFileName = "agent-speed-diagnostic.json", DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("JSON 文件") { Patterns = ["*.json"] }],
            });
            if (result.File is not { } file) return;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek) stream.SetLength(0);
            var bytes = System.Text.Encoding.UTF8.GetBytes(DataStatusPresentation.Diagnostics(_controller.Speed.Snapshot,
                _controller.History.State, _controller.CodexDirectory, _controller.Settings));
            await stream.WriteAsync(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _controller.ReportError("无法导出诊断：" + exception.Message);
        }
    }
}
