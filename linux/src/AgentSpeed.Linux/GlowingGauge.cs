using System.Diagnostics;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Gauge;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Titles;
using TitleThemeModel = AgentSpeed.Presentation.Titles.TitleTheme;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Styles;
using AgentSpeed.Presentation.Motion;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentSpeed.Linux;

/// <summary>Original vehicle artwork and shared motion model, rendered as a neon instrument on Linux.</summary>
internal sealed class GlowingGauge : Control
{
    private static readonly Geometry[] Segments = Enumerable.Range(0, GaugeLayout.SegmentCount).Select(Segment).ToArray();
    private static readonly Pen ShellBorder = new(Ui.GaugeBorder, .6);
    private static readonly Pen GlassBorder = new(Ui.GaugeGlassBorder, .5);
    private readonly VehicleArt _art = VehicleArt.LoadEmbedded();
    private readonly HeroStage _stage;
    private readonly VehiclePainter _painter;
    private readonly OrbStylePainter _stylePainter = new();
    private OrbStyle _style;
    private TitleThemeModel _titleTheme = TitleThemeModel.Vehicles;
    private bool _styleSnap = true;
    private readonly TitleTracker _titles = new();
    private readonly TierTracker _tiers = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer = new();
    private readonly EllipseGeometry _vehicleClip = new(new Rect(15, 15, 70, 70));
    private Window? _window;
    private double _redline = 400;
    private double _target;
    private string? _readoutOverride;
    private string? _readoutUnitOverride;
    public string? ReadoutOverride
    {
        get => _readoutOverride;
        set { if (_readoutOverride == value) return; _readoutOverride = value; InvalidateVisual(); }
    }
    public string? ReadoutUnitOverride
    {
        get => _readoutUnitOverride;
        set { if (_readoutUnitOverride == value) return; _readoutUnitOverride = value; InvalidateVisual(); }
    }
    private double _pulseAt = -10;
    private double _peakAt = -10;
    private DateTimeOffset? _responseAt;
    private double _lastPeak;
    private bool _first = true;
    private bool _hasRunningSnapshot;
    private EffectsIntensity _effects = EffectsIntensity.Standard;
    private bool _reduceMotion;
    private (double Target, double Redline, SpeedTitle Title, bool Dark, EffectsIntensity Effects, bool Reduce)? _staticVisual;
    private SpeedSnapshot _snapshot = SpeedSnapshot.Empty;

    public GlowingGauge()
    {
        _stage = new(_art);
        _painter = new(_art);
        _timer.Interval = TimeSpan.FromSeconds(1d / 15);
        _timer.Tick += (_, _) => Tick();
        ClipToBounds = false;
    }

    public double Redline
    {
        get => _redline;
        set { _redline = double.IsFinite(value) ? Math.Clamp(value, HeatMath.MinRedline, HeatMath.MaxRedlineTotal) : 400; Tick(); }
    }
    public string CurrentVehicleKey => _stage.CurrentKey;
    public long AnimationFrameCount { get; private set; }
    public bool AnimationTimerEnabled => _timer.IsEnabled;
    public void ResetReading() { _first = true; _styleSnap = true; _staticVisual = null; _responseAt = null; _lastPeak = 0; }
    public OrbStyle Style
    {
        get => _style;
        set { if (_style == value) return; _style = Enum.IsDefined(value) ? value : OrbStyle.Led; _styleSnap = true; _staticVisual = null; Tick(); }
    }
    public TitleThemeModel TitleTheme
    {
        get => _titleTheme;
        set { if (_titleTheme == value) return; _titleTheme = value ?? TitleThemeModel.Vehicles; _staticVisual = null; Tick(); VisualStateChanged?.Invoke(); }
    }
    public EffectsIntensity Effects
    {
        get => _effects;
        set
        {
            var next = Enum.IsDefined(value) ? value : EffectsIntensity.Standard;
            if (_effects == next) return;
            _effects = next; ResetMotionOptions();
        }
    }
    public bool ReduceMotion
    {
        get => _reduceMotion;
        set { if (_reduceMotion == value) return; _reduceMotion = value; ResetMotionOptions(); }
    }
    private bool Animated => !ReduceMotion && Effects != EffectsIntensity.Off;

    private void ResetMotionOptions()
    {
        _first = true;
        _staticVisual = null;
        Tick();
        SyncTimer();
    }
    public Color HeatColor
    {
        get { var color = HeroPalette.Stroke(_stage.Heat, Ui.IsDark); return Color.FromRgb(color.R8, color.G8, color.B8); }
    }
    public event Action? VisualStateChanged;
    public string SpeedTitleText => _snapshot.State.Kind switch
    {
        MonitorStateKind.Starting => "读取中",
        MonitorStateKind.NoSources => "无记录",
        _ when !double.IsFinite(_snapshot.Live.Output) => "速度未知",
        _ => LinuxTitleText.Label(TitleTheme, (int)_stage.Title),
    };

