using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed class DailyReviewRepository(
    IDbContextFactory<TimeControlDbContext> dbContextFactory)
{
    public async Task<IReadOnlyDictionary<string, DailyReviewActionAudit>> LatestAsync(
        IReadOnlyCollection<string> caseIds,
        CancellationToken cancellationToken)
    {
        if (caseIds.Count == 0)
        {
            return new Dictionary<string, DailyReviewActionAudit>();
        }

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.DailyReviewActionAudits
            .AsNoTracking()
            .Where(item => caseIds.Contains(item.CaseId))
            .OrderByDescending(item => item.Id)
            .ToListAsync(cancellationToken);
        return rows.GroupBy(item => item.CaseId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<DailyReviewActionAudit>> ListAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DailyReviewActionAudits
            .AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<DailyReviewActionAudit> SaveAsync(
        DailyReviewActionAudit action,
        DailyCorrectionProposal? proposal,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        context.DailyReviewActionAudits.Add(action);
        if (proposal is not null)
        {
            context.DailyCorrectionProposals.Add(proposal);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return action;
    }

    public async Task<long> SaveReportAsync(
        string technician,
        IReadOnlyList<string> caseIds,
        string content,
        string generatedBy,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var row = new DailyGeneratedFactualReport
        {
            Technician = technician,
            CaseIdsJson = JsonSerializer.Serialize(caseIds),
            Content = content,
            GeneratedBy = generatedBy,
            GeneratedAt = generatedAt,
        };
        context.DailyGeneratedFactualReports.Add(row);
        await context.SaveChangesAsync(cancellationToken);
        return row.Id;
    }
}
