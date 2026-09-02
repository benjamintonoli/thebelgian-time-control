using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

/// <summary>
/// Reproduces Power BI Aantal KM CJ / extra 75km CJ YTD / KM-bedrag CJ inside
/// existing payroll/report filter context (PayrollPeriodSnapshot), intersected with
/// CJ_FirstDay..EvaluationDate. Does not remove period filters (no ALL/REMOVEFILTERS).
/// Extra75 has no HFDTAAK 5 filter; EligibleKm excludes HFDTAAK 5 only.
/// </summary>
public static class LegacyKmAllowanceCalculator
{
    private const int TravelHfdTaakId = 5;

    public static LegacyKmAllowanceResult Calculate(
        IReadOnlyList<LegacyDailyPerformanceInput> periodRows,
        PayrollPeriodSnapshot period,
        KmAllowanceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(periodRows);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(configuration);

        var window = LegacyCurrentYearWindow.FromPeriod(period);
        var range = LegacyKmEffectiveDateRange.Intersect(period, window);
        if (range is null)
        {
            return new LegacyKmAllowanceResult(0m, 0m, 0m, 0m, configuration.RatePerKm, 0m);
        }

        var inRange = periodRows
            .Where(row => range.Contains(row.Date))
            .ToList();

        var eligibleKm = inRange
            .Where(row => row.HfdTaakId != TravelHfdTaakId)
            .Sum(row => row.Km ?? 0m);

        var extra75RawKm = SumExtra75RawKm(inRange);
        var extra75YtdHours = extra75RawKm / 60m;
        var netKmLegacyQuantity = eligibleKm - extra75YtdHours;
        var kmAmount = configuration.RatePerKm * netKmLegacyQuantity;

        return new LegacyKmAllowanceResult(
            eligibleKm,
            extra75RawKm,
            extra75YtdHours,
            netKmLegacyQuantity,
            configuration.RatePerKm,
            kmAmount);
    }

    public static decimal SumExtra75RawKm(IReadOnlyList<LegacyDailyPerformanceInput> rowsInRange)
    {
        decimal total = 0m;
        foreach (var dayGroup in rowsInRange.GroupBy(row => row.Date))
        {
            var dayRows = dayGroup.ToList();
            var travelInputs = dayRows
                .Select(row => new LegacyTravelPerformanceInput(
                    row.PerformanceId,
                    row.HfdTaakId,
                    row.Start?.TimeOfDay,
                    row.AtlHoursRaw))
                .ToList();
            var travelById = LegacyTravelDerivation.CalculateRows(travelInputs)
                .ToDictionary(result => result.PerformanceId);
            // No HFDTAAK 5 exclusion — matches Power BI extra 75km CJ YTD measure.
            total += LegacyExtra75Calculator.CalculateRows(dayRows, travelById)
                .Sum(result => result.Extra75Km);
        }

        return total;
    }
}
