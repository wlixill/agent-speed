using System.Collections.Immutable;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Titles;

namespace AgentSpeed.Linux.Tests;

public sealed class OrbConfigurationTests
{
    [Fact]
    public void LegacyOrbMigrationPreservesSizeOpacityVisibilityAndPositions()
    {
        var placements = new Dictionary<string, OrbPlacement> { ["layout"] = new("left", .2, .8) };
        var old = new UserSettings(OrbX: -1000, OrbY: 400, OrbVisible: false)
        { OrbSize = 300, OrbOpacity = .5, OrbClickThrough = true, OrbSnapToEdges = false, OrbPlacements = placements };
        var migrated = LinuxOrbDefinition.FromLegacy(old).Validated();
        Assert.Equal(300, migrated.Size); Assert.Equal(.5, migrated.Opacity);
        Assert.False(migrated.Visible); Assert.True(migrated.ClickThrough); Assert.False(migrated.SnapToEdges);
        Assert.Equal(-1000, migrated.OrbX); Assert.Equal(placements["layout"], migrated.Placements!["layout"]);
        Assert.Equal(OrbStyle.Led, migrated.Style); Assert.Equal("general", migrated.TitleTheme); Assert.Null(migrated.Sources);
    }

    [Fact]
    public void DefinitionsBoundUntrustedSettingsAndKeepSourceSetsIndependent()
    {
        var definitions = LinuxOrbDefinition.ValidateList(Enumerable.Range(0, 10).Select(index => new LinuxOrbDefinition("duplicate")
        { Sources = [SourceKind.Codex, SourceKind.Codex], Size = index == 0 ? double.NaN : 500, Opacity = .1, Style = (OrbStyle)200 }));
        Assert.Equal(8, definitions.Count); Assert.Equal(8, definitions.Select(orb => orb.Id).Distinct().Count());
        Assert.Equal(212, definitions[0].Size); Assert.Equal(300, definitions[1].Size);
        Assert.All(definitions, orb => { Assert.Equal(.4, orb.Opacity); Assert.Equal(OrbStyle.Led, orb.Style); Assert.Equal(SourceSet.Of(SourceKind.Codex), orb.SourceSet); });
        Assert.True(new LinuxOrbDefinition("none") { Sources = [] }.SourceSet!.Value.IsEmpty);
        Assert.Null(new LinuxOrbDefinition("all").SourceSet);
    }

    [Fact]
    public void EveryOriginalPresetHasLocalizedThirteenLevelsAndIdle()
    {
        Assert.Equal(9, TitlePresets.All.Count);
        foreach (var preset in TitlePresets.All)
        {
            var theme = TitleTheme.Of(preset);
            Assert.DoesNotContain("titles.", LinuxTitleText.Name(theme));
            for (var level = 0; level <= 13; level++)
            {
                var label = LinuxTitleText.Label(theme, level);
                Assert.False(string.IsNullOrWhiteSpace(label)); Assert.DoesNotContain("titles.", label);
            }
        }
    }

    [Fact]
    public void CustomThemesRoundTripThroughOriginalDocumentCodecAndEmptyFieldsUseBase()
    {
        var custom = new CustomTitleSet("custom-a", "我的动物", TitlePreset.Animals, "休息中", ["我的蜗牛", ""]);
        var document = new TitleThemesDocument { Selected = custom.Id, Custom = ImmutableArray.Create(custom) };
        var options = new JsonSerializerOptions { Converters = { new LinuxTitleThemesConverter() } };
        var read = JsonSerializer.Deserialize<TitleThemesDocument>(JsonSerializer.Serialize(document, options), options)!;
        Assert.Equal(document, read); Assert.Equal("休息中", LinuxTitleText.Label(read.General, 0)); Assert.Equal("我的蜗牛", LinuxTitleText.Label(read.General, 1));
        Assert.Equal(LinuxTitleText.Label(TitleTheme.Of(TitlePreset.Animals), 2), LinuxTitleText.Label(read.General, 2));
        var tooMany = new TitleThemesDocument { Custom = Enumerable.Range(0, 12).Select(index => new CustomTitleSet("custom-" + index, "主题", TitlePreset.Space)).ToImmutableArray() };
        Assert.Equal(8, tooMany.Validated().Custom.Length);
    }
}
