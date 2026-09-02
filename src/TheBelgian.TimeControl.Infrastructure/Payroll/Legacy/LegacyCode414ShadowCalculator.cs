using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

/// <summary>
/// Legacy bedrag looncode 414 = city allowance + KM-bedrag CJ.
/// Completeness: both components must be Calculated; otherwise null.
/// </summary>
public static class LegacyCode414ShadowCalculator
{
    public static (PayrollMonthCalculationStatus Status, decimal? Amount) Calculate(
        PayrollMonthCalculationStatus cityStatus,
        decimal? cityAllowanceAmount,
        PayrollMonthCalculationStatus kmStatus,
        decimal? kmAmount)
    {
        if (cityStatus != PayrollMonthCalculationStatus.Calculated
            || kmStatus != PayrollMonthCalculationStatus.Calculated
            || cityAllowanceAmount is null
            || kmAmount is null)
        {
            return (PayrollMonthCalculationStatus.NotCalculated, null);
        }

        return (
            PayrollMonthCalculationStatus.Calculated,
            cityAllowanceAmount.Value + kmAmount.Value);
    }
}
