using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Linux;

/// <summary>Baseline/delta accounting. Missing observations never turn into historical zero samples.</summary>
public sealed class LinuxTrafficAccounting
{
    private readonly Dictionary<(string Day, string Key), TrafficDay> _days;
    private Dictionary<string, TrafficBytes> _interfaces = new(StringComparer.Ordinal);
    private Dictionary<(ulong Inode, ulong Cookie), LinuxTcpCounter> _sockets = [];
    private readonly Dictionary<string, TrafficSplit> _agentToday = new(StringComparer.Ordinal);
    private readonly Dictionary<SourceKind, TrafficSplit> _report = [];
    private readonly List<TrafficPoint> _machineHistory = [];
    private readonly Dictionary<SourceKind, List<TrafficPoint>> _sourceHistory = [];
    private DateTimeOffset? _previousTime;
    private string? _day;
    private bool _processPair;
    private string? _eventEpoch;
    private Dictionary<LinuxProcessIdentity, LinuxEventCounter> _eventCounters = [];
    private readonly Dictionary<LinuxProcessIdentity, LinuxTrafficProcess> _knownProcesses = [];
    private readonly long _epoch = DateTimeOffset.UtcNow.UtcTicks;
    public TrafficSnapshot Snapshot { get; private set; } = TrafficSnapshot.Off;
    public IReadOnlyList<TrafficDay> Days => _days.Values.OrderBy(row => row.Day, StringComparer.Ordinal).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
    public string? Since => _days.Keys.Select(key => key.Day).Order(StringComparer.Ordinal).FirstOrDefault();

    public LinuxTrafficAccounting(IEnumerable<TrafficDay>? existing = null) => _days = (existing ?? [])
        .Where(ValidDay).GroupBy(row => (row.Day, row.Key)).ToDictionary(group => group.Key, group => group.Last());

    public void ResetBaselines()
    {
        _interfaces.Clear(); _sockets.Clear(); _eventCounters.Clear(); _eventEpoch = null; _previousTime = null; _processPair = false;
        _machineHistory.Clear(); _sourceHistory.Clear();
    }

    public TrafficSnapshot Apply(LinuxTrafficSample sample, SpeedSnapshot? agents = null)
    {
        var day = sample.Time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_day != day) { _agentToday.Clear(); _day = day; }
        var interval = _previousTime is { } previous ? (sample.Time - previous).TotalMilliseconds : 0;
        var pair = interval is > 0 and <= 15000;
        var machine = TrafficBytes.Zero;
        var machinePair = false;
        if (sample.MachineError is null && pair)
            foreach (var (name, current) in sample.Interfaces)
                if (_interfaces.TryGetValue(name, out var before) && current.Rx >= before.Rx && current.Tx >= before.Tx)
                {
                    machine += Delta(before, current, interval);
                    machinePair = true;
                }
        _interfaces = new(sample.Interfaces, StringComparer.Ordinal);
        if (machinePair) AddDay(day, TrafficDay.MachineKey, new(machine, default), (long)interval);
        var machineRate = machinePair ? Rate(machine, interval) : (TrafficRate?)null;
        if (machineRate is { } observed) AddPoint(_machineHistory, sample.Time, observed);

