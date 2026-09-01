using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyOverlapCalculatorTests
{
    private static readonly DateOnly Day = new(2026, 7, 1);
    private static DateTimeOffset T(int hour, int minute = 0) =>
        new(Day.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromHours(hour).Add(TimeSpan.FromMinutes(minute))), DateTimeKind.Unspecified),
            TimeSpan.FromHours(2));

    [Fact]
    public void NoOverlap_ReturnsZero()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(8), T(10), 2m),
            Input(2, 2, 9, T(10), T(12), 2m),
        };

        var results = LegacyOverlapCalculator.Calculate(rows);
        Assert.All(results, result => Assert.Equal(0m, result.OverlapHours));
    }

    [Fact]
    public void ExactDuplicate_FullOverlapEqualsAtl()
    {
        var start = T(8);
        var end = T(10);
        var rows = new[]
        {
            Input(1, 1, 9, start, end, 2m),
            Input(2, 2, 9, start, end, 2m),
        };

        var second = LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2);
        Assert.Equal(2m, second.OverlapHours);
    }

    [Fact]
    public void PartialOverlap_IsCappedByAtl()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(8), T(11), 3m),
            Input(2, 2, 9, T(10), T(12), 1m),
        };

        var second = LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2);
        Assert.Equal(1m, second.OverlapHours);
    }

    [Fact]
    public void NestedInterval_UsesPreviousEndChain()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(8), T(17), 9m),
            Input(2, 2, 9, T(9), T(12), 3m),
            Input(3, 3, 9, T(10), T(11), 1m),
        };

        var results = LegacyOverlapCalculator.Calculate(rows);
        Assert.Equal(3m, results.Single(result => result.PerformanceId == 2).OverlapHours);
        Assert.Equal(1m, results.Single(result => result.PerformanceId == 3).OverlapHours);
    }

    [Fact]
    public void SameStart_UsesSortKeyTieBreak()
    {
        var start = T(8);
        var rows = new[]
        {
            Input(1, 2, 9, start, T(10), 2m),
            Input(2, 1, 9, start, T(11), 2m),
        };

        var results = LegacyOverlapCalculator.Calculate(rows);
        Assert.Equal(0m, results.Single(result => result.PerformanceId == 2).OverlapHours);
        Assert.Equal(2m, results.Single(result => result.PerformanceId == 1).OverlapHours);
    }

    [Fact]
    public void ThreeOverlappingRows_ChainsPreviousEnd()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(8), T(10), 2m),
            Input(2, 2, 9, T(9), T(11), 2m),
            Input(3, 3, 9, T(9, 30), T(12), 2m),
        };

        var results = LegacyOverlapCalculator.Calculate(rows);
        Assert.Equal(1m, results.Single(result => result.PerformanceId == 2).OverlapHours);
        Assert.Equal(1.5m, results.Single(result => result.PerformanceId == 3).OverlapHours);
    }

    [Fact]
    public void LongPreviousInterval_SpansMultipleRows()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(7), T(18), 11m),
            Input(2, 2, 9, T(8), T(9), 1m),
            Input(3, 3, 9, T(10), T(11), 1m),
            Input(4, 4, 9, T(12), T(13), 1m),
        };

        var results = LegacyOverlapCalculator.Calculate(rows);
        Assert.All(results.Where(result => result.PerformanceId > 1), result => Assert.Equal(1m, result.OverlapHours));
    }

    [Fact]
    public void OvernightCurrentRow_OverlapsPreviousEnd()
    {
        var day1 = new DateOnly(2026, 7, 1);
        var start1 = new DateTimeOffset(day1.ToDateTime(new TimeOnly(22, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));
        var end1 = new DateTimeOffset(day1.AddDays(1).ToDateTime(new TimeOnly(6, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));
        var start2 = new DateTimeOffset(day1.ToDateTime(new TimeOnly(23, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));
        var end2 = new DateTimeOffset(day1.AddDays(1).ToDateTime(new TimeOnly(7, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));

        var rows = new[]
        {
            Input(1, 1, 9, start1, end1, 8m),
            Input(2, 2, 9, start2, end2, 8m),
        };

        Assert.True(LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2).OverlapHours > 0m);
    }

    [Fact]
    public void OvernightPreviousRow_ExtendsOverlapForCurrent()
    {
        var day1 = new DateOnly(2026, 7, 1);
        var previousStart = new DateTimeOffset(day1.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));
        var previousEnd = new DateTimeOffset(day1.AddDays(1).ToDateTime(new TimeOnly(4, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));
        var currentStart = new DateTimeOffset(day1.AddDays(1).ToDateTime(new TimeOnly(3, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));
        var currentEnd = new DateTimeOffset(day1.AddDays(1).ToDateTime(new TimeOnly(8, 0), DateTimeKind.Unspecified), TimeSpan.FromHours(2));

        var rows = new[]
        {
            Input(1, 1, 9, previousStart, previousEnd, 8m),
            Input(2, 2, 9, currentStart, currentEnd, 5m),
        };

        Assert.Equal(1m, LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2).OverlapHours);
    }

    [Fact]
    public void TravelRow_ParticipatesInOverlapChain()
    {
        var rows = new[]
        {
            Input(1, 1, 5, T(8), T(9), 1m),
            Input(2, 2, 9, T(8, 30), T(11), 2m),
        };

        Assert.True(LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2).OverlapHours > 0m);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(18)]
    [InlineData(23)]
    public void ExcludedHfdTaak_DoesNotParticipateInChain(int excludedHfdTaak)
    {
        var rows = new[]
        {
            Input(1, 1, excludedHfdTaak, T(8), T(12), 4m),
            Input(2, 2, 9, T(9), T(11), 2m),
        };

        Assert.Equal(0m, LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2).OverlapHours);
    }

    [Fact]
    public void BlankStart_IsIneligible()
    {
        var rows = new[]
        {
            Input(1, 1, 9, null, T(10), 2m),
            Input(2, 2, 9, T(9), T(11), 2m),
        };

        var results = LegacyOverlapCalculator.Calculate(rows);
        Assert.Equal(0m, results.Single(result => result.PerformanceId == 1).OverlapHours);
        Assert.Equal(0m, results.Single(result => result.PerformanceId == 2).OverlapHours);
    }

    [Fact]
    public void AtlSmallerThanRawOverlap_WithNoPause_UsesGrossDurationCap()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(7), T(17), 10m),
            Input(2, 2, 9, T(9, 45), T(13, 20), 3.58m, pauseHours: 0m),
        };

        var second = LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2);
        Assert.Equal(3.583333333333333333333333333m, second.OverlapHours, 10);
    }

    [Fact]
    public void AtlSmallerThanRawOverlap_WithPause_CapsByAtl()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(8), T(12), 4m),
            Input(2, 2, 9, T(9), T(13, 30), 1.25m, pauseHours: 0.5m),
        };

        var second = LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2);
        Assert.Equal(1.25m, second.OverlapHours);
    }

    [Fact]
    public void SameBon_DoesNotChangeOverlapSemantics()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(8), T(10), 2m),
            Input(2, 2, 9, T(9), T(11), 2m),
        };

        var baseline = LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2).OverlapHours;
        Assert.Equal(baseline, LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2).OverlapHours);
    }

    [Fact]
    public void DifferentBon_DoesNotChangeOverlapSemantics()
    {
        var rows = new[]
        {
            Input(1, 1, 9, T(8), T(10), 2m),
            Input(2, 2, 9, T(9), T(11), 2m),
        };

        Assert.Equal(1m, LegacyOverlapCalculator.Calculate(rows).Single(result => result.PerformanceId == 2).OverlapHours);
    }

    private static LegacyOverlapPerformanceInput Input(
        long id,
        long sortKey,
        int hfdTaak,
        DateTimeOffset? start,
        DateTimeOffset? end,
        decimal atl,
        decimal pauseHours = 0m) =>
        new(id, sortKey, hfdTaak, start, end, atl, pauseHours);
}
