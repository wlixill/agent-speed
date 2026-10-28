using System.Globalization;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Styles;
using Avalonia;
using Avalonia.Media;

namespace AgentSpeed.Linux;

/// <summary>Avalonia vector drawings driven by the original platform-neutral style models.</summary>
internal sealed class OrbStylePainter
{
    private readonly NeedleModel _needle = new();
    private readonly LiquidModel _liquid = new();
    private readonly ReactorModel _reactor = new();
    private readonly MinimalModel _minimal = new();
    private OrbStyle _last;
    private double _time;
    private readonly double[] _surface = new double[65];

    private IOrbStyleModel? Model(OrbStyle style) => style switch
    { OrbStyle.Needle => _needle, OrbStyle.Liquid => _liquid, OrbStyle.Reactor => _reactor, OrbStyle.Minimal => _minimal, _ => null };
    public void Pulse(OrbStyle style, double heat, double strength) => Model(style)?.Pulse(heat, strength);
    public void Peak(OrbStyle style) => Model(style)?.Peak();

    public void Update(OrbStyle style, in OrbFrame frame, bool snap)
    {
        var model = Model(style);
        var next = frame with { Dt = Math.Clamp(frame.Time - _time, 0, .1) };
        if (snap || style != _last) model?.Snap(next); else model?.Update(next);
        _last = style; _time = frame.Time;
    }

    public void Draw(DrawingContext context, OrbStyle style, string value, string title, bool unavailable, string unit = "tok/s")
    {
        switch (style)
        {
            case OrbStyle.Needle: Needle(context); break;
            case OrbStyle.Liquid: Liquid(context); break;
            case OrbStyle.Reactor: Reactor(context); break;
            case OrbStyle.Minimal: Minimal(context); break;
        }
        var y = style == OrbStyle.Minimal ? 31 : style == OrbStyle.Needle ? 41 : 36;
        var size = style == OrbStyle.Minimal ? 20 : 12;
        if (style == OrbStyle.Liquid) context.DrawRectangle(Ui.OrbLabel, null, new Rect(21, 33, 58, 36), 6, 6);
        Text(context, unavailable ? "—" : value, y, size, Ui.Text, 54);
        Text(context, unit, y + size * 1.55, 4.8, Ui.Muted, 38);
        if (style == OrbStyle.Minimal) Text(context, title, 74, 5.5, Ui.Accent, 58);
    }

    private void Needle(DrawingContext context)
    {
        Arc(context, 41, -225, 270, new Pen(Ui.Line, 7.5));
        Arc(context, 46.55, -225 + 270 * NeedleGeometry.RedZoneStart,
            270 * (1 - NeedleGeometry.RedZoneStart), P(_needle.Palette.RedZone, 1.7));
        foreach (var tick in _needle.Ticks)
        {
            if (!tick.Visible) continue;
            context.DrawLine(P(tick.Color, tick.Width * 100), At(tick.InnerRadius * 100, tick.Degrees), At(tick.OuterRadius * 100, tick.Degrees));
        }
        if (_needle.ShowLamps)
            foreach (var lamp in _needle.Lamps)
                context.DrawEllipse(B(lamp.Color.MultiplyAlpha(lamp.Opacity)), null,
                    new(50 + lamp.X * 100, 50 + lamp.Y * 100), 1.6, 1.6);
        if (_needle.TraceVisible)
            Arc(context, 36.6, StyleMath.DialDegrees(0), 270 * Math.Clamp(_needle.TraceEnd, 0, 1),
                P(_needle.TraceColor(_needle.TraceEnd).MultiplyAlpha(_needle.TraceOpacityEnd), 1.1));
        foreach (var spark in _needle.Sparks)
            context.DrawLine(P(spark.Color.MultiplyAlpha(spark.Alpha), .4),
                new(50 + (spark.X + spark.StreakX) * 100, 50 + (spark.Y + spark.StreakY) * 100),
                new(50 + spark.X * 100, 50 + spark.Y * 100));
        using (context.PushTransform(Matrix.CreateRotation(_needle.NeedleDegrees * Math.PI / 180) * Matrix.CreateTranslation(50, 50)))
        {
            var blade = new StreamGeometry();
            using (var path = blade.Open())
            { path.BeginFigure(new(-9, -2.8), true); path.LineTo(new(44, -.6)); path.LineTo(new(44, .6)); path.LineTo(new(-9, 2.8)); path.EndFigure(true); }
            context.DrawGeometry(B(_needle.NeedleFill), null, blade);
        }
        context.DrawEllipse(Ui.GaugeGlass, new Pen(Ui.Line, 1.3), new(50, 50), 23.5, 23.5);
    }

