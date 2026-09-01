using TheBelgian.TimeControl.Core.Payroll.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public sealed record LegacyCityAllowanceMonthlyResult(
    int CityTripUnits,
    decimal CityAllowanceAmount);

public static class LegacyCityAllowanceMonthlyCalculator
{
    public static LegacyCityAllowanceMonthlyResult Calculate(
        int totalRowUnits,
        CityAllowanceConfiguration configuration) =>
        new(
            totalRowUnits,
            totalRowUnits * configuration.TripAmount);
}
