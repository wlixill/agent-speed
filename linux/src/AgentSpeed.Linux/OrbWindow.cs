using AgentSpeed.Core;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Core.Traffic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentSpeed.Linux;

internal sealed class OrbWindow : Window
{
    private readonly SpeedController _controller;
    private readonly GlowingGauge _gauge = new() { Width = 212, Height = 212, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
    private readonly TextBlock _reading = Ui.Label("—", 27);
    private readonly TextBlock _titleText = Ui.Label("待机", 12, Ui.Accent);
    private readonly TextBlock _kindText = Ui.Label("实时输出", 10, Ui.Muted);
    private readonly TextBlock _totalText = Ui.Label("", 10, Ui.Muted);
    private readonly TextBlock _costText = Ui.Label("", 10, Ui.Muted);
    private readonly TextBlock _nameText = Ui.Label("", 10, Ui.Muted);
    private readonly TextBlock _trafficText = Ui.Label("", 10, Ui.Muted);
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _placed;
    private bool _changingVisibility;
    private bool _applyingOptions;
    private bool _adjustingPosition;
    private bool _shapePending;
    private bool _shapeWarningReported;
    private bool _disposed;
    private PixelPoint _lastPosition;
    private IReadOnlyList<OrbDisplay> _displays = [];
    private string _layoutKey = "";
    private double _appliedSize;
    private bool _appliedClickThrough;
    private bool _appliedSnap;
    private SpeedReading? _presentation;
    private LinuxOrbDefinition? _definition;
    private DateTimeOffset _lastActiveAt = DateTimeOffset.UtcNow;
    private bool _trafficVisible;
    private bool _advancedMenu;
    public event Action? DragSettled;
    public LinuxOrbDefinition Definition => _definition ?? _controller.OrbDefinitions.First();

    public OrbWindow(SpeedController controller, LinuxOrbDefinition definition, Action dashboard, Action history, Action toggle, Action quit)
        : this(controller, dashboard, history, toggle, quit)
    { ApplyDefinition(definition); }

    public void ApplyDefinition(LinuxOrbDefinition definition)
    {
        var restorePosition = _placed && _definition is { } previous && (previous.OrbX != definition.OrbX || previous.OrbY != definition.OrbY);
        if (_definition is null || _definition.SourceSet != definition.SourceSet || _definition.Metric != definition.Metric) _gauge.ResetReading();
        _definition = definition; Update();
        if (restorePosition && definition.OrbX is { } x && definition.OrbY is { } y)
            SetPosition(OrbPlacementMath.KeepOnScreen(new(x, y), WindowPixelSize, _displays, false));
    }

    public OrbWindow(SpeedController controller, Action dashboard, Action history, Action toggle, Action quit)
    {
        _controller = controller;
        Title = "Agent Speed · Codex 悬浮窗";
        Width = 244;
        Height = 310;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        var content = new StackPanel { Spacing = 2, Margin = new Thickness(8, 5) };
        content.Children.Add(_gauge);
        var label = new StackPanel { Spacing = 2 };
        _reading.FontWeight = FontWeight.Bold;
        foreach (var text in new[] { _reading, _titleText, _kindText, _nameText, _totalText, _costText, _trafficText })
        {
            text.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            label.Children.Add(text);
        }
        content.Children.Add(new Border
        {
            Background = Ui.OrbLabel, BorderBrush = Ui.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(8, 5), Child = label,
        });
        Content = content;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        var menu = new ContextMenu();
        foreach (var (name, action) in new (string, Action)[] { ("打开实时仪表", dashboard), ("打开用量历史", history), ("隐藏悬浮窗", toggle), ("退出 Agent Speed", quit) })
        {
            var item = new MenuItem { Header = name };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        ContextMenu = menu;
        ToolTip.SetTip(this, "拖动调整位置 · 双击打开实时仪表 · 右键打开菜单");
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) dashboard();
            else BeginMoveDrag(e);
        };
        PositionChanged += (_, _) =>
        {
            if (_placed && IsVisible && !_changingVisibility && !_adjustingPosition)
            {
                // Persist the screen-space client origin, matching the monitor working areas and Position setter.
                _lastPosition = this.PointToScreen(default);
                _saveTimer.Stop();
                _saveTimer.Start();
            }
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            KeepOnScreen(Definition.SnapToEdges);
            DragSettled?.Invoke();
            SavePosition();
        };
        Opened += (_, _) => Place();
        SizeChanged += (_, _) => QueueInputShape();
        ScalingChanged += (_, _) =>
        {
            if (_placed)
            {
                KeepOnScreen(false);
                SavePosition();
            }
            QueueInputShape();
        };
        Screens.Changed += ScreensChanged;
        Closing += (_, _) => RestoreInputShape();
        Closed += (_, _) =>
        {
            _disposed = true;
            _saveTimer.Stop();
            Screens.Changed -= ScreensChanged;
            controller.Changed -= Update;
            _gauge.VisualStateChanged -= UpdateTitle;
        };
        _gauge.VisualStateChanged += UpdateTitle;
        PointerEntered += (_, _) => Update();
        PointerExited += (_, _) => Update();
        controller.Changed += Update;
        Update();
    }

