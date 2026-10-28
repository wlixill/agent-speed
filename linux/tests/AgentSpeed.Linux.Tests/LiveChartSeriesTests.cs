using AgentSpeed.Core;

namespace AgentSpeed.Linux.Tests;

public sealed class LiveChartSeriesTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WindowKeepsExactlyTheRequestedLastSecondsIncludingTheCurrentFractionalSecond()
    {
        var history = Enumerable.Range(0, 300).Select(index => Point(End.AddSeconds(index - 299), index, 1)).ToArray();
        var window = LiveChartSeries.Window(history, End.AddMilliseconds(700), 60);
        Assert.Equal(60, window.Count);
        Assert.Equal(End.AddSeconds(-59), window[0].Time);
        Assert.Equal(300, window[^1].Total);
        Assert.True(window[0].StartsNewSegment);
        Assert.All(window.Skip(1), point => Assert.False(point.StartsNewSegment));
    }

    [Fact]
    public void MissingTimesAndInvalidNumbersCreateGapsInsteadOfFabricatedZeroes()
    {
        var history = new[] { Point(End.AddSeconds(-4), 10), Point(DateTimeOffset.MinValue, 20),
            Point(End.AddSeconds(-3), double.NaN), Point(End.AddSeconds(-2), -1), Point(End.AddSeconds(-1), 30), Point(End, 0) };
        var points = LiveChartSeries.Window(history, End, 60);
        Assert.Equal(new[] { 10d, 30d, 0d }, points.Select(point => point.Total));
        Assert.True(points[1].StartsNewSegment);
        Assert.False(points[2].StartsNewSegment);
    }

    [Fact]
    public void ATimeGapPreservesItsActualHorizontalSpaceAndCannotBeHoveredAsARealSample()
    {
        var points = LiveChartSeries.Window([Point(End.AddSeconds(-20), 10), Point(End, 20)], End, 60);
        Assert.True(points[1].StartsNewSegment);
        var olderX = LiveChartSeries.X(points[0].Time, End, 60, 590);
        Assert.Equal(390, olderX);
        Assert.Equal(590, LiveChartSeries.X(points[1].Time, End, 60, 590));
        Assert.Equal(-1, LiveChartSeries.IndexAt(points, End, 60, 490, 590));
        Assert.Equal(0, LiveChartSeries.IndexAt(points, End, 60, 390, 590));
    }

    [Fact]
    public void AStaleSnapshotDoesNotMoveOldSamplesToNowOrConnectDuplicateTimes()
    {
        Assert.Empty(LiveChartSeries.Window([Point(End.AddMinutes(-5), 10)], End, 60));
        var points = LiveChartSeries.Window([Point(End.AddSeconds(-1), 10), Point(End.AddSeconds(-1), 100), Point(End, 20)], End, 60);
        Assert.Equal(2, points.Count);
        Assert.True(points[1].StartsNewSegment);
        Assert.Equal(20, points[1].Total);
    }

    [Fact]
    public void EstimatesStaySeparateAndOverflowCannotPoisonTheScale()
    {
        var points = LiveChartSeries.Window([Point(End.AddSeconds(-1), 12, 8), Point(End, double.MaxValue, double.MaxValue)], End, 60);
        var point = Assert.Single(points);
        Assert.Equal(12, point.Measured);
        Assert.Equal(8, point.Estimated);
        Assert.Equal(20, point.Total);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(120, 120)]
    [InlineData(300, 300)]
    [InlineData(0, 120)]
    [InlineData(-1, 120)]
    [InlineData(500, 120)]
    public void UnsupportedRangesUseTheDefault(int input, int expected) => Assert.Equal(expected, LiveChartSeries.NormalizeWindow(input));

    private static HistoryPoint Point(DateTimeOffset time, double measured, double estimated = 0) =>
        new(time, new Rate(measured), new Rate(estimated));
}
