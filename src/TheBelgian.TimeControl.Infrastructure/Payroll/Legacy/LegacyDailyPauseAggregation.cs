using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyDailyPauseAggregation
{
    private static readonly HashSet<int> ExcludedHfdTaakIds = [10, 18, 23];

    public static decimal SumRegisteredPauseHours(IEnumerable<LegacyDailyPerformanceInput> rows) =>
        rows
            .Where(row => row.HfdTaakId is not null && !ExcludedHfdTaakIds.Contains(row.HfdTaakId.Value))
            .Sum(row => row.PauseHoursRaw);
}
