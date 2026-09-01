using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

/// <summary>
/// Resolves historical KM for Extra75 calculation when the CSV export omits KM.
/// Current Plenion KM reflects post-snapshot drift and must not override zero exports.
/// </summary>
public static class HistoricalKmResolver
{
    public static decimal ResolveHistoricalKm(
        decimal? exportedExtra75Km,
        decimal? plenionKm,
        bool isDailyMinVan,
        bool isDailyMaxVan)
    {
        var exported = exportedExtra75Km ?? 0m;
        if (exported == 0m)
        {
            return 0m;
        }

        return InferKmFromExportedExtra75(exported, isDailyMinVan, isDailyMaxVan);
    }

    public static decimal InferKmFromExportedExtra75(
        decimal exportedExtra75,
        bool isDailyMinVan,
        bool isDailyMaxVan)
    {
        if (exportedExtra75 == 0m)
        {
            return 0m;
        }

        if (isDailyMinVan && isDailyMaxVan)
        {
            var kmFrom150Branch = exportedExtra75 + 150m;
            if (kmFrom150Branch > 150m && exportedExtra75 == kmFrom150Branch - 150m)
            {
                return kmFrom150Branch;
            }

            return exportedExtra75 + 75m;
        }

        if (isDailyMinVan || isDailyMaxVan)
        {
            return exportedExtra75 + 75m;
        }

        return 0m;
    }
}
