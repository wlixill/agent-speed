using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Linux.Tests;

public sealed class LinuxTrafficEnhancedTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 5, 0, 0, TimeSpan.Zero);
    private static LinuxTrafficProcess Root(SourceKind source = SourceKind.Codex, long start = 100) =>
        new(new(10, start), 0, source, [], ["/synthetic/session.jsonl"], true);
    private static LinuxTrafficSample Sample(int seconds, string epoch, params LinuxEventCounter[] counters) => new(Start.AddSeconds(seconds),
        new Dictionary<string, TrafficBytes>(), [Root()], [], "synthetic") { Events = new(epoch, 0, counters) };

    [Fact]
    public void EventCountersIncludeShortLivedProcessWithoutAnOpenSocketOrProcEntry()
    {
        var accounting = new LinuxTrafficAccounting();
        accounting.Apply(Sample(0, "capture-a"));
        // No process/socket existed at the previous poll. Kernel event counters still prove its root identity.
        var counter = new LinuxEventCounter(new(20, 200), new(10, 100), "curl", 4096, 123);
        var snapshot = accounting.Apply(Sample(1, "capture-a", counter));
        Assert.Equal(new TrafficBytes(4096, 123), snapshot.Sources[SourceKind.Codex].Today.Tools);
        Assert.Equal(new TrafficRate(4096, 123), snapshot.Sources[SourceKind.Codex].LiveWithTools);
        var next = accounting.Apply(Sample(2, "capture-a", counter));
        Assert.Equal(new TrafficBytes(4096, 123), next.Sources[SourceKind.Codex].Today.Tools);
        Assert.Equal(TrafficRate.Zero, next.Sources[SourceKind.Codex].LiveWithTools);
    }

    [Fact]
    public void HelperRestartDoesNotAddPriorEpochCountersAndFreshPidUsesItsOwnBaseline()
    {
        var accounting = new LinuxTrafficAccounting();
        accounting.Apply(Sample(0, "a", new LinuxEventCounter(new(10, 100), default, "codex", 100, 100)));
        var measured = accounting.Apply(Sample(1, "a", new LinuxEventCounter(new(10, 100), default, "codex", 300, 400)));
        Assert.Equal(new TrafficBytes(200, 300), measured.Sources[SourceKind.Codex].Today.Self);
        var restart = accounting.Apply(Sample(2, "b", new LinuxEventCounter(new(10, 100), default, "codex", 9000, 9000)));
        Assert.Null(restart.Sources[SourceKind.Codex].LiveSelf);
        Assert.Equal(new TrafficBytes(200, 300), restart.Sources[SourceKind.Codex].Today.Self);
    }

    [Fact]
    public void ExitedParentNeverAttachesToReusedPidOfAnotherSource()
    {
        var accounting = new LinuxTrafficAccounting();
        accounting.Apply(Sample(0, "a"));
        var sample = Sample(1, "a", new LinuxEventCounter(new(20, 200), new(10, 100), "curl", 400, 300)) with
        { Processes = [Root(SourceKind.ClaudeCode, 999)] };
        var snapshot = accounting.Apply(sample);
        Assert.Equal(new TrafficBytes(400, 300), snapshot.Sources[SourceKind.Codex].Today.Tools);
        Assert.Equal(TrafficBytes.Zero, snapshot.Sources[SourceKind.ClaudeCode].Today.Total(true));
    }

    [Fact]
    public void EventSessionAttributionStillRequiresAUniqueOpenedTranscript()
    {
        var speed = SpeedSnapshot.Empty with { Agents = [new("/synthetic/session.jsonl", SourceKind.Codex, "private", "m", AgentPhase.Generating, false, Rate.Zero, null, Start)] };
        var accounting = new LinuxTrafficAccounting();
        accounting.Apply(Sample(0, "a"), speed);
        var snapshot = accounting.Apply(Sample(1, "a", new LinuxEventCounter(new(20, 200), new(10, 100), "curl", 200, 100)), speed);
        Assert.Equal(new TrafficBytes(200, 100), snapshot.Agents["/synthetic/session.jsonl"].Today.Tools);
        Assert.Equal(TrafficBytes.Zero, snapshot.Sources[SourceKind.Codex].Unassigned);
    }

    [Fact]
    public void ExitedParentDoesNotReuseAnOldOpenTranscriptAsCurrentSessionProof()
    {
        var speed = SpeedSnapshot.Empty with { Agents = [new("/synthetic/session.jsonl", SourceKind.Codex, "private", "m", AgentPhase.Generating, false, Rate.Zero, null, Start)] };
        var accounting = new LinuxTrafficAccounting();
        accounting.Apply(Sample(0, "a"), speed);
        var sample = Sample(1, "a", new LinuxEventCounter(new(20, 200), new(10, 100), "curl", 200, 100)) with { Processes = [] };
        var snapshot = accounting.Apply(sample, speed);
        Assert.Empty(snapshot.Agents);
        Assert.Equal(new TrafficBytes(200, 100), snapshot.Sources[SourceKind.Codex].Unassigned);
    }

    [Fact]
    public void HelperProtocolRejectsAnotherUidAndNegativeOrUnboundedCounters()
    {
        const string good = "{\"schema\":1,\"status\":\"sample\",\"uid\":1000,\"epoch\":\"one\",\"lost\":0,\"rows\":[{\"pid\":12,\"start\":22,\"parent\":10,\"parentStart\":11,\"rx\":300,\"tx\":100,\"comm\":\"codex\"}]}";
        var value = LinuxTrafficEnhanced.Parse(good, 1000);
        Assert.Equal(300, Assert.Single(value.Counters).Rx);
        Assert.Throws<JsonException>(() => LinuxTrafficEnhanced.Parse(good, 1001));
        Assert.Throws<JsonException>(() => LinuxTrafficEnhanced.Parse(good.Replace("\"rx\":300", "\"rx\":-1"), 1000));
        Assert.Throws<JsonException>(() => LinuxTrafficEnhanced.Parse(good.Replace("\"comm\":\"codex\"", "\"comm\":\"12345678901234567\""), 1000));
        Assert.Throws<JsonException>(() => LinuxTrafficEnhanced.Parse(good.Replace("\"lost\":0", "\"lost\":-1"), 1000));
    }

    [Fact]
    public void VersionOneTrafficHistoryRemainsReadableAndNewCsvDoesNotMislabelItAsCompleteUdp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agent-speed-traffic-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new LinuxTrafficStore(dir);
            File.WriteAllText(store.Path, "{\"Version\":1,\"Coverage\":\"old TCP only\",\"Days\":[{\"Day\":\"2026-10-26\",\"Key\":\"codex\",\"Rx\":100,\"Tx\":200,\"RxTools\":0,\"TxTools\":0,\"SampledMs\":1000}]}");
            var rows = store.Load();
            Assert.Equal(100, Assert.Single(rows).Rx);
            store.Save(rows);
            Assert.Equal(rows, store.Load());
            Assert.Contains("mixed_capture_modes_unobserved_paths_excluded", LinuxTrafficStore.Csv(rows));
        }
        finally { Directory.Delete(dir, true); }
    }
}
