using System.Diagnostics;
using System.Text.Json;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Linux;

public sealed record LinuxEventCounter(LinuxProcessIdentity Identity, LinuxProcessIdentity Parent, string Comm, long Rx, long Tx);
public sealed record LinuxEventCapture(string Epoch, long Lost, IReadOnlyList<LinuxEventCounter> Counters);

/// <summary>Fixed installed, explicitly authorized helper. Stdin EOF controls its privileged lifetime.</summary>
public sealed class LinuxTrafficEnhanced : IDisposable
{
    public const string HelperPath = "/opt/agent-speed/traffic/agent-speed-traffic-helper";
    public const string Coverage = "增强观测：IPv4／IPv6 TCP＋UDP 的成功协议发送／socket recvmsg 应用字节，包含采样间已关闭的连接；不含协议报文头、MSG_PEEK；sendfile／splice／io_uring 未逐路径验证。仅当前用户；无法证明会话归属时保留未归属。";
    private readonly object _gate = new();
    private readonly Dictionary<LinuxProcessIdentity, LinuxEventCounter> _counters = [];
    private readonly Dictionary<LinuxProcessIdentity, DateTimeOffset> _seen = [];
    private Process? _process;
    private CancellationTokenSource? _stop;
    private string _epoch = "";
    private string? _lastError;
    private long _lost;
    private bool _active;
    private string _status = "增强观测未开启；使用无需管理员权限的 TCP 采样。";
    private bool _disposed;
    public bool Active { get { lock (_gate) return _active; } }
    public string Status { get { lock (_gate) return _status; } }
    public bool Available => OperatingSystem.IsLinux() && File.Exists(HelperPath) && File.Exists("/usr/bin/pkexec") && File.Exists("/sys/kernel/btf/vmlinux");
    public event Action? Changed;

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_disposed || _process is not null) return;
            if (!Available) { _status = "增强观测组件或内核 BTF 不可用，继续使用基础 TCP 采样。"; Changed?.Invoke(); return; }
            _status = "正在请求管理员认证；只观测当前用户的套接字字节数。";
            _counters.Clear(); _seen.Clear(); _epoch = ""; _lost = 0; _lastError = null;
            _stop = new();
        }
        Changed?.Invoke();
        Process? process = null;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var start = new ProcessStartInfo("/usr/bin/pkexec")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(HelperPath); start.ArgumentList.Add("--capture");
            process = Process.Start(start) ?? throw new IOException("无法启动增强观测组件。");
            lock (_gate) { if (_disposed) { process.StandardInput.Close(); return; } _process = process; }
            var token = _stop!.Token;
            _ = ReadOutput(process, ready, token);
            _ = ReadErrors(process, token);
            _ = Ended(process, ready);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(120), token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            lock (_gate) { _active = false; _status = error is OperationCanceledException ? "增强观测已停止。" : "增强观测未开始，保留基础采样：" + error.Message; }
            try { process?.StandardInput.Close(); } catch (InvalidOperationException) { }
            Changed?.Invoke();
        }
    }

    private async Task ReadOutput(Process process, TaskCompletionSource<bool> ready, CancellationToken token)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                var parsed = Parse(line, CurrentUid());
                lock (_gate)
                {
                    if (!ReferenceEquals(_process, process) || token.IsCancellationRequested) return;
                    if (_epoch != parsed.Epoch) { _epoch = parsed.Epoch; _counters.Clear(); _seen.Clear(); }
                    foreach (var counter in parsed.Counters) { _counters[counter.Identity] = counter; _seen[counter.Identity] = DateTimeOffset.UtcNow; }
                    foreach (var key in _seen.Where(item => item.Value < DateTimeOffset.UtcNow.AddSeconds(-30)).Select(item => item.Key).ToArray()) { _counters.Remove(key); _seen.Remove(key); }
                    if (_counters.Count > 32768)
                    {
                        // Bound memory. A removed, previously consumed identity cannot be mistaken for a reused PID.
                        foreach (var key in _counters.Keys.Take(_counters.Count - 32768).ToArray()) { _counters.Remove(key); _seen.Remove(key); }
                        _lost++;
                    }
                    _lost = Math.Max(_lost, parsed.Lost);
                    _active = true;
                    _status = _lost == 0 ? "增强观测已开启（当前用户，TCP＋UDP／短连接）。" : $"增强观测已开启，但有 {_lost} 次遗漏或容量限制；数字为观测下限。";
                }
                ready.TrySetResult(true); Changed?.Invoke();
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                lock (_gate) { _active = false; _status = "增强观测数据无效，已回退基础采样。"; }
                ready.TrySetResult(false); try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
                Changed?.Invoke();
            }
        }
    }
    private async Task ReadErrors(Process process, CancellationToken token)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                lock (_gate) if (ReferenceEquals(_process, process)) { _lastError = line[..Math.Min(line.Length, 500)]; if (!_active) _status = "增强观测：" + _lastError; }
                Changed?.Invoke();
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task Ended(Process process, TaskCompletionSource<bool> ready)
    {
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null; _active = false;
                    _status = process.ExitCode == 0 ? "增强观测已停止，基础 TCP 采样仍可使用。" : "增强观测未启动（" + (_lastError ?? "系统认证取消或内核拒绝访问") + "）；继续使用基础 TCP 采样。";
                }
            }
            ready.TrySetResult(false); process.Dispose(); Changed?.Invoke();
        }
    }
    public LinuxEventCapture? Snapshot()
    { lock (_gate) return !_active ? null : new(_epoch, _lost, _counters.Values.ToArray()); }
    public void Stop()
    {
        lock (_gate)
        {
            _active = false;
            _stop?.Cancel();
            try { _process?.StandardInput.Close(); } catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or IOException) { }
            _status = "增强观测已停止，基础 TCP 采样仍可使用。";
        }
        Changed?.Invoke();
    }
    public void Dispose() { lock (_gate) _disposed = true; Stop(); }
    private static int CurrentUid()
    {
        var line = File.ReadLines("/proc/self/status").First(line => line.StartsWith("Uid:", StringComparison.Ordinal));
        return int.Parse(line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0], System.Globalization.CultureInfo.InvariantCulture);
    }
    public static LinuxEventCapture Parse(string text, int uid)
    {
        if (text.Length > 8 * 1024 * 1024) throw new JsonException("Enhanced sample too large");
        using var json = JsonDocument.Parse(text);
        var value = json.RootElement;
        if (value.GetProperty("schema").GetInt32() != 1 || value.GetProperty("uid").GetInt32() != uid
            || value.GetProperty("status").GetString() is not ("ready" or "sample")) throw new JsonException("Unexpected helper identity or schema");
        var epoch = value.GetProperty("epoch").GetString();
        var lost = value.GetProperty("lost").GetInt64();
        if (epoch is null || epoch.Length is < 1 or > 100 || lost < 0) throw new JsonException("Invalid helper epoch");
        var rows = new List<LinuxEventCounter>();
        foreach (var row in value.GetProperty("rows").EnumerateArray())
        {
            if (rows.Count >= 32768) throw new JsonException("Too many helper counters");
            var pid = row.GetProperty("pid").GetInt32(); var start = row.GetProperty("start").GetInt64();
            var parent = row.GetProperty("parent").GetInt32(); var parentStart = row.GetProperty("parentStart").GetInt64();
            var rx = row.GetProperty("rx").GetInt64(); var tx = row.GetProperty("tx").GetInt64(); var comm = row.GetProperty("comm").GetString();
            if (pid <= 0 || start < 0 || parent < 0 || parentStart < 0 || rx < 0 || tx < 0 || comm is null || comm.Length > 16) throw new JsonException("Invalid helper counter");
            rows.Add(new(new(pid, start), new(parent, parentStart), comm, rx, tx));
        }
        return new(epoch, lost, rows);
    }
}