        var processPair = pair && _processPair && sample.ProcessError is null;
        var processes = EventProcesses(sample);
        var groups = ClassifyTrees(processes);
        var deniedSources = processes.Where(process => sample.Events is null && !process.Readable && groups.ContainsKey(process.Identity))
            .Select(process => groups[process.Identity].Source).ToHashSet();
        var owners = new Dictionary<ulong, List<(LinuxTrafficProcess Process, LinuxTrafficOwner Owner)>>();
        foreach (var process in processes)
        {
            if (!groups.TryGetValue(process.Identity, out var owner)) continue;
            foreach (var inode in process.Sockets)
            {
                if (!owners.TryGetValue(inode, out var list)) owners[inode] = list = [];
                list.Add((process, owner));
            }
        }
        var increments = new Dictionary<SourceKind, TrafficSplit>();
        var agentIncrements = new Dictionary<string, TrafficSplit>(StringComparer.Ordinal);
        var nextSockets = new Dictionary<(ulong, ulong), LinuxTcpCounter>();
        if (sample.Events is { } events)
        {
            processPair = processPair && _eventEpoch == events.Epoch;
            var nextEvents = events.Counters.GroupBy(row => row.Identity).ToDictionary(group => group.Key, group => group.Last());
            foreach (var (identity, counter) in nextEvents)
            {
                if (!processPair || !groups.TryGetValue(identity, out var owner)) continue;
                var before = _eventCounters.GetValueOrDefault(identity);
                var delta = Delta(before is null ? TrafficBytes.Zero : new(before.Rx, before.Tx), new(counter.Rx, counter.Tx), interval);
                var split = owner.Tools ? new TrafficSplit(default, delta) : new TrafficSplit(delta, default);
                increments[owner.Source] = increments.GetValueOrDefault(owner.Source) + split;
                if (MatchAgent(owner.Source, owner.Root, groups, processes, agents) is { } id)
                    agentIncrements[id] = agentIncrements.GetValueOrDefault(id) + split;
            }
            _eventCounters = nextEvents; _eventEpoch = events.Epoch;
        }
        else if (sample.ProcessError is null)
            foreach (var socket in sample.Sockets)
            {
                var key = (socket.Inode, socket.Cookie);
                nextSockets[key] = socket;
                if (!processPair || !_sockets.TryGetValue(key, out var before) || !owners.TryGetValue(socket.Inode, out var list)) continue;
                var sources = list.Select(item => item.Owner.Source).Distinct().ToArray();
                if (sources.Length != 1) continue; // Shared across sources: attribution is unknown, never counted twice.
                var source = sources[0];
                var delta = Delta(new(before.Rx, before.Tx), new(socket.Rx, socket.Tx), interval);
                var tools = list.All(item => item.Owner.Tools);
                var split = tools ? new TrafficSplit(default, delta) : new TrafficSplit(delta, default);
                increments[source] = increments.GetValueOrDefault(source) + split;
                var roots = list.Select(item => item.Owner.Root).Distinct().ToArray();
                if (roots.Length == 1 && MatchAgent(source, roots[0], groups, processes, agents) is { } agent)
                    agentIncrements[agent] = agentIncrements.GetValueOrDefault(agent) + split;
            }
        _sockets = nextSockets;
        _processPair = sample.ProcessError is null;
        _previousTime = sample.Time;
        var sourceKinds = groups.Values.Select(owner => owner.Source)
            .Concat(_days.Values.Where(row => row.Day == day && row.Source is not null).Select(row => row.Source!.Value)).Distinct().ToArray();
        foreach (var source in sourceKinds)
        {
            var bytes = increments.GetValueOrDefault(source);
            if (processPair)
            {
                AddDay(day, Key(source), bytes, (long)interval);
                if (!_sourceHistory.TryGetValue(source, out var history)) _sourceHistory[source] = history = [];
                AddPoint(history, sample.Time, Rate(bytes.Total(true), interval));
                _report[source] = _report.GetValueOrDefault(source) + bytes;
            }
        }
        foreach (var (id, bytes) in agentIncrements) _agentToday[id] = _agentToday.GetValueOrDefault(id) + bytes;
        var sourceRows = new Dictionary<SourceKind, SourceTraffic>();
        foreach (var source in sourceKinds)
        {
            var row = _days.GetValueOrDefault((day, Key(source)));
            var today = row is null ? TrafficSplit.Zero : new(new(row.Rx, row.Tx), new(row.RxTools, row.TxTools));
            // Agent attribution is memory-only; persisted source bytes remain explicitly unassigned after restart.
            var attributed = _agentToday.Where(item => agents?.Agents.Any(agent => agent.Id == item.Key && agent.Source == source) == true)
                .Aggregate(TrafficBytes.Zero, (sum, item) => sum + item.Value.Total(true));
            var total = today.Total(true);
            sourceRows[source] = new(source, today, new(Math.Max(0, total.Rx - attributed.Rx), Math.Max(0, total.Tx - attributed.Tx)),
                processPair && !deniedSources.Contains(source) ? Rate(increments.GetValueOrDefault(source).Total(true), interval) : null,
                processPair && !deniedSources.Contains(source) ? Rate(increments.GetValueOrDefault(source).Self, interval) : null);
        }
        var agentRows = _agentToday.ToDictionary(item => item.Key, item => new AgentTraffic(item.Key, item.Value,
            processPair ? Rate(agentIncrements.GetValueOrDefault(item.Key).Total(true), interval) : null,
            processPair ? Rate(agentIncrements.GetValueOrDefault(item.Key).Self, interval) : null), StringComparer.Ordinal);
        var machineDay = _days.GetValueOrDefault((day, TrafficDay.MachineKey));
        var state = sample.ProcessError is not null ? ProcessTrafficState.Unavailable(TrafficUnavailableReason.Sampler)
            : deniedSources.Count > 0 ? ProcessTrafficState.Unavailable(TrafficUnavailableReason.Denied)
            : !processPair ? ProcessTrafficState.Starting : ProcessTrafficState.Running(Math.Max(1, (int)Math.Round(interval / 1000)));
        Snapshot = new()
        {
            Enabled = true, Timestamp = sample.Time, MachineLive = machineRate,
            MachineToday = machineDay is null ? default : new(machineDay.Rx, machineDay.Tx),
            MachineSampledMs = machineDay?.SampledMs ?? 0, MachineHistory = _machineHistory.ToArray(),
            ProcessState = state, ProcessError = sample.ProcessError,
            ProcessSampledMs = sourceKinds.Select(source => _days.GetValueOrDefault((day, Key(source)))?.SampledMs ?? 0).DefaultIfEmpty().Max(),
            Sources = sourceRows, Agents = agentRows,
            SourceHistory = _sourceHistory.ToDictionary(item => item.Key, item => (IReadOnlyList<TrafficPoint>)item.Value.ToArray()),
            DayStart = new DateTimeOffset(sample.Time.LocalDateTime.Date),
            ReportTotals = new(_epoch, new Dictionary<SourceKind, TrafficSplit>(_report)),
        };
        return Snapshot;
    }

    private IReadOnlyList<LinuxTrafficProcess> EventProcesses(LinuxTrafficSample sample)
    {
        var knownSources = ClassifyTrees(sample.Processes);
        foreach (var process in sample.Processes.Where(process => knownSources.ContainsKey(process.Identity))) _knownProcesses[process.Identity] = process;
        if (_knownProcesses.Count > 65536)
            foreach (var key in _knownProcesses.Keys.Take(_knownProcesses.Count - 65536).ToArray()) _knownProcesses.Remove(key);
        if (sample.Events is not { } events) return sample.Processes;
        var rows = sample.Processes.ToDictionary(process => process.Identity);
        var liveIdentities = rows.Keys.ToHashSet();
        var eventIdentities = events.Counters.Select(counter => counter.Identity).ToHashSet();
        foreach (var counter in events.Counters)
        {
            var known = _knownProcesses.GetValueOrDefault(counter.Identity);
            // A kernel-recorded parent identity must match; PID reuse can never reconnect an old child to a new root.
            var parent = _knownProcesses.ContainsKey(counter.Parent) || eventIdentities.Contains(counter.Parent)
                ? counter.Parent.Pid : 0;
            rows[counter.Identity] = known is not null ? known with { ParentPid = parent, ParentIdentity = parent > 0 ? counter.Parent : null, OpenFiles = liveIdentities.Contains(counter.Identity) ? known.OpenFiles : [], Readable = true }
                : new(counter.Identity, parent, LinuxTrafficSampler.Classify("/" + counter.Comm), [], [], true) { ParentIdentity = parent > 0 ? counter.Parent : null };
        }
        // Include already-observed exited parents only when their full kernel identity is referenced by an event.
        foreach (var counter in events.Counters)
            if (_knownProcesses.TryGetValue(counter.Parent, out var parent) && !rows.ContainsKey(parent.Identity)) rows[parent.Identity] = parent with { ParentPid = 0, OpenFiles = [] };
        return rows.Values.ToArray();
    }

    private void AddDay(string day, string key, TrafficSplit delta, long elapsed)
    {
        var old = _days.GetValueOrDefault((day, key)) ?? new(day, key, 0, 0, 0, 0, 0);
        _days[(day, key)] = old with { Rx = old.Rx + delta.Self.Rx, Tx = old.Tx + delta.Self.Tx,
            RxTools = old.RxTools + delta.Tools.Rx, TxTools = old.TxTools + delta.Tools.Tx, SampledMs = old.SampledMs + elapsed };
    }
    private static TrafficBytes Delta(TrafficBytes before, TrafficBytes now, double elapsed)
    {
        var limit = 12_500_000_000d * elapsed / 1000;
        long One(long previous, long current) => current >= previous && current - previous <= limit ? current - previous : 0;
        return new(One(before.Rx, now.Rx), One(before.Tx, now.Tx));
    }
    private static TrafficRate Rate(TrafficBytes bytes, double elapsed) => new(bytes.Rx * 1000d / elapsed, bytes.Tx * 1000d / elapsed);
    private static void AddPoint(List<TrafficPoint> history, DateTimeOffset time, TrafficRate rate)
    {
        history.Add(new(time, rate));
        history.RemoveAll(point => point.Time < time.AddMinutes(-5));
        if (history.Count > 300) history.RemoveRange(0, history.Count - 300);
    }
    public sealed record LinuxTrafficOwner(SourceKind Source, LinuxProcessIdentity Root, bool Tools);
    public static IReadOnlyDictionary<LinuxProcessIdentity, LinuxTrafficOwner> ClassifyTrees(IReadOnlyList<LinuxTrafficProcess> processes)
    {
        var byIdentity = processes.ToDictionary(process => process.Identity);
        var byPid = processes.GroupBy(process => process.Identity.Pid).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        var result = new Dictionary<LinuxProcessIdentity, LinuxTrafficOwner>();
        foreach (var process in processes)
        {
            var chain = new List<LinuxTrafficProcess>();
            var seen = new HashSet<LinuxProcessIdentity>();
            var current = process;
            while (seen.Add(current.Identity))
            {
                chain.Add(current);
                var next = current.ParentIdentity is { } identity ? byIdentity.GetValueOrDefault(identity) : byPid.GetValueOrDefault(current.ParentPid);
                if (next is null) break;
                current = next;
            }
            var owner = chain.FirstOrDefault(item => item.Source is not null);
            if (owner?.Source is not { } source) continue;
            var root = owner;
            foreach (var ancestor in chain.Skip(chain.IndexOf(owner) + 1))
            {
                if (ancestor.Source is { } ancestorSource && ancestorSource != source) break;
                if (ancestor.Source == source) root = ancestor;
            }
            result[process.Identity] = new(source, root.Identity, process.Source != source);
        }
        return result;
    }
    private static string? MatchAgent(SourceKind source, LinuxProcessIdentity root,
        IReadOnlyDictionary<LinuxProcessIdentity, LinuxTrafficOwner> groups, IReadOnlyList<LinuxTrafficProcess> processes, SpeedSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        var files = processes.Where(process => groups.TryGetValue(process.Identity, out var owner) && owner.Root == root)
            .SelectMany(process => process.OpenFiles).ToHashSet(StringComparer.Ordinal);
        var candidates = snapshot.Agents.Where(agent => agent.Source == source && Path.IsPathFullyQualified(agent.Id) && files.Contains(agent.Id)).ToArray();
        var byId = snapshot.Agents.ToDictionary(agent => agent.Id, StringComparer.Ordinal);
        string Root(AgentStatus agent)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (agent.ParentId is { } parent && seen.Add(agent.Id) && byId.TryGetValue(parent, out var next)) agent = next;
            return agent.Id;
        }
        var roots = candidates.Select(Root).Distinct(StringComparer.Ordinal).ToArray();
        return roots.Length == 1 ? roots[0] : null;
    }
    public static string Key(SourceKind source) => source switch
    {
        SourceKind.Codex => "codex", SourceKind.ClaudeCode => "claudeCode", SourceKind.ClaudeDesktop => "claudeDesktop",
        SourceKind.DeepSeek => "deepseek", SourceKind.OpenCode => "opencode", SourceKind.Gemini => "gemini", SourceKind.Omp => "omp",
        SourceKind.Antigravity => "antigravity", SourceKind.WorkBuddy => "workbuddy", SourceKind.WorkBuddyAi => "workbuddyAi",
        SourceKind.ZCode => "zcode", SourceKind.Kimi => "kimi", SourceKind.GrokBuild => "grokBuild", _ => "unknown",
    };
    public static bool ValidDay(TrafficDay row) => row is not null && DateOnly.TryParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        && (row.IsMachine || row.Source is not null) && row.Rx >= 0 && row.Tx >= 0 && row.RxTools >= 0 && row.TxTools >= 0 && row.SampledMs >= 0;
}

