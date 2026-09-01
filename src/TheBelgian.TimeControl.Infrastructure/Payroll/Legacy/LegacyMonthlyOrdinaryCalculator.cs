using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyMonthlyOrdinaryCalculator
{
    public static LegacyMonthlyOrdinaryResult Calculate(
        PayrollPeriodSnapshot period,
        string resourceId,
        IReadOnlyList<LegacyDailyPayrollResult> dailyResults,
        decimal theoreticalHours)
    {
        var periodDays = dailyResults
            .Where(day => day.ResourceId == resourceId)
            .Where(day => day.Date >= period.PeriodStart && day.Date <= period.PeriodEnd)
            .GroupBy(day => day.Date)
            .Select(group => group.Single())
            .ToList();

        var actualHours = periodDays.Sum(day => day.FinalDailyTotalHours);
        var differenceHours = actualHours - theoreticalHours;

        return new LegacyMonthlyOrdinaryResult(
            resourceId,
            theoreticalHours,
            actualHours,
            differenceHours,
            periodDays.Count);
    }
}

public sealed record LegacyMonthlyOrdinaryResult(
    string ResourceId,
    decimal TheoreticalHours,
    decimal ActualOrdinaryHours,
    decimal DifferenceHours,
    int ResourceDays);
