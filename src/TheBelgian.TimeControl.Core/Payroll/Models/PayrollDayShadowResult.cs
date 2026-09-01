namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Shell for future explainable daily payroll components. No formulas in increment 1.
/// </summary>
public sealed class PayrollDayShadowResult
{
    public required string ResourceId { get; init; }
    public required DateOnly Date { get; init; }

    public decimal? TheoreticalMinutes { get; init; }
    public decimal? RawOrdinaryAtlMinutes { get; init; }
    public decimal? FirstTravelDeductionMinutes { get; init; }
    public decimal? LastTravelDeductionMinutes { get; init; }
    public decimal? PauseCorrectionMinutes { get; init; }
    public decimal? OverlapCorrectionMinutes { get; init; }
    public decimal? Extra15Minutes { get; init; }
    public decimal? Extra75AsMinutes { get; init; }
    public decimal? LegacyPayableOrdinaryMinutes { get; init; }
    public decimal? StandbyRawMinutes { get; init; }
    public decimal? LegacyStandbyTotalMinutes { get; init; }
    public decimal? EligibleKm { get; init; }
    public int? CityTripUnits { get; init; }
}
