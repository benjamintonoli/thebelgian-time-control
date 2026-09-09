using System.Collections.Concurrent;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

/// <summary>
/// Bounded in-memory GPS day cache (ResourceId+Date). Does not alter vehicle identity.
/// </summary>
internal sealed class PayrollProject100GpsCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string resourceId, DateOnly date, out StandbyGpsDayEvidence? evidence, out bool hit)
    {
        var key = Key(resourceId, date);
        if (_entries.TryGetValue(key, out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            evidence = entry.Evidence;
            hit = true;
            return true;
        }

        evidence = null;
        hit = false;
        return false;
    }

    public void Set(string resourceId, DateOnly date, StandbyGpsDayEvidence? evidence)
    {
        var key = Key(resourceId, date);
        _entries[key] = new Entry(evidence, DateTimeOffset.UtcNow.Add(Ttl));
        TrimIfNeeded();
    }

    public bool Has(string resourceId, DateOnly date) =>
        _entries.TryGetValue(Key(resourceId, date), out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow;

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

    private sealed record Entry(StandbyGpsDayEvidence? Evidence, DateTimeOffset ExpiresAtUtc);
}
