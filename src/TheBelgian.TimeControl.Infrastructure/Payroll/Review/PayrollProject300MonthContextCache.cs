using System.Collections.Concurrent;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

/// <summary>
/// Month-scoped read-only Project300 workbench context (performances, planning, BON memos).
/// Selection after warm uses memory only — no per-click Plenion.
/// </summary>
internal sealed class PayrollProject300MonthContextCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(45);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _buildLocks = new(StringComparer.Ordinal);

    public bool TryGet(string revisionKey, out PayrollProject300MonthContext? context)
    {
        if (_entries.TryGetValue(revisionKey, out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            context = entry.Context;
            return true;
        }

        context = null;
        return false;
    }

    public void Set(string revisionKey, PayrollProject300MonthContext context)
    {
        _entries[revisionKey] = new Entry(context, DateTimeOffset.UtcNow.Add(Ttl));
        TrimIfNeeded();
    }

    public void InvalidateMonth(int year, int month)
    {
        var prefix = $"{year:0000}|{month:00}|";
        foreach (var key in _entries.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                _entries.TryRemove(key, out _);
            }
        }
    }

    public SemaphoreSlim GetBuildLock(string revisionKey) =>
        _buildLocks.GetOrAdd(revisionKey, _ => new SemaphoreSlim(1, 1));

    private void TrimIfNeeded()
    {
        if (_entries.Count < 8)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Entry(PayrollProject300MonthContext Context, DateTimeOffset ExpiresAtUtc);
}

internal sealed record PayrollProject300MonthContext(
    int Year,
    int Month,
    string RevisionKey,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    IReadOnlyDictionary<string, DaySources> DaysByResourceDate,
    IReadOnlyDictionary<string, string?> BonMemosByBonNr,
    PayrollProject300MonthContextMetrics Metrics)
{
    public static string DayKey(string resourceId, DateOnly date) =>
        $"{resourceId.Trim()}|{date:yyyyMMdd}";

    public bool TryGetDay(string resourceId, DateOnly date, out DaySources day) =>
        DaysByResourceDate.TryGetValue(DayKey(resourceId, date), out day!);
}

internal sealed record DaySources(
    IReadOnlyList<NormalizedPerformanceEntry> Performances,
    IReadOnlyList<PayrollPlanningReservation> Planning);

internal sealed record PayrollProject300MonthContextMetrics(
    int PerformanceQueries,
    int PlanningQueries,
    int BonQueries,
    int ResourceCount,
    int DayCount,
    long BuildMilliseconds);
