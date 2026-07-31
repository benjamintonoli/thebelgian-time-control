using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed class AdminReviewDecisionRepository(
    IDbContextFactory<TimeControlDbContext> dbContextFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public async Task<AdminReviewDecisionAudit> AppendAsync(
        AdminReviewDecisionAudit row,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        context.AdminReviewDecisionAudits.Add(row);
        await context.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task<IReadOnlyList<AdminReviewDecisionAudit>> ListForPerformanceAsync(
        long performanceId,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AdminReviewDecisionAudits
            .AsNoTracking()
            .Where(item => item.PerformanceId == performanceId)
            .OrderBy(item => item.DecidedAt)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, AdminReviewDecisionAudit>> LatestByPerformanceAsync(
        IReadOnlyCollection<long> performanceIds,
        CancellationToken cancellationToken)
    {
        if (performanceIds.Count == 0)
        {
            return new Dictionary<long, AdminReviewDecisionAudit>();
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.AdminReviewDecisionAudits
            .AsNoTracking()
            .Where(item => performanceIds.Contains(item.PerformanceId))
            .OrderByDescending(item => item.DecidedAt)
            .ThenByDescending(item => item.Id)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(item => item.PerformanceId)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public static string SerializeStopIds(IReadOnlyList<string>? stopIds) =>
        JsonSerializer.Serialize(stopIds ?? [], JsonOptions);

    public static IReadOnlyList<string> DeserializeStopIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
    }
}
