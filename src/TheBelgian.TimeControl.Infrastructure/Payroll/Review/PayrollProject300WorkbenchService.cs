using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

internal sealed class PayrollProject300WorkbenchService(
    IPayrollReviewQueueService reviewQueueService,
    IPayrollPerformanceSource performanceSource,
    IPayrollPlanningSource planningSource,
    IPayrollStandbyGpsSource gpsSource,
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IOptions<PayrollShadowOptions> shadowOptions,
    TimeProvider timeProvider,
    ILogger<PayrollProject300WorkbenchService> logger) : IPayrollProject300WorkbenchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PayrollProject300WorkbenchPage> GetWorkbenchAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var scopedFilter = filter with { Category = PayrollReviewCategory.Project300 };
        var queue = await reviewQueueService.GetAdminQueueAsync(year, month, scopedFilter, cancellationToken);
        var cases = queue.AdminCases;
        var selected = ResolveSelected(cases, selectedAdminCaseKey);

        if (selected is null)
        {
            return new PayrollProject300WorkbenchPage(
                year,
                month,
                queue.AdminSummary,
                cases,
                null,
                null,
                new PayrollProject300WorkbenchMetrics(0, 0, 0));
        }

        var detail = await LoadDetailAsync(selected, cancellationToken);
        return new PayrollProject300WorkbenchPage(
            year,
            month,
            queue.AdminSummary,
            cases,
            selected.AdminCaseKey,
            detail.Detail,
            detail.Metrics);
    }

    public async Task<PayrollProject300ProposeCorrectionResult> ProposeTimeCorrectionAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        TimeOnly newStart,
        TimeOnly newEnd,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new PayrollProject300ProposeCorrectionResult(false, "Reden is verplicht.", null, null);
        }

        if (newEnd <= newStart)
        {
            return new PayrollProject300ProposeCorrectionResult(
                false,
                "Voorstelduur moet positief zijn (geen VAN=TOT).",
                null,
                "NonPositiveDuration");
        }

        var page = await GetWorkbenchAsync(
            year,
            month,
            new PayrollReviewQueueFilter(PayrollReviewCategory.Project300, Scope: PayrollReviewQueueScope.All),
            adminCaseKey,
            cancellationToken);
        var detail = page.Detail;
        if (detail is null)
        {
            return new PayrollProject300ProposeCorrectionResult(false, "Admin-case niet gevonden.", null, null);
        }

        var target = detail.CorrectionTargets.FirstOrDefault(item => item.PerformanceId == performanceId);
        if (target is null)
        {
            return new PayrollProject300ProposeCorrectionResult(false, "Prestatie hoort niet bij deze case.", null, null);
        }

        if (target.CorrectionCapability != PayrollProject300CorrectionCapability.SupportedVanTot)
        {
            return new PayrollProject300ProposeCorrectionResult(
                false,
                target.CapabilityMessage,
                null,
                target.CorrectionCapability.ToString());
        }

        if (target.CurrentStart is null || target.CurrentEnd is null)
        {
            return new PayrollProject300ProposeCorrectionResult(false, "Huidige VAN/TOT ontbreekt.", null, null);
        }

        var booked = detail.BookedRows.First(item => item.PerformanceId == performanceId);
        var date = detail.AdminCase.Date;
        var proposedStart = new DateTimeOffset(date.ToDateTime(newStart), target.CurrentStart.Value.Offset);
        var proposedEnd = new DateTimeOffset(date.ToDateTime(newEnd), target.CurrentEnd.Value.Offset);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadowMonth is null)
        {
            return new PayrollProject300ProposeCorrectionResult(false, "Shadow-maand niet gevonden.", null, null);
        }

        var findingKey = detail.AdminCase.FindingKeys.Count > 0
            ? detail.AdminCase.FindingKeys[0]
            : $"Project300Workbench:{detail.AdminCase.ResourceId}:{date:yyyyMMdd}:{performanceId}";
        var findingId = detail.AdminCase.FindingIds.Count > 0 ? detail.AdminCase.FindingIds[0] : 0;
        var actionKey = $"p300-adjust:{detail.AdminCase.ResourceId}:{date:yyyyMMdd}:{performanceId}";

        var existing = await context.PayrollProposedActionRecords
            .Where(item => item.ShadowMonthId == shadowMonth.Id && item.FindingKey == actionKey)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var adjust = new PayrollActionAdjustProposal(
            performanceId,
            target.CurrentStart.Value,
            target.CurrentEnd.Value,
            proposedStart,
            proposedEnd,
            PayrollStandbyActivityTypes.WaitingTime,
            target.HfdTaakId);

        var evidence = new PayrollActionEvidenceSnapshot(
            findingKey,
            PayrollFindingType.Project300WithoutPlanning,
            PayrollFindingSeverity.Review,
            null,
            $"Workbench VAN/TOT voorstel voor PerformanceId={performanceId}; OMSCHR={booked.Description ?? "—"}.",
            "Project 300 tijdscorrectie (voorstel)",
            reason.Trim(),
            [performanceId],
            proposedStart,
            proposedEnd,
            (decimal)(proposedEnd - proposedStart).TotalHours,
            booked.ProjectId,
            booked.BonNr,
            detail.AdminCase.FindingKeys.ToArray(),
            detail.AdminCase.FindingIds.ToArray());

        var now = timeProvider.GetUtcNow();
        if (existing is null
            || existing.Status is PayrollProposedActionStatus.Applied
                or PayrollProposedActionStatus.Cancelled
                or PayrollProposedActionStatus.Failed)
        {
            existing = new PayrollProposedActionRecord
            {
                ActionId = Guid.NewGuid(),
                ShadowMonthId = shadowMonth.Id,
                FindingKey = actionKey,
                FindingId = findingId > 0 ? findingId : null,
                ResourceId = detail.AdminCase.ResourceId,
                CreatedAtUtc = now,
                CreatedBy = actor,
            };
            context.PayrollProposedActionRecords.Add(existing);
        }

        existing.ActionType = PayrollProposedActionType.AdjustExistingPerformanceTime;
        existing.Status = PayrollProposedActionStatus.ReadyForApproval;
        existing.BlockReason = null;
        existing.EvidenceSnapshotJson = JsonSerializer.Serialize(evidence, JsonOptions);
        existing.ProposalSnapshotJson = JsonSerializer.Serialize(adjust, JsonOptions);
        existing.SourceRevision = $"p300:{performanceId}:{target.CurrentStart:O}:{target.CurrentEnd:O}";
        existing.Comment = reason.Trim();
        existing.UpdatedAtUtc = now;

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Created Project300 workbench adjust proposal {ActionId} for performance {PerformanceId} (ExecutionEnabled gate still applies).",
            existing.ActionId,
            performanceId);

        return new PayrollProject300ProposeCorrectionResult(
            true,
            "Correctievoorstel opgeslagen (niet uitgevoerd).",
            existing.ActionId,
            null);
    }

    private async Task<(PayrollProject300CaseDetail Detail, PayrollProject300WorkbenchMetrics Metrics)> LoadDetailAsync(
        PayrollAdminCase adminCase,
        CancellationToken cancellationToken)
    {
        var resourceIds = new[] { adminCase.ResourceId };
        var performances = await performanceSource.ReadPerformancesAsync(
            adminCase.Date,
            adminCase.Date,
            resourceIds,
            cancellationToken);
        var planning = await planningSource.ReadWorkReservationsAsync(
            adminCase.Date,
            adminCase.Date,
            resourceIds,
            cancellationToken);
        var gpsBatch = await gpsSource.ReadStandbyGpsAsync(
            adminCase.Date,
            adminCase.Date,
            [(adminCase.ResourceId, adminCase.DisplayName ?? adminCase.ResourceId, adminCase.Date)],
            cancellationToken);
        var gps = gpsBatch.Days.FirstOrDefault(item =>
            string.Equals(item.ResourceId, adminCase.ResourceId, StringComparison.Ordinal)
            && item.Date == adminCase.Date);

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(adminCase, performances, planning, gps);
        var metrics = new PayrollProject300WorkbenchMetrics(1, 1, gpsBatch.ApiCallCount);
        return (detail, metrics);
    }

    private static PayrollAdminCase? ResolveSelected(
        IReadOnlyList<PayrollAdminCase> cases,
        string? selectedAdminCaseKey)
    {
        if (!string.IsNullOrWhiteSpace(selectedAdminCaseKey))
        {
            var match = cases.FirstOrDefault(item =>
                string.Equals(item.AdminCaseKey, selectedAdminCaseKey, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        for (var i = 0; i < cases.Count; i++)
        {
            if (PayrollReviewCategories.IsUnresolved(cases[i].WorkflowStatus))
            {
                return cases[i];
            }
        }

        return cases.Count > 0 ? cases[0] : null;
    }

    private void EnsureEnabled()
    {
        if (!shadowOptions.Value.Enabled)
        {
            throw new InvalidOperationException("Payroll shadow is disabled.");
        }
    }
}
