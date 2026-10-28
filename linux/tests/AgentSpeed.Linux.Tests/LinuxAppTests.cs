using AgentSpeed.Core;

namespace AgentSpeed.Linux.Tests;

public sealed class LinuxAppTests : IDisposable
{
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "agent-speed-linux-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OptionsAcceptIsolatedPathsAndRejectInvalidExitTimers()
    {
        var options = LaunchOptions.Parse(["--codex-dir", _temporary, "--data-dir", _temporary, "--exit-after", "10", "--dashboard"]);
        Assert.Equal(_temporary, options.CodexDirectory);
        Assert.Equal(10, options.ExitAfter);
        Assert.True(options.Dashboard);
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--codex-dir"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--exit-after"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--exit-after", "-1"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--unknown"]));
    }

    [Fact]
    public void SettingsRoundTripAndPreserveCorruptInput()
    {
        var storage = new AppStorage(_temporary);
        Assert.Equal(new UserSettings(), storage.Load());
        var settings = new UserSettings("/tmp/codex/sessions", -1200, 100, false);
        storage.Save(settings);
        Assert.Equal(settings, storage.Load());
        Assert.Equal(Path.Combine(_temporary, "history.sqlite"), storage.HistoryPath);

        File.WriteAllText(storage.SettingsPath, "{broken");
        Assert.Equal(new UserSettings(), storage.Load());
        var copy = Assert.Single(Directory.GetFiles(_temporary, "settings.json.corrupt-*"));
        Assert.Equal("{broken", File.ReadAllText(copy));
        Assert.NotNull(storage.SettingsWarning);
    }

    [Fact]
    public void LiveTodayUpdatesOneHistoryRowWithoutAddingTheSameUsageTwice()
    {
        var date = new DateTime(2026, 10, 26);
        var stored = new[] { Day("2026-10-26", 100, 2), Day("2026-10-25", 40, 1) };
        var live = Live(date, 160, 3);
        var rows = UsageProjection.Rows(stored, live, date, 7);
        Assert.Equal(2, rows.Count);
        Assert.Equal(160, rows[0].Output);
        Assert.Equal(3, rows[0].Requests);
        Assert.Equal(40, rows[1].Output);
    }

    [Fact]
    public void PartialLiveSeedDoesNotEraseFullHistoricalTotals()
    {
        var date = new DateTime(2026, 10, 26);
        var rows = UsageProjection.Rows([Day("2026-10-26", 200, 5)], Live(date, 50, 1), date, null);
        Assert.Equal(200, rows[0].Output);
        Assert.Equal(5, rows[0].Requests);
        Assert.Null(rows[0].AverageSpeed);
        Assert.Null(rows[0].Peak);
    }

    [Fact]
    public void StartingOrPreviousDaySnapshotsNeverCreateFictitiousUsage()
    {
        var date = new DateTime(2026, 10, 26);
        Assert.Empty(UsageProjection.Rows([], SpeedSnapshot.Empty, date, null));
        Assert.Empty(UsageProjection.Rows([], Live(date.AddDays(-1), 100, 2), date, null));
        Assert.Empty(UsageProjection.Rows([], Live(date, 0, 0), date, null));
    }

    [Fact]
    public void RangeFiltersByCalendarDayAndSourcesStayCodexOnly()
    {
        var rows = UsageProjection.Rows([Day("2026-10-19", 100, 1), Day("2026-10-20", 200, 2),
            Day("2026-10-26", 300, 3) with { Source = SourceKind.ClaudeCode }], SpeedSnapshot.Empty, new DateTime(2026, 10, 26), 7);
        Assert.Equal("2026-10-20", Assert.Single(rows).Day);
    }

    [Fact]
    public void UnknownRatesStayUnknownAndGeneratingEstimatesAreLabelled()
    {
        Assert.Equal("—", UsageProjection.Rate(null));
        Assert.Equal("—", UsageProjection.Rate(double.NaN));
        Assert.Contains("估算", UsageProjection.State(SpeedSnapshot.Empty with
        {
            State = MonitorState.Running, GeneratingCount = 1, LiveEstimatedShare = 1,
        }));
    }

    private static DailyUsage Day(string date, long output, long requests) => new(date, SourceKind.Codex,
        new TokenTotals(Requests: requests, Output: output), 0, 0, 0, 1, null, []);
    private static SpeedSnapshot Live(DateTime date, long output, long requests) => SpeedSnapshot.Empty with
    {
        State = MonitorState.Running,
        DayStart = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local)),
        Today = new TokenTotals(Requests: requests, Output: output),
    };

    public void Dispose()
    {
        if (Directory.Exists(_temporary)) Directory.Delete(_temporary, true);
    }
}
