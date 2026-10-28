using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Linux;

public sealed record LinuxTrafficOptions
{
    public bool IncludeLoopback { get; init; }
    public IReadOnlyList<string>? Interfaces { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
}

public readonly record struct LinuxProcessIdentity(int Pid, long StartTicks);
public sealed record LinuxTrafficProcess(LinuxProcessIdentity Identity, int ParentPid, SourceKind? Source,
    IReadOnlyList<ulong> Sockets, IReadOnlyList<string> OpenFiles, bool Readable)
{
    public LinuxProcessIdentity? ParentIdentity { get; init; }
}
public sealed record LinuxTrafficSample(DateTimeOffset Time, IReadOnlyDictionary<string, TrafficBytes> Interfaces,
    IReadOnlyList<LinuxTrafficProcess> Processes, IReadOnlyList<LinuxTcpCounter> Sockets,
    string InterfaceScope, string? MachineError = null, string? ProcessError = null, int UnreadableProcesses = 0)
{
    public LinuxEventCapture? Events { get; init; }
}

/// <summary>Reads counters and same-user process metadata, never socket payloads or stored command lines.</summary>
public sealed class LinuxTrafficSampler
{
    private readonly LinuxTrafficOptions _options;
    private readonly LinuxSocketDiagnostics _sockets = new();
    public LinuxTrafficSampler(LinuxTrafficOptions? options = null) => _options = options ?? new();

    public LinuxTrafficSample Read()
    {
        IReadOnlyDictionary<string, TrafficBytes> interfaces = new Dictionary<string, TrafficBytes>();
        var scope = "物理网卡";
        string? machineError = null, processError = null;
        try
        {
            var all = ParseInterfaces(File.ReadAllText("/proc/net/dev"));
            var requested = _options.Interfaces?.ToHashSet(StringComparer.Ordinal);
            var physical = all.Keys.Where(name => Directory.Exists("/sys/class/net/" + name + "/device")).ToHashSet(StringComparer.Ordinal);
            var fallback = requested is null && physical.Count == 0;
            interfaces = all.Where(pair => (pair.Key != "lo" || _options.IncludeLoopback)
                && (requested is not null ? requested.Contains(pair.Key) : physical.Contains(pair.Key) || fallback || pair.Key == "lo" && _options.IncludeLoopback))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            scope = requested is not null ? "指定接口" : fallback ? "非环回接口（虚拟接口可能重复）" : "物理网卡";
            if (_options.IncludeLoopback) scope += "＋环回";
            scope += "：" + string.Join(", ", interfaces.Keys.Order(StringComparer.Ordinal));
            if (interfaces.Count == 0) machineError = "未找到所选网络接口。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { machineError = "无法读取网卡计数器。"; }
        var (processes, unreadable) = ReadProcesses();
        IReadOnlyList<LinuxTcpCounter> sockets = [];
        try { sockets = _sockets.Read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { processError = error.Message; }
        return new(DateTimeOffset.UtcNow, interfaces, processes, sockets, scope, machineError, processError, unreadable);
    }

    public static IReadOnlyDictionary<string, TrafficBytes> ParseInterfaces(string text)
    {
        var result = new Dictionary<string, TrafficBytes>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.LastIndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var fields = line[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 16 && long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rx)
                && long.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out var tx) && rx >= 0 && tx >= 0)
                result[name] = new(rx, tx);
        }
        return result;
    }

