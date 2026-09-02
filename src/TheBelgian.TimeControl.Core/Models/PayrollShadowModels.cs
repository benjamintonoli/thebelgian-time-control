using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Models;

// PayrollMonthCalculationStatus lives in Core.Payroll.Models.

public sealed class PayrollEmployeeConfigurationRecord
{
    public int Id { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public PayrollEligibilityStatus EligibilityStatus { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? Comment { get; set; }
    public PayrollEligibilityDecisionSource DecisionSource { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;

    public PayrollEmployeeConfiguration ToDomain() =>
        new(
            ResourceId,
            ValidFrom,
            ValidTo,
            EligibilityStatus,
            ReasonCode,
            Comment,
            DecisionSource);

    public bool IsActiveFor(DateOnly periodStart, DateOnly periodEnd) =>
        ToDomain().IsActiveFor(periodStart, periodEnd);
}

public sealed class PayrollShadowMonth
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public DateOnly EvaluationDate { get; set; }
    public PayrollShadowMonthStatus Status { get; set; }
    public string CalculationVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset? LastReviewedAtUtc { get; set; }
    public string? LastReviewedBy { get; set; }
    public DateTimeOffset? FinalizedAtUtc { get; set; }
    public string? FinalizedBy { get; set; }
    public string ConfigurationSnapshotJson { get; set; } = string.Empty;
}

public sealed class PayrollShadowEmployeeResult
{
    public int Id { get; set; }
    public int ShadowMonthId { get; set; }
    public string ResourceId { get; set; } = string.Empty;
    public string DisplayNameSnapshot { get; set; } = string.Empty;
    public string ResourceCodeSnapshot { get; set; } = string.Empty;
    public string? EmailSnapshot { get; set; }
    public PayrollEligibilityStatus EligibilityStatus { get; set; }
    public string? EligibilityReason { get; set; }
    public PayrollEligibilityStatus? SuggestedEligibility { get; set; }
    public string? SuggestedReason { get; set; }
    public decimal? LegacyTheoreticalHours { get; set; }
    public decimal? LegacyActualOrdinaryHours { get; set; }
    public decimal? LegacyDifferenceHours { get; set; }
    public decimal? StandbyExactHours { get; set; }
    public decimal? StandbyRoundedHours { get; set; }
    public decimal? Code135At150Units { get; set; }
    public decimal? Code135At200Units { get; set; }
    public int? CityTripUnits { get; set; }
    public decimal? CityAllowanceAmount { get; set; }
    public decimal? EligibleKm { get; set; }
    public decimal? Extra75LegacyValue { get; set; }
    public decimal? KmRate { get; set; }
    public decimal? KmAmount { get; set; }
    public decimal? Code414Amount { get; set; }
    public AcertaIdentityStatus AcertaIdentityStatus { get; set; }
    public PayrollMonthCalculationStatus OrdinaryStatus { get; set; }
    public PayrollMonthCalculationStatus StandbyStatus { get; set; }
    public PayrollMonthCalculationStatus CityStatus { get; set; }
    public PayrollMonthCalculationStatus KmStatus { get; set; }
    public PayrollMonthCalculationStatus Code414Status { get; set; }
    public PayrollEmployeeReviewStatus ReviewStatus { get; set; }
    public string? ReviewComment { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? ReviewedBy { get; set; }
}

public sealed class PayrollShadowReviewAudit
{
    public int Id { get; set; }
    public int ShadowMonthId { get; set; }
    public string? ResourceId { get; set; }
    public PayrollShadowAuditAction Action { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
    public string? ReasonCode { get; set; }
    public string? Comment { get; set; }
}
