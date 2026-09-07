using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

internal sealed class PayrollReviewQueueService(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IPayrollActionService actionService,
    IOptions<PayrollShadowOptions> shadowOptions,
    IOptions<PayrollActionsOptions> actionsOptions,
    TimeProvider timeProvider,
    ILogger<PayrollReviewQueueService> logger) : IPayrollReviewQueueService
{
    public async Task<PayrollReviewQueuePage> GetQueueAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadowMonth is null)
        {
            return EmptyPage(year, month);
        }

        var employees = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        var included = employees
            .Where(item => item.EligibilityStatus == PayrollEligibilityStatus.Included)
            .OrderBy(item => string.IsNullOrWhiteSpace(item.DisplayNameSnapshot) ? item.ResourceId : item.DisplayNameSnapshot, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToList();
        var includedByResource = included.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);

        var findings = await context.PayrollFindingRecords.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        findings = findings
            .Where(item => includedByResource.ContainsKey(item.ResourceId))
            .ToList();

        // ListActions only — avoid Propose/GPS N+1 on every queue render.
        var actions = await LoadActionsAsync(year, month, cancellationToken);
        var allCases = PayrollReviewCaseBuilder.Build(findings, includedByResource, actions);
        var filtered = PayrollReviewCaseBuilder.ApplyFilter(allCases, filter);
        var summary = PayrollReviewCaseBuilder.Summarize(allCases, included, actions);

        var openResourceIds = allCases
            .Where(item => PayrollReviewCategories.IsUnresolved(item.WorkflowStatus))
            .Select(item => item.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        var withOpen = included
            .Where(item => openResourceIds.Contains(item.ResourceId))
            .Select(item => (item.ResourceId, (string?)item.DisplayNameSnapshot))
            .ToList();
        var withoutOpen = included
            .Where(item => !openResourceIds.Contains(item.ResourceId))
            .Select(item => (item.ResourceId, (string?)item.DisplayNameSnapshot))
            .ToList();

        return new PayrollReviewQueuePage(
            year,
            month,
            summary,
            filtered,
            included,
            withOpen,
            withoutOpen);
    }

    public async Task SetCaseStatusAsync(
        int year,
        int month,
        string caseKey,
        PayrollFindingStatus status,
        string? comment,
        string actor,
        CancellationToken cancellationToken)
    {
        var result = await BulkSetCaseStatusAsync(
            year,
            month,
            [caseKey],
            status,
            comment ?? string.Empty,
            actor,
            requireComment: status == PayrollFindingStatus.Reviewed || status == PayrollFindingStatus.Dismissed,
            cancellationToken);
        if (result.Updated == 0)
        {
            throw new InvalidOperationException($"Case niet gevonden: {caseKey}");
        }
    }

    public Task<PayrollReviewBulkUpdateResult> BulkSetCaseStatusAsync(
        int year,
        int month,
        IReadOnlyList<string> caseKeys,
        PayrollFindingStatus status,
        string comment,
        string actor,
        CancellationToken cancellationToken) =>
        BulkSetCaseStatusAsync(year, month, caseKeys, status, comment, actor, requireComment: true, cancellationToken);

    private async Task<PayrollReviewBulkUpdateResult> BulkSetCaseStatusAsync(
        int year,
        int month,
        IReadOnlyList<string> caseKeys,
        PayrollFindingStatus status,
        string comment,
        string actor,
        bool requireComment,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (caseKeys is null || caseKeys.Count == 0)
        {
            throw new ArgumentException("Selecteer minstens één case.", nameof(caseKeys));
        }

        // Safety: only explicit keys — never expand to "all month".
        var distinctKeys = caseKeys
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctKeys.Length == 0)
        {
            throw new ArgumentException("Selecteer minstens één case.", nameof(caseKeys));
        }

        if (status is not (PayrollFindingStatus.NeedsFollowUp
            or PayrollFindingStatus.Reviewed
            or PayrollFindingStatus.Dismissed
            or PayrollFindingStatus.Open
            or PayrollFindingStatus.Resolved))
        {
            throw new ArgumentOutOfRangeException(nameof(status), "Status niet toegelaten voor queue-update.");
        }

        // Bulk may never execute writes / resolve executable proposals — only disposition statuses.
        if (status is PayrollFindingStatus.Resolved)
        {
            throw new InvalidOperationException(
                "Bulk mag geen 'Opgelost' zetten via de triage-queue (geen write-resolutie).");
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new InvalidOperationException("Actor is verplicht.");
        }

        if (requireComment
            && status is PayrollFindingStatus.Reviewed or PayrollFindingStatus.Dismissed
            && string.IsNullOrWhiteSpace(comment))
        {
            throw new InvalidOperationException("Een reden/commentaar is verplicht.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken)
            ?? throw new InvalidOperationException($"Geen payroll shadow-maand voor {year}-{month:00}.");
        if (shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            throw new InvalidOperationException("Afgesloten shadow-maand kan niet gewijzigd worden.");
        }

        var findings = await context.PayrollFindingRecords
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        var trimmedActor = actor.Trim();
        var trimmedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        var updatedKeys = new List<string>();

        foreach (var caseKey in distinctKeys)
        {
            var matches = findings
                .Where(item => string.Equals(
                    PayrollReviewCaseBuilder.GroupKey(item),
                    caseKey,
                    StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
            {
                continue;
            }

            foreach (var finding in matches)
            {
                finding.Status = status;
                finding.ReviewedAtUtc = now;
                finding.ReviewedBy = trimmedActor;
                finding.ReviewComment = trimmedComment;
            }

            var auditAction = status switch
            {
                PayrollFindingStatus.Reviewed => PayrollShadowAuditAction.ReviewAccepted,
                PayrollFindingStatus.NeedsFollowUp => PayrollShadowAuditAction.ReviewNeedsFollowUp,
                PayrollFindingStatus.Dismissed => PayrollShadowAuditAction.ReviewReset,
                _ => PayrollShadowAuditAction.ReviewReset,
            };
            context.PayrollShadowReviewAudits.Add(new PayrollShadowReviewAudit
            {
                ShadowMonthId = shadowMonth.Id,
                ResourceId = matches[0].ResourceId,
                Action = auditAction,
                Actor = trimmedActor,
                TimestampUtc = now,
                ReasonCode = caseKey,
                Comment = trimmedComment is null
                    ? $"queue-case:{caseKey}"
                    : $"queue-case:{caseKey} | {trimmedComment}",
            });
            updatedKeys.Add(caseKey);
        }

        if (updatedKeys.Count == 0)
        {
            return new PayrollReviewBulkUpdateResult(distinctKeys.Length, 0, []);
        }

        shadowMonth.LastReviewedAtUtc = now;
        shadowMonth.LastReviewedBy = trimmedActor;
        await context.SaveChangesAsync(cancellationToken);
        return new PayrollReviewBulkUpdateResult(distinctKeys.Length, updatedKeys.Count, updatedKeys);
    }

    private async Task<IReadOnlyList<PayrollProposedActionRecord>> LoadActionsAsync(
        int year,
        int month,
        CancellationToken cancellationToken)
    {
        if (!actionsOptions.Value.Enabled)
        {
            return [];
        }

        try
        {
            return await actionService.ListActionsAsync(year, month, resourceId: null, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "ListActions failed for payroll review queue {Year}-{Month:00}.",
                year,
                month);
            return [];
        }
    }

    private void EnsureEnabled()
    {
        shadowOptions.Value.Validate();
        if (!shadowOptions.Value.Enabled)
        {
            throw new InvalidOperationException("Payroll shadow is uitgeschakeld.");
        }
    }

    private static PayrollReviewQueuePage EmptyPage(int year, int month) =>
        new(
            year,
            month,
            new PayrollReviewQueueSummary(
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Enum.GetValues<PayrollReviewCategory>()
                    .Where(item => item != PayrollReviewCategory.All)
                    .ToDictionary(item => item, _ => 0),
                Enum.GetValues<PayrollReviewCategory>()
                    .Where(item => item != PayrollReviewCategory.All)
                    .ToDictionary(item => item, _ => 0)),
            [],
            [],
            [],
            []);
}
