using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Linux.Tests;

public sealed class LinuxTrafficTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesPerInterfaceBytesAndProcessIdentityWithoutDependingOnProcessNameSpaces()
    {
        var interfaces = LinuxTrafficSampler.ParseInterfaces("Inter-| Receive | Transmit\n lo: 120 1 0 0 0 0 0 0 130 1 0 0 0 0 0 0\n eth0: 1000 2 0 0 0 0 0 0 2000 2 0 0 0 0 0 0\n bad: nope\n");
        Assert.Equal(new TrafficBytes(1000, 2000), interfaces["eth0"]);
        Assert.Equal(new TrafficBytes(120, 130), interfaces["lo"]);
        var stat = "42 (name with ) brackets) S 12 " + string.Join(' ', Enumerable.Range(5, 17).Select(_ => "0")) + " 98765 0";
        Assert.Equal((12, 98765L), LinuxTrafficSampler.ParseStat(stat));
        Assert.Null(LinuxTrafficSampler.ParseStat("broken"));
        Assert.Equal(SourceKind.Codex, LinuxTrafficSampler.Classify("/usr/local/bin/codex"));
        Assert.Equal(SourceKind.Codex, LinuxTrafficSampler.Classify("/usr/bin/node", "/opt/npm/node_modules/@openai/codex/bin/codex.js"));
        Assert.Null(LinuxTrafficSampler.Classify("/usr/bin/curl", "secret prompt with codex"));
    }

    [Fact]
    public void SocketParserValidatesBoundsUidAndKernelCookieBeforeReadingPayloadCounters()
    {
        var bytes = new byte[16 + 72 + 140];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 7);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16 + 44), 123456789);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + 64), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + 68), 42);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16 + 72), 140);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16 + 74), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16 + 76 + 120), 120000);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16 + 76 + 128), 90000);
        Assert.Equal(new LinuxTcpCounter(42, 123456789, 90000, 120000), Assert.Single(LinuxSocketDiagnostics.Parse(bytes, 7, 1000).Rows));
        Assert.Empty(LinuxSocketDiagnostics.Parse(bytes, 7, 1001).Rows);
        Assert.Empty(LinuxSocketDiagnostics.Parse(bytes, 8, 1000).Rows);
        Assert.Throws<IOException>(() => LinuxSocketDiagnostics.Parse(bytes.AsSpan(0, 20), 7, 1000));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16 + 72), 500);
        Assert.Throws<IOException>(() => LinuxSocketDiagnostics.Parse(bytes, 7, 1000));
    }

    [Fact]
    public void BaselinesResetsSocketReuseAndSuspensionNeverBecomeTrafficSpikes()
    {
        var accounting = new LinuxTrafficAccounting();
        var first = accounting.Apply(Sample(Start, 100, 50));
        Assert.Null(first.MachineLive);
        Assert.Null(first.Sources[SourceKind.Codex].LiveSelf);
        var measured = accounting.Apply(Sample(Start.AddSeconds(1), 150, 90));
        Assert.Equal(new TrafficBytes(50, 40), measured.MachineToday);
        Assert.Equal(new TrafficBytes(50, 40), measured.Sources[SourceKind.Codex].Today.Self);
        Assert.Equal(new TrafficRate(50, 40), measured.Sources[SourceKind.Codex].LiveSelf);
        var reset = accounting.Apply(Sample(Start.AddSeconds(2), 5, 3));
        Assert.Equal(new TrafficBytes(50, 40), reset.MachineToday);
        var reused = accounting.Apply(Sample(Start.AddSeconds(3), 10, 8, cookie: 2));
        Assert.Equal(new TrafficBytes(50, 40), reused.Sources[SourceKind.Codex].Today.Self);
        var gap = accounting.Apply(Sample(Start.AddSeconds(40), 10000, 9000, cookie: 2));
        Assert.Null(gap.MachineLive);
        Assert.Null(gap.Sources[SourceKind.Codex].LiveSelf);
        Assert.Equal(new TrafficBytes(50, 40), gap.Sources[SourceKind.Codex].Today.Self);
    }

    [Fact]
    public void SharedSocketsAreDeduplicatedToolsStaySeparateAndAgentAttributionRequiresOpenTranscriptProof()
    {
        var root = Process(10, 0, SourceKind.Codex, [1], ["/synthetic/session.jsonl"]);
        var native = Process(11, 10, SourceKind.Codex, [1]);
        var tool = Process(12, 10, null, [2]);
        var processes = new[] { root, native, tool };
        var speed = SpeedSnapshot.Empty with
        {
            Agents = [new("/synthetic/session.jsonl", SourceKind.Codex, "project", "m", AgentPhase.Generating, false, Rate.Zero, null, Start)],
        };
        LinuxTrafficSample At(DateTimeOffset time, long bytes) => new(time, new Dictionary<string, TrafficBytes>(), processes,
            [new(1, 100, bytes, bytes), new(2, 200, bytes * 2, bytes * 2)], "synthetic");
        var accounting = new LinuxTrafficAccounting();
        accounting.Apply(At(Start, 0), speed);
        var snapshot = accounting.Apply(At(Start.AddSeconds(1), 100), speed);
        Assert.Equal(new TrafficBytes(100, 100), snapshot.Sources[SourceKind.Codex].Today.Self);
        Assert.Equal(new TrafficBytes(200, 200), snapshot.Sources[SourceKind.Codex].Today.Tools);
        Assert.Equal(new TrafficBytes(300, 300), snapshot.Agents["/synthetic/session.jsonl"].Today.Total(true));
        Assert.Equal(TrafficBytes.Zero, snapshot.Sources[SourceKind.Codex].Unassigned);
        var unassigned = new LinuxTrafficAccounting();
        unassigned.Apply(At(Start, 0));
        var next = unassigned.Apply(At(Start.AddSeconds(1), 100));
        Assert.Empty(next.Agents);
        Assert.Equal(new TrafficBytes(300, 300), next.Sources[SourceKind.Codex].Unassigned);
    }

    [Fact]
    public void MissingProcessObservationIsUnknownAndHistoryCsvStatesPartialCoverage()
    {
        var accounting = new LinuxTrafficAccounting();
        accounting.Apply(Sample(Start, 100, 50));
        var unavailable = accounting.Apply(Sample(Start.AddSeconds(1), 200, 100) with { ProcessError = "permission denied" });
        Assert.Equal(ProcessTrafficKind.Unavailable, unavailable.ProcessState.Kind);
        Assert.Null(unavailable.Sources[SourceKind.Codex].LiveSelf);
        Assert.Empty(unavailable.Agents);
        var csv = LinuxTrafficStore.Csv(accounting.Days);
        Assert.Contains("observed_interface_sum_virtual_overlap_possible_monitoring_intervals_only", csv);
        var resumed = accounting.Apply(Sample(Start.AddSeconds(2), 300, 150));
        Assert.Null(resumed.Sources[SourceKind.Codex].LiveSelf);
        var denied = accounting.Apply(Sample(Start.AddSeconds(3), 400, 200) with
        { Processes = [Process(10, 0, SourceKind.Codex, []) with { Readable = false }] });
        Assert.Equal(ProcessTrafficKind.Unavailable, denied.ProcessState.Kind);
        Assert.Equal(TrafficUnavailableReason.Denied, denied.ProcessState.Reason);
        Assert.Null(denied.Sources[SourceKind.Codex].LiveSelf);
    }

    [Fact]
    public void StorePreservesOldRowsAndNeverOverwritesCorruptUserHistoryOrTheTokenDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "linux-traffic-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tokenHistory = Path.Combine(directory, "history.sqlite");
            File.WriteAllText(tokenHistory, "existing-token-history-sentinel");
            var store = new LinuxTrafficStore(directory);
            var rows = new[] { new TrafficDay("2026-10-27", "machine", 123, 456, 0, 0, 1000),
                new TrafficDay("2026-10-27", "codex", 100, 200, 23, 24, 1000) };
            store.Save(rows);
            Assert.Equal(rows, store.Load());
            Assert.Contains("observed_application_bytes_mixed_capture_modes_unobserved_paths_excluded", LinuxTrafficStore.Csv(rows));
            File.WriteAllText(store.Path, "corrupt-sentinel");
            Assert.Empty(store.Load());
            Assert.NotNull(store.Error);
            store.Save([]);
            Assert.Equal("corrupt-sentinel", File.ReadAllText(store.Path));
            Assert.Equal("existing-token-history-sentinel", File.ReadAllText(tokenHistory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task ActualUnprivilegedLocalTcpPayloadIsAttributedBySocketInodeToTheRealCodexProcess()
    {
        var directory = Path.Combine(Path.GetTempPath(), "linux-traffic-tcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Process? child = null;
        try
        {
            var python = File.ResolveLinkTarget("/usr/bin/python3", true)?.FullName ?? "/usr/bin/python3";
            var executable = Path.Combine(directory, "codex");
            File.Copy(python, executable);
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var transcript = Path.Combine(directory, "synthetic-session.jsonl");
            var script = "import socket,sys\nf=open(sys.argv[1],'a')\ns=socket.socket();s.bind(('127.0.0.1',0));s.listen(1)\nprint(s.getsockname()[1],flush=True)\nc,_=s.accept();print('READY',flush=True)\nsys.stdin.readline()\nn=0\nwhile n<120000:\n n+=len(c.recv(min(16384,120000-n)))\nc.sendall(b'OK');print(n,flush=True)\nsys.stdin.readline()\n";
            var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-u"); start.ArgumentList.Add("-c"); start.ArgumentList.Add(script); start.ArgumentList.Add(transcript);
            child = System.Diagnostics.Process.Start(start)!;
            var portText = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(int.TryParse(portText, out var port), "synthetic TCP server could not start: " + portText);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            Assert.Equal("READY", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var sampler = new LinuxTrafficSampler(new() { IncludeLoopback = true, Interfaces = ["lo"] });
            var baseline = sampler.Read();
            Assert.Null(baseline.ProcessError);
            Assert.Contains(baseline.Processes, process => process.Identity.Pid == child.Id && process.Source == SourceKind.Codex && process.Sockets.Count > 0);
            var speed = SpeedSnapshot.Empty with { Agents = [new(transcript, SourceKind.Codex, "synthetic", "m", AgentPhase.Generating, false, Rate.Zero, null, Start)] };
            using var service = new LinuxTrafficService(new AppStorage(Path.Combine(directory, "data")), () => speed,
                new() { IncludeLoopback = true, Interfaces = ["lo"], Interval = TimeSpan.FromMilliseconds(250) });
            service.Start();
            Assert.False(service.Snapshot.Enabled);
            service.SetEnabled(true);
            await WaitUntil(() => service.Snapshot.ProcessState.Kind == ProcessTrafficKind.Running);
            Assert.Equal(ProcessTrafficKind.Unavailable, service.ReportSnapshot.ProcessState.Kind);
            Assert.Empty(service.ReportSnapshot.Sources);
            Assert.Empty(service.ReportSnapshot.Agents);
            Assert.Null(service.ReportSnapshot.MachineLive);
            Assert.Equal(TrafficBytes.Zero, service.ReportSnapshot.MachineToday);
            var accounting = new LinuxTrafficAccounting();
            accounting.Apply(baseline, speed);
            await child.StandardInput.WriteLineAsync("go");
            await child.StandardInput.FlushAsync();
            await client.GetStream().WriteAsync(new byte[120000]);
            var acknowledgement = new byte[2];
            await client.GetStream().ReadExactlyAsync(acknowledgement).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("120000", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await Task.Delay(150);
            var measured = accounting.Apply(sampler.Read(), speed);
            Assert.Equal(120000, measured.Sources[SourceKind.Codex].Today.Self.Rx);
            Assert.Equal(2, measured.Sources[SourceKind.Codex].Today.Self.Tx);
            Assert.Equal(120000, measured.Agents[transcript].Today.Self.Rx);
            Assert.True(measured.MachineToday.Rx >= 120000);
            await WaitUntil(() => service.Snapshot.Sources.TryGetValue(SourceKind.Codex, out var source) && source.Today.Self.Rx >= 120000);
            Assert.Equal(120000, service.Snapshot.Agents[transcript].Today.Self.Rx);
            service.SetEnabled(false);
            Assert.False(service.Snapshot.Enabled);
            Assert.Contains(service.Days, row => row.Key == "codex" && row.Rx == 120000);
            Assert.Contains(new LinuxTrafficStore(Path.Combine(directory, "data")).Load(), row => row.Key == "codex" && row.Rx == 120000);
        }
        finally
        {
            if (child is not null)
            {
                try { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); }
                finally { child.Dispose(); }
            }
            Directory.Delete(directory, true);
        }
    }

    private static LinuxTrafficProcess Process(int pid, int parent, SourceKind? source, ulong[] sockets, string[]? files = null) =>
        new(new(pid, pid * 100L), parent, source, sockets, files ?? [], true);
    private static LinuxTrafficSample Sample(DateTimeOffset time, long rx, long tx, ulong cookie = 1) => new(time,
        new Dictionary<string, TrafficBytes> { ["eth0"] = new(rx, tx) }, [Process(10, 0, SourceKind.Codex, [1])],
        [new(1, cookie, rx, tx)], "synthetic physical NIC");
    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < until) await Task.Delay(25);
        Assert.True(condition(), "Linux traffic service did not publish the expected observation");
    }
}