    private static string TitleText(SpeedTitle title) => title switch
    {
        SpeedTitle.Walk => "步行", SpeedTitle.Bicycle => "自行车", SpeedTitle.EBike => "电动自行车",
        SpeedTitle.Motorcycle => "摩托车", SpeedTitle.Car => "汽车", SpeedTitle.SportsCar => "跑车",
        SpeedTitle.BulletTrain => "高铁", SpeedTitle.RaceCar => "赛车", SpeedTitle.Maglev => "磁悬浮",
        SpeedTitle.Jetliner => "喷气客机", SpeedTitle.FighterJet => "战斗机", SpeedTitle.Hypersonic => "极音速飞机",
        SpeedTitle.Rocket => "火箭", _ => "待机",
    };

    public void Update(SpeedSnapshot snapshot)
    {
        var running = snapshot.State.Kind == MonitorStateKind.Running && double.IsFinite(snapshot.Live.Output);
        _target = running ? Math.Max(0, snapshot.Live.Output) : 0;
        if (running != _hasRunningSnapshot) _first = true;
        _hasRunningSnapshot = running;
        var responseAt = snapshot.LastResponse?.EndedAt;
        if (running && _responseAt is not null && responseAt is not null && responseAt != _responseAt)
        {
            _pulseAt = _clock.Elapsed.TotalSeconds;
            if (Animated) _stylePainter.Pulse(Style, HeatMath.Heat(_target, Redline),
                Math.Clamp(Math.Log10(Math.Max(1, snapshot.LastResponse?.OutputTokens ?? 1)) / 4, .3, 1.25));
        }
        _responseAt = responseAt;
        if (_snapshot.State.Kind == MonitorStateKind.Running && snapshot.PeakToday.Output > _lastPeak && _lastPeak > 0)
        {
            _peakAt = _clock.Elapsed.TotalSeconds;
            if (Animated) _stylePainter.Peak(Style);
        }
        _lastPeak = snapshot.PeakToday.Output;
        _snapshot = snapshot;
        Tick();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null) _window.PropertyChanged += WindowChanged;
        Ui.PaletteChanged += PaletteChanged;
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        if (_window is not null) _window.PropertyChanged -= WindowChanged;
        Ui.PaletteChanged -= PaletteChanged;
        _window = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) SyncTimer();
    }

    private void WindowChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty || change.Property == Window.WindowStateProperty) SyncTimer();
    }

    private void PaletteChanged()
    {
        _staticVisual = null;
        Tick();
    }

    private void SyncTimer()
    {
        if (_timer is null) return;
        if (Animated && _window is { IsVisible: true } && _window.WindowState != WindowState.Minimized && IsVisible)
        {
            if (!_timer.IsEnabled) { _first = true; Tick(); _timer.Start(); }
        }
        else _timer.Stop();
    }

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var snapped = _first;
        if (_first)
        {
            _titles.Reset(SpeedTitles.Raw(_target, Redline));
            _tiers.Reset(HeatMath.RawTier(_target, Redline));
        }
        _titles.Update(_target, Redline, now);
        _tiers.Update(_target, Redline, now);
        // The generic ladder selects level 1 while waiting for its idle deadline. At zero output that would replace
        // a coasting plane/rocket with a walker. Preserve the last actual actor until the original idle deadline.
        var title = _target < HeatMath.CruiseThreshold && _titles.Displayed != SpeedTitle.Idle ? _stage.Title : _titles.Displayed;
        var previousTitle = _stage.Title;
        _stage.Update(now, _target, Redline, _tiers.Displayed, title,
            new HeroOptions(Effects, ReduceMotion, Dark: Ui.IsDark), _first || !Animated);
        var frame = new OrbFrame(now, 0, _stage.Heat, _stage.Overdrive, HeatMath.Heat(_target, Redline),
            HeatMath.Overdrive(_target, Redline), _tiers.Displayed, Math.Max(96, Bounds.Width),
            new EffectsPolicy(Effects, ReduceMotion), Ui.IsDark, Unavailable: !_hasRunningSnapshot);
        _stylePainter.Update(Style, frame, _first || _styleSnap || !Animated);
        _styleSnap = false;
        _first = false;
        _timer.Interval = TimeSpan.FromSeconds(_target >= .5 || _stage.StartupRunning ? 1d / 30 : 1d / 15);
        if (_stage.Title != previousTitle || snapped) VisualStateChanged?.Invoke();
        var state = (_target, Redline, _stage.Title, Ui.IsDark, Effects, ReduceMotion);
        var repaint = Animated || _staticVisual != state;
        _staticVisual = state;
        if (repaint && _window is { IsVisible: true } && _window.WindowState != WindowState.Minimized && IsVisible)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        AnimationFrameCount++;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        using var transform = context.PushTransform(Matrix.CreateScale(side / 100, side / 100)
            * Matrix.CreateTranslation((Bounds.Width - side) / 2, (Bounds.Height - side) / 2));
        var now = _clock.Elapsed.TotalSeconds;
        var heat = _stage.Heat;
        var accent = HeatPalette.Sample(heat);
        var effectScale = Effects switch { EffectsIntensity.Off => 0, EffectsIntensity.Soft => .45, EffectsIntensity.Max => 1.35, _ => 1 };
        var breathing = Animated ? .8 + .2 * Math.Sin(now * 1.7) : .85;
        for (var i = Effects == EffectsIntensity.Off ? -1 : 4; i >= 0; i--)
            context.DrawEllipse(null, _painter.Pen(accent, (.018 + .016 * (4 - i)) * breathing * (.35 + heat) * effectScale, 2 + i * 2),
                new Point(50, 50), 45.8, 45.8);
        context.DrawEllipse(Ui.GaugeShell, ShellBorder, new(50, 50), 44.5, 44.5);
        context.DrawEllipse(Ui.GaugeGlass, GlassBorder, new(50, 50), 34.7, 34.7);
        if (Style != OrbStyle.Led)
        {
            _stylePainter.Draw(context, Style, ReadoutOverride ?? MetricPresentation.Rate(_target), SpeedTitleText,
                !_hasRunningSnapshot, ReadoutUnitOverride ?? "tok/s");
            return;
        }
        for (var i = 0; i < Segments.Length; i++)
        {
            var color = HeroPalette.Stroke(GaugeLayout.SegmentHeat(i), Ui.IsDark);
            var fill = GaugeLayout.SegmentFill(heat, i);
            if (fill > .01 && effectScale > 0)
            {
                context.DrawGeometry(null, _painter.Pen(color, fill * .06 * effectScale, 3.2), Segments[i]);
                context.DrawGeometry(null, _painter.Pen(color, fill * .15 * effectScale, 1.3), Segments[i]);
            }
            context.DrawGeometry(_painter.Brush(color, (Ui.IsDark ? .2 : .12) + (Ui.IsDark ? .8 : .88) * fill), null, Segments[i]);
        }
        if (TitleTheme.ShowsVehicleArt)
        {
            using (context.PushGeometryClip(_vehicleClip))
            using (context.PushTransform(Matrix.CreateScale(.72, .72) * Matrix.CreateTranslation(14, 14)))
                _painter.Draw(context, _stage);
        }
        else
        {
            OrbStylePainter.Text(context, SpeedTitleText, 39, 14, new SolidColorBrush(HeatColor), 57);
            OrbStylePainter.Text(context, "LEVEL " + (int)_stage.Title, 59, 5, Ui.Muted, 50);
        }
        if (Animated && heat > .02 && Effects >= EffectsIntensity.Standard)
        {
            var angle = (now * (1.4 + heat) + .4 * heat) % (2 * Math.PI);
            var comet = new Point(50 + 46.3 * Math.Cos(angle), 50 + 46.3 * Math.Sin(angle));
            context.DrawEllipse(_painter.Brush(accent, .08), null, comet, 3.2, 3.2);
            context.DrawEllipse(_painter.Brush(accent, .32), null, comet, 1.8, 1.8);
            context.DrawEllipse(_painter.Brush(HeatPalette.WhiteHot, .9), null, comet, .65, .65);
        }
        var pulse = (now - _pulseAt) / .85;
        if (Animated && Effects >= EffectsIntensity.Standard && pulse is >= 0 and <= 1)
            context.DrawEllipse(null, _painter.Pen(accent, (1 - pulse) * .5, .8), new(50, 50),
                34 + 15 * pulse, 34 + 15 * pulse);
        var peak = now - _peakAt;
        if (Animated && Effects >= EffectsIntensity.Standard && peak is >= 0 and < 1)
            context.DrawEllipse(null, _painter.Pen(HeatPalette.Gold, (1 - peak) * .55, 1.4), new(50, 50),
                43 + peak * 8, 43 + peak * 8);
        context.DrawEllipse(_painter.Brush(HeatPalette.SourceColor(SourceKind.Codex), .35 + heat * .65), null,
            new(50, 87), 1.1, 1.1);
    }

    private static Geometry Segment(int index)
    {
        var arc = GaugeLayout.Segment(index);
        Point At(double radius, double angle)
        {
            var radians = angle * Math.PI / 180;
            return new(50 + radius * 100 * Math.Cos(radians), 50 + radius * 100 * Math.Sin(radians));
        }
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(At(arc.OuterRadius, arc.StartDegrees), true);
        context.ArcTo(At(arc.OuterRadius, arc.EndDegrees), new Size(arc.OuterRadius * 100, arc.OuterRadius * 100), 0, false, SweepDirection.Clockwise);
        context.LineTo(At(arc.InnerRadius, arc.EndDegrees));
        context.ArcTo(At(arc.InnerRadius, arc.StartDegrees), new Size(arc.InnerRadius * 100, arc.InnerRadius * 100), 0, false, SweepDirection.CounterClockwise);
        context.EndFigure(true);
        return geometry;
    }
}
