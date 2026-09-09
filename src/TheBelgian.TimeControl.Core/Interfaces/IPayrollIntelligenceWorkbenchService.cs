using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Core.Interfaces;

/// <summary>
/// Lean shared workbench for Overlap and MissingTechnician categories.
/// </summary>
public interface IPayrollIntelligenceWorkbenchService
{
    Task<PayrollIntelligenceWorkbenchPage> GetShellAsync(
        int year,
        int month,
        PayrollReviewCategory category,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    Task<PayrollIntelligenceWorkbenchPage> GetCoreDetailAsync(
        int year,
        int month,
        PayrollReviewCategory category,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    Task<PayrollIntelligenceGpsLoadResult> GetGpsContextAsync(
        int year,
        int month,
        PayrollReviewCategory category,
        string adminCaseKey,
        CancellationToken cancellationToken = default);

    Task<PayrollIntelligenceProposeResult> ProposeAdjustAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        TimeOnly newStart,
        TimeOnly newEnd,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    Task<PayrollIntelligenceProposeResult> ProposeDeleteAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    Task<PayrollIntelligenceProposeResult> ProposeCreateAsync(
        int year,
        int month,
        string adminCaseKey,
        TimeOnly start,
        TimeOnly endTime,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    PayrollIntelligenceGpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate);

    void InvalidateQueueCache(int year, int month);
}
