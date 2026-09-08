using System.Collections.Concurrent;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

/// <summary>
/// Short-lived Project200 admin-queue cache so selection changes do not rebuild the full month queue.
/// </summary>
internal sealed class PayrollProject200QueueCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(int year, int month, PayrollReviewQueueFilter filter, out PayrollAdminQueuePage? page)
    {
        if (_entries.TryGetValue(Key(year, month, filter), out var entry)
            && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            page = entry.Page;
            return true;
        }

        page = null;
        return false;
    }

    public void Set(int year, int month, PayrollReviewQueueFilter filter, PayrollAdminQueuePage page)
    {
        _entries[Key(year, month, filter)] = new Entry(page, DateTimeOffset.UtcNow.Add(Ttl));
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

    private void TrimIfNeeded()
    {
        if (_entries.Count < 32)
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

    private static string Key(int year, int month, PayrollReviewQueueFilter filter) =>
        $"{year:0000}|{month:00}|{filter.Category}|{filter.Scope}|{filter.Search ?? ""}|{filter.Sort}|{filter.WorkflowStatus}|{filter.Severity}|{filter.Actionability}";

    private sealed record Entry(PayrollAdminQueuePage Page, DateTimeOffset ExpiresAtUtc);
}