    /// <summary>PID identity includes /proc starttime, which does not reset when a PID is reused.</summary>
    public static (int Parent, long Start)? ParseStat(string text)
    {
        var end = text.LastIndexOf(')');
        if (end < 0) return null;
        var fields = text[(end + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19 && int.TryParse(fields[1], out var parent) && long.TryParse(fields[19], out var start) && start >= 0
            ? (parent, start) : null;
    }

    private static (IReadOnlyList<LinuxTrafficProcess> Rows, int Unreadable) ReadProcesses()
    {
        var rows = new List<LinuxTrafficProcess>();
        var unreadable = 0;
        var uid = ReadUid("/proc/self/status");
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
            try
            {
                if (ReadUid(directory + "/status") != uid) continue;
                var before = ParseStat(File.ReadAllText(directory + "/stat"));
                if (before is not { } identity) continue;
                var executable = new FileInfo(directory + "/exe").LinkTarget;
                if (executable is null) continue;
                if (executable.EndsWith(" (deleted)", StringComparison.Ordinal)) executable = executable[..^10];
                var name = Path.GetFileName(executable);
                var script = name is "node" or "nodejs" or "bun" or "deno" ? ReadScript(directory + "/cmdline") : null;
                var source = Classify(executable, script);
                var sockets = new HashSet<ulong>();
                var files = new HashSet<string>(StringComparer.Ordinal);
                var readable = true;
                try
                {
                    var count = 0;
                    foreach (var fd in Directory.EnumerateFiles(directory + "/fd"))
                    {
                        if (++count > 16384) { readable = false; break; }
                        var target = new FileInfo(fd).LinkTarget;
                        if (target is null) continue;
                        if (target.StartsWith("socket:[", StringComparison.Ordinal) && target.EndsWith(']')
                            && ulong.TryParse(target.AsSpan(8, target.Length - 9), out var inode)) sockets.Add(inode);
                        else if (Path.IsPathFullyQualified(target)) files.Add(target.EndsWith(" (deleted)", StringComparison.Ordinal) ? target[..^10] : target);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { readable = false; }
                // A PID can exit/reappear during an fd scan. Never attach old sockets to its replacement.
                if (ParseStat(File.ReadAllText(directory + "/stat")) != before) continue;
                if (!readable) unreadable++;
                rows.Add(new(new(pid, identity.Start), identity.Parent, source, sockets.ToArray(), files.ToArray(), readable));
            }
            catch (UnauthorizedAccessException) { unreadable++; }
            catch (IOException) { } // A process exited while /proc was enumerated.
        }
        return (rows, unreadable);
    }

    private static int? ReadUid(string path)
    {
        var line = File.ReadLines(path).FirstOrDefault(line => line.StartsWith("Uid:", StringComparison.Ordinal));
        return line is not null && int.TryParse(line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out var uid) ? uid : null;
    }

    private static string? ReadScript(string path)
    {
        // Inspect only a bounded script-host argv to identify known package entry paths; never expose or retain argv.
        using var stream = File.OpenRead(path);
        var buffer = new byte[16384];
        var length = stream.Read(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, length).Split('\0').Skip(1)
            .FirstOrDefault(argument => argument.Length > 0 && !argument.StartsWith('-'));
    }

    public static SourceKind? Classify(string executable, string? scriptEntry = null)
    {
        var name = Path.GetFileName(executable);
        if (name == "codex" || name.StartsWith("codex-", StringComparison.Ordinal) && name.Contains("linux", StringComparison.Ordinal)
            || scriptEntry?.Contains("/@openai/codex/bin/codex.js", StringComparison.Ordinal) == true) return SourceKind.Codex;
        if (name == "claude" || executable.Contains("/.local/share/claude/versions/", StringComparison.Ordinal)
            || scriptEntry?.Contains("/@anthropic-ai/claude-code/", StringComparison.Ordinal) == true) return SourceKind.ClaudeCode;
        if (name is "opencode" or "opencode-cli" || scriptEntry?.Contains("/opencode-ai/bin/opencode", StringComparison.Ordinal) == true) return SourceKind.OpenCode;
        if (name == "gemini" || scriptEntry?.Contains("/@google/gemini-cli/", StringComparison.Ordinal) == true) return SourceKind.Gemini;
        if (name == "omp" || scriptEntry?.Contains("/@oh-my-pi/", StringComparison.Ordinal) == true) return SourceKind.Omp;
        if (name is "kimi" or "kimi-code") return SourceKind.Kimi;
        if (name is "grok" or "grok-build" or "xai-grok-pager") return SourceKind.GrokBuild;
        if (name is "zcode" or "zcode-cli") return SourceKind.ZCode;
        if (name == "dsh") return SourceKind.DeepSeek;
        if (name == "agy" || name.StartsWith("language_server_linux_", StringComparison.Ordinal)
            && executable.Contains("antigravity", StringComparison.OrdinalIgnoreCase)) return SourceKind.Antigravity;
        return null;
    }
}
