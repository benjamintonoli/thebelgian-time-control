using TheBelgian.TimeControl.Core.Payroll.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyCityAllowanceRowCalculator
{
    public static int CalculateRowUnits(
        string? normalizedPostcode,
        bool isDailyMinVan,
        bool isDailyMaxVan,
        CityAllowanceConfiguration configuration)
    {
        if (!configuration.IsQualifyingPostcode(normalizedPostcode))
        {
            return 0;
        }

        var units = 0;
        if (isDailyMinVan)
        {
            units++;
        }

        if (isDailyMaxVan)
        {
            units++;
        }

        return units;
    }
}
