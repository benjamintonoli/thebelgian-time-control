using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IPayrollProject200WorkbenchService
{
    Task<PayrollProject200WorkbenchPage> GetWorkbenchAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Left queue shell without blocking on month Plenion batch / detail.
    /// </summary>
    Task<PayrollProject200WorkbenchPage> GetShellAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Core detail without PowerFleet (GPS deferred). Uses month-batch context after warm.
    /// </summary>
    Task<PayrollProject200WorkbenchPage> GetCoreDetailAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken);

    Task<PayrollProject200GpsLoadResult> GetGpsContextAsync(
        int year,
        int month,
        string adminCaseKey,
        bool prefetchNext = true,
        CancellationToken cancellationToken = default);

    Task<PayrollProject200ProposeCorrectionResult> ProposeTimeCorrectionAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        TimeOnly newStart,
        TimeOnly newEnd,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    Task<PayrollProject200ProposeCorrectionResult> ProposeDeletePerformanceAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        string reason,
        string actor,
        CancellationToken cancellationToken);

    PayrollProject200GpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate);

    void InvalidateQueueCache(int year, int month);
}

public sealed record PayrollProject200ProposeCorrectionResult(
    bool Ok,
    string Message,
    Guid? ActionId,
    string? BlockReason);

public sealed record PayrollProject200GpsLoadResult(
    string AdminCaseKey,
    string ResourceId,
    DateOnly Date,
    PayrollProject200GpsContext GpsContext,
    bool CacheHit,
    int PowerFleetApiCalls,
    string? PrefetchAdminCaseKey,
    bool PrefetchStarted);
