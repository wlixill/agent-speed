using System.Globalization;
using System.Text.Json;
using AgentSpeed.Core;

namespace AgentSpeed.Linux;

public sealed record SpeedReading(string Value, string KindLabel, string VehiclePrefix, string LastMeasurement,
    string Explanation, bool IsEstimated, bool IsAvailable);

/// <summary>Presentation only: the monitor and persisted history always retain both original rates.</summary>
public static class MetricPresentation
{
    public static string Name(SpeedMetric metric) => metric == SpeedMetric.Total ? "输入＋输出吞吐量" : "输出速度";
    public static SpeedSnapshot Project(SpeedSnapshot snapshot, SpeedMetric metric) => metric == SpeedMetric.Output ? snapshot : snapshot with
    {
        Live = new(snapshot.Live.Total, snapshot.Live.Total),
        PeakToday = new(snapshot.PeakToday.Total, snapshot.PeakToday.Total),
        History = snapshot.History.Select(point => point with
        {
            Measured = new(point.Measured.Total, point.Measured.Total),
            Estimated = new(point.Estimated.Total, point.Estimated.Total),
        }).ToArray(),
    };

    public static double EstimatedShare(SpeedSnapshot snapshot, SpeedMetric metric)
    {
        if (metric == SpeedMetric.Output) return snapshot.LiveEstimatedShare;
        var sources = snapshot.Sources.Where(source => source.Enabled).ToArray();
        return sources.Length > 0 && snapshot.Live.Total > 0
            ? Math.Clamp(sources.Sum(source => source.LiveEstimated.Total) / snapshot.Live.Total, 0, 1)
            : snapshot.LiveEstimatedShare;
    }

    public static string Rate(double? value) => value is not { } number || !double.IsFinite(number) || number < 0 ? "—"
        : number >= 1_000_000 ? (number / 1_000_000).ToString("0.##", CultureInfo.CurrentCulture) + "M"
        : number >= 10_000 ? (number / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "K"
        : UsageProjection.Rate(number);
}

public static class SpeedReadingPresentation
{
    public static SpeedReading From(SpeedSnapshot snapshot, SpeedMetric metric = SpeedMetric.Output)
    {
        var last = LastMeasurement(snapshot.LastResponse, snapshot.Timestamp, metric);
        SpeedReading Missing(string label, string explanation) => new("—", label, "", last, explanation, false, false);
        if (snapshot.State.Kind == MonitorStateKind.Starting) return Missing("正在读取记录", "完成读取后显示可用的实测与历史参考估算。");
        if (snapshot.State.Kind == MonitorStateKind.NoSources) return Missing("未找到记录", "请在工具来源与路径中检查已启用的目录与读取状态。");
        var value = snapshot.Live.ValueFor(metric);
        var share = MetricPresentation.EstimatedShare(snapshot, metric);
        if (!double.IsFinite(value) || value < 0 || !double.IsFinite(share) || share < 0 || share > 1)
            return Missing("速度未知", "尚无有效的速度记录。");
        if (value == 0 && snapshot.GeneratingCount > 0)
            return Missing("等待本次用量记录", "已检测到请求进行中，目前没有可用的近期测速参考。本次用量写入后才可实测。");
        var totalNote = metric == SpeedMetric.Total ? "输入含缓存；该指标表示请求吞吐量，不是模型逐字解码速度。" : "";
        if (value > 0 && share > 0)
            return new("≈" + MetricPresentation.Rate(value), share >= 1 - 1e-9 ? "历史参考估算" : "含历史估算", "参考 · ", last,
                "生成中的部分参考最近已完成请求的速度，不代表当前屏幕的出字速度。长时间等待会拉低参考值；新用量写入后校正。" + totalNote,
                true, true);
        return new(MetricPresentation.Rate(value), value > 0 ? "请求实测 · 含等待" : "暂无实时输出", "", last,
            "实测值按请求 token 数 / 请求耗时计算，并投影到最近 3 秒，包含首 token 等待。" + totalNote, false, true);
    }

