using System.Collections.Concurrent;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

internal sealed class PayrollIntelligenceQueueCache
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

    public void Set(int year, int month, PayrollReviewQueueFilter filter, PayrollAdminQueuePage page) =>
        _entries[Key(year, month, filter)] = new Entry(page, DateTimeOffset.UtcNow.Add(Ttl));

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

    private static string Key(int year, int month, PayrollReviewQueueFilter filter) =>
        $"{year:0000}|{month:00}|{filter.Category}|{filter.Scope}|{filter.Search ?? ""}|{filter.Sort}|{filter.WorkflowStatus}|{filter.Severity}";

    private sealed record Entry(PayrollAdminQueuePage Page, DateTimeOffset ExpiresAtUtc);
}

internal sealed class PayrollIntelligenceDayCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string resourceId, DateOnly date, out IReadOnlyList<NormalizedPerformanceEntry> rows)
    {
        if (_entries.TryGetValue(Key(resourceId, date), out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            rows = entry.Rows;
            return true;
        }

        rows = [];
        return false;
    }

    public void Set(string resourceId, DateOnly date, IReadOnlyList<NormalizedPerformanceEntry> rows) =>
        _entries[Key(resourceId, date)] = new Entry(rows, DateTimeOffset.UtcNow.Add(Ttl));

    private static string Key(string resourceId, DateOnly date) => $"{resourceId}|{date:yyyyMMdd}";

    private sealed record Entry(IReadOnlyList<NormalizedPerformanceEntry> Rows, DateTimeOffset ExpiresAtUtc);
}

internal sealed class PayrollIntelligenceGpsCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string resourceId, DateOnly date, out StandbyGpsDayEvidence? evidence, out bool hit)
    {
        if (_entries.TryGetValue(Key(resourceId, date), out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            evidence = entry.Evidence;
            hit = true;
            return true;
        }

        evidence = null;
        hit = false;
        return false;
    }

    public void Set(string resourceId, DateOnly date, StandbyGpsDayEvidence? evidence) =>
        _entries[Key(resourceId, date)] = new Entry(evidence, DateTimeOffset.UtcNow.Add(Ttl));

    private static string Key(string resourceId, DateOnly date) => $"{resourceId}|{date:yyyyMMdd}";

    private sealed record Entry(StandbyGpsDayEvidence? Evidence, DateTimeOffset ExpiresAtUtc);
}

internal sealed class PayrollIntelligenceGpsContextCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string adminCaseKey, out PayrollIntelligenceGpsContext? context)
    {
        if (_entries.TryGetValue(adminCaseKey, out var entry) && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            context = entry.Context;
            return true;
        }

        context = null;
        return false;
    }

    public void Set(string adminCaseKey, PayrollIntelligenceGpsContext context) =>
        _entries[adminCaseKey] = new Entry(context, DateTimeOffset.UtcNow.Add(Ttl));

    private sealed record Entry(PayrollIntelligenceGpsContext Context, DateTimeOffset ExpiresAtUtc);
}

internal sealed class PayrollIntelligenceHfdCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
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
        _entries["all"] = new Entry(map, DateTimeOffset.UtcNow.Add(Ttl));

    private sealed record Entry(IReadOnlyDictionary<int, HfdTaakDefinition> Map, DateTimeOffset ExpiresAtUtc);
}
