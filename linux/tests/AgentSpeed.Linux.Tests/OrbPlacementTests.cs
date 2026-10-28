using Avalonia;

namespace AgentSpeed.Linux.Tests;

public sealed class OrbPlacementTests
{
    private static readonly OrbDisplay Left = new("left", new(-1920, 0, 1920, 1080), new(-1920, 24, 1920, 1056), 1, false);
    private static readonly OrbDisplay Primary = new("primary", new(0, 0, 2560, 1440), new(0, 28, 2560, 1412), 1, true);
    private static readonly OrbDisplay[] Displays = [Primary, Left];

    [Fact]
    public void NormalizedPlacementPreservesRightBottomAnchorWhenOrbSizeChanges()
    {
        var oldSize = new PixelSize(244, 328);
        var saved = OrbPlacementMath.Capture(new(2316, 1112), oldSize, Displays);
        Assert.Equal(new OrbPlacement("primary", 1, 1), saved);
        Assert.Equal(new PixelPoint(2228, 1024), OrbPlacementMath.Restore(saved, new(332, 416), Displays));
    }

    [Fact]
    public void NegativeMonitorCoordinatesRoundTripAndAvoidPrimaryFallback()
    {
        var size = new PixelSize(244, 328);
        var position = new PixelPoint(-1500, 312);
        var saved = OrbPlacementMath.Capture(position, size, Displays);
        Assert.Equal("left", saved!.DisplayKey);
        Assert.Equal(position, OrbPlacementMath.Restore(saved, size, Displays));
    }

    [Fact]
    public void SameMonitorPlacementAdaptsToChangedWorkArea()
    {
        var saved = new OrbPlacement("primary", .5, 1);
        var resized = Primary with { WorkingArea = new(0, 56, 2560, 1384) };
        Assert.Equal(new PixelPoint(1158, 1112), OrbPlacementMath.Restore(saved, new(244, 328), [resized]));
    }

    [Fact]
    public void LayoutIdentityIsOrderIndependentButDistinguishesMonitorArrangementsAndScale()
    {
        var key = OrbPlacementMath.LayoutKey(Displays);
        Assert.Equal(key, OrbPlacementMath.LayoutKey([Left, Primary]));
        Assert.NotEqual(key, OrbPlacementMath.LayoutKey([Primary]));
        Assert.NotEqual(key, OrbPlacementMath.LayoutKey([Primary, Left with { Bounds = new(2560, 0, 1920, 1080) }]));
        Assert.NotEqual(key, OrbPlacementMath.LayoutKey([Primary with { Scaling = 1.25 }, Left]));
        Assert.Equal(key, OrbPlacementMath.LayoutKey([Primary with { WorkingArea = new(0, 56, 2560, 1384) }, Left]));
    }

    [Fact]
    public void MissingDisplayAndNonFiniteSavedValuesRequireSafeFallback()
    {
        Assert.Null(OrbPlacementMath.Restore(new("removed", .5, .5), new(244, 328), Displays));
        Assert.Null(OrbPlacementMath.Restore(new("primary", double.NaN, .5), new(244, 328), Displays));
        Assert.Null(OrbPlacementMath.Restore(new("primary", .5, double.PositiveInfinity), new(244, 328), Displays));
        Assert.Equal(new PixelPoint(0, 1112), OrbPlacementMath.Restore(new("primary", -10, 20), new(244, 328), Displays));
    }

    [Fact]
    public void OffscreenLegacyWindowMovesToNearestReachableWorkingArea()
    {
        var size = new PixelSize(244, 328);
        var old = new PixelPoint(-5000, -2000);
        Assert.False(OrbPlacementMath.IsFullyVisible(old, size, Displays));
        var restored = OrbPlacementMath.KeepOnScreen(old, size, Displays, false);
        Assert.Equal(new PixelPoint(-1920, 24), restored);
        Assert.True(OrbPlacementMath.IsFullyVisible(restored, size, Displays));
    }

    [Fact]
    public void EdgeSnapUsesDisplayScaleAndCanBeDisabled()
    {
        var display = Primary with { Scaling = 2 };
        var size = new PixelSize(244, 328);
        var nearEdge = new PixelPoint(25, 55);
        Assert.Equal(new PixelPoint(0, 28), OrbPlacementMath.KeepOnScreen(nearEdge, size, [display], true));
        Assert.Equal(nearEdge, OrbPlacementMath.KeepOnScreen(nearEdge, size, [display], false));
        Assert.Equal(new PixelPoint(40, 68), OrbPlacementMath.KeepOnScreen(new(40, 68), size, [display], true));
    }

    [Fact]
    public void OversizedWindowAndNoDisplayNeverCreateInvalidClampBounds()
    {
        Assert.Equal(new PixelPoint(0, 28), OrbPlacementMath.KeepOnScreen(new(30, 60), new(3000, 2000), [Primary], true));
        Assert.Equal(new PixelPoint(80, 80), OrbPlacementMath.Default(new(244, 328), []));
        Assert.Null(OrbPlacementMath.Capture(new(10, 10), new(244, 328), []));
    }
}
