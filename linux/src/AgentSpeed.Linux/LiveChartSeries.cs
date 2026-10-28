using AgentSpeed.Core;

namespace AgentSpeed.Linux;

/// <summary>One actual one-second speed bucket. A new segment deliberately leaves unavailable periods unconnected.</summary>
public readonly record struct LiveChartBucket(DateTimeOffset Time, double Measured, double Estimated, bool StartsNewSegment)
{
    public double Total => Measured + Estimated;
}

/// <summary>Time-based projection for the live chart; missing or invalid observations never turn into invented zeroes.</summary>
public static class LiveChartSeries
{
    public static int NormalizeWindow(int seconds) => seconds is 60 or 120 or 300 ? seconds : 120;

    public static DateTimeOffset EndTime(IReadOnlyList<HistoryPoint> history, DateTimeOffset snapshotTime)
    {
        var time = snapshotTime != DateTimeOffset.MinValue ? snapshotTime
            : history.LastOrDefault(point => point.Time != DateTimeOffset.MinValue)?.Time ?? DateTimeOffset.MinValue;
        return time == DateTimeOffset.MinValue ? time : DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds());
    }

    public static IReadOnlyList<LiveChartBucket> Window(IReadOnlyList<HistoryPoint> history, DateTimeOffset snapshotTime, int seconds)
    {
        var end = EndTime(history, snapshotTime);
        if (end == DateTimeOffset.MinValue) return [];
        var start = end.AddSeconds(1 - NormalizeWindow(seconds));
        var points = new List<LiveChartBucket>();
        var discontinuity = true;
        DateTimeOffset? previous = null;
        foreach (var point in history)
        {
            if (point.Time == DateTimeOffset.MinValue || point.Time > end)
            {
                discontinuity = true;
                continue;
            }
            if (point.Time < start) continue;
            var measured = point.Measured.Output;
            var estimated = point.Estimated.Output;
            if (!double.IsFinite(measured) || !double.IsFinite(estimated) || measured < 0 || estimated < 0
                || !double.IsFinite(measured + estimated) || previous is { } last && point.Time <= last)
            {
                discontinuity = true;
                continue;
            }
            var gap = previous is { } known && (point.Time - known).TotalMilliseconds > 1500;
            points.Add(new(point.Time, measured, estimated, discontinuity || gap));
            previous = point.Time;
            discontinuity = false;
        }
        return points;
    }

    public static double X(DateTimeOffset time, DateTimeOffset end, int seconds, double width)
    {
        if (!double.IsFinite(width) || width <= 0) return 0;
        var length = NormalizeWindow(seconds) - 1;
        var fraction = (time - end.AddSeconds(-length)).TotalSeconds / length;
        return Math.Clamp(fraction, 0, 1) * width;
    }

    /// <summary>Nearest known bucket at a plot x, or -1 when the pointer is over an unavailable gap.</summary>
    public static int IndexAt(IReadOnlyList<LiveChartBucket> points, DateTimeOffset end, int seconds, double x, double width)
    {
        if (points.Count == 0 || !double.IsFinite(x) || !double.IsFinite(width) || width <= 0) return -1;
        var length = NormalizeWindow(seconds) - 1;
        var target = end.AddSeconds(-length + Math.Clamp(x / width, 0, 1) * length);
        var nearest = -1;
        var distance = double.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var candidate = Math.Abs((points[i].Time - target).TotalMilliseconds);
            if (candidate < distance) { nearest = i; distance = candidate; }
        }
        return distance <= 750 ? nearest : -1;
    }
}