    private void Place()
    {
        RefreshDisplays();
        if (_placed) { SetPosition(OrbPlacementMath.KeepOnScreen(_lastPosition, WindowPixelSize, _displays, false)); return; }
        var size = WindowPixelSize;
        var saved = Definition;
        OrbPlacement? normalized = null;
        saved.Placements?.TryGetValue(_layoutKey, out normalized);
        var target = OrbPlacementMath.Restore(normalized, size, _displays);
        if (target is null && saved.OrbX is { } x && saved.OrbY is { } y
            && OrbPlacementMath.IsFullyVisible(new(x, y), size, _displays)) target = new(x, y);
        SetPosition(target ?? OrbPlacementMath.Default(size, _displays));
        _placed = true;
    }

    public void ShowFloating()
    {
        _changingVisibility = true;
        try { Show(); Place(); }
        finally { _changingVisibility = false; }
        // GNOME removes EWMH state on unmap. Avalonia retains its property values across Hide/Show, so explicitly
        // resend both hints after mapping rather than leaving a restored orb behind other windows or in the taskbar.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible) return;
            Topmost = false;
            Topmost = Definition.AlwaysOnTop;
            ShowInTaskbar = true;
            ShowInTaskbar = false;
            QueueInputShape();
        }, DispatcherPriority.Background);
    }

    public void HideFloating()
    {
        _changingVisibility = true;
        try { _saveTimer.Stop(); RestoreInputShape(); Hide(); }
        finally { _changingVisibility = false; }
    }

    public void SavePosition()
    {
        if (!_placed) return;
        var positions = Definition.Placements is { } previous ? new Dictionary<string, OrbPlacement>(previous) : [];
        if (OrbPlacementMath.Capture(_lastPosition, WindowPixelSize, _displays) is { } placement)
        { positions.Remove(_layoutKey); positions[_layoutKey] = placement; }
        while (positions.Count > 16) positions.Remove(positions.Keys.First());
        _definition = Definition with { OrbX = _lastPosition.X, OrbY = _lastPosition.Y, Placements = positions };
        _controller.SaveOrbDefinition(_definition);
    }

    public string CurrentVehicleKey => _gauge.CurrentVehicleKey;
    public long AnimationFrameCount => _gauge.AnimationFrameCount;
    public bool AnimationTimerEnabled => _gauge.AnimationTimerEnabled;
    public double DisplayedRate { get; private set; }
    public string ReadingText => _reading.Text ?? "";
    public string TitleText => _titleText.Text ?? "";
    public string TrafficText => _trafficText.Text ?? "";
    public bool NameVisible => _nameText.IsVisible;
    public bool IdleFadeApplied { get; private set; }
    public double EffectiveRedline => _gauge.Redline;
    public bool ClickThroughApplied { get; private set; }
    public double GaugeSize => _gauge.Width;
    public double WindowOpacity => Opacity;
    public string PlacementLayoutKey => _layoutKey;
    public OrbPlacement? CurrentPlacement => OrbPlacementMath.Capture(_lastPosition, WindowPixelSize, _displays);
    public long NativeWindowId => TryGetPlatformHandle()?.Handle.ToInt64() ?? 0;
    public object PlacementDiagnostics => new
    {
        layoutKey = _layoutKey, placement = CurrentPlacement, x = _lastPosition.X, y = _lastPosition.Y,
        pixelWidth = WindowPixelSize.Width, pixelHeight = WindowPixelSize.Height,
        id = Definition.Id, name = Definition.DisplayName, style = Definition.Style.ToString(), titleTheme = Definition.TitleTheme,
        sources = Definition.Sources, visible = IsVisible, reading = ReadingText, displayedRate = DisplayedRate,
        gaugeSize = GaugeSize, opacity = Opacity, snapToEdges = Definition.SnapToEdges,
        clickThroughRequested = Definition.ClickThrough, clickThroughApplied = ClickThroughApplied,
        nativeWindowId = NativeWindowId,
    };

    private PixelSize WindowPixelSize => new((int)Math.Ceiling(Width * RenderScaling), (int)Math.Ceiling(Height * RenderScaling));
    public PixelSize ClientPixelSize => WindowPixelSize;
    public PixelPoint ClientPosition => _lastPosition;
    public IReadOnlyList<OrbDisplay> ConnectedDisplays => _displays;
    public void MoveToPosition(PixelPoint target)
    {
        if (!_placed) return;
        target = OrbPlacementMath.KeepOnScreen(target, WindowPixelSize, _displays, false);
        if (target == _lastPosition) return;
        SetPosition(target); SavePosition();
    }

    public void ConfigureManagement(Action manage, Func<bool> split, Func<bool> merge, Func<string?> duplicate, Func<bool> remove, Func<bool> undo)
    {
        if (_advancedMenu || ContextMenu is not { } menu) return;
        _advancedMenu = true;
        menu.Items.Add(new Separator());
        foreach (var (name, action) in new (string, Action)[]
        {
            ("管理这个悬浮窗…", manage), ("按来源拆分", () => split()), ("合并所有悬浮窗到这里", () => merge()),
            ("复制这个悬浮窗", () => duplicate()), ("删除这个悬浮窗", () => remove()), ("撤销上一步悬浮窗操作", () => undo()),
        })
        { var item = new MenuItem { Header = name }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    }

    private void RefreshDisplays()
    {
        _displays = Screens.All.Select(screen => new OrbDisplay(
            (screen.DisplayName ?? "display") + "@" + screen.Bounds.X + "," + screen.Bounds.Y,
            screen.Bounds, screen.WorkingArea, screen.Scaling, screen.IsPrimary)).ToArray();
        _layoutKey = OrbPlacementMath.LayoutKey(_displays);
    }

    private void SetPosition(PixelPoint target)
    {
        _adjustingPosition = true;
        try { _lastPosition = target; Position = target; }
        finally { _adjustingPosition = false; }
    }

    private void KeepOnScreen(bool snap) => SetPosition(OrbPlacementMath.KeepOnScreen(_lastPosition, WindowPixelSize, _displays, snap));

    private void FitReadout()
    {
        if (Content is not Control content) return;
        content.Measure(new Size(Width, double.PositiveInfinity));
        var height = Math.Ceiling(content.DesiredSize.Height) + 2;
        if (!double.IsFinite(height) || height <= 0 || Math.Abs(Height - height) < 1) return;
        Height = height;
        if (_placed) KeepOnScreen(false);
        QueueInputShape();
    }

    private void ScreensChanged(object? sender, EventArgs args)
    {
        if (!_placed || _disposed) return;
        SavePosition();
        RefreshDisplays();
        OrbPlacement? saved = null;
        Definition.Placements?.TryGetValue(_layoutKey, out saved);
        SetPosition(OrbPlacementMath.Restore(saved, WindowPixelSize, _displays)
            ?? OrbPlacementMath.KeepOnScreen(_lastPosition, WindowPixelSize, _displays, false));
        SavePosition();
        QueueInputShape();
    }

    private void ApplyOptions()
    {
        if (_applyingOptions || _disposed) return;
        _applyingOptions = true;
        try
        {
            var settings = Definition;
            Title = "Agent Speed · " + settings.DisplayName + " 悬浮窗";
            var size = double.IsFinite(settings.Size) ? Math.Clamp(settings.Size, 96, 300) : 212;
            var trafficVisible = _controller.Settings.TrafficEnabled && settings.Traffic != OrbTraffic.Off;
            if (_appliedSize != size || _trafficVisible != trafficVisible)
            {
                var placement = _placed ? OrbPlacementMath.Capture(_lastPosition, WindowPixelSize, _displays) : null;
                _gauge.Width = _gauge.Height = _appliedSize = size;
                Width = size + 32;
                _trafficVisible = trafficVisible;
                Height = size + 132 + (trafficVisible ? 32 : 0);
                _reading.FontSize = Math.Clamp(size * 27 / 212, 13, 34);
                _titleText.FontSize = Math.Clamp(size * 12 / 212, 10, 14);
                _kindText.FontSize = _totalText.FontSize = _costText.FontSize = _nameText.FontSize = _trafficText.FontSize = Math.Clamp(size * 10 / 212, 9, 11);
                foreach (var text in new[] { _reading, _titleText, _kindText, _totalText, _costText, _nameText, _trafficText })
                { text.MaxWidth = size; text.TextTrimming = TextTrimming.CharacterEllipsis; }
                if (_placed) SetPosition(OrbPlacementMath.Restore(placement, WindowPixelSize, _displays)
                    ?? OrbPlacementMath.KeepOnScreen(_lastPosition, WindowPixelSize, _displays, false));
                QueueInputShape();
            }
            Opacity = double.IsFinite(settings.Opacity) ? Math.Clamp(settings.Opacity, .4, 1) : 1;
            Topmost = settings.AlwaysOnTop;
            _nameText.IsVisible = settings.ShowName;
            _trafficText.IsVisible = trafficVisible;
            if (_appliedClickThrough != settings.ClickThrough)
            {
                _appliedClickThrough = settings.ClickThrough;
                _shapeWarningReported = false;
                QueueInputShape();
            }
            if (_appliedSnap != settings.SnapToEdges)
            {
                _appliedSnap = settings.SnapToEdges;
                if (_placed) { KeepOnScreen(_appliedSnap); SavePosition(); }
            }
        }
        finally { _applyingOptions = false; }
    }

    private void QueueInputShape()
    {
        if (_shapePending || _disposed) return;
        _shapePending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _shapePending = false;
            if (!IsVisible || _disposed) return;
            ApplyInputShape();
        }, DispatcherPriority.Background);
    }

    private void ApplyInputShape()
    {
        var requested = Definition.ClickThrough;
        if (!requested && !ClickThroughApplied) return;
        var handle = TryGetPlatformHandle();
        string? error;
        var succeeded = handle is { Handle: not 0, HandleDescriptor: "XID" }
            ? X11InputShape.TrySet(handle.Handle, requested, out error)
            : FailedPlatform(out error);
        ClickThroughApplied = succeeded && requested;
        if (succeeded) _shapeWarningReported = false;
        else if (!_shapeWarningReported)
        {
            _shapeWarningReported = true;
            _controller.ReportError("无法应用悬浮窗鼠标穿透：" + error + " 可从托盘关闭鼠标穿透。");
        }
    }

    private static bool FailedPlatform(out string? error)
    {
        error = "当前窗口不是可用的 X11 窗口。";
        return false;
    }

    private void RestoreInputShape()
    {
        if (!ClickThroughApplied) return;
        var handle = TryGetPlatformHandle();
        if (handle is not { Handle: not 0, HandleDescriptor: "XID" }) { ClickThroughApplied = false; return; }
        if (X11InputShape.TrySet(handle.Handle, false, out var error)) ClickThroughApplied = false;
        else if (!_shapeWarningReported)
        {
            _shapeWarningReported = true;
            _controller.ReportError("恢复悬浮窗鼠标操作失败：" + error + " 可从托盘关闭悬浮窗后重试。");
        }
    }

    private void UpdateTitle()
    {
        _titleText.Text = _presentation is { IsAvailable: false } ? _presentation.KindLabel
            : (_presentation?.VehiclePrefix ?? "") + _gauge.SpeedTitleText;
        _titleText.Foreground = new SolidColorBrush(_gauge.HeatColor);
    }

    private void Update()
    {
        var definition = Definition;
        var metric = definition.Metric ?? _controller.Settings.Metric;
        var snapshot = _controller.GetSnapshotForSources(definition.SourceSet);
        ApplyOptions();
        _presentation = SpeedReadingPresentation.From(snapshot, metric);
        _gauge.Redline = definition.EffectiveRedline(metric, _controller.Settings);
        _gauge.Style = definition.Style;
        _gauge.TitleTheme = _controller.Settings.TitleThemes.OrbTheme(definition.TitleTheme);
        _gauge.Effects = _controller.Settings.Effects;
        _gauge.ReduceMotion = _controller.EffectiveReduceMotion;
        _gauge.Update(MetricPresentation.Project(snapshot, metric));
        DisplayedRate = _presentation.IsAvailable ? snapshot.Live.ValueFor(metric) : double.NaN;
        var money = _controller.GetLiveMoney(definition.SourceSet);
        var moneyReading = _presentation.IsAvailable ? money.InlineText + "/s" : "—";
        _reading.Text = definition.Unit == OrbUnit.Cost ? moneyReading : _presentation.Value + " tok/s";
        _gauge.ReadoutOverride = definition.Unit == OrbUnit.Cost ? _presentation.IsAvailable ? money.NumberText : "—" : null;
        _gauge.ReadoutUnitOverride = definition.Unit == OrbUnit.Cost ? money.Unit : null;
        _kindText.Text = _presentation.KindLabel;
        _costText.Text = definition.Unit == OrbUnit.Cost ? _presentation.Value + " tok/s · " + MetricPresentation.Name(metric) : moneyReading;
        _nameText.Text = definition.DisplayName;
        _totalText.Text = "今日" + (metric == SpeedMetric.Total ? "总量 " : "输出 ")
            + (snapshot.State.Kind == MonitorStateKind.Running ? UsageProjection.Compact(metric == SpeedMetric.Total ? snapshot.Today.Total : snapshot.Today.Output) : "—");
        var enabledTraffic = SourceSet.From(LinuxSources.Enabled(_controller.Settings));
        var trafficSources = definition.SourceSet is { } selectedTraffic ? selectedTraffic.Intersect(enabledTraffic) : enabledTraffic;
        _trafficText.Text = LinuxTrafficReadout.Describe(_controller.Traffic.Snapshot, trafficSources, definition.Traffic,
            _controller.Settings.TrafficUnit == TrafficUnit.Bits, _controller.Settings.TrafficIncludeTools);
        ToolTip.SetTip(_trafficText, _controller.Traffic.Coverage);
        if (snapshot.Live.Output > 0 || snapshot.GeneratingCount > 0 || snapshot.ToolsCount > 0) _lastActiveAt = DateTimeOffset.UtcNow;
        IdleFadeApplied = definition.FadeWhenIdle && !IsPointerOver && DateTimeOffset.UtcNow - _lastActiveAt >= TimeSpan.FromSeconds(10);
        Opacity = definition.Opacity * (IdleFadeApplied ? .4 : 1);
        ToolTip.SetTip(this, _presentation.Explanation + "\n" + _presentation.LastMeasurement
            + "\n拖动调整位置 · 双击打开实时仪表 · 右键打开菜单");
        UpdateTitle();
        FitReadout();
    }
}
