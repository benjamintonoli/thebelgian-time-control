using System.Collections.Concurrent;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

/// <summary>
/// Bounded cache for BON.MEMO technician remarks keyed by BONNR.
/// </summary>
internal sealed class PayrollProject200BonMemoCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string bonNr, out string? memo)
    {
        if (_entries.TryGetValue(Normalize(bonNr), out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            memo = entry.Memo;
            return true;
        }

        memo = null;
        return false;
    }

    public void Set(string bonNr, string? memo)
    {
        _entries[Normalize(bonNr)] = new Entry(memo, DateTimeOffset.UtcNow.Add(Ttl));
        TrimIfNeeded();
    }

    public void SetMany(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var pair in values)
        {
            Set(pair.Key, pair.Value);
        }
    }

    private void TrimIfNeeded()
    {
        if (_entries.Count < 512)
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

    private static string Normalize(string bonNr) => bonNr.Trim();

    private sealed record Entry(string? Memo, DateTimeOffset ExpiresAtUtc);
}
