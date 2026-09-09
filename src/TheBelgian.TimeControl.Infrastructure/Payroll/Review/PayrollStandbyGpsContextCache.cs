using System.Collections.Concurrent;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

/// <summary>
/// Caches assembled GPS context by admin-case key so revisits skip Plenion + PowerFleet.
/// </summary>
internal sealed class PayrollStandbyGpsContextCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string adminCaseKey, out PayrollStandbyGpsContext? context)
    {
        if (_entries.TryGetValue(adminCaseKey, out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            context = entry.Context;
            return true;
        }

        context = null;
        return false;
    }

    public void Set(string adminCaseKey, PayrollStandbyGpsContext context)
    {
        _entries[adminCaseKey] = new Entry(context, DateTimeOffset.UtcNow.Add(Ttl));
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

    private sealed record Entry(PayrollStandbyGpsContext Context, DateTimeOffset ExpiresAtUtc);
}