    private static string LastMeasurement(ResponseSummary? response, DateTimeOffset now, SpeedMetric metric)
    {
        if (response is null) return "尚无已完成请求的实测记录";
        if (response.EndedAt > now) return "最近记录时间晚于当前时间，请检查系统时钟";
        var age = now - response.EndedAt;
        var when = response.EndedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        var prefix = age > TimeSpan.FromMinutes(10) ? "旧记录 · " : "";
        var amount = metric == SpeedMetric.Total ? response.TotalTokens : response.OutputTokens;
        if (response.Duration is not { } duration || duration <= TimeSpan.Zero || response.Speed is not { } speed || !double.IsFinite(speed))
            return $"{prefix}最近完成 {amount:N0} token · 耗时未知 · {when}";
        if (metric == SpeedMetric.Total) speed = amount / duration.TotalSeconds;
        return $"{prefix}最近实测 {MetricPresentation.Rate(speed)} tok/s · {amount:N0} token / {duration.TotalSeconds:0.0} 秒（含等待） · {when}";
    }
}

public static class DataStatusPresentation
{
    public static string Describe(SpeedSnapshot snapshot, string directory)
    {
        var sources = snapshot.Sources.Where(source => source.Enabled).ToArray();
        var available = sources.Where(source => source.Available).ToArray();
        var state = snapshot.State.Kind switch
        {
            MonitorStateKind.Starting => $"正在载入 {snapshot.State.Progress:P0}",
            MonitorStateKind.NoSources => "未发现可读取的会话记录",
            _ => sources.Length == 0 ? "未启用工具来源"
                : available.Length > 0 ? $"{available.Length}/{sources.Length} 个来源可读 · 已发现 {available.Sum(source => source.SessionCount)} 个会话" : "目录当前不可用",
        };
        var latest = snapshot.Agents.Count > 0 ? snapshot.Agents.Max(agent => agent.LastActivity) : (DateTimeOffset?)null;
        var activity = latest is { } time ? time > snapshot.Timestamp ? "记录时间晚于系统时间"
            : time.ToLocalTime().ToString("MM-dd HH:mm:ss") + $"（{Math.Max(0, (snapshot.Timestamp - time).TotalSeconds):N0} 秒前）" : "暂无近期活动记录";
        var roots = sources.Length == 0 ? "尚未选择" : string.Join("\n", sources.Select(source => LinuxSources.Name(source.Kind) + "：" + source.RootPath));
        return state + "\n最近日志活动：" + activity + "\n读取目录：" + roots + "\n空闲时日志停止更新属于正常情况。";
    }

    public static string Diagnostics(SpeedSnapshot snapshot, HistoryState history, string directory, UserSettings settings) =>
        JsonSerializer.Serialize(new
        {
            generatedAt = snapshot.Timestamp, version = BuildInformation.Version,
            desktop = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            codexDirectory = directory, monitor = snapshot.State.Kind.ToString(),
            sources = snapshot.Sources.Where(source => source.Enabled).Select(source => new { kind = source.Kind.ToString(), source.RootPath, source.Available, source.SessionCount }),
            sessions = snapshot.Sources.Where(source => source.Enabled).Sum(source => source.SessionCount),
            history = history.Kind.ToString(), historyUpdatedAt = history.UpdatedAt,
            selectedMetric = settings.Metric.ToString(), snapshot.Live, snapshot.LiveEstimatedShare,
            currency = settings.Currency, publicDataUpdates = settings.EnablePriceUpdates,
            appearance = settings.Appearance.ToString(), effects = settings.Effects.ToString(), settings.ReduceMotion, settings.TrayShowSpeed,
            executionMode = ExecutionModePresentation.Current(snapshot, null), configuredMode = ExecutionModePresentation.ConfiguredSource(snapshot, null),
            reading = SpeedReadingPresentation.From(snapshot, settings.Metric),
            lastResponse = snapshot.LastResponse is { } response ? new
            {
                source = response.Source.ToString(), response.Model, response.OutputTokens, response.TotalTokens, response.Duration, response.Speed, response.EndedAt,
            } : null,
            snapshot.GeneratingCount, snapshot.ToolsCount,
            latestLogActivity = snapshot.Agents.Count > 0 ? snapshot.Agents.Max(agent => agent.LastActivity) : (DateTimeOffset?)null,
            warnings = snapshot.Warnings.Select(warning => warning.ToString()),
        }, new JsonSerializerOptions { WriteIndented = true });
}
