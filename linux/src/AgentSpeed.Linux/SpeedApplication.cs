using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace AgentSpeed.Linux;

internal sealed class SpeedApplication : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SpeedController? _controller;
    private DashboardWindow? _dashboard;
    private HistoryWindow? _historyWindow;
    private SettingsWindow? _settingsWindow;
    private OrbWindow? _orb;
    private OrbManager? _orbManager;
    private SourcesWindow? _sourcesWindow;
    private TrafficWindow? _trafficWindow;
    private UpdateWindow? _updateWindow;
    private ReportingWindow? _reportingWindow;
    private TrayIcon? _tray;
    private TrayIcon? _trafficDownTray;
    private TrayIcon? _trafficUpTray;
    private WindowIcon? _defaultIcon;
    private LinuxAppearanceService? _appearance;
    private readonly TrayReadoutRenderer _trayReadout = new();
    private readonly TrayReadoutRenderer _trafficDownReadout = new();
    private readonly TrayReadoutRenderer _trafficUpReadout = new();
    private long _lastTrayUpdateTick = Environment.TickCount64 - 1000;
    private NativeMenuItem? _toggleItem;
    private NativeMenuItem? _clickThroughItem;
    private FileStream? _instance;
    private bool _quitting;
    private bool _restart;
    private readonly List<DispatcherTimer> _timers = [];

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Name = "Agent Speed";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var storage = new AppStorage(Program.Options.DataDirectory);
        Directory.CreateDirectory(storage.DataDirectory);
        try
        {
            _instance = new FileStream(Path.Combine(storage.DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            if (!Program.Options.Background || Program.Options.Dashboard)
                File.WriteAllText(Path.Combine(storage.DataDirectory, "show.request"), "show");
            Console.WriteLine(Program.Options.Background && !Program.Options.Dashboard
                ? "Agent Speed 已在后台运行。" : "Agent Speed 已在运行，已请求打开窗口。");
            // Startup has not entered its dispatcher frame yet. Queue shutdown so a second launch exits cleanly.
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
            base.OnFrameworkInitializationCompleted();
            return;
        }
        _controller = new(Program.Options);
        _dashboard = new(_controller, ShowHistory, ShowSettings, ToggleOrb, Quit);
        _historyWindow = new(_controller);
        _settingsWindow = new(_controller);
        _orbManager = new(_controller, ShowDashboard, ShowHistory, Quit);
        _orb = _orbManager.Main;
        _sourcesWindow = new(_controller);
        _trafficWindow = new(_controller.Traffic, () => LinuxSources.Enabled(_controller.Settings).ToHashSet(),
            () => _controller.Speed.Snapshot.Agents.GroupBy(agent => agent.Id).ToDictionary(group => group.Key, group => group.First().Source));
        _controller.Changed += _trafficWindow.Refresh;
        _updateWindow = new(_controller.Updates, () => { _restart = true; Quit(); });
        _reportingWindow = new(_controller.Reporting);
        _controller.OpenSourcesRequested += () => ShowWindow(_sourcesWindow);
        _controller.OpenOrbsRequested += () => _orbManager.OpenManagementWindow();
        _controller.OpenTrafficRequested += () => ShowWindow(_trafficWindow);
        _controller.OpenUpdatesRequested += () => ShowWindow(_updateWindow);
        _controller.OpenReportingRequested += () => ShowWindow(_reportingWindow);
        _orbManager.Changed += () =>
        {
            _orb = _orbManager.Main;
            if (desktop.MainWindow is OrbWindow) desktop.MainWindow = _orb;
            if (_defaultIcon is not null) foreach (var window in _orbManager.Windows) window.Icon = _defaultIcon;
            UpdateToggle();
        };
        desktop.MainWindow = _controller.Settings.OrbVisible ? _orb
            : Program.Options.ShouldShowDashboard(_controller.Settings) ? _dashboard : null;
        using var iconStream = AssetLoader.Open(new Uri("avares://AgentSpeed.Linux/Assets/icon.png"));
        var icon = new WindowIcon(iconStream);
        _defaultIcon = icon;
        _dashboard.Icon = icon;
        _historyWindow.Icon = icon;
        _settingsWindow.Icon = icon;
        _orb.Icon = icon;
        foreach (var window in _orbManager.Windows) window.Icon = icon;
        foreach (var window in new Window[] { _sourcesWindow, _trafficWindow, _updateWindow, _reportingWindow }) window.Icon = icon;
        _appearance = new(PlatformSettings);
        _appearance.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (_quitting) return;
            RequestedThemeVariant = _appearance.ResolvedTheme;
            _controller.UpdateAppearanceState(_appearance.IsDark, _appearance.EffectiveReduceMotion, _appearance.Status);
        });
        _appearance.SetOptions(_controller.Settings.Appearance, _controller.Settings.Effects, _controller.Settings.ReduceMotion);
        RequestedThemeVariant = _appearance.ResolvedTheme;
        _controller.UpdateAppearanceState(_appearance.IsDark, _appearance.EffectiveReduceMotion, _appearance.Status);
        var menu = new NativeMenu();
        var open = new NativeMenuItem("打开实时仪表");
        open.Click += (_, _) => ShowDashboard();
        var history = new NativeMenuItem("打开用量历史");
        history.Click += (_, _) => ShowHistory();
        var settings = new NativeMenuItem("设置与诊断");
        settings.Click += (_, _) => ShowSettings();
        _toggleItem = new NativeMenuItem("隐藏悬浮窗");
        _toggleItem.Click += (_, _) => ToggleOrb();
        _clickThroughItem = new NativeMenuItem("启用悬浮窗鼠标穿透");
        _clickThroughItem.Click += (_, _) =>
        {
            var current = _controller.Settings;
            _controller.SetOrbOptions(current.OrbSize, current.OrbOpacity, !current.OrbClickThrough, current.OrbSnapToEdges);
        };
        var quit = new NativeMenuItem("退出 Agent Speed");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(open);
        menu.Items.Add(history);
        menu.Items.Add(settings);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_clickThroughItem);
        foreach (var (label, action) in new (string, Action)[]
        {
            ("工具来源与路径", _controller.ShowSources), ("管理悬浮窗与主题", _controller.ShowOrbManagement),
            ("网络流量", _controller.ShowTraffic), ("检查应用更新", _controller.ShowUpdates), ("上报、头像与设备", _controller.ShowReporting),
        })
        {
            var item = new NativeMenuItem(label); item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);
        _tray = new TrayIcon { Icon = icon, ToolTipText = "Agent Speed", Menu = menu, IsVisible = true };
        _tray.Clicked += (_, _) => ShowDashboard();
        NativeMenu TrafficMenu()
        {
            var result = new NativeMenu(); var traffic = new NativeMenuItem("打开网络流量");
            traffic.Click += (_, _) => _controller.ShowTraffic(); result.Items.Add(traffic); return result;
        }
        _trafficDownTray = new TrayIcon { Icon = icon, ToolTipText = "下载流量", Menu = TrafficMenu(), IsVisible = false };
        _trafficUpTray = new TrayIcon { Icon = icon, ToolTipText = "上传流量", Menu = TrafficMenu(), IsVisible = false };
        _trafficDownTray.Clicked += (_, _) => _controller.ShowTraffic(); _trafficUpTray.Clicked += (_, _) => _controller.ShowTraffic();
        TrayIcon.SetIcons(this, new TrayIcons { _tray, _trafficDownTray, _trafficUpTray });
        _controller.Changed += () =>
        {
            _appearance.SetOptions(_controller.Settings.Appearance, _controller.Settings.Effects, _controller.Settings.ReduceMotion);
            var reading = SpeedReadingPresentation.From(_controller.Speed.Snapshot, _controller.Settings.Metric);
            var enabled = LinuxSources.Enabled(_controller.Settings);
            var sourceName = enabled.Length == 1 ? LinuxSources.Name(enabled[0]) : enabled.Length == 0 ? "未启用来源" : "全部来源";
            var money = _controller.GetLiveMoney();
            _tray.ToolTipText = sourceName + " · " + reading.Value + " tok/s\n" + MetricPresentation.Name(_controller.Settings.Metric) + " · " + reading.KindLabel
                + "\n" + (reading.IsAvailable ? money.InlineText + "/秒 · " + money.HoverLine : "—");
            UpdateTrayReadout();
            UpdateToggle();
        };
        _dashboard.Closing += (_, e) => { if (!_quitting) { e.Cancel = true; _dashboard.Hide(); } };
        _historyWindow.Closing += (_, e) => { if (!_quitting) { e.Cancel = true; _historyWindow.Hide(); } };
        _settingsWindow.Closing += (_, e) => { if (!_quitting) { e.Cancel = true; _settingsWindow.Hide(); } };
        foreach (var window in new Window[] { _sourcesWindow, _trafficWindow, _updateWindow, _reportingWindow })
            window.Closing += (_, e) => { if (!_quitting) { e.Cancel = true; window.Hide(); } };
        desktop.Exit += (_, _) =>
        {
            foreach (var timer in _timers) timer.Stop();
            _appearance.Dispose();
            _orbManager.Dispose();
            _controller.Dispose();
            _tray.Dispose();
            _trafficDownTray.Dispose(); _trafficUpTray.Dispose();
            _instance.Dispose();
            if (_restart && File.Exists("/opt/agent-speed/AgentSpeed.Linux"))
            {
                var launch = new System.Diagnostics.ProcessStartInfo("/opt/agent-speed/AgentSpeed.Linux") { UseShellExecute = false };
                launch.ArgumentList.Add("--dashboard");
                if (Program.Options.DataDirectory is { } directory) { launch.ArgumentList.Add("--data-dir"); launch.ArgumentList.Add(directory); }
                if (Program.Options.CodexDirectory is { } codex) { launch.ArgumentList.Add("--codex-dir"); launch.ArgumentList.Add(codex); }
                if (Program.Options.SoftwareRendering) launch.ArgumentList.Add("--software-rendering");
                try { System.Diagnostics.Process.Start(launch); } catch (System.ComponentModel.Win32Exception) { }
            }
        };
        desktop.ShutdownRequested += (_, _) => _orbManager.SavePositions();
        if (Program.Options.ShouldShowDashboard(_controller.Settings)) ShowDashboard();
        UpdateToggle();
        _controller.Start();
        Timer(TimeSpan.FromSeconds(1), UpdateTrayReadout);
        Timer(TimeSpan.FromSeconds(1), () =>
        {
            var request = Path.Combine(storage.DataDirectory, "show.request");
            if (!File.Exists(request)) return;
            File.Delete(request);
            ShowDashboard();
        });
        if (Program.Options.CaptureDirectory is not null)
        {
            // Diagnostic captures exercise real, visible windows in an isolated data directory.
            if (!Program.Options.Background || Program.Options.Dashboard) ShowHistory();
            Timer(TimeSpan.FromSeconds(3), Capture);
        }
        if (Program.Options.ExitAfter is { } seconds)
            Timer(TimeSpan.FromSeconds(seconds), () => { Capture(); Quit(); });
        base.OnFrameworkInitializationCompleted();
    }

    private void Timer(TimeSpan interval, Action action)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += (_, _) => action();
        _timers.Add(timer);
        timer.Start();
    }

    private void UpdateTrayReadout()
    {
        var tick = Environment.TickCount64;
        if (_quitting || _tray is null || _controller is null || tick - _lastTrayUpdateTick < 1000) return;
        _lastTrayUpdateTick = tick;
        var reading = SpeedReadingPresentation.From(_controller.Speed.Snapshot, _controller.Settings.Metric);
        var settings = _controller.Settings;
        if (!settings.TrayShowSpeed) _tray.Icon = _defaultIcon;
        else if (settings.CostSettings.LiveUnit == LiveUnit.Cost)
        {
            var money = _controller.GetLiveMoney();
            var conversion = MoneyPresentation.ConversionRate(_controller.Cost.Currency, _controller.Cost.Rates, _controller.Cost.CurrencyTable);
            var amount = reading.IsAvailable && !money.Unknown && conversion is { } rate ? money.Value * (double)rate / 1_000_000 : (double?)null;
            _tray.Icon = _trayReadout.RenderLabeled(TrayReadoutRenderer.CompactMoney(amount), money.Unit, _controller.IsDark, money.AtLeast);
        }
        else _tray.Icon = _trayReadout.Render(reading.Value, reading.IsEstimated, _controller.IsDark);
        if (_trafficDownTray is null || _trafficUpTray is null) return;
        var visible = settings.TrafficEnabled && settings.TrafficBar != BarTraffic.Off;
        _trafficDownTray.IsVisible = _trafficUpTray.IsVisible = visible;
        if (!visible) return;
        var traffic = _controller.Traffic.Snapshot;
        var mode = settings.TrafficBar == BarTraffic.Machine ? OrbTraffic.Machine : OrbTraffic.Sources;
        var sources = SourceSet.From(LinuxSources.Enabled(settings));
        var live = LinuxTrafficReadout.Rate(traffic, sources, mode, settings.TrafficIncludeTools);
        var bits = settings.TrafficUnit == TrafficUnit.Bits;
        var unit = bits ? "b/s" : "B/s"; var factor = bits ? 8 : 1;
        _trafficDownTray.Icon = _trafficDownReadout.RenderLabeled(TrayReadoutRenderer.Compact(live?.Rx * factor), "↓ " + unit, _controller.IsDark);
        _trafficUpTray.Icon = _trafficUpReadout.RenderLabeled(TrayReadoutRenderer.Compact(live?.Tx * factor), "↑ " + unit, _controller.IsDark);
        var details = LinuxTrafficReadout.Describe(traffic, sources, mode, bits, settings.TrafficIncludeTools) + "\n" + _controller.Traffic.Coverage;
        _trafficDownTray.ToolTipText = "下载 · " + details; _trafficUpTray.ToolTipText = "上传 · " + details;
    }

    private void ShowDashboard()
    {
        if (_dashboard is null) return;
        _dashboard.Show();
        _dashboard.WindowState = WindowState.Normal;
        _dashboard.Activate();
    }

    private void ToggleOrb()
    {
        if (_orbManager is null) return;
        _orbManager.ToggleMain();
        UpdateToggle();
    }

    private static void ShowWindow(Window? window)
    {
        if (window is null) return;
        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
    }

    private void ShowHistory()
    {
        if (_historyWindow is null) return;
        _historyWindow.Show();
        _historyWindow.WindowState = WindowState.Normal;
        _historyWindow.Activate();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null) return;
        _settingsWindow.Show();
        _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void UpdateToggle()
    {
        if (_toggleItem is not null) _toggleItem.Header = _orb?.IsVisible == true ? "隐藏悬浮窗" : "显示悬浮窗";
        if (_clickThroughItem is not null && _controller is not null)
            _clickThroughItem.Header = _controller.Settings.OrbClickThrough ? "恢复悬浮窗鼠标操作" : "启用悬浮窗鼠标穿透";
    }

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _orbManager?.SavePositions();
        if (_controller is not null) await _controller.FlushHistoryAsync();
        _orbManager?.Dispose();
        _desktop?.Shutdown();
    }

    private void Capture()
    {
        var directory = Program.Options.CaptureDirectory;
        if (directory is null || _controller is null) return;
        Directory.CreateDirectory(directory);
        foreach (var (name, window) in new (string, Window?)[] { ("dashboard", _dashboard), ("history", _historyWindow), ("settings", _settingsWindow), ("orb", _orb) })
        {
            if (window is not { IsVisible: true } || window.Bounds.Width < 1) continue;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
        }
        var speed = _controller.Speed.Snapshot;
        var reading = SpeedReadingPresentation.From(speed, _controller.Settings.Metric);
        var rows = UsageProjection.Rows(_controller.History.Days, speed, DateTime.Now.Date, null, null);
        File.WriteAllText(Path.Combine(directory, "status.json"), JsonSerializer.Serialize(new
        {
            monitor = speed.State.Kind.ToString(), history = _controller.History.State.Kind.ToString(),
            liveSpeed = speed.Live.Output, maximumObservedSpeed = _controller.MaximumObservedSpeed,
            speedReading = reading, selectedMetric = _controller.Settings.Metric.ToString(), selectedLiveSpeed = speed.Live.ValueFor(_controller.Settings.Metric),
            speed.LiveEstimatedShare, speed.LastResponse, dataStatus = DataStatusPresentation.Describe(speed, _controller.CodexDirectory),
            diagnostics = DataStatusPresentation.Diagnostics(speed, _controller.History.State, _controller.CodexDirectory, _controller.Settings),
            agentCount = speed.Agents.Count, displayedAgentCount = _dashboard?.DisplayedAgentCount,
            dashboardVisible = _dashboard?.IsVisible, historyVisible = _historyWindow?.IsVisible, settingsVisible = _settingsWindow?.IsVisible,
            clickThrough = _controller.Settings.OrbClickThrough,
            enabledSources = LinuxSources.Enabled(_controller.Settings).Select(LinuxSources.Name),
            orbCount = _orbManager?.Count, orbs = _orbManager?.Windows.Select(orb => orb.PlacementDiagnostics),
            trafficCoverage = _controller.Traffic.Coverage, trafficEnabled = _controller.Settings.TrafficEnabled,
            traffic = _controller.Traffic.Snapshot, updateStatus = _controller.Updates.Status,
            reportingEnabled = _controller.Reporting.Enabled, reportingStatus = _controller.Reporting.Status,
            appearance = _controller.Settings.Appearance.ToString(), resolvedDark = _controller.IsDark,
            effects = _controller.Settings.Effects.ToString(), effectiveReduceMotion = _controller.EffectiveReduceMotion,
            trayShowSpeed = _controller.Settings.TrayShowSpeed, trayReadout = _trayReadout.LastText, trayRevision = _trayReadout.Revision,
            liveMoney = reading.IsAvailable ? _controller.FormatLiveMoney(speed.LiveCost, speed.Live.Output) : "—", currency = _controller.Settings.Currency,
            publicDataUpdates = _controller.Settings.EnablePriceUpdates, costStatus = _controller.Cost.Status,
            executionMode = ExecutionModePresentation.Current(speed, null), configuredMode = ExecutionModePresentation.ConfiguredSource(speed, null),
            historyModeRows = _historyWindow?.VisibleModeRowCount, historyModes = _historyWindow?.ExecutionModesSummary,
            dashboardVehicle = _dashboard?.CurrentVehicleKey, dashboardAnimationFrames = _dashboard?.AnimationFrameCount,
            orbVehicle = _orb?.CurrentVehicleKey, orbAnimationFrames = _orb?.AnimationFrameCount,
            curvePoints = _dashboard?.CurvePointCount, historyVisibleDays = _historyWindow?.VisibleDayCount,
            historyStatistics = _historyWindow?.StatisticsSummary,
            historyModels = _historyWindow?.ModelStatisticsSummary, historyModelCount = _historyWindow?.VisibleModelCount,
            speed.Today, dailyRows = rows, sessions = speed.Sources.Where(source => source.Enabled).Sum(source => source.SessionCount),
            orbGeometry = _orb?.PlacementDiagnostics,
            orbVisible = _orb?.IsVisible, orbX = _orb?.PointToScreen(default).X, orbY = _orb?.PointToScreen(default).Y, error = _controller.Error,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
