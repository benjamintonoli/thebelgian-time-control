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
}
