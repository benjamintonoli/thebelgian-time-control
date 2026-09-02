using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

/// <summary>
/// Reproduces Power BI Aantal KM CJ / extra 75km CJ YTD / KM-bedrag CJ.
/// Extra75 YTD has no HFDTAAK 5 filter; EligibleKm excludes HFDTAAK 5 only.
/// </summary>
public static class LegacyKmAllowanceCalculator
{
    private const int TravelHfdTaakId = 5;

    public static LegacyKmAllowanceResult Calculate(
        IReadOnlyList<LegacyDailyPerformanceInput> ytdRows,
        LegacyCurrentYearWindow window,
        KmAllowanceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(ytdRows);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(configuration);

        var inWindow = ytdRows
            .Where(row => window.IsInCalculationWindow(row.Date))
            .ToList();

        var eligibleKm = inWindow
            .Where(row => row.HfdTaakId != TravelHfdTaakId)
            .Sum(row => row.Km ?? 0m);

        var extra75RawKm = SumExtra75RawKm(inWindow);
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

    public static decimal SumExtra75RawKm(IReadOnlyList<LegacyDailyPerformanceInput> rowsInWindow)
    {
        decimal total = 0m;
        foreach (var dayGroup in rowsInWindow.GroupBy(row => row.Date))
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
