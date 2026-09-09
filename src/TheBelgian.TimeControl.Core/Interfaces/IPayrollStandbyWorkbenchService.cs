using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollStandbyWorkbenchService
{
    Task<PayrollStandbyWorkbenchPage> GetWorkbenchAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Left queue shell without blocking on month Plenion batch / detail.
    /// </summary>
    Task<PayrollStandbyWorkbenchPage> GetShellAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Core detail without PowerFleet (GPS deferred). Uses month-batch context after warm.
    /// </summary>
    Task<PayrollStandbyWorkbenchPage> GetCoreDetailAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    Task<PayrollStandbyGpsLoadResult> GetGpsContextAsync(
        int year,
        int month,
        string adminCaseKey,
        bool prefetchNext = true,
        CancellationToken cancellationToken = default);

    Task<PayrollStandbyProposeCorrectionResult> ProposeTimeCorrectionAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        TimeOnly newStart,
        TimeOnly newEnd,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    Task<PayrollStandbyProposeCorrectionResult> ProposeDeletePerformanceAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    PayrollStandbyGpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate);

    void InvalidateQueueCache(int year, int month);
}

public sealed record PayrollStandbyProposeCorrectionResult(
    bool Ok,
    string Message,
    Guid? ActionId,
    string? BlockReason);

public sealed record PayrollStandbyGpsLoadResult(
    string AdminCaseKey,
    string ResourceId,
    DateOnly Date,
    PayrollStandbyGpsContext GpsContext,
    bool CacheHit,
    int PowerFleetApiCalls,
    string? PrefetchAdminCaseKey,
    bool PrefetchStarted);
