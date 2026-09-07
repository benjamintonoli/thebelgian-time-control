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
    private const string ProposeActor = "payroll-review-queue";

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

        var actions = await LoadActionsAsync(year, month, cancellationToken);
        var allCases = PayrollReviewCaseBuilder.Build(findings, includedByResource, actions);
        var filtered = PayrollReviewCaseBuilder.ApplyFilter(allCases, filter);
        var summary = PayrollReviewCaseBuilder.Summarize(allCases, included, actions);

        var openResourceIds = allCases
            .Where(item => item.WorkflowStatus is PayrollFindingStatus.Open or PayrollFindingStatus.NeedsFollowUp)
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
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(caseKey))
        {
            throw new ArgumentException("CaseKey is verplicht.", nameof(caseKey));
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new InvalidOperationException("Actor is verplicht.");
        }

        if (status is not (PayrollFindingStatus.Open
            or PayrollFindingStatus.Reviewed
            or PayrollFindingStatus.NeedsFollowUp
            or PayrollFindingStatus.Dismissed
            or PayrollFindingStatus.Resolved))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Ongeldige finding-status.");
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
        var matches = findings
            .Where(item => string.Equals(
                PayrollReviewCaseBuilder.GroupKey(item),
                caseKey.Trim(),
                StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 0)
        {
            throw new InvalidOperationException($"Case niet gevonden: {caseKey}");
        }

        var now = timeProvider.GetUtcNow();
        var trimmedActor = actor.Trim();
        var trimmedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
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
            PayrollFindingStatus.Open => PayrollShadowAuditAction.ReviewReset,
            _ => PayrollShadowAuditAction.ReviewReset,
        };
        context.PayrollShadowReviewAudits.Add(new PayrollShadowReviewAudit
        {
            ShadowMonthId = shadowMonth.Id,
            ResourceId = matches[0].ResourceId,
            Action = auditAction,
            Actor = trimmedActor,
            TimestampUtc = now,
            ReasonCode = caseKey.Trim(),
            Comment = trimmedComment,
        });

        shadowMonth.LastReviewedAtUtc = now;
        shadowMonth.LastReviewedBy = trimmedActor;
        await context.SaveChangesAsync(cancellationToken);
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
            // Refresh eligibility / hybrid standby blocks while building the queue.
            return await actionService.ProposeFromFindingsAsync(
                year,
                month,
                resourceId: null,
                ProposeActor,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "ProposeFromFindings failed for payroll review queue {Year}-{Month:00}; falling back to ListActions.",
                year,
                month);
            try
            {
                return await actionService.ListActionsAsync(year, month, resourceId: null, cancellationToken);
            }
            catch (Exception listException)
            {
                logger.LogWarning(
                    listException,
                    "ListActions also failed for payroll review queue {Year}-{Month:00}.",
                    year,
                    month);
                return [];
            }
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
                    .ToDictionary(item => item, _ => 0)),
            [],
            [],
            [],
            []);
}
