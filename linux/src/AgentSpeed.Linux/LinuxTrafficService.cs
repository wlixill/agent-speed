using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Linux;

/// <summary>Opt-in Linux traffic monitoring. Optional explicitly authorized helper observes counters only.</summary>
public sealed class LinuxTrafficService : IDisposable
{
    public const string ProcessCoverage = "TCP 已观测下限：仅已采样套接字的确认上传／接收计数，不含协议报文头；不含 UDP、采样间关闭的连接、其他用户或网络命名空间";
    private readonly object _gate = new();
    private readonly LinuxTrafficSampler _sampler;
    private readonly LinuxTrafficEnhanced _enhanced = new();
    private bool _usingEnhanced;
    public bool EnhancedActive => _enhanced.Active;
    public bool EnhancedAvailable => _enhanced.Available;
    public string EnhancedStatus => _enhanced.Status;
    public Task StartEnhancedAsync()
    {
        lock (_gate) if (!_enabled) return Task.CompletedTask;
        return _enhanced.StartAsync();
    }
    public void StopEnhanced() => _enhanced.Stop();
    private readonly LinuxTrafficAccounting _accounting;
    private readonly LinuxTrafficStore _store;
    private readonly Func<SpeedSnapshot>? _agents;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _worker;
    private bool _enabled, _disposed;
    private DateTimeOffset _lastSave, _lastDays;
    private string _coverage = ProcessCoverage;
    private string[] _interfacesUsed = [];
    private string _interfaceScope = "尚未读取接口";
    public TrafficHub Hub { get; }
    public TrafficSnapshot Snapshot => Hub.Snapshot;
    public IReadOnlyList<TrafficDay> Days => Hub.Days;
    public string? Since => Hub.Since;
    public string Coverage => Volatile.Read(ref _coverage);
    public bool IsPartial => true;
    public IReadOnlyList<string> InterfacesUsed => Volatile.Read(ref _interfacesUsed);
    public string InterfaceScope => Volatile.Read(ref _interfaceScope);
    public string? Error { get; private set; }
    public event Action? Changed;

    /// <summary>
    /// The report schema cannot mark mixed incomplete observation domains. Keep it unavailable and empty there, never expose
    /// machine-wide NIC totals through reporting. Local traffic views continue to show the labelled observations.
    /// </summary>
    public TrafficSnapshot ReportSnapshot => !Snapshot.Enabled ? TrafficSnapshot.Off : new()
    {
        Enabled = true, Timestamp = Snapshot.Timestamp,
        ProcessState = ProcessTrafficState.Unavailable(TrafficUnavailableReason.Sampler),
        ProcessError = "Linux 应用字节观测存在未覆盖路径，不能表示为完整进程流量；本机网卡流量不参与上报。",
    };

    public LinuxTrafficService(AppStorage storage, Func<SpeedSnapshot>? agents = null, LinuxTrafficOptions? options = null,
        SynchronizationContext? context = null)
    {
        _agents = agents;
        _enhanced.Changed += () => Changed?.Invoke();
        options ??= new();
        _interval = TimeSpan.FromMilliseconds(Math.Clamp(options.Interval.TotalMilliseconds, 250, 5000));
        _sampler = new(options);
        _store = new(storage.DataDirectory);
        _accounting = new(_store.Load());
        Error = _store.Error;
        Hub = new(context);
        Hub.SnapshotChanged += (_, _) => Changed?.Invoke();
        Hub.DaysChanged += (_, _) => Changed?.Invoke();
        Hub.PublishDays(_accounting.Days, _accounting.Since);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _worker is not null) return;
            _lastSave = DateTimeOffset.UtcNow;
            _worker = Task.Run(RunAsync);
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!enabled) _enhanced.Stop();
        IReadOnlyList<TrafficDay>? days = null;
        string? since = null;
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            _accounting.ResetBaselines();
            if (!enabled) { days = _accounting.Days; since = _accounting.Since; Save(); }
        }
        Hub.Publish(enabled ? new TrafficSnapshot { Enabled = true, Timestamp = DateTimeOffset.UtcNow, ProcessState = ProcessTrafficState.Starting }
            : TrafficSnapshot.Off);
        if (days is not null) Hub.PublishDays(days, since);
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                bool enabled;
                lock (_gate) enabled = _enabled;
                if (!enabled) { await _wake.WaitAsync(_stop.Token).ConfigureAwait(false); continue; }
                LinuxTrafficSample sample;
                try { sample = _sampler.Read(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    sample = new(DateTimeOffset.UtcNow, new Dictionary<string, TrafficBytes>(), [], [], "网卡状态未知",
                        "无法完成本次网卡采样。", "无法完成本次进程采样。");
                }
                TrafficSnapshot? snapshot = null;
                IReadOnlyList<TrafficDay>? days = null;
                string? since = null;
                lock (_gate)
                {
                    if (_enabled && !_disposed)
                    {
                        var enhanced = _enhanced.Snapshot();
                        if (_usingEnhanced != (enhanced is not null)) { _accounting.ResetBaselines(); _usingEnhanced = enhanced is not null; }
                        if (enhanced is not null) sample = sample with { Events = enhanced, ProcessError = null };
                        snapshot = _accounting.Apply(sample, _agents?.Invoke());
                        Volatile.Write(ref _interfacesUsed, sample.Interfaces.Keys.Order(StringComparer.Ordinal).ToArray());
                        Volatile.Write(ref _interfaceScope, sample.InterfaceScope);
                        var coverage = (enhanced is null ? ProcessCoverage : LinuxTrafficEnhanced.Coverage) + "；" + sample.InterfaceScope
                            + "。网卡含协议开销，进程为应用字节，两者口径不同。";
                        if (enhanced is { Lost: > 0 }) coverage += $" 事件采集有 {enhanced.Lost} 次遗漏或容量限制。";
                        if (sample.UnreadableProcesses > 0) coverage += $" {sample.UnreadableProcesses} 个进程的权限或文件描述符不完整。";
                        if (sample.MachineError is not null) coverage += " " + sample.MachineError;
                        if (sample.ProcessError is not null) coverage += " " + sample.ProcessError;
                        Volatile.Write(ref _coverage, coverage);
                        if (sample.Time - _lastSave >= TimeSpan.FromSeconds(60)) Save();
                        if (sample.Time - _lastDays >= TimeSpan.FromSeconds(10))
                        { days = _accounting.Days; since = _accounting.Since; _lastDays = sample.Time; }
                    }
                }
                if (days is not null) Hub.PublishDays(days, since);
                if (snapshot is not null) Hub.Publish(snapshot);
                await Task.Delay(_interval, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { lock (_gate) Save(); }
    }

    private void Save()
    {
        try
        {
            _store.Save(_accounting.Days);
            Error = _store.Error;
            _lastSave = DateTimeOffset.UtcNow;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Error = "无法保存流量历史；已观测数据暂留内存，下次保存会重试。"; }
    }

    public void Dispose()
    {
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _enabled = false; worker = _worker;
        }
        _enhanced.Dispose();
        _stop.Cancel();
        if (worker is not null) worker.Wait(TimeSpan.FromSeconds(2));
        else lock (_gate) Save();
        if (worker is null || worker.IsCompleted) _stop.Dispose();
        else _ = worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
