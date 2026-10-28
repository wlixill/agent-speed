using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Hero;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Settings;
using Avalonia;
using Avalonia.Media;

namespace AgentSpeed.Linux;

/// <summary>Avalonia renderer for the original 100-unit vehicle art and platform-neutral motion model.</summary>
internal sealed class VehiclePainter
{
    private readonly VehicleArt _art;
    private readonly Dictionary<VehiclePart, Geometry> _shapes = [];
    private readonly Dictionary<(uint Color, int Width), Pen> _pens = [];
    private readonly Dictionary<uint, SolidColorBrush> _brushes = [];
    private readonly Geometry[] _clouds;
    private readonly RigPose _pose = new();

    public VehiclePainter(VehicleArt art)
    {
        _art = art;
        foreach (var vehicle in art.Vehicles)
        foreach (var part in vehicle.Parts)
            if (part.Path is { } path) _shapes[part] = Build(path, part.Role is PartRole.Flame or PartRole.FlameCore);
        _clouds = art.Scenery.Clouds.Select(path => Build(path, false)).ToArray();
    }

    public void Draw(DrawingContext context, HeroStage stage)
    {
        var paint = stage.Paint();
        using var opacity = context.PushOpacity(paint.Presence);
        DrawScenery(context, stage, paint.Color);
        if (stage.Animated && stage.Previous is { } previous) DrawActor(context, stage, previous, true);
        DrawActor(context, stage, stage.Current, false);
        foreach (ref readonly var streak in stage.Streaks.Items)
        {
            if (!stage.Animated || !streak.Alive) continue;
            var alpha = streak.Alpha * Math.Clamp(1 - streak.Age / .4, 0, 1);
            context.DrawLine(Pen(paint.Color, alpha, .55), new(streak.X, streak.Y),
                new(streak.X - streak.DirectionX * streak.Length, streak.Y - streak.DirectionY * streak.Length));
        }
        foreach (ref readonly var particle in stage.Particles.Items)
        {
            if (!stage.Animated || !particle.Alive || particle.Progress < 0) continue;
            var alpha = (1 - particle.Progress) * .7;
            var point = new Point(particle.X, particle.Y);
            if (particle.Kind == HeroParticleKind.Spark)
                context.DrawLine(Pen(paint.Hot, alpha, .55), point,
                    point - new Vector(particle.VelocityX * .022, particle.VelocityY * .022));
            else
            {
                var radius = particle.Size + particle.Grow * particle.Progress;
                context.DrawEllipse(null, Pen(paint.Color, alpha, .55), point, radius, radius);
            }
        }
        if (stage.Animated && stage.Flash is { } flash)
        {
            for (var ring = 2; ring >= 0; ring--)
                context.DrawEllipse(null, Pen(paint.Hot, flash.Alpha * (.13 + .1 * (2 - ring)), 2 + ring * 3),
                    P(flash.Center), flash.Radius, flash.Radius);
        }
    }

