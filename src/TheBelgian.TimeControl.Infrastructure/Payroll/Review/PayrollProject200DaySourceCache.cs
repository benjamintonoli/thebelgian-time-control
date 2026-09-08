using System.Collections.Concurrent;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

/// <summary>
/// Bounded in-memory day cache for Project200 workbench Plenion reads (ResourceId+Date).
/// Avoids re-hitting Plenion on every left-pane selection / GPS revisit.
/// </summary>
internal sealed class PayrollProject200DaySourceCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(
        string resourceId,
        DateOnly date,
        out IReadOnlyList<NormalizedPerformanceEntry> performances,
        out IReadOnlyList<PayrollPlanningReservation> planning)
    {
        if (_entries.TryGetValue(Key(resourceId, date), out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            performances = entry.Performances;
            planning = entry.Planning;
            return true;
        }

        performances = [];
        planning = [];
        return false;
    }

    public void Set(
        string resourceId,
        DateOnly date,
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<PayrollPlanningReservation> planning)
    {
        _entries[Key(resourceId, date)] = new Entry(
            performances,
            planning,
            DateTimeOffset.UtcNow.Add(Ttl));
        TrimIfNeeded();
    }

    private void TrimIfNeeded()
    {
        if (_entries.Count < 256)
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

    private static string Key(string resourceId, DateOnly date) =>
        $"{resourceId.Trim()}|{date:yyyyMMdd}";

    private sealed record Entry(
        IReadOnlyList<NormalizedPerformanceEntry> Performances,
        IReadOnlyList<PayrollPlanningReservation> Planning,
        DateTimeOffset ExpiresAtUtc);
}
