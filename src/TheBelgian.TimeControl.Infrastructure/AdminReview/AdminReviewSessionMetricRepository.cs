using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed class AdminReviewSessionMetricRepository(
    IDbContextFactory<TimeControlDbContext> dbContextFactory)
{
    public async Task<AdminReviewSessionMetric> MarkOpenedAsync(
        long performanceId,
        string? matcherStatus,
        DateTimeOffset openedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var open = await context.AdminReviewSessionMetrics
            .Where(item => item.PerformanceId == performanceId && item.DecidedAt == null)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (open is not null)
        {
            return open;
        }

        var row = new AdminReviewSessionMetric
        {
            PerformanceId = performanceId,
            OpenedAt = openedAt,
            MatcherStatus = matcherStatus,
        };
        context.AdminReviewSessionMetrics.Add(row);
        await context.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task<AdminReviewSessionMetric> CompleteAsync(
        long performanceId,
        string decision,
        string? matcherStatus,
        bool proposedCandidateConfirmed,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var open = await context.AdminReviewSessionMetrics
            .Where(item => item.PerformanceId == performanceId && item.DecidedAt == null)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (open is null)
        {
            open = new AdminReviewSessionMetric
            {
                PerformanceId = performanceId,
                OpenedAt = decidedAt,
                MatcherStatus = matcherStatus,
            };
            context.AdminReviewSessionMetrics.Add(open);
        }

        open.DecidedAt = decidedAt;
        open.Decision = decision;
        open.MatcherStatus = matcherStatus ?? open.MatcherStatus;
        open.ProposedCandidateConfirmed = proposedCandidateConfirmed;
        open.DurationSeconds = Math.Max(0, (decidedAt - open.OpenedAt).TotalSeconds);
        await context.SaveChangesAsync(cancellationToken);
        return open;
    }

    public async Task<IReadOnlyList<AdminReviewSessionMetric>> ListForPerformanceAsync(
        long performanceId,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.AdminReviewSessionMetrics
            .Where(item => item.PerformanceId == performanceId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }
}
