using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollProject100WorkbenchService
{
    Task<PayrollProject100WorkbenchPage> GetWorkbenchAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Left queue shell without blocking on month Plenion batch / detail.
    /// </summary>
    Task<PayrollProject100WorkbenchPage> GetShellAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Core detail without PowerFleet (GPS deferred). Uses month-batch context after warm.
    /// </summary>
    Task<PayrollProject100WorkbenchPage> GetCoreDetailAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    Task<PayrollProject100GpsLoadResult> GetGpsContextAsync(
        int year,
        int month,
        string adminCaseKey,
        bool prefetchNext = true,
        CancellationToken cancellationToken = default);

    Task<PayrollProject100ProposeCorrectionResult> ProposeTimeCorrectionAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        TimeOnly newStart,
        TimeOnly newEnd,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    Task<PayrollProject100ProposeCorrectionResult> ProposeDeletePerformanceAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    PayrollProject100GpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate);

    void InvalidateQueueCache(int year, int month);
}

public sealed record PayrollProject100ProposeCorrectionResult(
    bool Ok,
    string Message,
    Guid? ActionId,
    string? BlockReason);

public sealed record PayrollProject100GpsLoadResult(
    string AdminCaseKey,
    string ResourceId,
    DateOnly Date,
    PayrollProject100GpsContext GpsContext,
    bool CacheHit,
    int PowerFleetApiCalls,
    string? PrefetchAdminCaseKey,
    bool PrefetchStarted);