    private void DrawActor(DrawingContext context, HeroStage stage, HeroActor actor, bool outgoing)
    {
        var vehicle = _art.Vehicles[actor.Vehicle];
        var placement = stage.Animated ? stage.Place(actor, outgoing) : new ActorPlacement(0, 0, 1, 0, 1, 0, 0);
        if (placement.Alpha <= .002) return;
        var fx = stage.Animated ? stage.Startup(actor, outgoing) : StartupFx.None;
        var motion = stage.Motion(vehicle, placement, fx);
        var paint = stage.Paint();
        var unsprung = Matrix.CreateTranslation(motion.OffsetX, motion.OffsetY);
        var sprung = AtRotation(motion.Pivot, motion.PitchDegrees * Math.PI / 180)
            * Matrix.CreateTranslation(motion.OffsetX, motion.SprungY);
        using var opacity = context.PushOpacity(placement.Alpha);
        DrawTrails(context, stage, vehicle, motion, fx, paint.Color);
        var rigged = stage.PoseRig(vehicle, fx, _pose);
        if (rigged) DrawLimbs(context, stage, 0, _pose.FarCount, unsprung, paint.Color);
        foreach (var part in vehicle.Parts)
        {
            if (stage.Level == EffectsIntensity.Off && part.Role is PartRole.Glow or PartRole.Flame or PartRole.FlameCore or PartRole.Shock) continue;
            var role = _art.Role(part.Role);
            var matrix = part.Sprung ? sprung : unsprung;
            var factor = role.Opacity;
            var color = role.Hot ? paint.Hot : paint.Color;
            if (part.Role == PartRole.Rider && rigged && vehicle.Pedal is not null)
                matrix = Matrix.CreateTranslation(_pose.RiderOffset.X, _pose.RiderOffset.Y) * matrix;
            if (part.Spin is { } spin)
                matrix = AtRotation(spin.Pivot, stage.SpinAngle(vehicle, spin)) * matrix;
            if (part.Role == PartRole.Light) factor *= stage.LightFactor(fx);
            if (part.Role == PartRole.Glow)
            {
                factor *= fx.Hover * (.5 + .5 * stage.Band) * stage.Wake;
                matrix = Matrix.CreateTranslation(0, motion.Hover / 2) * matrix;
            }
            if (part.Role == PartRole.Shock && part.Path is { } cone)
            {
                var shock = stage.ShockCone(fx);
                matrix = AtScale(cone.Start, 1, shock.Scale) * matrix;
                factor *= shock.Opacity;
            }
            if (part.Role is PartRole.Flame or PartRole.FlameCore)
            {
                if (part.Nozzle is not { } nozzle) continue;
                var flame = stage.FlameScale(fx);
                if (flame.Length <= .02) continue;
                var angle = Math.Atan2(nozzle.Direction.Y, nozzle.Direction.X);
                matrix = Matrix.CreateTranslation(-nozzle.At.X, -nozzle.At.Y) * Matrix.CreateRotation(-angle)
                    * Matrix.CreateScale(flame.Length, flame.Width) * Matrix.CreateRotation(angle)
                    * Matrix.CreateTranslation(nozzle.At.X, nozzle.At.Y) * matrix;
                factor *= stage.Wake;
            }
            if (factor <= .003) continue;
            using var transform = context.PushTransform(matrix);
            var width = _art.StrokeWidth * role.Weight;
            if (part.Circle is { } circle)
                Circle(context, stage, P(circle.Center), circle.Radius, color, factor, width, role.Glow);
            else if (_shapes.TryGetValue(part, out var geometry))
            {
                if (part.Role is PartRole.Flame or PartRole.FlameCore)
                    context.DrawGeometry(Brush(part.Role == PartRole.FlameCore ? paint.Core : paint.Hot,
                        factor * role.FillOpacity), null, geometry);
                if (width > .005) Shape(context, stage, geometry, color, factor, width, role.Glow);
            }
        }
        if (rigged)
        {
            DrawLimbs(context, stage, _pose.FarCount, _pose.Count, unsprung, paint.Color);
            if (_pose.HasHead)
            {
                using var transform = context.PushTransform(unsprung);
                var role = _art.Role(PartRole.Rider);
                Circle(context, stage, P(_pose.Head.Center), _pose.Head.Radius, paint.Color, role.Opacity,
                    _art.StrokeWidth * role.Weight, role.Glow);
            }
        }
        if (stage.Sweep is { } sweep && !outgoing)
        {
            using var clip = context.PushClip(new Rect(sweep.X - 2, 0, 4, 100));
            foreach (var part in vehicle.Parts)
            {
                if (!_shapes.TryGetValue(part, out var shape) || part.Role is PartRole.Flame or PartRole.FlameCore) continue;
                using var transform = context.PushTransform(part.Sprung ? sprung : unsprung);
                context.DrawGeometry(null, Pen(paint.Hot, sweep.Amount * .75, _art.StrokeWidth * _art.Role(part.Role).Weight), shape);
            }
        }
    }

