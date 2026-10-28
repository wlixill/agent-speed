using AgentSpeed.Core;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings.Orbs;
using Avalonia;

namespace AgentSpeed.Linux.Tests;

public sealed class AdvancedOrbTests
{
    private static readonly IReadOnlyList<OrbDisplay> Screens = [new("primary", new(0, 0, 1440, 1080), new(0, 24, 1440, 1056), 1, true)];

    [Fact]
    public void SplitCopiesDisplayOptionsAndPartitionedSourcesWithoutChangingOriginal()
    {
        var original = new LinuxOrbDefinition("primary") { Name = "My agents", Unit = OrbUnit.Cost, Traffic = OrbTraffic.Machine,
            Style = OrbStyle.Needle, RedlineOutput = 600, RedlineTotal = 120000, AlwaysOnTop = false, FadeWhenIdle = true,
            ShowName = false, ClickThrough = true, SnapToOrbs = false, OrbX = 400, OrbY = 300 };
        var available = SourceSet.From([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini]);
        var result = LinuxOrbEditing.Split([original], original.Id, available)!;
        Assert.Equal(3, result.Orbs.Count); Assert.Equal(2, result.Created.Count);
        Assert.Equal(available, SourceSet.From(result.Orbs.SelectMany(orb => orb.Sources!)));
        Assert.All(result.Orbs, orb =>
        {
            Assert.Null(orb.Name); Assert.Equal(OrbUnit.Cost, orb.Unit); Assert.Equal(OrbTraffic.Machine, orb.Traffic);
            Assert.Equal(600, orb.RedlineOutput); Assert.Equal(120000, orb.RedlineTotal);
            Assert.False(orb.AlwaysOnTop); Assert.True(orb.FadeWhenIdle); Assert.False(orb.ShowName); Assert.False(orb.SnapToOrbs);
        });
        Assert.Equal(400, result.Orbs[0].OrbX); Assert.Null(result.Orbs[1].OrbX);
        Assert.Equal("My agents", original.Name); Assert.Null(original.Sources);
    }

    [Fact]
    public void SplitAtCapKeepsRemainingSourcesInsteadOfSilentlyDroppingThem()
    {
        var originals = Enumerable.Range(0, 7).Select(index => new LinuxOrbDefinition("orb-" + index)).ToArray();
        var sources = SourceSet.From([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini]);
        var result = LinuxOrbEditing.Split(originals, "orb-0", sources)!;
        Assert.True(result.Limited); Assert.Equal(8, result.Orbs.Count); Assert.Single(result.Created);
        Assert.Equal(sources, SourceSet.From(result.Orbs.Take(2).SelectMany(orb => orb.Sources!)));
        Assert.Equal(2, result.Orbs[1].Sources!.Length);
        Assert.Null(LinuxOrbEditing.Split(result.Orbs, "orb-2", sources));
        Assert.Null(LinuxOrbEditing.Split(originals, "orb-0", SourceSet.Of(SourceKind.Codex)));
    }

    [Fact]
    public void MergeUnionsSourcesPreservesTargetOptionsAndLeavesUnselectedWindows()
    {
        LinuxOrbDefinition[] orbs = [new("a") { Sources = [SourceKind.Codex], Unit = OrbUnit.Cost, RedlineOutput = 800,
            Style = OrbStyle.Liquid, ShowName = false, OrbX = 100 }, new("b") { Sources = [SourceKind.ClaudeCode] },
            new("c") { Sources = [SourceKind.Gemini] }];
        var merged = LinuxOrbEditing.Merge(orbs, "a", "b")!;
        Assert.Equal(2, merged.Count); Assert.Equal(orbs[2], merged[1]);
        Assert.Equal(SourceSet.From([SourceKind.Codex, SourceKind.ClaudeCode]), merged[0].SourceSet);
        Assert.Equal(OrbUnit.Cost, merged[0].Unit); Assert.Equal(OrbStyle.Liquid, merged[0].Style);
        Assert.Equal(800, merged[0].RedlineOutput); Assert.Equal(100, merged[0].OrbX); Assert.False(merged[0].ShowName);
        var all = LinuxOrbEditing.Merge([orbs[0], orbs[1] with { Sources = null }], "a")!;
        Assert.Null(Assert.Single(all).Sources);
        Assert.Null(Assert.Single(LinuxOrbEditing.Merge(orbs, "a")!).Sources);
        Assert.Null(LinuxOrbEditing.Merge(orbs, "a", "missing"));
    }

