using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyCityAllowancePerformanceCalculator
{
    public static int CalculateMonthlyUnits(
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        CityAllowanceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(performances);
        ArgumentNullException.ThrowIfNull(configuration);

        return performances
            .GroupBy(row => row.Date)
            .Sum(group => CalculateDayUnits(group.ToList(), configuration));
    }

    private static int CalculateDayUnits(
        List<NormalizedPerformanceEntry> dayRows,
        CityAllowanceConfiguration configuration)
    {
        if (dayRows.Count == 0)
        {
            return 0;
        }

        var travelInputs = dayRows
            .Select(row => new LegacyTravelPerformanceInput(
                row.SourceEntryId,
                row.HfdTaakId,
                row.Start?.TimeOfDay,
                row.AtlHoursRaw))
            .ToList();
        var travelById = LegacyTravelDerivation.CalculateRows(travelInputs)
            .ToDictionary(result => result.PerformanceId);

        var total = 0;
        foreach (var row in dayRows)
        {
            travelById.TryGetValue(row.SourceEntryId, out var travel);
            total += LegacyCityAllowanceRowCalculator.CalculateRowUnits(
                PostcodeNormalizer.TryNormalize(row.Postcode),
                travel?.IsDailyMinVan ?? false,
                travel?.IsDailyMaxVan ?? false,
                configuration);
        }

        return total;
    }
}
