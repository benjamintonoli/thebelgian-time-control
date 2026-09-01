using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

/// <summary>
/// Legacy Power BI parity theoretical hours from a global weekday calendar.
/// Verified for legacy parity: July 2026 (179h, no public-holiday subtraction).
/// This is NOT the future contractual-hours provider.
/// </summary>
public static class LegacyGlobalWeekdayTheoreticalHoursProvider
{
    public static decimal GetMonthlyHours(PayrollPeriodSnapshot period) =>
        SumWeekdayHours(period.PeriodStart, period.EffectiveEnd);

    public static decimal GetDayHours(DateOnly date) =>
        date.DayOfWeek switch
        {
            DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday => 8m,
            DayOfWeek.Friday => 7m,
            _ => 0m,
        };

    private static decimal SumWeekdayHours(DateOnly from, DateOnly through)
    {
        if (through < from)
        {
            return 0m;
        }

        decimal total = 0m;
        for (var date = from; date <= through; date = date.AddDays(1))
        {
            total += GetDayHours(date);
        }

        return total;
    }
}