    private void DrawLimbs(DrawingContext context, HeroStage stage, int start, int end, Matrix matrix, Rgba color)
    {
        using var transform = context.PushTransform(matrix);
        for (var i = start; i < end; i++)
        {
            var limb = _pose[i];
            var role = _art.Role(limb.Ink == LimbInk.Crank ? PartRole.Crank : PartRole.Rider);
            var alpha = role.Opacity * (limb.Ink == LimbInk.Far ? .42 : 1);
            for (var p = 1; p < limb.Count; p++)
            {
                if (stage.Level != EffectsIntensity.Off && Glow(stage) > .01)
                    context.DrawLine(Pen(color, alpha * Glow(stage) * .15, _art.StrokeWidth * role.Weight + 3),
                        P(limb[p - 1]), P(limb[p]));
                context.DrawLine(Pen(color, alpha, _art.StrokeWidth * role.Weight), P(limb[p - 1]), P(limb[p]));
            }
        }
    }

    private void DrawTrails(DrawingContext context, HeroStage stage, Vehicle vehicle, ActorMotion motion, StartupFx fx, Rgba color)
    {
        if (!stage.Animated || stage.Wake < .05 || stage.Ground < 3) return;
        using var transform = context.PushTransform(Matrix.CreateTranslation(motion.OffsetX, motion.OffsetY));
        foreach (var emitter in vehicle.Emitters)
        {
            if (emitter.Kind == EmitterKind.Trail)
            {
                for (var i = 0; i < emitter.Lanes.Count; i++)
                {
                    var length = emitter.Length * (.5 + .5 * stage.Band)
                        * (1 + .15 * Math.Sin(stage.Time * (5.3 + i) * 2 * Math.PI));
                    var y = emitter.At.Y + emitter.Lanes[i];
                    context.DrawLine(Pen(color, .28 * stage.Wake, .7), new(emitter.At.X - 2, y), new(emitter.At.X - length, y));
                }
            }
            else if (emitter.Kind == EmitterKind.Contrail)
            {
                var length = (emitter.Weak ? 22 : 46) * fx.Contrail;
                for (var i = 0; i < 16; i++)
                {
                    Point Trail(double d) => new(emitter.At.X - d,
                        emitter.At.Y + .6 * Math.Sin(stage.Time * 2 - d / 12) * d / Math.Max(length, 1));
                    context.DrawLine(Pen(color, (emitter.Weak ? .2 : .45) * stage.Wake * (1 - i / 16d), .7),
                        Trail(length * i / 16), Trail(length * (i + 1) / 16));
                }
            }
        }
    }

    private void DrawScenery(DrawingContext context, HeroStage stage, Rgba color)
    {
        var vehicle = stage.CurrentVehicle;
        var scenery = _art.Scenery;
        var distance = stage.DistanceFor(vehicle);
        var alpha = .16 + .18 * stage.Wake;
        if (vehicle.GroundY is { } y)
        {
            if (vehicle.Scenery == SceneryKind.Road)
            {
                var spacing = scenery.RoadDash + scenery.RoadGap;
                for (var x = -distance * scenery.RoadParallax % spacing; x < 110; x += spacing)
                    context.DrawLine(Pen(color, alpha, .6), new(x, y), new(x + scenery.RoadDash, y));
            }
            else
            {
                context.DrawLine(Pen(color, alpha, .65), new(-5, y), new(105, y));
                context.DrawLine(Pen(color, alpha * .6, .55), new(-5, y + 3), new(105, y + 3));
                var spacing = vehicle.Scenery == SceneryKind.Rail ? scenery.SleeperSpacing : scenery.PylonSpacing;
                for (var x = -distance % spacing; x < 110; x += spacing)
                    context.DrawLine(Pen(color, alpha * .6, .5), new(x, y), new(x - 2, y + (vehicle.Scenery == SceneryKind.Rail ? 3 : 10)));
            }
        }
        else if (vehicle.Scenery == SceneryKind.Sky)
        {
            for (var i = 0; i < scenery.SkyLayers.Count; i++)
            {
                var layer = scenery.SkyLayers[i];
                var offset = (distance * layer.Parallax + i * 31) % 72;
                for (var x = 104 - offset; x > -25; x -= 72)
                {
                    using var transform = context.PushTransform(Matrix.CreateScale(layer.Scale, layer.Scale) * Matrix.CreateTranslation(x - 8, layer.Y));
                    context.DrawGeometry(null, Pen(color, layer.Opacity * alpha, .6 / layer.Scale), _clouds[i % _clouds.Length]);
                }
            }
        }
        else if (vehicle.Scenery == SceneryKind.Stratosphere)
        {
            context.DrawEllipse(null, Pen(color, alpha * .6, .8), new(50, scenery.HorizonCenterY), scenery.HorizonRadius, scenery.HorizonRadius);
        }
        else if (vehicle.Scenery == SceneryKind.Space)
        {
            for (var i = 0; i < scenery.Stars; i++)
            {
                var x = (i * 41.37 - distance * scenery.SpaceParallax) % 110;
                if (x < 0) x += 110;
                var starY = i * 29.43 % 100;
                context.DrawEllipse(Brush(color, stage.Animated ? .2 + .15 * Math.Sin(stage.Time + i) : .27), null, new(x, starY), .35, .35);
            }
        }
    }

