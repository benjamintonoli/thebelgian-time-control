using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyPauseCorrection
{
    private const decimal RequiredPauseHours = 0.5m;
    private static readonly HashSet<int> OrdinaryWorkExcludedHfdTaakIds = [5, 10, 18, 23];

    public static LegacyDailyPauseResult Calculate(
        string resourceId,
        DateOnly date,
        IReadOnlyList<LegacyDailyPerformanceInput> rows)
    {
        var registeredPause = LegacyDailyPauseAggregation.SumRegisteredPauseHours(rows);
        var hasOrdinaryWork = rows.Any(row =>
            row.HfdTaakId is not null &&
            !OrdinaryWorkExcludedHfdTaakIds.Contains(row.HfdTaakId.Value) &&
            row.AtlHoursRaw > 0m);

        var pauseCorrection = CalculatePauseCorrectionHours(date, registeredPause, hasOrdinaryWork);
        return new LegacyDailyPauseResult(
            resourceId,
            date,
            registeredPause,
            hasOrdinaryWork,
            pauseCorrection);
    }

    public static decimal CalculatePauseCorrectionHours(
        DateOnly date,
        decimal registeredPauseHours,
        bool hasOrdinaryWork)
    {
        if (IsWeekend(date) || !hasOrdinaryWork)
        {
            return 0m;
        }

        if (registeredPauseHours >= RequiredPauseHours)
        {
            return 0m;
        }

        return registeredPauseHours - RequiredPauseHours;
    }

    private static bool IsWeekend(DateOnly date) =>
        date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}
