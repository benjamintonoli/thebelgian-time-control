using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyExtra75Calculator
{
    private const decimal Threshold75 = 75m;
    private const decimal Threshold150 = 150m;

    public static IReadOnlyList<LegacyExtra75RowResult> CalculateRows(
        IReadOnlyList<LegacyDailyPerformanceInput> rows,
        IReadOnlyDictionary<long, LegacyTravelRowResult> travelByPerformanceId)
    {
        return rows
            .Select(row =>
            {
                travelByPerformanceId.TryGetValue(row.PerformanceId, out var travel);
                var extra75 = CalculateRowKm(
                    row.Km,
                    travel?.IsDailyMinVan ?? false,
                    travel?.IsDailyMaxVan ?? false);
                return new LegacyExtra75RowResult(row.PerformanceId, extra75);
            })
            .ToList();
    }

    public static decimal CalculateRowKm(decimal? km, bool isDailyMin, bool isDailyMax)
    {
        if (km is null or <= 0m)
        {
            return 0m;
        }

        var kmValue = km.Value;
        if (kmValue > Threshold150 && isDailyMax && isDailyMin)
        {
            return kmValue - Threshold150;
        }

        if (kmValue < Threshold150 && isDailyMax && isDailyMin)
        {
            return 0m;
        }

        if (kmValue > Threshold75 && (isDailyMax || isDailyMin))
        {
            return kmValue - Threshold75;
        }

        return 0m;
    }
}
