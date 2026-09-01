namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Shell for future monthly shadow payroll rollup. No formulas in increment 1.
/// </summary>
public sealed class PayrollMonthShadowResult
{
    public required string ResourceId { get; init; }
    public required int Year { get; init; }
    public required int Month { get; init; }

    public decimal? TheoreticalMinutes { get; init; }
    public decimal? PayableOrdinaryMinutes { get; init; }
    public decimal? DifferenceMinutes { get; init; }
    public decimal? Overtime150Units { get; init; }
    public decimal? StandbyExactMinutes { get; init; }
    public decimal? StandbyRoundedHours { get; init; }
    public decimal? Standby200Units { get; init; }
    public decimal? EligibleKm { get; init; }
    public decimal? KmAmount { get; init; }
    public int? CityTripUnits { get; init; }
    public decimal? CityAllowanceAmount { get; init; }
    public decimal? Code414Amount { get; init; }
}
