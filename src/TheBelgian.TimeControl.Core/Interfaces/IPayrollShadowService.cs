using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollShadowService
{
    Task<IReadOnlyList<PayrollShadowMonthSummary>> ListMonthsAsync(CancellationToken cancellationToken);

    Task<PayrollShadowMonthDetail?> GetMonthDetailAsync(
        int year,
        int month,
        PayrollShadowEmployeeFilter filter,
        CancellationToken cancellationToken);

    Task<PayrollShadowEmployeeDetail?> GetEmployeeDetailAsync(
        int year,
        int month,
        string resourceId,
        CancellationToken cancellationToken);

    Task<PayrollShadowMonth> CreateSnapshotAsync(
        int year,
        int month,
        DateOnly evaluationDate,
        string actor,
        CancellationToken cancellationToken);

    Task<PayrollShadowMonth> StartReviewAsync(
        int year,
        int month,
        string actor,
        CancellationToken cancellationToken);

    Task<PayrollShadowMonth> FinalizeAsync(
        int year,
        int month,
        string actor,
        CancellationToken cancellationToken);

    Task SetEligibilityAsync(
        SetPayrollEligibilityRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task SetReviewStatusAsync(
        SetPayrollReviewStatusRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task ResetEligibilityAsync(
        SetPayrollEligibilityResetRequest request,
        string actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<PayrollShadowReviewAudit>> GetAuditTrailAsync(
        int year,
        int month,
        string? resourceId,
        CancellationToken cancellationToken);
}

public sealed record SetPayrollEligibilityResetRequest(
    string ResourceId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string ReasonCode,
    string? Comment);

public sealed record PayrollShadowMonthSummary(
    int Year,
    int Month,
    PayrollShadowMonthStatus Status,
    DateTimeOffset CreatedAtUtc,
    DateOnly EvaluationDate,
    string CalculationVersion,
    int TotalCandidates,
    int Included,
    int Excluded,
    int NeedsDecision,
    int PendingReview,
    int NeedsFollowUp,
    int Accepted);

public sealed record PayrollShadowMonthDetail(
    PayrollShadowMonth Month,
    PayrollShadowMonthSummary Summary,
    IReadOnlyList<PayrollShadowEmployeeRow> Employees);

public sealed record PayrollShadowEmployeeRow(
    string ResourceId,
    string DisplayName,
    PayrollEligibilityStatus EligibilityStatus,
    PayrollEligibilityStatus? SuggestedEligibility,
    PayrollEmployeeReviewStatus ReviewStatus,
    decimal? LegacyTheoreticalHours,
    decimal? LegacyActualOrdinaryHours,
    decimal? LegacyDifferenceHours,
    decimal? StandbyRoundedHours,
    decimal? CityAllowanceAmount,
    decimal? KmAmount,
    decimal? Code414Amount,
    AcertaIdentityStatus AcertaIdentityStatus);

public sealed record PayrollShadowEmployeeDetail(
    PayrollShadowMonth Month,
    PayrollShadowEmployeeResult Employee,
    IReadOnlyList<PayrollEmployeeConfigurationRecord> EligibilityConfigurations,
    IReadOnlyList<PayrollShadowReviewAudit> AuditTrail);

public sealed record PayrollShadowEmployeeFilter(
    PayrollEligibilityStatus? Eligibility = null,
    PayrollEmployeeReviewStatus? Review = null,
    bool NeedsDecisionOnly = false,
    bool NeedsFollowUpOnly = false,
    bool MissingAcertaIdentityOnly = false,
    bool NegativeDifferenceOnly = false,
    bool NonzeroStandbyOnly = false);

public sealed record SetPayrollEligibilityRequest(
    string ResourceId,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    PayrollEligibilityStatus EligibilityStatus,
    string ReasonCode,
    string? Comment);

public sealed record SetPayrollReviewStatusRequest(
    int Year,
    int Month,
    string ResourceId,
    PayrollEmployeeReviewStatus ReviewStatus,
    string? Comment);
