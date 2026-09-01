using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyGlobalWeekdayTheoreticalHoursProviderTests
{
    [Fact]
    public void July2026_ClosedMonth_Returns179Hours()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 7, 31));
        Assert.Equal(179m, LegacyGlobalWeekdayTheoreticalHoursProvider.GetMonthlyHours(period));
    }

    [Fact]
    public void July2026_EvaluatedThroughJuly15_ReturnsExactPartialWeekdaySum()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 7, 15));
        var expected = 0m;
        for (var date = period.PeriodStart; date <= period.EvaluationDate; date = date.AddDays(1))
        {
            expected += LegacyGlobalWeekdayTheoreticalHoursProvider.GetDayHours(date);
        }

        Assert.Equal(expected, LegacyGlobalWeekdayTheoreticalHoursProvider.GetMonthlyHours(period));
    }

    [Fact]
    public void July21_CountsAsEightHourWeekday()
    {
        var nationalDay = new DateOnly(2026, 7, 21);
        Assert.Equal(8m, LegacyGlobalWeekdayTheoreticalHoursProvider.GetDayHours(nationalDay));
    }

    [Fact]
    public void WeekendDays_ReturnZero()
    {
        Assert.Equal(0m, LegacyGlobalWeekdayTheoreticalHoursProvider.GetDayHours(new DateOnly(2026, 7, 11)));
        Assert.Equal(0m, LegacyGlobalWeekdayTheoreticalHoursProvider.GetDayHours(new DateOnly(2026, 7, 12)));
    }

    [Fact]
    public void Friday_ReturnsSevenHours()
    {
        Assert.Equal(7m, LegacyGlobalWeekdayTheoreticalHoursProvider.GetDayHours(new DateOnly(2026, 7, 3)));
    }

    [Fact]
    public void EvaluationDateAfterMonthEnd_UsesFullMonth()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 8, 15));
        Assert.Equal(179m, LegacyGlobalWeekdayTheoreticalHoursProvider.GetMonthlyHours(period));
    }

    [Fact]
    public void DoesNotUseResourceWorkSchedule()
    {
        var schedule = ResourceWorkSchedule.StandardFullTime("ft", new DateOnly(2026, 1, 1));
        var period = PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 7, 31));
        Assert.NotEqual(
            (decimal)(schedule.ContractualDuration(DayOfWeek.Monday).TotalHours * 23d),
            LegacyGlobalWeekdayTheoreticalHoursProvider.GetMonthlyHours(period));
    }
}
