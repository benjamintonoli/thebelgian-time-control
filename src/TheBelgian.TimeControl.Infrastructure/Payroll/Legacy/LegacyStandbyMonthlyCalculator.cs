using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyStandbyMonthlyCalculator
{
    public static LegacyStandbyMonthlyResult Calculate(
        PayrollPeriodSnapshot period,
        string resourceId,
        IReadOnlyDictionary<DateOnly, decimal> dailyStandbyTotals)
    {
        var periodTotals = dailyStandbyTotals
            .Where(pair => pair.Key >= period.PeriodStart && pair.Key <= period.PeriodEnd)
            .Select(pair => pair.Value)
            .Where(total => total > 0m)
            .ToList();

        var exactHours = periodTotals.Sum();
        var roundedHours = exactHours <= 0m ? 0m : Math.Ceiling(exactHours - 0.0000001m);

        return new LegacyStandbyMonthlyResult(resourceId, exactHours, roundedHours, periodTotals.Count);
    }
}

public sealed record LegacyStandbyMonthlyResult(
    string ResourceId,
    decimal ExactHours,
    decimal RoundedHours,
    int StandbyDays);
