namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Monthly shadow payroll rollup. Monetary allowance fields remain null until Increment 6C.
/// </summary>
public sealed class PayrollMonthShadowResult
{
    public required string ResourceId { get; init; }
    public required int Year { get; init; }
    public required int Month { get; init; }

    public DateOnly? EvaluationDate { get; init; }

    public decimal? LegacyTheoreticalHours { get; init; }
    public decimal? LegacyActualOrdinaryHours { get; init; }
    public decimal? LegacyDifferenceHours { get; init; }

    public decimal? StandbyExactHours { get; init; }

    public PayrollCode135ShadowCandidate? Code135At150 { get; init; }
    public PayrollCode135ShadowCandidate? Code135At200 { get; init; }

    public PayrollMonthCalculationStatus OrdinaryStatus { get; init; } = PayrollMonthCalculationStatus.NotCalculated;
    public PayrollMonthCalculationStatus StandbyStatus { get; init; } = PayrollMonthCalculationStatus.NotCalculated;
    public PayrollMonthCalculationStatus KmStatus { get; init; } = PayrollMonthCalculationStatus.NotCalculated;
    public PayrollMonthCalculationStatus CityStatus { get; init; } = PayrollMonthCalculationStatus.NotCalculated;
    public PayrollMonthCalculationStatus Code414Status { get; init; } = PayrollMonthCalculationStatus.NotCalculated;

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
