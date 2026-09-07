using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollReviewQueueService
{
    Task<PayrollReviewQueuePage> GetQueueAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        CancellationToken cancellationToken);

    Task<PayrollAdminQueuePage> GetAdminQueueAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        CancellationToken cancellationToken);

    Task SetCaseStatusAsync(
        int year,
        int month,
        string caseKey,
        PayrollFindingStatus status,
        string? comment,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates only the explicitly provided review-case keys (never an implicit full-month select).
    /// Allowed statuses: NeedsFollowUp, Reviewed, Dismissed.
    /// </summary>
    Task<PayrollReviewBulkUpdateResult> BulkSetCaseStatusAsync(
        int year,
        int month,
        IReadOnlyList<string> caseKeys,
        PayrollFindingStatus status,
        string comment,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies a guided admin decision to all findings under an AdminCase.
    /// </summary>
    Task<PayrollAdminDecisionResult> SetAdminDecisionAsync(
        int year,
        int month,
        string adminCaseKey,
        string decisionCode,
        string? comment,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Bulk disposition for explicitly selected AdminCase keys (low-risk categories only).
    /// </summary>
    Task<PayrollReviewBulkUpdateResult> BulkSetAdminDecisionAsync(
        int year,
        int month,
        IReadOnlyList<string> adminCaseKeys,
        string decisionCode,
        string comment,
        string actor,
        CancellationToken cancellationToken);
}
