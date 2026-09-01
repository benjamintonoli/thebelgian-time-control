using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyDailyPayrollCalculatorTests
{
    private static readonly DateOnly Monday = new(2026, 7, 6);
    private static readonly DateOnly Friday = new(2026, 7, 10);
    private static readonly DateOnly Saturday = new(2026, 7, 11);

    [Fact]
    public void OrdinaryWorkBelowNorm_NoAbsence_ReturnsPayableWork()
    {
        var rows = new[] { Daily(1, 9, 6m) };
        var result = Calculate(rows, Monday, pauseCorrection: 0m);
        Assert.Equal(6m, result.FinalDailyTotalHours);
        Assert.False(result.HasAbsence);
    }

    [Fact]
    public void OrdinaryWorkAboveNorm_NoAbsence_IsNotCapped()
    {
        var rows = new[] { Daily(1, 9, 9.5m) };
        var result = Calculate(rows, Monday, pauseCorrection: 0m);
        Assert.Equal(9.5m, result.FinalDailyTotalHours);
    }

    [Fact]
    public void MondayAbsenceOnly_CapsAtEight()
    {
        var rows = new[] { Daily(1, 10, 12m) };
        var result = Calculate(rows, Monday, pauseCorrection: 0m);
        Assert.Equal(8m, result.FinalDailyTotalHours);
        Assert.True(result.HasAbsence);
    }

    [Fact]
    public void FridayAbsenceOnly_CapsAtSeven()
    {
        var rows = new[] { Daily(1, 10, 8m) };
        var result = Calculate(rows, Friday, pauseCorrection: 0m);
        Assert.Equal(7m, result.FinalDailyTotalHours);
    }

    [Fact]
    public void WeekendAbsence_ReturnsZero()
    {
        var rows = new[] { Daily(1, 10, 8m) };
        var result = Calculate(rows, Saturday, pauseCorrection: 0m);
        Assert.Equal(0m, result.FinalDailyTotalHours);
    }

    [Fact]
    public void WorkPlusAbsence_CapsAtTheoretical()
    {
        var rows = new[]
        {
            Daily(1, 9, 4m),
            Daily(2, 10, 4m),
        };
        var result = Calculate(rows, Monday, pauseCorrection: 0m);
        Assert.Equal(8m, result.FinalDailyTotalHours);
    }

    [Fact]
    public void WorkPlusAbsence_WhenWorkAlreadyExceedsNorm_StillCapsAtEight()
    {
        var rows = new[]
        {
            Daily(1, 9, 7m),
            Daily(2, 10, 4m),
        };
        var result = Calculate(rows, Monday, pauseCorrection: 0m);
        Assert.Equal(8m, result.FinalDailyTotalHours);
    }

    [Fact]
    public void StandbyExcludedFromWorkRows()
    {
        var rows = new[]
        {
            Daily(1, 9, 8m),
            Daily(2, 23, 5m),
        };
        var result = Calculate(rows, Monday, pauseCorrection: 0m);
        Assert.Equal(8m, result.RegisteredWorkHours);
        Assert.Equal(8m, result.FinalDailyTotalHours);
    }

    [Fact]
    public void TravelIncludedInWorkRows()
    {
        var rows = new[]
        {
            Daily(1, 5, 1m),
            Daily(2, 9, 8m),
        };
        var travel = new Dictionary<long, LegacyTravelRowResult>
        {
            [1] = new(1, true, false, 1m, 0m, 0.25m),
            [2] = new(2, false, false, 0m, 0m, 0m),
        };
        var result = Calculate(rows, Monday, pauseCorrection: 0m, travelOverrides: travel);
        Assert.Equal(9m, result.RegisteredWorkHours);
        Assert.Equal(8.25m, result.FinalDailyTotalHours);
    }

    [Fact]
    public void NegativePayableWork_ClampedToZero()
    {
        var rows = new[] { Daily(1, 9, 1m) };
        var travel = new Dictionary<long, LegacyTravelRowResult>
        {
            [1] = new(1, true, true, 2m, 2m, 0.25m),
        };
        var result = Calculate(rows, Monday, pauseCorrection: 0m, travelOverrides: travel);
        Assert.Equal(0m, result.PayableWorkHours);
    }

    private static LegacyDailyPayrollResult Calculate(
        IReadOnlyList<LegacyDailyPerformanceInput> rows,
        DateOnly date,
        decimal pauseCorrection,
        IReadOnlyDictionary<long, LegacyTravelRowResult>? travelOverrides = null)
    {
        var overlapById = rows.ToDictionary(
            row => row.PerformanceId,
            row => new LegacyOverlapResult(row.PerformanceId, null, 0m, row.AtlHoursRaw, 0m));
        var travelById = travelOverrides ?? rows.ToDictionary(
            row => row.PerformanceId,
            row => new LegacyTravelRowResult(row.PerformanceId, false, false, 0m, 0m, 0m));
        var extra75ById = rows.ToDictionary(
            row => row.PerformanceId,
            row => new LegacyExtra75RowResult(row.PerformanceId, 0m));
        var pause = new LegacyDailyPauseResult("495", date, 0m, true, pauseCorrection);
        return LegacyDailyPayrollCalculator.Calculate(
            "495",
            date,
            rows,
            overlapById,
            travelById,
            extra75ById,
            pause);
    }

    private static LegacyDailyPerformanceInput Daily(long id, int hfd, decimal atl) =>
        new(id, id, hfd, null, null, atl, 0m, null, Monday);
}
