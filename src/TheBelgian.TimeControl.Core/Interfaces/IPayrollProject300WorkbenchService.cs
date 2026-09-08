using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollProject300WorkbenchService
{
    Task<PayrollProject300WorkbenchPage> GetWorkbenchAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    Task<PayrollProject300ProposeCorrectionResult> ProposeTimeCorrectionAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        TimeOnly newStart,
        TimeOnly newEnd,
        string reason,
        string actor,
        CancellationToken cancellationToken);
}

public sealed record PayrollProject300ProposeCorrectionResult(
    bool Ok,
    string Message,
    Guid? ActionId,
    string? BlockReason);
