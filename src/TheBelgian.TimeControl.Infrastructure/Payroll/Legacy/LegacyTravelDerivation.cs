using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

/// <summary>
/// Reproduces Power BI legacy min/max VAN travel semantics for one resource/day.
/// </summary>
public static class LegacyTravelDerivation
{
    private const int TravelHfdTaakId = 5;
    private const decimal Extra15Hours = 0.25m;

    public static LegacyTravelDayResult CalculateDay(
        string resourceId,
        DateOnly date,
        IReadOnlyList<LegacyTravelPerformanceInput> rows)
    {
        var rowResults = CalculateRows(rows);
        return new LegacyTravelDayResult(
            resourceId,
            date,
            rowResults.Max(result => result.TravelBeginHours),
            rowResults.Max(result => result.TravelEndHours),
            rowResults.Sum(result => result.Extra15Hours));
    }

    public static IReadOnlyList<LegacyTravelRowResult> CalculateRows(
        IReadOnlyList<LegacyTravelPerformanceInput> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var vanTimes = rows
            .Where(row => row.VanTimeOfDay is not null)
            .Select(row => row.VanTimeOfDay!.Value)
            .ToList();

        if (vanTimes.Count == 0)
        {
            return rows.Select(row => EmptyRow(row.PerformanceId)).ToList();
        }

        var minVan = vanTimes.Min();
        var maxVan = vanTimes.Max();

        var results = new List<LegacyTravelRowResult>(rows.Count);
        foreach (var row in rows)
        {
            var isMin = row.VanTimeOfDay == minVan;
            var isMax = row.VanTimeOfDay == maxVan;
            var isTravel = row.HfdTaakId == TravelHfdTaakId;

            var payableHours = PayableHoursForTravel(row);
            var travelBegin = isTravel && isMin ? payableHours : 0m;
            var travelEnd = isTravel && isMax ? payableHours : 0m;
            var extra15 = isTravel && (isMin || isMax) ? Extra15Hours : 0m;

            results.Add(new LegacyTravelRowResult(
                row.PerformanceId,
                isMin,
                isMax,
                travelBegin,
                travelEnd,
                extra15));
        }

        return results;
    }

    private static LegacyTravelRowResult EmptyRow(long performanceId) =>
        new(performanceId, false, false, 0m, 0m, 0m);

    private static decimal PayableHoursForTravel(LegacyTravelPerformanceInput row)
    {
        if (row.GrossHoursRaw is not null && row.GrossHoursRaw > row.AtlHoursRaw)
        {
            return row.GrossHoursRaw.Value;
        }

        return row.AtlHoursRaw;
    }
}