    private void Liquid(DrawingContext context)
    {
        using var clip = context.PushGeometryClip(new EllipseGeometry(new Rect(4.5, 4.5, 91, 91)));
        _liquid.FillSurface(_surface);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(new(4.5, 99), true);
            for (var i = 0; i < _surface.Length; i++) path.LineTo(new(4.5 + i * 91d / (_surface.Length - 1), 50 + _surface[i] * 100));
            path.LineTo(new(95.5, 99)); path.EndFigure(true);
        }
        var fill = new LinearGradientBrush
        { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = [new GradientStop(C(_liquid.BodyTopColor), 0), new GradientStop(C(_liquid.BodyBottomColor), 1)] };
        context.DrawGeometry(fill, P(_liquid.MeniscusColor, .6), geometry);
        if (_liquid.CurrentsVisible)
            for (var lane = 0; lane < 3; lane++)
                for (var i = 1; i <= 40; i++)
                    context.DrawLine(P(_liquid.CurrentColor(lane), .35),
                        new(5 + (i - 1) * 2.25, 50 + _liquid.CurrentY(lane, (i - 1) / 40d) * 100),
                        new(5 + i * 2.25, 50 + _liquid.CurrentY(lane, i / 40d) * 100));
        foreach (var sprite in _liquid.Sprites)
        {
            var center = new Point(50 + sprite.X * 100, 50 + sprite.Y * 100);
            if (sprite.Kind == LiquidSpriteKind.Mote && sprite.Hot < .5)
                context.DrawEllipse(null, P(_liquid.BubbleRingColor.MultiplyAlpha(sprite.Alpha), .35), center, sprite.Radius * 100, sprite.Radius * 100);
            else context.DrawEllipse(B(sprite.Color.MultiplyAlpha(sprite.Alpha)), null, center, sprite.Radius * 100, sprite.Radius * 100);
        }
        context.DrawEllipse(null, P(_liquid.GlassWallColor, 1), new(50, 50), 45.5, 45.5);
        Arc(context, 41.5, 210, 42, P(_liquid.SpecularColor, 2));
    }

    private void Reactor(DrawingContext context)
    {
        context.DrawEllipse(null, new Pen(Ui.Line, .6), new(50, 50), 46.8, 46.8);
        for (var i = 0; i < _reactor.NotchCount; i++)
            context.DrawLine(new Pen(Ui.Muted, .5), At(45.6, 360d * i / _reactor.NotchCount), At(47.4, 360d * i / _reactor.NotchCount));
        for (var i = 0; i < 3; i++)
            Arc(context, 42.5, _reactor.RotorADegrees + i * 120, _reactor.ArcADegrees, P(_reactor.ArcAHeadColor, _reactor.ArcAWidth * 100));
        for (var i = 0; i < _reactor.CoilCount; i++)
            Arc(context, 38.3, _reactor.CoilAngleDegrees(i), _reactor.CoilArcDegrees,
                P(_reactor.CoilColor.MultiplyAlpha(_reactor.CoilOpacities[i]), 1.8));
        for (var i = 0; i < 2; i++)
            Arc(context, 33.8, _reactor.RotorCDegrees + i * 180, _reactor.ArcCDegrees, P(_reactor.ArcCHeadColor, .9));
        context.DrawEllipse(Ui.GaugeGlass, P(_reactor.RimColor, .9), new(50, 50), 30, 30);
        if (_reactor.FlareVisible) context.DrawEllipse(null, P(_reactor.FlareColor, _reactor.FlareWidth * 100), new(50, 50), _reactor.FlareRadius * 100, _reactor.FlareRadius * 100);
    }

    private void Minimal(DrawingContext context)
    {
        context.DrawEllipse(null, P(_minimal.TrackColor, _minimal.RingWidth * 100), new(50, 50), 42.5, 42.5);
        if (!_minimal.RingVisible) return;
        var sweep = _minimal.Lap1EndDegrees - _minimal.StartDegrees;
        var count = MinimalArcs.SegmentCount(sweep);
        for (var i = 0; i < count; i++)
        {
            var piece = MinimalArcs.Segment(_minimal.StartDegrees, sweep, i);
            Arc(context, 42.5, piece.FromDegrees, piece.ToDegrees - piece.FromDegrees + .2,
                P(_minimal.Lap1ColorAt(piece.Fraction), _minimal.RingWidth * 100));
        }
        if (_minimal.HasSecondLap) Arc(context, 42.5, _minimal.StartDegrees, _minimal.Lap2EndDegrees - _minimal.StartDegrees, P(_minimal.HeadColor, _minimal.RingWidth * 100));
        if (_minimal.HeadDotVisible)
            context.DrawEllipse(B(_minimal.HeadDotColor.MultiplyAlpha(_minimal.HeadDotOpacity)), null,
                At(42.5, _minimal.HeadDegrees), _minimal.HeadDotRadius * 100, _minimal.HeadDotRadius * 100);
    }

    private static Point At(double radius, double degrees) => new(50 + radius * Math.Cos(degrees * Math.PI / 180), 50 + radius * Math.Sin(degrees * Math.PI / 180));
    private static Color C(Rgba color) => Color.FromArgb(color.A8, color.R8, color.G8, color.B8);
    private static IBrush B(Rgba color) => new SolidColorBrush(C(color));
    private static Pen P(Rgba color, double width) => new(B(color), width, lineCap: PenLineCap.Round);
    private static void Arc(DrawingContext context, double radius, double start, double sweep, Pen pen)
    {
        if (sweep <= .01) return;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(At(radius, start), false);
            var pieces = Math.Max(1, (int)Math.Ceiling(sweep / 120));
            for (var i = 1; i <= pieces; i++) path.ArcTo(At(radius, start + sweep * i / pieces), new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            path.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }
    internal static void Text(DrawingContext context, string value, double y, double size, IBrush brush, double maxWidth)
    {
        var face = new Typeface("Noto Sans CJK SC", FontStyle.Normal, FontWeight.Bold);
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);
        if (text.Width > maxWidth) text = new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size * maxWidth / text.Width, brush);
        context.DrawText(text, new((100 - text.Width) / 2, y));
    }
}
