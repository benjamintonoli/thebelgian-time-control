using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public static class HistoricalCityParityCalculator
{
    private static readonly CityAllowanceConfiguration Configuration =
        CityAllowanceConfiguration.July2026Legacy;

    public static int CalculateMonthlyUnits(
        IReadOnlyList<PowerBiDetailRow> detailRows,
        string resourceId)
    {
        var resourceRows = detailRows
            .Where(row => row.ResourceId == resourceId)
            .ToList();

        return resourceRows
            .GroupBy(row => row.Date)
            .Sum(group => CalculateDayUnits(group.ToList()));
    }

    public static int CalculateDayUnits(IReadOnlyList<PowerBiDetailRow> dayRows)
    {
        if (dayRows.Count == 0)
        {
            return 0;
        }

        var travelInputs = dayRows
            .Select(HistoricalLegacyParityAdapter.ToTravelInput)
            .ToList();
        var travelById = LegacyTravelDerivation.CalculateRows(travelInputs)
            .ToDictionary(result => result.PerformanceId);

        var total = 0;
        foreach (var row in dayRows)
        {
            var performanceId = HistoricalLegacyParityAdapter.ParsePerformanceId(row.PerformanceId);
            travelById.TryGetValue(performanceId, out var travel);
            var normalizedPostcode = PostcodeNormalizer.TryNormalize(row.Postcode);
            total += LegacyCityAllowanceRowCalculator.CalculateRowUnits(
                normalizedPostcode,
                travel?.IsDailyMinVan ?? false,
                travel?.IsDailyMaxVan ?? false,
                Configuration);
        }

        return total;
    }

    public static int CalculateRowUnits(PowerBiDetailRow row, LegacyTravelRowResult travel) =>
        LegacyCityAllowanceRowCalculator.CalculateRowUnits(
            PostcodeNormalizer.TryNormalize(row.Postcode),
            travel.IsDailyMinVan,
            travel.IsDailyMaxVan,
            Configuration);
}
