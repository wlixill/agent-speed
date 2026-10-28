using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Linux.Tests;

public sealed class CostAndReadoutParityTests
{
    [Fact]
    public void SavingCostChoicesRetainsAllSubscriptionMarksAndLegacyCurrency()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cost-parity-" + Guid.NewGuid());
        try
        {
            var storage = new AppStorage(directory);
            storage.Save(new UserSettings { Currency = "CNY", CostSettings = new()
            { LiveUnit = LiveUnit.Cost, Subscriptions = SourceSet.Of(SourceKind.Codex).With(SourceKind.ClaudeCode),
                IncludeSubscriptions = false, HistoryIncludeSubscriptions = true } });
            var restored = storage.Load().Validated();
            Assert.Equal("CNY", restored.CostSettings.Currency);
            Assert.Equal(LiveUnit.Cost, restored.CostSettings.LiveUnit);
            Assert.Equal(SourceSet.Of(SourceKind.Codex).With(SourceKind.ClaudeCode), restored.CostSettings.Subscriptions);
            Assert.True(restored.CostSettings.HistoryIncludeSubscriptions);
            Assert.False(restored.CostSettings.IncludeSubscriptions);
            Assert.False(restored.EnablePriceUpdates);
            Assert.False(restored.CostSettings.SyncPrices);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SubscriptionExclusionChangesOnlyMoneyAndNeverTheUnderlyingUsage()
    {
        var snapshot = SpeedSnapshot.Empty with
        {
            State = MonitorState.Running, Live = new(150, 1500), Today = new(Requests: 3, Output: 150),
            Sources = [Source(SourceKind.Codex, 100, 1000), Source(SourceKind.ClaudeCode, 50, 500)],
        };
        var display = new CostDisplay(new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans"));
        Assert.Equal(1500, display.Live(snapshot).Value);
        display.Settings = new() { Subscriptions = SourceSet.Of(SourceKind.Codex) };
        Assert.Equal(500, display.Live(snapshot).Value);
        var codexOnly = display.Live(snapshot, SourceSet.Of(SourceKind.Codex));
        Assert.Equal("0", codexOnly.Number); Assert.False(codexOnly.Unknown);
        display.Settings = display.Settings with { IncludeSubscriptions = true };
        Assert.Equal(1500, display.Live(snapshot).Value);
        Assert.Equal(150, snapshot.Live.Output);
        Assert.Equal(3, snapshot.Today.Requests);
        Assert.Equal(100, snapshot.Sources[0].Live.Output);
        Assert.Equal(1000, snapshot.Sources[0].LiveCost.MicroUsdPerSecond);
    }

    [Fact]
    public void ProductionReadoutKeepsFreePartialPricingAsALowerBoundAndSourceRowsShowApiEquivalent()
    {
        var snapshot = SpeedSnapshot.Empty with { State = MonitorState.Running,
            Sources = [Source(SourceKind.Codex, 100, 0) with { LiveCost = new(0, 50) }] };
        var display = new CostDisplay(new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans"));
        var freePart = MoneyPresentation.Readout(display, snapshot);
        Assert.True(freePart.AtLeast);
        Assert.StartsWith("≥", freePart.NumberText);
        snapshot = snapshot with { Sources = [Source(SourceKind.Codex, 100, 1000)] };
        display.Settings = new() { Subscriptions = SourceSet.Of(SourceKind.Codex) };
        Assert.Equal("0", MoneyPresentation.Readout(display, snapshot).Number);
        Assert.Equal(1000, MoneyPresentation.Readout(display, snapshot, SourceSet.Of(SourceKind.Codex), apiEquivalent: true).Value);
        snapshot = snapshot with { Sources = [Source(SourceKind.Codex, 100, double.NaN)] };
        Assert.True(MoneyPresentation.Readout(display, snapshot, apiEquivalent: true).Unknown);
    }

    [Fact]
    public void TrafficRatesRetainUnknownAndDoNotConfuseBytesWithBits()
    {
        var snapshot = new TrafficSnapshot { Enabled = true, MachineLive = new(1000, 2000), ProcessState = ProcessTrafficState.Starting };
        var machine = LinuxTrafficReadout.Describe(snapshot, null, OrbTraffic.Machine, true, true);
        Assert.Contains("Kb/s", machine);
        Assert.Contains("网卡", machine);
        var process = LinuxTrafficReadout.Describe(snapshot, null, OrbTraffic.Sources, false, true);
        Assert.Contains("—", process);
        Assert.DoesNotContain("0 B/s", process);
        Assert.Equal("", LinuxTrafficReadout.Describe(TrafficSnapshot.Off, null, OrbTraffic.Machine, false, true));
        Assert.Equal("", LinuxTrafficReadout.Describe(snapshot, null, OrbTraffic.Off, false, true));
    }

    [Theory]
    [InlineData(.0001, "1e-4")]
    [InlineData(0, "0")]
    [InlineData(double.NaN, "—")]
    public void SmallMonetaryTrayValuesDoNotBecomeMisleadingLessThanOne(double amount, string expected) =>
        Assert.Equal(expected, TrayReadoutRenderer.CompactMoney(amount));

    private static SourceStatus Source(SourceKind source, double output, double microUsd) =>
        new(source, true, "/synthetic", true, 1, new(output, output * 10), new(Requests: 1, Output: (long)output), 1)
        { LiveCost = new(microUsd, output), TodayCost = new((long)microUsd) };
}