    private void Shape(DrawingContext context, HeroStage stage, Geometry geometry, Rgba color, double alpha, double width, double glow)
    {
        if (stage.Level != EffectsIntensity.Off && glow > .01 && Glow(stage) > .01)
        {
            context.DrawGeometry(null, Pen(color, alpha * glow * Glow(stage) * .08, width + 5), geometry);
            context.DrawGeometry(null, Pen(color, alpha * glow * Glow(stage) * .18, width + 2.2), geometry);
        }
        context.DrawGeometry(null, Pen(color, alpha, width), geometry);
    }

    private void Circle(DrawingContext context, HeroStage stage, Point center, double radius, Rgba color, double alpha, double width, double glow)
    {
        if (stage.Level != EffectsIntensity.Off && glow > .01 && Glow(stage) > .01)
            context.DrawEllipse(null, Pen(color, alpha * glow * Glow(stage) * .15, width + 3), center, radius, radius);
        context.DrawEllipse(null, Pen(color, alpha, width), center, radius, radius);
    }

    private static double Glow(HeroStage stage) => stage.Animated ? stage.GlowOpacity : (stage.Options.Dark ? .3 : .14) * stage.Wake;

    internal SolidColorBrush Brush(Rgba color, double alpha = 1)
    {
        var a = (byte)Math.Clamp(Math.Round(alpha * color.A * 32) * 255 / 32, 0, 255);
        var key = ((uint)a << 24) | ((uint)color.R8 << 16) | ((uint)color.G8 << 8) | color.B8;
        if (_brushes.TryGetValue(key, out var cached)) return cached;
        if (_brushes.Count > 4096) _brushes.Clear();
        return _brushes[key] = new SolidColorBrush(Color.FromArgb(a, color.R8, color.G8, color.B8));
    }

    internal Pen Pen(Rgba color, double alpha, double width)
    {
        var brush = Brush(color, alpha);
        var key = ((uint)brush.Color.ToUInt32(), (int)Math.Round(width * 20));
        if (_pens.TryGetValue(key, out var cached)) return cached;
        if (_pens.Count > 4096) _pens.Clear();
        return _pens[key] = new Pen(brush, key.Item2 / 20d, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    }

    private static Matrix AtRotation(PointD center, double angle) => Matrix.CreateTranslation(-center.X, -center.Y)
        * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(center.X, center.Y);
    private static Matrix AtScale(PointD center, double x, double y) => Matrix.CreateTranslation(-center.X, -center.Y)
        * Matrix.CreateScale(x, y) * Matrix.CreateTranslation(center.X, center.Y);
    private static Point P(PointD point) => new(point.X, point.Y);

    private static Geometry Build(PathData path, bool filled)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        var open = false;
        foreach (var segment in path.Segments)
        {
            switch (segment.Verb)
            {
                case PathVerb.Move:
                    if (open) context.EndFigure(false);
                    context.BeginFigure(P(segment.P1), filled);
                    open = true;
                    break;
                case PathVerb.Line: context.LineTo(P(segment.P1)); break;
                case PathVerb.Quad: context.QuadraticBezierTo(P(segment.P1), P(segment.P2)); break;
                case PathVerb.Cubic: context.CubicBezierTo(P(segment.P1), P(segment.P2), P(segment.P3)); break;
                case PathVerb.Close: context.EndFigure(true); open = false; break;
            }
        }
        if (open) context.EndFigure(false);
        return geometry;
    }
}
