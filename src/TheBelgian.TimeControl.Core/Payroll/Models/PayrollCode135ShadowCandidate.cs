namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Shadow calculation candidate for looncode 135. Export disposition is intentionally unknown.
/// </summary>
public sealed record PayrollCode135ShadowCandidate(
    int Percentage,
    decimal CalculatedUnits,
    PayrollMonthCalculationStatus CalculationStatus = PayrollMonthCalculationStatus.Calculated);