/// <summary>Traffic has its own additive file, preserving the token-history database and all older user data.</summary>
public sealed class LinuxTrafficStore
{
    public string Path { get; }
    public string? Error { get; private set; }
    public LinuxTrafficStore(string directory) => Path = System.IO.Path.Combine(directory, "traffic-linux.json");
    private sealed record Document(int Version, string Coverage, IReadOnlyList<TrafficDay> Days);
    public IReadOnlyList<TrafficDay> Load()
    {
        try
        {
            if (!File.Exists(Path)) return [];
            if (new FileInfo(Path).Length > 8 * 1024 * 1024) throw new IOException("流量历史文件过大，未改动原文件。");
            var data = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path));
            if (data?.Version is not (1 or 2) || data.Days is null) throw new IOException("流量历史格式不受支持，未改动原文件。");
            return data.Days.Where(LinuxTrafficAccounting.ValidDay).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Error = "无法读取流量历史；原文件未改动。"; return []; }
    }
    public void Save(IReadOnlyList<TrafficDay> days)
    {
        // Never replace an unreadable/unknown-format file with an apparently valid empty history.
        if (Error is not null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Document(2, "Mixed observed intervals: basic TCP counters and optional TCP/UDP socket-I/O events; unobserved paths excluded.", days)));
        File.Move(temp, Path, true);
    }
    public static string Csv(IEnumerable<TrafficDay> rows)
    {
        var builder = new StringBuilder("date,source,rx_observed_bytes,tx_observed_bytes,rx_tools_observed_bytes,tx_tools_observed_bytes,sampled_ms,coverage\r\n");
        foreach (var row in rows.Where(LinuxTrafficAccounting.ValidDay).OrderBy(row => row.Day, StringComparer.Ordinal).ThenBy(row => row.Key, StringComparer.Ordinal))
            builder.AppendJoin(',', row.Day, row.Key, row.Rx.ToString(CultureInfo.InvariantCulture), row.Tx.ToString(CultureInfo.InvariantCulture),
                row.RxTools.ToString(CultureInfo.InvariantCulture), row.TxTools.ToString(CultureInfo.InvariantCulture), row.SampledMs.ToString(CultureInfo.InvariantCulture),
                row.IsMachine ? "observed_interface_sum_virtual_overlap_possible_monitoring_intervals_only" : "observed_application_bytes_mixed_capture_modes_unobserved_paths_excluded").Append("\r\n");
        return builder.ToString();
    }
}
