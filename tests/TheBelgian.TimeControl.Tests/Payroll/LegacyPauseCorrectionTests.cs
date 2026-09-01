using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyPauseCorrectionTests
{
    private static readonly DateOnly Monday = new(2026, 7, 6);
    private static readonly DateOnly Saturday = new(2026, 7, 11);
    private static readonly DateOnly Sunday = new(2026, 7, 12);

    [Fact]
    public void WeekdayOrdinaryWorkNoPause_ReturnsMinusThirtyMinutes()
    {
        var rows = new[] { Daily(1, 9, 8m, 0m) };
        var result = LegacyPauseCorrection.Calculate("495", Monday, rows);
        Assert.Equal(-0.5m, result.PauseCorrectionHours);
    }

    [Fact]
    public void WeekdayFifteenMinutes_ReturnsMinusFifteenMinutes()
    {
        var rows = new[] { Daily(1, 9, 8m, 0.25m) };
        Assert.Equal(-0.25m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void WeekdaySeventeenMinutes_ReturnsMinusThirteenMinutes()
    {
        var rows = new[] { Daily(1, 9, 8m, 17m / 60m) };
        Assert.Equal(-13m / 60m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void WeekdayThirtyMinutes_ReturnsZero()
    {
        var rows = new[] { Daily(1, 9, 8m, 0.5m) };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void WeekdayFortyFiveMinutes_ReturnsZero()
    {
        var rows = new[] { Daily(1, 9, 8m, 0.75m) };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void SaturdayNoPause_ReturnsZero()
    {
        var rows = new[] { Daily(1, 9, 8m, 0m) };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Saturday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void SundayNoPause_ReturnsZero()
    {
        var rows = new[] { Daily(1, 9, 8m, 0m) };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Sunday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void OnlyTravelRows_ReturnsZero()
    {
        var rows = new[] { Daily(1, 5, 1m, 0m) };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void OnlyAbsence10_ReturnsZero()
    {
        var rows = new[] { Daily(1, 10, 8m, 0m) };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void OnlyAbsence18_ReturnsZero()
    {
        var rows = new[] { Daily(1, 18, 8m, 0m) };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void OnlyStandby23_ReturnsZero()
    {
        var rows = new[] { Daily(1, 23, 8m, 0m) };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void TravelAndOrdinaryWork_SumsTravelPause()
    {
        var rows = new[]
        {
            Daily(1, 5, 1m, 0.25m),
            Daily(2, 9, 8m, 0.25m),
        };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    [Fact]
    public void MultipleRows_SumsPauses()
    {
        var rows = new[]
        {
            Daily(1, 9, 4m, 0.25m),
            Daily(2, 9, 4m, 0.25m),
        };
        Assert.Equal(0m, LegacyPauseCorrection.Calculate("495", Monday, rows).PauseCorrectionHours);
    }

    private static LegacyDailyPerformanceInput Daily(long id, int hfd, decimal atl, decimal pause) =>
        new(id, id, hfd, null, null, atl, pause, null, Monday);
}
