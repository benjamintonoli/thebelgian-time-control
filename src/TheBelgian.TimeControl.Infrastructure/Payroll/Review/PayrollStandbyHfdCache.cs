using System.Collections.Concurrent;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

internal sealed class PayrollStandbyHfdCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(out IReadOnlyDictionary<int, HfdTaakDefinition> map)
    {
        if (_entries.TryGetValue("all", out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            map = entry.Map;
            return true;
        }

        map = new Dictionary<int, HfdTaakDefinition>();
        return false;
    }

    public void Set(IReadOnlyDictionary<int, HfdTaakDefinition> map) =>
        _entries["all"] = new Entry(map, DateTimeOffset.UtcNow.AddHours(1));

    private sealed record Entry(IReadOnlyDictionary<int, HfdTaakDefinition> Map, DateTimeOffset ExpiresAtUtc);
}
