using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;

namespace AgentSpeed.Linux;

/// <summary>Position within a display's available window travel, independent of the orb's current size.</summary>
public sealed record OrbPlacement(string DisplayKey, double X, double Y);

public sealed record OrbDisplay(string Key, PixelRect Bounds, PixelRect WorkingArea, double Scaling, bool IsPrimary);

/// <summary>Pure screen geometry shared by restoration, dragging, and display changes.</summary>
public static class OrbPlacementMath
{
    public static string LayoutKey(IReadOnlyList<OrbDisplay> displays)
    {
        var text = string.Join(";", displays.OrderBy(display => display.Key, StringComparer.Ordinal)
            .ThenBy(display => display.Bounds.X).ThenBy(display => display.Bounds.Y)
            .Select(display => string.Create(CultureInfo.InvariantCulture,
                $"{display.Key.Length}:{display.Key}:{display.Bounds.X},{display.Bounds.Y},{display.Bounds.Width},{display.Bounds.Height}:{display.Scaling:0.###}:{display.IsPrimary}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
    }

    public static OrbPlacement? Capture(PixelPoint position, PixelSize size, IReadOnlyList<OrbDisplay> displays)
    {
        var display = SelectDisplay(position, size, displays);
        if (display is null) return null;
        var area = display.WorkingArea;
        return new(display.Key, Fraction(position.X - area.X, area.Width - size.Width),
            Fraction(position.Y - area.Y, area.Height - size.Height));
    }

    public static PixelPoint? Restore(OrbPlacement? placement, PixelSize size, IReadOnlyList<OrbDisplay> displays)
    {
        if (placement is null || !double.IsFinite(placement.X) || !double.IsFinite(placement.Y)) return null;
        var display = displays.FirstOrDefault(display => display.Key == placement.DisplayKey);
        if (display is null) return null;
        var area = display.WorkingArea;
        return new(area.X + (int)Math.Round(Math.Max(0, area.Width - size.Width) * Math.Clamp(placement.X, 0, 1)),
            area.Y + (int)Math.Round(Math.Max(0, area.Height - size.Height) * Math.Clamp(placement.Y, 0, 1)));
    }

    public static PixelPoint KeepOnScreen(PixelPoint position, PixelSize size, IReadOnlyList<OrbDisplay> displays,
        bool snapToEdges, double snapDistance = 18)
    {
        var display = SelectDisplay(position, size, displays);
        if (display is null) return position;
        var area = display.WorkingArea;
        var right = Math.Max(area.X, area.Right - size.Width);
        var bottom = Math.Max(area.Y, area.Bottom - size.Height);
        var x = Math.Clamp(position.X, area.X, right);
        var y = Math.Clamp(position.Y, area.Y, bottom);
        if (snapToEdges)
        {
            var distance = Math.Max(0, snapDistance) * display.Scaling;
            x = Snap(x, area.X, right, distance);
            y = Snap(y, area.Y, bottom, distance);
        }
        return new(x, y);
    }

    public static bool IsFullyVisible(PixelPoint position, PixelSize size, IReadOnlyList<OrbDisplay> displays) =>
        displays.Any(display => display.WorkingArea.Contains(new PixelRect(position, size)));

    public static PixelPoint Default(PixelSize size, IReadOnlyList<OrbDisplay> displays)
    {
        var display = displays.FirstOrDefault(display => display.IsPrimary) ?? displays.FirstOrDefault();
        if (display is null) return new(80, 80);
        var area = display.WorkingArea;
        return KeepOnScreen(new(area.Right - size.Width - 28, area.Y + 80), size, displays, false);
    }

    private static double Fraction(int value, int maximum) => maximum <= 0 ? 0 : Math.Clamp((double)value / maximum, 0, 1);
    private static int Snap(int value, int minimum, int maximum, double distance) =>
        value - minimum <= distance && value - minimum <= maximum - value ? minimum
        : maximum - value <= distance ? maximum : value;

    private static OrbDisplay? SelectDisplay(PixelPoint position, PixelSize size, IReadOnlyList<OrbDisplay> displays) =>
        displays.OrderByDescending(display => Overlap(position, size, display.WorkingArea))
            .ThenBy(display => Distance(position, size, display.WorkingArea)).FirstOrDefault();

    private static long Overlap(PixelPoint position, PixelSize size, PixelRect area) =>
        (long)Math.Max(0, Math.Min((long)position.X + size.Width, area.Right) - Math.Max(position.X, area.X))
        * Math.Max(0, Math.Min((long)position.Y + size.Height, area.Bottom) - Math.Max(position.Y, area.Y));

    private static double Distance(PixelPoint position, PixelSize size, PixelRect area)
    {
        var x = position.X + size.Width / 2d;
        var y = position.Y + size.Height / 2d;
        var dx = x - Math.Clamp(x, area.X, area.Right);
        var dy = y - Math.Clamp(y, area.Y, area.Bottom);
        return dx * dx + dy * dy;
    }
}

public sealed record OrbObstacle(string Id, PixelRect Bounds);
public sealed record OrbPlacementResult(PixelPoint Position, bool IsClear);

/// <summary>Window-to-window placement includes the name/readout area, so arranging discs cannot cover another label.</summary>
public static class OrbWindowArrangement
{
    public static OrbPlacementResult Place(PixelPoint desired, PixelSize size, IReadOnlyList<OrbDisplay> displays,
        IReadOnlyList<OrbObstacle> obstacles, bool snapEdges = true, bool snapOrbs = true)
    {
        var point = OrbPlacementMath.KeepOnScreen(desired, size, displays, snapEdges);
        var placement = OrbPlacementMath.Capture(point, size, displays);
        var display = displays.FirstOrDefault(item => item.Key == placement?.DisplayKey);
        if (display is null) return new(point, !Overlaps(point, size, obstacles));
        var area = display.WorkingArea;
        var gap = Math.Max(4, (int)Math.Round(12 * display.Scaling));
        if (snapOrbs)
        {
            var distance = 16 * display.Scaling;
            var align = 8 * display.Scaling;
            var lockX = snapEdges && (point.X == area.X || point.X + size.Width == area.Right);
            var lockY = snapEdges && (point.Y == area.Y || point.Y + size.Height == area.Bottom);
            int? x = null, y = null;
            double bestX = distance + 1, bestY = distance + 1;
            foreach (var obstacle in obstacles)
            {
                var other = obstacle.Bounds;
                if (!lockX && point.Y < other.Bottom + gap && point.Y + size.Height > other.Y - gap)
                    foreach (var candidate in new[] { other.Right + gap, other.X - size.Width - gap })
                        if (Math.Abs(candidate - point.X) <= distance && Math.Abs(candidate - point.X) < bestX)
                        { x = candidate; bestX = Math.Abs(candidate - point.X); }
                if (!lockY && point.X < other.Right + gap && point.X + size.Width > other.X - gap)
                    foreach (var candidate in new[] { other.Bottom + gap, other.Y - size.Height - gap })
                        if (Math.Abs(candidate - point.Y) <= distance && Math.Abs(candidate - point.Y) < bestY)
                        { y = candidate; bestY = Math.Abs(candidate - point.Y); }
                if (!lockY && Math.Abs(point.Y - other.Y) <= align && Math.Abs(point.Y - other.Y) < bestY)
                { y = other.Y; bestY = Math.Abs(point.Y - other.Y); }
                if (!lockX && Math.Abs(point.X - other.X) <= align && Math.Abs(point.X - other.X) < bestX)
                { x = other.X; bestX = Math.Abs(point.X - other.X); }
            }
            point = OrbPlacementMath.KeepOnScreen(new(x ?? point.X, y ?? point.Y), size, displays, false);
        }
        if (!Overlaps(point, size, obstacles)) return new(point, true);
        var candidates = new HashSet<PixelPoint>();
        void Add(int x, int y) => candidates.Add(OrbPlacementMath.KeepOnScreen(new(x, y), size, displays, false));
        foreach (var obstacle in obstacles)
        {
            var other = obstacle.Bounds;
            foreach (var x in new[] { other.Right + gap, other.X - size.Width - gap, point.X, other.X })
                foreach (var y in new[] { other.Bottom + gap, other.Y - size.Height - gap, point.Y, other.Y }) Add(x, y);
        }
        // Scan a bounded grid too: neighboring windows can form a barrier around the nearest touching positions.
        for (var y = area.Y; y <= Math.Max(area.Y, area.Bottom - size.Height); y += Math.Max(32, size.Height + gap))
            for (var x = area.X; x <= Math.Max(area.X, area.Right - size.Width); x += Math.Max(32, size.Width + gap)) Add(x, y);
        Add(area.Right - size.Width, area.Bottom - size.Height);
        var clear = candidates.Where(candidate => !Overlaps(candidate, size, obstacles))
            .OrderBy(candidate => Distance(candidate, desired)).ThenBy(candidate => candidate.Y).ThenBy(candidate => candidate.X).ToArray();
        return clear.Length > 0 ? new(clear[0], true) : new(point, false);
    }

    public static bool Overlaps(PixelPoint point, PixelSize size, IReadOnlyList<OrbObstacle> obstacles) => obstacles.Any(item =>
        point.X < item.Bounds.Right && (long)point.X + size.Width > item.Bounds.X
        && point.Y < item.Bounds.Bottom && (long)point.Y + size.Height > item.Bounds.Y);

    private static double Distance(PixelPoint a, PixelPoint b) => (a.X - (double)b.X) * (a.X - (double)b.X)
        + (a.Y - (double)b.Y) * (a.Y - (double)b.Y);
}
