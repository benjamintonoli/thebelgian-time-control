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

    Task SetCaseStatusAsync(
        int year,
        int month,
        string caseKey,
        PayrollFindingStatus status,
        string? comment,
        string actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates only the explicitly provided case keys (never an implicit full-month select).
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
}
