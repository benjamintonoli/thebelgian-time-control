using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Persistence;

public sealed class ExceptionRepository(
    IDbContextFactory<TimeControlDbContext> contextFactory) : IExceptionRepository
{
    public async Task<IReadOnlyList<DetectedException>> SearchAsync(
        ExceptionFilter filter,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = context.DetectedExceptions.AsNoTracking().AsQueryable();

        if (filter.FromDate is { } fromDate)
        {
            query = query.Where(item => item.Date >= fromDate);
        }

        if (filter.ThroughDate is { } throughDate)
        {
            query = query.Where(item => item.Date <= throughDate);
        }

        if (!string.IsNullOrWhiteSpace(filter.Technician))
        {
            query = query.Where(item =>
                item.TechnicianName.Contains(filter.Technician) ||
                item.TechnicianExternalId.Contains(filter.Technician));
        }

        if (filter.Priority is { } priority)
        {
            query = query.Where(item => item.Priority == priority);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(item => item.ReviewDecision == status);
        }

        return await query
            .OrderByDescending(item => item.Date)
            .ThenByDescending(item => item.Priority)
            .ThenBy(item => item.TechnicianName)
            .ToListAsync(cancellationToken);
    }

    public async Task<DetectedException?> GetAsync(
        int id,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DetectedExceptions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task UpsertAsync(
        IEnumerable<DetectedException> exceptions,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var detected in exceptions)
        {
            var current = await context.DetectedExceptions.SingleOrDefaultAsync(
                item => item.ExternalKey == detected.ExternalKey,
                cancellationToken);
            if (current is null)
            {
                context.DetectedExceptions.Add(detected);
                continue;
            }

            var originalReview = current.ReviewDecision;
            var originalCreatedAt = current.CreatedAt;
            context.Entry(current).CurrentValues.SetValues(detected);
            current.ReviewDecision = originalReview;
            current.CreatedAt = originalCreatedAt;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateReviewAsync(
        int id,
        ReviewDecision decision,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var detected = await context.DetectedExceptions.SingleOrDefaultAsync(
            item => item.Id == id,
            cancellationToken)
            ?? throw new KeyNotFoundException($"Afwijking {id} bestaat niet.");
        detected.ReviewDecision = decision;
        await context.SaveChangesAsync(cancellationToken);
    }
}