    [Fact]
    public void DuplicateCopiesOptionsButGetsNewIdentityAndFreshPlacement()
    {
        var source = new LinuxOrbDefinition("a") { Sources = [SourceKind.Codex], AlwaysOnTop = false, RedlineTotal = 99999,
            OrbX = 20, OrbY = 30, Placements = new() { ["layout"] = new("primary", .3, .3) } };
        var result = LinuxOrbEditing.Duplicate([source], "a", () => "a")!;
        var copy = result.Orbs[1];
        Assert.NotEqual(source.Id, copy.Id); Assert.Equal(source.SourceSet, copy.SourceSet); Assert.False(copy.AlwaysOnTop);
        Assert.Equal(99999, copy.RedlineTotal); Assert.Null(copy.OrbX); Assert.Null(copy.OrbY); Assert.Null(copy.Placements);
    }

    [Fact]
    public void PerWindowRedlinesFallBackIndependentlyAndRejectNonfiniteSettings()
    {
        var global = new UserSettings { Redline = 400, RedlineTotal = 200000 };
        var definition = new LinuxOrbDefinition("a") { RedlineOutput = 800, RedlineTotal = double.NaN, Unit = (OrbUnit)99, Traffic = (OrbTraffic)99 }.Validated();
        Assert.Equal(800, definition.EffectiveRedline(SpeedMetric.Output, global));
        Assert.Equal(200000, definition.EffectiveRedline(SpeedMetric.Total, global));
        Assert.Null(definition.RedlineTotal); Assert.Equal(OrbUnit.Tokens, definition.Unit); Assert.Equal(OrbTraffic.Off, definition.Traffic);
    }

    [Fact]
    public void NeighborSnapAlignsAndAvoidsTheEntireReadoutRectangle()
    {
        var obstacles = new[] { new OrbObstacle("a", new(100, 100, 240, 344)) };
        var result = OrbWindowArrangement.Place(new(358, 106), new(240, 344), Screens, obstacles);
        Assert.True(result.IsClear); Assert.Equal(new PixelPoint(352, 100), result.Position);
        Assert.False(OrbWindowArrangement.Overlaps(result.Position, new(240, 344), obstacles));
        var overlapping = OrbWindowArrangement.Place(new(110, 110), new(240, 344), Screens, obstacles);
        Assert.True(overlapping.IsClear); Assert.True(OrbPlacementMath.IsFullyVisible(overlapping.Position, new(240, 344), Screens));
        Assert.False(OrbWindowArrangement.Overlaps(overlapping.Position, new(240, 344), obstacles));
    }

    [Fact]
    public void ArrangementKeepsScreenEdgePriorityAndReportsWhenNoFreeSpaceExists()
    {
        var result = OrbWindowArrangement.Place(new(4, 400), new(240, 344), Screens, [new("other", new(8, 24, 240, 344))]);
        Assert.Equal(0, result.Position.X); Assert.True(result.IsClear);
        var tiny = new[] { new OrbDisplay("tiny", new(0, 0, 240, 344), new(0, 0, 240, 344), 1, true) };
        var full = OrbWindowArrangement.Place(new(0, 0), new(240, 344), tiny, [new("a", new(0, 0, 240, 344))]);
        Assert.False(full.IsClear); Assert.Equal(new PixelPoint(0, 0), full.Position);
    }
}
