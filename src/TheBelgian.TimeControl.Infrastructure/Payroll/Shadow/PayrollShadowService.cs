using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Legacy;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Eligibility;
using TheBelgian.TimeControl.Infrastructure.Payroll.Findings;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Shadow;

internal sealed class PayrollShadowService(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IPayrollResourceReader resourceReader,
    IPayrollPerformanceSource performanceSource,
    IPayrollCalendarSource calendarSource,
    IPayrollPlanningSource planningSource,
    IPayrollStandbyGpsSource standbyGpsSource,
    PayrollShadowCalculationService calculationService,
    IOptions<PayrollShadowOptions> options,
    TimeProvider timeProvider) : IPayrollShadowService
{
    public async Task<IReadOnlyList<PayrollShadowMonthSummary>> ListMonthsAsync(
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var months = await context.PayrollShadowMonths.AsNoTracking()
            .OrderByDescending(item => item.Year)
            .ThenByDescending(item => item.Month)
            .ToListAsync(cancellationToken);
        var summaries = new List<PayrollShadowMonthSummary>(months.Count);
        foreach (var month in months)
        {
            summaries.Add(await BuildSummaryAsync(context, month, cancellationToken));
        }

        return summaries;
    }

    public async Task<PayrollShadowMonthDetail?> GetMonthDetailAsync(
        int year,
        int month,
        PayrollShadowEmployeeFilter filter,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadowMonth is null)
        {
            return null;
        }

        var employees = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .OrderBy(item => item.DisplayNameSnapshot)
            .ToListAsync(cancellationToken);
        var findings = await context.PayrollFindingRecords.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        var findingsByResource = findings
            .GroupBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var query = ApplyFilter(employees, findingsByResource, filter);
        IEnumerable<PayrollShadowEmployeeResult> ordered = filter.PrioritizeReviewExceptions
            ? query
                .OrderByDescending(item => HighestSeverity(findingsByResource, item.ResourceId))
                .ThenByDescending(item => FindingCount(findingsByResource, item.ResourceId))
                .ThenByDescending(item => item.ReviewStatus == PayrollEmployeeReviewStatus.NeedsFollowUp)
                .ThenByDescending(item => Math.Abs(item.LegacyDifferenceHours.GetValueOrDefault()))
                .ThenBy(item => item.ReviewStatus == PayrollEmployeeReviewStatus.Pending ? 0 : 1)
                .ThenBy(item => item.DisplayNameSnapshot, StringComparer.OrdinalIgnoreCase)
            : query.OrderBy(item => item.DisplayNameSnapshot, StringComparer.OrdinalIgnoreCase);
        var filtered = ordered
            .Select(item => MapEmployeeRow(item, findingsByResource.GetValueOrDefault(item.ResourceId) ?? []))
            .ToList();
        var summary = await BuildSummaryAsync(context, shadowMonth, cancellationToken);
        var withFindings = employees.Count(item =>
            item.EligibilityStatus == PayrollEligibilityStatus.Included
            && findingsByResource.ContainsKey(item.ResourceId));
        var includedCount = employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.Included);
        return new PayrollShadowMonthDetail(
            shadowMonth,
            summary,
            filtered,
            withFindings,
            Math.Max(0, includedCount - withFindings));
    }

    public async Task<PayrollShadowEmployeeDetail?> GetEmployeeDetailAsync(
        int year,
        int month,
        string resourceId,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadowMonth is null)
        {
            return null;
        }

        var employee = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ShadowMonthId == shadowMonth.Id && item.ResourceId == resourceId, cancellationToken);
        if (employee is null)
        {
            return null;
        }

        var configurations = await context.PayrollEmployeeConfigurationRecords.AsNoTracking()
            .Where(item => item.ResourceId == resourceId)
            .ToListAsync(cancellationToken);
        configurations = configurations
            .OrderByDescending(item => item.ValidFrom)
            .ToList();
        var audit = await context.PayrollShadowReviewAudits.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id
                && (item.ResourceId == null || item.ResourceId == resourceId))
            .ToListAsync(cancellationToken);
        audit = audit
            .OrderByDescending(item => item.TimestampUtc)
            .ToList();
        var findings = await context.PayrollFindingRecords.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id && item.ResourceId == resourceId)
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.Date)
            .ToListAsync(cancellationToken);
        return new PayrollShadowEmployeeDetail(shadowMonth, employee, configurations, audit, findings);
    }

    public async Task<PayrollShadowMonth> CreateSnapshotAsync(
        int year,
        int month,
        DateOnly evaluationDate,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        var period = PayrollPeriodSnapshot.ForMonth(year, month, evaluationDate);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.PayrollShadowMonths
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (existing is not null)
        {
            if (existing.Status == PayrollShadowMonthStatus.Finalized)
            {
                throw new InvalidOperationException("Een afgesloten shadow-maand kan niet opnieuw berekend worden.");
            }

            throw new InvalidOperationException(
                "Er bestaat al een shadow-maandsnapshot voor deze periode.");
        }

        var material = await MaterializeSnapshotAsync(context, period, cancellationToken);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var shadowMonth = new PayrollShadowMonth
        {
            Year = year,
            Month = month,
            PeriodStart = period.PeriodStart,
            PeriodEnd = period.PeriodEnd,
            EvaluationDate = evaluationDate,
            Status = PayrollShadowMonthStatus.ReadyForReview,
            CalculationVersion = PayrollShadowConfigurationSnapshot.CurrentCalculationVersion(),
            CreatedAtUtc = now,
            CreatedBy = actor.Trim(),
            ConfigurationSnapshotJson = material.ConfigurationSnapshotJson,
        };
        context.PayrollShadowMonths.Add(shadowMonth);
        await context.SaveChangesAsync(cancellationToken);
        AddEmployeeResults(context, shadowMonth.Id, material);
        ReplaceFindings(context, shadowMonth.Id, material);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return shadowMonth;
    }

    public async Task<PayrollShadowMonth> RebuildSnapshotAsync(
        int year,
        int month,
        DateOnly evaluationDate,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        var period = PayrollPeriodSnapshot.ForMonth(year, month, evaluationDate);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadowMonth is null)
        {
            throw new InvalidOperationException("Shadow-maand bestaat niet en kan niet herbouwd worden.");
        }

        if (shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            throw new InvalidOperationException("Een afgesloten shadow-maand kan niet opnieuw berekend worden.");
        }

        var previousStatus = shadowMonth.Status;
        var material = await MaterializeSnapshotAsync(context, period, cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var existingEmployees = await context.PayrollShadowEmployeeResults
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        var preservedReviews = existingEmployees
            .Where(item => item.ReviewStatus is PayrollEmployeeReviewStatus.Accepted
                or PayrollEmployeeReviewStatus.NeedsFollowUp)
            .ToDictionary(
                item => item.ResourceId,
                item => (
                    item.ReviewStatus,
                    item.ReviewComment,
                    item.ReviewedAtUtc,
                    item.ReviewedBy),
                StringComparer.Ordinal);

        context.PayrollShadowEmployeeResults.RemoveRange(existingEmployees);
        var existingFindings = await context.PayrollFindingRecords
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        context.PayrollFindingRecords.RemoveRange(existingFindings);

        shadowMonth.PeriodStart = period.PeriodStart;
        shadowMonth.PeriodEnd = period.PeriodEnd;
        shadowMonth.EvaluationDate = evaluationDate;
        shadowMonth.Status = previousStatus == PayrollShadowMonthStatus.InReview
            ? PayrollShadowMonthStatus.InReview
            : PayrollShadowMonthStatus.ReadyForReview;
        shadowMonth.CalculationVersion = PayrollShadowConfigurationSnapshot.CurrentCalculationVersion();
        shadowMonth.ConfigurationSnapshotJson = material.ConfigurationSnapshotJson;

        AddEmployeeResults(context, shadowMonth.Id, material);
        ReplaceFindings(context, shadowMonth.Id, material);
        await context.SaveChangesAsync(cancellationToken);

        if (preservedReviews.Count > 0)
        {
            var rebuiltEmployees = await context.PayrollShadowEmployeeResults
                .Where(item => item.ShadowMonthId == shadowMonth.Id)
                .ToListAsync(cancellationToken);
            foreach (var employee in rebuiltEmployees)
            {
                if (!preservedReviews.TryGetValue(employee.ResourceId, out var preserved))
                {
                    continue;
                }

                if (employee.EligibilityStatus == PayrollEligibilityStatus.Excluded)
                {
                    continue;
                }

                employee.ReviewStatus = preserved.ReviewStatus;
                employee.ReviewComment = preserved.ReviewComment;
                employee.ReviewedAtUtc = preserved.ReviewedAtUtc;
                employee.ReviewedBy = preserved.ReviewedBy;
            }
        }

        await AppendAuditAsync(
            context,
            shadowMonth.Id,
            null,
            PayrollShadowAuditAction.MonthSnapshotRebuilt,
            actor,
            null,
            preservedReviews.Count > 0
                ? $"Non-finalized shadow month rebuilt; preserved {preservedReviews.Count} review decision(s)."
                : "Non-finalized shadow month rebuilt after roster/eligibility correction.");
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return shadowMonth;
    }

    public async Task<PayrollMonthPeriodEligibilityInsight> GetPeriodEligibilityInsightAsync(
        int year,
        int month,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken)
            ?? throw new InvalidOperationException("Shadow-maand niet gevonden.");

        var employees = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        var configurations = (await LoadConfigurationsAsync(context, cancellationToken))
            .Select(item => item.ToDomain())
            .ToList();

        var laterDecisions = configurations
            .Where(item =>
                item.EligibilityStatus is PayrollEligibilityStatus.Included or PayrollEligibilityStatus.Excluded
                && item.ValidFrom > shadowMonth.PeriodEnd)
            .ToList();
        var earliestLater = laterDecisions.Count == 0
            ? (DateOnly?)null
            : laterDecisions.Min(item => item.ValidFrom);

        string? warning = null;
        if (earliestLater is not null)
        {
            warning =
                $"De payrollselectie is geldig vanaf {earliestLater.Value:dd/MM/yyyy} en is niet van toepassing op {shadowMonth.Month:00}/{shadowMonth.Year}.";
        }

        return new PayrollMonthPeriodEligibilityInsight(
            shadowMonth.PeriodStart,
            shadowMonth.PeriodEnd,
            employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision),
            employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.Included),
            employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.Excluded),
            laterDecisions.Count > 0,
            earliestLater,
            laterDecisions.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.Included),
            laterDecisions.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.Excluded),
            warning);
    }

    public async Task<PayrollMonthFinalizationBlockers> GetFinalizationBlockersAsync(
        int year,
        int month,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken)
            ?? throw new InvalidOperationException("Shadow-maand niet gevonden.");
        var employees = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);

        var included = employees
            .Where(item => item.EligibilityStatus == PayrollEligibilityStatus.Included)
            .ToList();
        var pending = included.Count(item => item.ReviewStatus == PayrollEmployeeReviewStatus.Pending);
        var followUp = included.Count(item => item.ReviewStatus == PayrollEmployeeReviewStatus.NeedsFollowUp);
        var needsDecision = employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision);
        var missingAcerta = included.Count(item => item.AcertaIdentityStatus == AcertaIdentityStatus.Missing);
        var lines = new List<string>();
        if (included.Count == 0)
        {
            lines.Add("Geen Included medewerkers.");
        }

        if (pending > 0)
        {
            lines.Add($"{pending} Pending (te controleren)");
        }

        if (followUp > 0)
        {
            lines.Add($"{followUp} NeedsFollowUp (opvolging nodig)");
        }

        if (needsDecision > 0)
        {
            lines.Add($"{needsDecision} NeedsDecision");
        }

        if (missingAcerta > 0)
        {
            lines.Add($"{missingAcerta} Missing Acerta ID");
        }

        if (shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            lines.Add("Maand is al afgesloten.");
        }

        var canFinalize = lines.Count == 0
            && shadowMonth.Status is PayrollShadowMonthStatus.ReadyForReview or PayrollShadowMonthStatus.InReview;
        return new PayrollMonthFinalizationBlockers(
            canFinalize,
            pending,
            followUp,
            needsDecision,
            missingAcerta,
            included.Count,
            lines);
    }

    public async Task<ApplyConfirmedRosterToMonthResult> ApplyConfirmedRosterToMonthAsync(
        int year,
        int month,
        string actor,
        string? comment,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken)
            ?? throw new InvalidOperationException("Shadow-maand niet gevonden.");
        if (shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            throw new InvalidOperationException("Een afgesloten shadow-maand kan niet gewijzigd worden.");
        }

        var asOf = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        if (asOf < shadowMonth.PeriodEnd)
        {
            asOf = shadowMonth.PeriodEnd.AddDays(1);
        }

        var records = await LoadConfigurationsAsync(context, cancellationToken);
        var configurations = records.Select(item => item.ToDomain()).ToList();
        var confirmed = configurations
            .Where(item =>
                item.EligibilityStatus is PayrollEligibilityStatus.Included or PayrollEligibilityStatus.Excluded
                && item.IsActiveFor(asOf, asOf))
            .GroupBy(item => item.ResourceId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.ValidFrom).First())
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ToList();
        if (confirmed.Count == 0)
        {
            throw new InvalidOperationException(
                "Geen bevestigde payrollselectie gevonden om toe te passen op deze maand.");
        }

        var includedWritten = 0;
        var excludedWritten = 0;
        var skipped = 0;
        var reason = "RosterAppliedToMonth";
        var note = string.IsNullOrWhiteSpace(comment)
            ? $"Toegepast op {shadowMonth.Month:00}/{shadowMonth.Year} vanaf {shadowMonth.PeriodStart:yyyy-MM-dd}."
            : comment.Trim();
        var now = timeProvider.GetUtcNow();
        var working = configurations.ToList();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var decision in confirmed)
        {
            var activeForMonth = working
                .Where(item =>
                    item.ResourceId == decision.ResourceId
                    && item.IsActiveFor(shadowMonth.PeriodStart, shadowMonth.PeriodEnd))
                .ToArray();
            if (activeForMonth.Length == 1
                && activeForMonth[0].EligibilityStatus == decision.EligibilityStatus)
            {
                skipped++;
                continue;
            }

            if (activeForMonth.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Resource {decision.ResourceId} heeft al een andere eligibility in {shadowMonth.Month:00}/{shadowMonth.Year}.");
            }

            DateOnly? bridgeTo = null;
            var nextStart = working
                .Where(item =>
                    item.ResourceId == decision.ResourceId
                    && item.ValidFrom > shadowMonth.PeriodStart)
                .Select(item => item.ValidFrom)
                .DefaultIfEmpty()
                .Min();
            if (nextStart != default)
            {
                bridgeTo = nextStart.AddDays(-1);
                if (bridgeTo < shadowMonth.PeriodStart)
                {
                    throw new InvalidOperationException(
                        $"Kan roster niet toepassen voor resource {decision.ResourceId}: ongeldig bruginterval.");
                }
            }

            var candidate = new PayrollEmployeeConfiguration(
                decision.ResourceId,
                shadowMonth.PeriodStart,
                bridgeTo,
                decision.EligibilityStatus,
                reason,
                note,
                PayrollEligibilityDecisionSource.Admin);
            await EnsureConfigurationDoesNotOverlapFinalizedPeriodAsync(context, candidate, cancellationToken);
            PayrollEligibilityResolver.EnsureNoOverlap(working, candidate);

            context.PayrollEmployeeConfigurationRecords.Add(new PayrollEmployeeConfigurationRecord
            {
                ResourceId = candidate.ResourceId,
                ValidFrom = candidate.ValidFrom,
                ValidTo = candidate.ValidTo,
                EligibilityStatus = candidate.EligibilityStatus,
                ReasonCode = candidate.ReasonCode,
                Comment = candidate.Comment,
                DecisionSource = candidate.DecisionSource,
                CreatedAtUtc = now,
                CreatedBy = actor.Trim(),
            });
            working.Add(candidate);
            await ApplyEligibilityToOpenSnapshotsAsync(context, candidate, cancellationToken);

            if (decision.EligibilityStatus == PayrollEligibilityStatus.Included)
            {
                includedWritten++;
            }
            else
            {
                excludedWritten++;
            }
        }

        await AppendAuditAsync(
            context,
            shadowMonth.Id,
            null,
            PayrollShadowAuditAction.MonthRosterApplied,
            actor,
            reason,
            $"Included={includedWritten}; Excluded={excludedWritten}; Skipped={skipped}. {note}");
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ApplyConfirmedRosterToMonthResult(
            year,
            month,
            shadowMonth.PeriodStart,
            includedWritten,
            excludedWritten,
            skipped);
    }

    public async Task<PayrollShadowMonth> StartReviewAsync(
        int year,
        int month,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await RequireMutableMonthAsync(context, year, month, cancellationToken);
        if (shadowMonth.Status == PayrollShadowMonthStatus.WaitingForData)
        {
            throw new InvalidOperationException("Shadow-maand is nog niet klaar voor controle.");
        }

        if (shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            throw new InvalidOperationException("Een afgesloten shadow-maand kan niet opnieuw geopend worden.");
        }

        if (shadowMonth.Status == PayrollShadowMonthStatus.InReview)
        {
            throw new InvalidOperationException("Review is al gestart voor deze shadow-maand.");
        }

        shadowMonth.Status = PayrollShadowMonthStatus.InReview;
        shadowMonth.LastReviewedAtUtc = timeProvider.GetUtcNow();
        shadowMonth.LastReviewedBy = actor.Trim();
        await AppendAuditAsync(
            context,
            shadowMonth.Id,
            null,
            PayrollShadowAuditAction.MonthReviewStarted,
            actor,
            null,
            null);
        await context.SaveChangesAsync(cancellationToken);
        return shadowMonth;
    }

    public async Task<PayrollShadowMonth> FinalizeAsync(
        int year,
        int month,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await RequireMutableMonthAsync(context, year, month, cancellationToken);
        var employees = await context.PayrollShadowEmployeeResults
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        ValidateFinalization(shadowMonth, employees);

        shadowMonth.Status = PayrollShadowMonthStatus.Finalized;
        shadowMonth.FinalizedAtUtc = timeProvider.GetUtcNow();
        shadowMonth.FinalizedBy = actor.Trim();
        shadowMonth.LastReviewedAtUtc = shadowMonth.FinalizedAtUtc;
        shadowMonth.LastReviewedBy = actor.Trim();
        await AppendAuditAsync(
            context,
            shadowMonth.Id,
            null,
            PayrollShadowAuditAction.MonthFinalized,
            actor,
            null,
            null);
        await context.SaveChangesAsync(cancellationToken);
        return shadowMonth;
    }

    public async Task SetEligibilityAsync(
        SetPayrollEligibilityRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        if (string.IsNullOrWhiteSpace(request.ReasonCode))
        {
            throw new ArgumentException("ReasonCode is verplicht.", nameof(request));
        }

        if (request.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision)
        {
            throw new ArgumentException(
                "Gebruik Reset om terug te zetten naar NeedsDecision.",
                nameof(request));
        }

        var candidateConfig = new PayrollEmployeeConfiguration(
            request.ResourceId.Trim(),
            request.ValidFrom,
            request.ValidTo,
            request.EligibilityStatus,
            request.ReasonCode.Trim(),
            request.Comment?.Trim(),
            PayrollEligibilityDecisionSource.Admin);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureConfigurationDoesNotOverlapFinalizedPeriodAsync(context, candidateConfig, cancellationToken);

        var existing = await context.PayrollEmployeeConfigurationRecords
            .Where(item => item.ResourceId == candidateConfig.ResourceId)
            .ToListAsync(cancellationToken);
        CloseOpenEndedConfigsBefore(existing, candidateConfig.ValidFrom);
        PayrollEligibilityResolver.EnsureNoOverlap(
            existing.Select(item => item.ToDomain()).ToList(),
            candidateConfig);

        var now = timeProvider.GetUtcNow();
        context.PayrollEmployeeConfigurationRecords.Add(new PayrollEmployeeConfigurationRecord
        {
            ResourceId = candidateConfig.ResourceId,
            ValidFrom = candidateConfig.ValidFrom,
            ValidTo = candidateConfig.ValidTo,
            EligibilityStatus = candidateConfig.EligibilityStatus,
            ReasonCode = candidateConfig.ReasonCode,
            Comment = candidateConfig.Comment,
            DecisionSource = candidateConfig.DecisionSource,
            CreatedAtUtc = now,
            CreatedBy = actor.Trim(),
        });

        var action = request.EligibilityStatus switch
        {
            PayrollEligibilityStatus.Included => PayrollShadowAuditAction.EligibilityIncluded,
            PayrollEligibilityStatus.Excluded => PayrollShadowAuditAction.EligibilityExcluded,
            _ => PayrollShadowAuditAction.EligibilityReset,
        };
        await AppendAuditAsync(
            context,
            shadowMonthId: 0,
            request.ResourceId,
            action,
            actor,
            request.ReasonCode,
            request.Comment);
        await ApplyEligibilityToOpenSnapshotsAsync(context, candidateConfig, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task SetReviewStatusAsync(
        SetPayrollReviewStatusRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await RequireMutableMonthAsync(context, request.Year, request.Month, cancellationToken);
        if (shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            throw new InvalidOperationException("Afgesloten shadow-maand kan niet gewijzigd worden.");
        }
        var employee = await context.PayrollShadowEmployeeResults.SingleOrDefaultAsync(item =>
            item.ShadowMonthId == shadowMonth.Id && item.ResourceId == request.ResourceId, cancellationToken)
            ?? throw new InvalidOperationException("Medewerker niet gevonden in shadow-maand.");

        employee.ReviewStatus = request.ReviewStatus;
        employee.ReviewComment = request.Comment?.Trim();
        employee.ReviewedAtUtc = timeProvider.GetUtcNow();
        employee.ReviewedBy = actor.Trim();
        shadowMonth.LastReviewedAtUtc = employee.ReviewedAtUtc;
        shadowMonth.LastReviewedBy = actor.Trim();

        var action = request.ReviewStatus switch
        {
            PayrollEmployeeReviewStatus.Accepted => PayrollShadowAuditAction.ReviewAccepted,
            PayrollEmployeeReviewStatus.NeedsFollowUp => PayrollShadowAuditAction.ReviewNeedsFollowUp,
            _ => PayrollShadowAuditAction.ReviewReset,
        };
        await AppendAuditAsync(
            context,
            shadowMonth.Id,
            request.ResourceId,
            action,
            actor,
            null,
            request.Comment);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ResetEligibilityAsync(
        SetPayrollEligibilityResetRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        if (string.IsNullOrWhiteSpace(request.ReasonCode))
        {
            throw new ArgumentException("ReasonCode is verplicht.", nameof(request));
        }

        var candidateConfig = new PayrollEmployeeConfiguration(
            request.ResourceId.Trim(),
            request.ValidFrom,
            request.ValidTo,
            PayrollEligibilityStatus.NeedsDecision,
            request.ReasonCode.Trim(),
            request.Comment?.Trim(),
            PayrollEligibilityDecisionSource.Admin);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await EnsureConfigurationDoesNotOverlapFinalizedPeriodAsync(context, candidateConfig, cancellationToken);

        var existing = await context.PayrollEmployeeConfigurationRecords
            .Where(item => item.ResourceId == candidateConfig.ResourceId)
            .ToListAsync(cancellationToken);
        CloseOpenEndedConfigsBefore(existing, candidateConfig.ValidFrom);
        PayrollEligibilityResolver.EnsureNoOverlap(
            existing.Select(item => item.ToDomain()).ToList(),
            candidateConfig);

        context.PayrollEmployeeConfigurationRecords.Add(new PayrollEmployeeConfigurationRecord
        {
            ResourceId = candidateConfig.ResourceId,
            ValidFrom = candidateConfig.ValidFrom,
            ValidTo = candidateConfig.ValidTo,
            EligibilityStatus = candidateConfig.EligibilityStatus,
            ReasonCode = candidateConfig.ReasonCode,
            Comment = candidateConfig.Comment,
            DecisionSource = candidateConfig.DecisionSource,
            CreatedAtUtc = timeProvider.GetUtcNow(),
            CreatedBy = actor.Trim(),
        });
        await AppendAuditAsync(
            context,
            shadowMonthId: 0,
            request.ResourceId,
            PayrollShadowAuditAction.EligibilityReset,
            actor,
            request.ReasonCode,
            request.Comment);
        await ApplyEligibilityToOpenSnapshotsAsync(context, candidateConfig, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PayrollShadowReviewAudit>> GetAuditTrailAsync(
        int year,
        int month,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadowMonth is null)
        {
            return [];
        }

        var query = context.PayrollShadowReviewAudits.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id);
        if (!string.IsNullOrWhiteSpace(resourceId))
        {
            query = query.Where(item => item.ResourceId == resourceId);
        }

        var rows = await query.ToListAsync(cancellationToken);
        return rows.OrderByDescending(item => item.TimestampUtc).ToList();
    }

    public async Task<PayrollRosterPage> GetPayrollRosterAsync(
        PayrollRosterFilter filter,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var asOf = filter.AsOfDate ?? DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var configurations = (await LoadConfigurationsAsync(context, cancellationToken))
            .Select(item => item.ToDomain())
            .ToList();
        var allResources = await resourceReader.ReadCandidatesAsync(cancellationToken);
        var task23ResourceIds = await ResolveProjectLeiderTask23ResourceIdsAsync(
            allResources,
            asOf,
            asOf,
            cancellationToken);
        var autoIds = LegacyPayrollAutoCandidateSelector
            .SelectAutoCandidates(allResources, asOf, task23ResourceIds)
            .Select(item => item.ResourceId)
            .ToHashSet(StringComparer.Ordinal);

        var rosterResourceIds = new HashSet<string>(autoIds, StringComparer.Ordinal);
        foreach (var config in configurations.Where(item => item.IsActiveFor(asOf, asOf)))
        {
            rosterResourceIds.Add(config.ResourceId);
        }

        var rows = new List<PayrollRosterRow>();
        foreach (var resource in allResources
                     .Where(item => rosterResourceIds.Contains(item.ResourceId))
                     .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var resolution = PayrollEligibilityResolver.Resolve(resource, asOf, asOf, configurations);
            var activeConfig = configurations
                .Where(item => item.ResourceId == resource.ResourceId && item.IsActiveFor(asOf, asOf))
                .SingleOrDefault();
            var autoSuggested = autoIds.Contains(resource.ResourceId);
            var source = ResolveRosterSource(resolution, autoSuggested);
            var onPayrollSuggested = resolution.HasExplicitConfiguration
                ? resolution.EligibilityStatus == PayrollEligibilityStatus.Included
                : autoSuggested;
            var row = new PayrollRosterRow(
                resource.ResourceId,
                resource.DisplayName,
                resource.Function,
                autoSuggested,
                onPayrollSuggested,
                resolution.EligibilityStatus,
                resolution.HasExplicitConfiguration,
                source,
                resource.AcertaIdentityStatus,
                activeConfig?.ValidFrom,
                activeConfig?.ValidTo,
                activeConfig?.ReasonCode ?? resolution.EligibilityReason,
                activeConfig?.Comment,
                LegacyPayrollNameMarkers.IsLegacyOaMarker(resource.DisplayName),
                LegacyPayrollNameMarkers.IsLegacyStagiairMarker(resource.DisplayName));

            if (!MatchesRosterFilter(row, filter))
            {
                continue;
            }

            rows.Add(row);
        }

        return new PayrollRosterPage(
            asOf,
            rows,
            rows.Count(item => item.AutoSuggested),
            rows.Count(item => item.EffectiveEligibility == PayrollEligibilityStatus.Included),
            rows.Count(item => item.EffectiveEligibility == PayrollEligibilityStatus.Excluded),
            rows.Count(item => item.EffectiveEligibility == PayrollEligibilityStatus.NeedsDecision));
    }

    public async Task ConfirmPayrollRosterSelectionAsync(
        ConfirmPayrollRosterSelectionRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ReasonCode))
        {
            throw new ArgumentException("ReasonCode is verplicht.", nameof(request));
        }

        var included = (request.IncludedResourceIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var excluded = (request.ExcludedResourceIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (included.Intersect(excluded, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("Een resource kan niet tegelijk Included en Excluded zijn.");
        }

        foreach (var resourceId in included)
        {
            await SetEligibilityAsync(
                new SetPayrollEligibilityRequest(
                    resourceId,
                    request.ValidFrom,
                    null,
                    PayrollEligibilityStatus.Included,
                    request.ReasonCode.Trim(),
                    request.Comment),
                actor,
                cancellationToken);
        }

        foreach (var resourceId in excluded)
        {
            await SetEligibilityAsync(
                new SetPayrollEligibilityRequest(
                    resourceId,
                    request.ValidFrom,
                    null,
                    PayrollEligibilityStatus.Excluded,
                    request.ReasonCode.Trim(),
                    request.Comment),
                actor,
                cancellationToken);
        }
    }

    public async Task AddManualPayrollEmployeeAsync(
        AddManualPayrollEmployeeRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        ValidateActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ResourceId))
        {
            throw new ArgumentException("ResourceId is verplicht.", nameof(request));
        }

        var reason = string.IsNullOrWhiteSpace(request.ReasonCode)
            ? "ManualPayrollInclusion"
            : request.ReasonCode.Trim();

        var allResources = await resourceReader.ReadCandidatesAsync(cancellationToken);
        if (!allResources.Any(item => string.Equals(item.ResourceId, request.ResourceId.Trim(), StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Resource {request.ResourceId} is niet gevonden in Plenion.");
        }

        await SetEligibilityAsync(
            new SetPayrollEligibilityRequest(
                request.ResourceId.Trim(),
                request.ValidFrom,
                request.ValidTo,
                PayrollEligibilityStatus.Included,
                reason,
                request.Comment),
            actor,
            cancellationToken);
    }

    private async Task<IReadOnlySet<string>> ResolveProjectLeiderTask23ResourceIdsAsync(
        IReadOnlyList<PayrollEmployeeCandidate> resources,
        DateOnly periodStart,
        DateOnly periodEnd,
        CancellationToken cancellationToken)
    {
        var projectLeaderIds = resources
            .Where(item => item.IsActiveForPeriod(periodStart))
            .Where(item => LegacyPayrollTechnicianFunctions.IsProjectLeider(item.Function))
            .Where(item => !LegacyPayrollNameMarkers.IsLegacyOaMarker(item.DisplayName))
            .Where(item => !LegacyPayrollNameMarkers.IsLegacyStagiairMarker(item.DisplayName))
            .Select(item => item.ResourceId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (projectLeaderIds.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var performances = await performanceSource.ReadPerformancesAsync(
            periodStart,
            periodEnd,
            projectLeaderIds,
            cancellationToken);
        return performances
            .Where(item => item.HfdTaakId == LegacyPayrollPerformanceEligibility.ProjectLeiderIncludedHfdTaakId)
            .Select(item => item.ResourceId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static PayrollRosterSource ResolveRosterSource(
        PayrollEligibilityResolution resolution,
        bool autoSuggested)
    {
        if (!resolution.HasExplicitConfiguration)
        {
            return PayrollRosterSource.AutoProposal;
        }

        return resolution.EligibilityStatus switch
        {
            PayrollEligibilityStatus.Included => PayrollRosterSource.ManualIncluded,
            PayrollEligibilityStatus.Excluded => PayrollRosterSource.ManualExcluded,
            _ => PayrollRosterSource.AutoProposal,
        };
    }

    private static bool MatchesRosterFilter(PayrollRosterRow row, PayrollRosterFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            var haystack = $"{row.DisplayName} {row.Function} {row.ResourceId}";
            if (haystack.Contains(search, StringComparison.OrdinalIgnoreCase) == false)
            {
                return false;
            }
        }

        return filter.Kind switch
        {
            PayrollRosterFilterKind.All => true,
            PayrollRosterFilterKind.AutoProposed => row.AutoSuggested,
            PayrollRosterFilterKind.Included => row.EffectiveEligibility == PayrollEligibilityStatus.Included,
            PayrollRosterFilterKind.Excluded => row.EffectiveEligibility == PayrollEligibilityStatus.Excluded,
            PayrollRosterFilterKind.NeedsDecision => row.EffectiveEligibility == PayrollEligibilityStatus.NeedsDecision,
            PayrollRosterFilterKind.ManualExtras =>
                row.HasExplicitConfiguration
                && row.EffectiveEligibility == PayrollEligibilityStatus.Included
                && !row.AutoSuggested,
            PayrollRosterFilterKind.MissingAcerta => row.AcertaIdentityStatus == AcertaIdentityStatus.Missing,
            _ => true,
        };
    }

    private void EnsureEnabled()
    {
        options.Value.Validate();
        if (!options.Value.Enabled)
        {
            throw new InvalidOperationException("Payroll shadow is uitgeschakeld.");
        }
    }

    private static void ValidateActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new InvalidOperationException("Actor is verplicht.");
        }
    }

    private static async Task<PayrollShadowMonth> RequireMutableMonthAsync(
        TimeControlDbContext context,
        int year,
        int month,
        CancellationToken cancellationToken) =>
        await context.PayrollShadowMonths.SingleOrDefaultAsync(item =>
            item.Year == year && item.Month == month, cancellationToken)
        ?? throw new InvalidOperationException("Shadow-maand niet gevonden.");

    private static void CloseOpenEndedConfigsBefore(
        IReadOnlyList<PayrollEmployeeConfigurationRecord> existing,
        DateOnly nextValidFrom)
    {
        var closeThrough = nextValidFrom.AddDays(-1);
        foreach (var item in existing.Where(item =>
                     item.ValidTo is null && item.ValidFrom < nextValidFrom))
        {
            if (closeThrough < item.ValidFrom)
            {
                throw new InvalidOperationException(
                    $"Kan openstaande payrollconfiguratie voor resource {item.ResourceId} niet afsluiten vóór {nextValidFrom:yyyy-MM-dd}.");
            }

            item.ValidTo = closeThrough;
        }
    }

    private static async Task EnsureConfigurationDoesNotOverlapFinalizedPeriodAsync(
        TimeControlDbContext context,
        PayrollEmployeeConfiguration candidate,
        CancellationToken cancellationToken)
    {
        var candidateEnd = candidate.ValidTo ?? DateOnly.MaxValue;
        var finalizedMonths = await context.PayrollShadowMonths.AsNoTracking()
            .Where(item => item.Status == PayrollShadowMonthStatus.Finalized)
            .ToListAsync(cancellationToken);
        foreach (var month in finalizedMonths)
        {
            if (candidate.ValidFrom <= month.PeriodEnd && candidateEnd >= month.PeriodStart)
            {
                throw new InvalidOperationException(
                    "Eligibility-wijziging overlapt een afgesloten shadow-maand.");
            }
        }
    }

    private static void ValidateFinalization(
        PayrollShadowMonth shadowMonth,
        IReadOnlyList<PayrollShadowEmployeeResult> employees)
    {
        if (shadowMonth.Status != PayrollShadowMonthStatus.InReview
            && shadowMonth.Status != PayrollShadowMonthStatus.ReadyForReview)
        {
            throw new InvalidOperationException("Shadow-maand is niet klaar om af te sluiten.");
        }

        var included = employees
            .Where(item => item.EligibilityStatus == PayrollEligibilityStatus.Included)
            .ToList();
        if (included.Count == 0)
        {
            throw new InvalidOperationException(
                "Afsluiten vereist minstens één Included medewerker.");
        }

        foreach (var employee in included)
        {
            if (employee.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision)
            {
                throw new InvalidOperationException(
                    $"Included medewerker {employee.ResourceId} heeft nog NeedsDecision eligibility.");
            }

            if (employee.ReviewStatus is PayrollEmployeeReviewStatus.Pending
                or PayrollEmployeeReviewStatus.NeedsFollowUp)
            {
                throw new InvalidOperationException(
                    $"Included medewerker {employee.DisplayNameSnapshot} heeft open reviewstatus.");
            }

            if (employee.AcertaIdentityStatus == AcertaIdentityStatus.Missing)
            {
                throw new InvalidOperationException(
                    $"Included medewerker {employee.DisplayNameSnapshot} mist Acerta-identiteit.");
            }

            EnsureCalculated(employee.OrdinaryStatus, "ordinary", employee.DisplayNameSnapshot);
            EnsureCalculated(employee.StandbyStatus, "standby", employee.DisplayNameSnapshot);
            EnsureCalculated(employee.CityStatus, "city", employee.DisplayNameSnapshot);
            EnsureCalculated(employee.KmStatus, "KM", employee.DisplayNameSnapshot);
            EnsureCalculated(employee.Code414Status, "code414", employee.DisplayNameSnapshot);
        }
    }

    private static void EnsureCalculated(
        PayrollMonthCalculationStatus status,
        string component,
        string displayName)
    {
        if (status != PayrollMonthCalculationStatus.Calculated)
        {
            throw new InvalidOperationException(
                $"Included medewerker {displayName} heeft incomplete berekening ({component}).");
        }
    }

    private static async Task<List<PayrollEmployeeConfigurationRecord>> LoadConfigurationsAsync(
        TimeControlDbContext context,
        CancellationToken cancellationToken) =>
        await context.PayrollEmployeeConfigurationRecords.AsNoTracking().ToListAsync(cancellationToken);

    private static PayrollShadowEmployeeResult MapEmployeeResult(
        int shadowMonthId,
        PayrollEmployeeCandidate candidate,
        PayrollEligibilityResolution resolution,
        PayrollMonthShadowResult? calculated,
        PayrollEmployeeReviewStatus reviewStatus) =>
        new()
        {
            ShadowMonthId = shadowMonthId,
            ResourceId = candidate.ResourceId,
            DisplayNameSnapshot = candidate.DisplayName,
            ResourceCodeSnapshot = candidate.ResourceCode,
            EmailSnapshot = candidate.Email,
            EligibilityStatus = resolution.EligibilityStatus,
            EligibilityReason = resolution.EligibilityReason,
            SuggestedEligibility = resolution.SuggestedEligibility,
            SuggestedReason = resolution.SuggestedReason,
            LegacyTheoreticalHours = calculated?.LegacyTheoreticalHours,
            LegacyActualOrdinaryHours = calculated?.LegacyActualOrdinaryHours,
            LegacyDifferenceHours = calculated?.LegacyDifferenceHours,
            StandbyExactHours = calculated?.StandbyExactHours,
            StandbyRoundedHours = calculated?.StandbyRoundedHours,
            Code135At150Units = calculated?.Code135At150?.CalculatedUnits,
            Code135At200Units = calculated?.Code135At200?.CalculatedUnits,
            CityTripUnits = calculated?.CityTripUnits,
            CityAllowanceAmount = calculated?.CityAllowanceAmount,
            EligibleKm = calculated?.EligibleKm,
            Extra75LegacyValue = calculated?.Extra75YtdHours,
            KmRate = calculated?.KmRate,
            KmAmount = calculated?.KmAmount,
            Code414Amount = calculated?.Code414Amount,
            AcertaIdentityStatus = candidate.AcertaIdentityStatus,
            OrdinaryStatus = calculated?.OrdinaryStatus ?? PayrollMonthCalculationStatus.NotCalculated,
            StandbyStatus = calculated?.StandbyStatus ?? PayrollMonthCalculationStatus.NotCalculated,
            CityStatus = calculated?.CityStatus ?? PayrollMonthCalculationStatus.NotCalculated,
            KmStatus = calculated?.KmStatus ?? PayrollMonthCalculationStatus.NotCalculated,
            Code414Status = calculated?.Code414Status ?? PayrollMonthCalculationStatus.NotCalculated,
            ReviewStatus = reviewStatus,
        };

    private static PayrollShadowEmployeeRow MapEmployeeRow(
        PayrollShadowEmployeeResult employee,
        List<PayrollFindingRecord> findings)
    {
        var highest = findings.Count == 0
            ? (PayrollFindingSeverity?)null
            : findings.Max(item => item.Severity);
        return new(
            employee.ResourceId,
            employee.DisplayNameSnapshot,
            employee.EligibilityStatus,
            employee.SuggestedEligibility,
            employee.ReviewStatus,
            employee.LegacyTheoreticalHours,
            employee.LegacyActualOrdinaryHours,
            employee.LegacyDifferenceHours,
            employee.StandbyRoundedHours,
            employee.CityAllowanceAmount,
            employee.KmAmount,
            employee.Code414Amount,
            employee.AcertaIdentityStatus,
            findings.Count,
            highest);
    }

    private static IEnumerable<PayrollShadowEmployeeResult> ApplyFilter(
        IEnumerable<PayrollShadowEmployeeResult> employees,
        Dictionary<string, List<PayrollFindingRecord>> findingsByResource,
        PayrollShadowEmployeeFilter filter)
    {
        var query = employees;
        if (filter.Eligibility is not null)
        {
            query = query.Where(item => item.EligibilityStatus == filter.Eligibility);
        }
        else if (filter.HideExcluded)
        {
            query = query.Where(item => item.EligibilityStatus != PayrollEligibilityStatus.Excluded);
        }

        if (filter.Review is not null)
        {
            query = query.Where(item => item.ReviewStatus == filter.Review);
        }

        if (filter.NeedsDecisionOnly)
        {
            query = query.Where(item => item.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision);
        }

        if (filter.NeedsFollowUpOnly)
        {
            query = query.Where(item => item.ReviewStatus == PayrollEmployeeReviewStatus.NeedsFollowUp);
        }

        if (filter.MissingAcertaIdentityOnly)
        {
            query = query.Where(item => item.AcertaIdentityStatus == AcertaIdentityStatus.Missing);
        }

        if (filter.NegativeDifferenceOnly)
        {
            query = query.Where(item => item.LegacyDifferenceHours < 0m);
        }

        if (filter.NonzeroStandbyOnly)
        {
            query = query.Where(item => item.StandbyRoundedHours.GetValueOrDefault() != 0m);
        }

        if (filter.LargeAbsoluteDifferenceOnly)
        {
            query = query.Where(item => Math.Abs(item.LegacyDifferenceHours.GetValueOrDefault()) >= 8m);
        }

        if (filter.HasFindingsOnly)
        {
            query = query.Where(item => findingsByResource.ContainsKey(item.ResourceId));
        }

        if (filter.HighFindingsOnly)
        {
            query = query.Where(item =>
                findingsByResource.TryGetValue(item.ResourceId, out var rows)
                && rows.Any(finding => finding.Severity == PayrollFindingSeverity.High));
        }

        if (filter.FindingType is not null)
        {
            query = query.Where(item =>
                findingsByResource.TryGetValue(item.ResourceId, out var rows)
                && rows.Any(finding => finding.FindingType == filter.FindingType));
        }

        if (!string.IsNullOrWhiteSpace(filter.FindingFamily))
        {
            query = query.Where(item =>
                findingsByResource.TryGetValue(item.ResourceId, out var rows)
                && rows.Any(finding => MatchesFindingFamily(finding.FindingType, filter.FindingFamily)));
        }

        return query;
    }

    private static bool MatchesFindingFamily(PayrollFindingType type, string family) =>
        family.Trim().ToUpperInvariant() switch
        {
            "PROJECT100" => type is PayrollFindingType.Project100TrainingHours
                or PayrollFindingType.Project100TrainingInOvertime
                or PayrollFindingType.Project100ExceedsPlannedDuration,
            "PROJECT200" => type is PayrollFindingType.Project200WithoutPlanning
                or PayrollFindingType.Project200ExceedsPlanning,
            "PROJECT300" => type is PayrollFindingType.Project300WithoutPlanning,
            "OVERLAP" => type is PayrollFindingType.OverlappingPerformances,
            "STANDBY" => type is PayrollFindingType.StandbyPhoneExceeds15Min
                or PayrollFindingType.StandbyStartMismatch
                or PayrollFindingType.StandbyEndMismatch
                or PayrollFindingType.StandbyDurationMismatch
                or PayrollFindingType.StandbyPossibleWrongDossier
                or PayrollFindingType.StandbyAmbiguousEvidence
                or PayrollFindingType.StandbyNoGpsData,
            _ => false,
        };

    private async Task<SnapshotMaterial> MaterializeSnapshotAsync(
        TimeControlDbContext context,
        PayrollPeriodSnapshot period,
        CancellationToken cancellationToken)
    {
        var configurations = await LoadConfigurationsAsync(context, cancellationToken);
        var configurationDomains = configurations.Select(item => item.ToDomain()).ToList();
        var allResources = await resourceReader.ReadCandidatesAsync(cancellationToken);
        var task23ResourceIds = await ResolveProjectLeiderTask23ResourceIdsAsync(
            allResources,
            period.PeriodStart,
            period.PeriodEnd,
            cancellationToken);
        var candidates = LegacyPayrollAutoCandidateSelector.SelectSnapshotCandidates(
            allResources,
            period.PeriodStart,
            period.PeriodEnd,
            task23ResourceIds,
            configurationDomains);
        var activeCandidates = candidates.Where(item => item.IsActiveForPeriod(period.PeriodStart)).ToList();
        var resourceIds = activeCandidates.Select(item => item.ResourceId).ToArray();

        var performances = resourceIds.Length == 0
            ? []
            : await performanceSource.ReadPerformancesAsync(
                period.PeriodStart,
                period.PeriodEnd,
                resourceIds,
                cancellationToken);
        var planning = resourceIds.Length == 0
            ? []
            : await planningSource.ReadWorkReservationsAsync(
                period.PeriodStart,
                period.PeriodEnd,
                resourceIds,
                cancellationToken);
        var planningQueryCount = planningSource is PlenionPayrollPlanningReader planningReader
            ? planningReader.LastQueryCount
            : 1;
        var standbyPairs = performances
            .Where(item => !item.IsCalendarSynthetic && (item.IsStandby || item.HfdTaakId == 23))
            .Select(item => (item.ResourceId, item.Date))
            .Distinct()
            .ToArray();
        var standbyGps = standbyPairs.Length == 0
            ? new StandbyGpsBatchResult([], 0, 0, 0, 0, 0, "No standby rows.")
            : await standbyGpsSource.ReadStandbyGpsAsync(
                period.PeriodStart,
                period.PeriodEnd,
                standbyPairs,
                cancellationToken);
        var calendarRows = await calendarSource.ReadCalendarRowsAsync(
            period.PeriodStart,
            period.PeriodEnd,
            cancellationToken);
        var synthetic = LegacyCalendarSynthesis.Synthesize(
            calendarRows,
            period.PeriodStart,
            period.PeriodEnd,
            resourceIds.ToHashSet(StringComparer.Ordinal));

        var kmConfiguration = PayrollShadowConfigurationSnapshot.ResolveKmConfiguration(period);
        var cityConfiguration = PayrollShadowConfigurationSnapshot.CreateCityConfiguration(period);
        var configurationSnapshotJson = PayrollShadowConfigurationSnapshot.Build(
            period,
            kmConfiguration,
            cityConfiguration,
            configurationDomains.Count,
            PayrollShadowConfigurationSnapshot.ComputeEligibilityHash(configurationDomains));

        var rows = new List<PendingEmployeeResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var resolution = PayrollEligibilityResolver.Resolve(
                candidate,
                period.PeriodStart,
                period.PeriodEnd,
                configurationDomains);
            PayrollMonthShadowResult? calculated = null;
            if (candidate.IsActiveForPeriod(period.PeriodStart))
            {
                calculated = calculationService.Calculate(
                    period,
                    candidate.ResourceId,
                    candidate.Function,
                    performances,
                    synthetic);
            }

            var reviewStatus = resolution.EligibilityStatus == PayrollEligibilityStatus.Excluded
                ? PayrollEmployeeReviewStatus.ExcludedFromPayroll
                : PayrollEmployeeReviewStatus.Pending;

            rows.Add(new PendingEmployeeResult(candidate, resolution, calculated, reviewStatus));
        }

        var legacyDiff = rows.ToDictionary(
            item => item.Candidate.ResourceId,
            item => item.Calculated?.LegacyDifferenceHours,
            StringComparer.Ordinal);
        var findingsRun = PayrollFindingsEngine.Evaluate(
            performances,
            planning,
            legacyDiff,
            planningQueryCount,
            standbyGps.Days,
            standbyGps.ApiCallCount,
            standbyGps.Notes);

        return new SnapshotMaterial(configurationSnapshotJson, rows, findingsRun);
    }

    private void AddEmployeeResults(
        TimeControlDbContext context,
        int shadowMonthId,
        SnapshotMaterial material)
    {
        foreach (var row in material.Rows)
        {
            context.PayrollShadowEmployeeResults.Add(MapEmployeeResult(
                shadowMonthId,
                row.Candidate,
                row.Resolution,
                row.Calculated,
                row.ReviewStatus));
        }
    }

    private static void ReplaceFindings(
        TimeControlDbContext context,
        int shadowMonthId,
        SnapshotMaterial material)
    {
        foreach (var finding in material.Findings.Findings)
        {
            context.PayrollFindingRecords.Add(PayrollFindingsEngine.ToRecord(shadowMonthId, finding));
        }
    }

    private static int FindingCount(
        Dictionary<string, List<PayrollFindingRecord>> findingsByResource,
        string resourceId) =>
        findingsByResource.TryGetValue(resourceId, out var rows) ? rows.Count : 0;

    private static int HighestSeverity(
        Dictionary<string, List<PayrollFindingRecord>> findingsByResource,
        string resourceId) =>
        findingsByResource.TryGetValue(resourceId, out var rows) && rows.Count > 0
            ? rows.Max(item => (int)item.Severity)
            : -1;

    private sealed record PendingEmployeeResult(
        PayrollEmployeeCandidate Candidate,
        PayrollEligibilityResolution Resolution,
        PayrollMonthShadowResult? Calculated,
        PayrollEmployeeReviewStatus ReviewStatus);

    private sealed record SnapshotMaterial(
        string ConfigurationSnapshotJson,
        IReadOnlyList<PendingEmployeeResult> Rows,
        PayrollFindingsRunResult Findings);

    private async Task<PayrollShadowMonthSummary> BuildSummaryAsync(
        TimeControlDbContext context,
        PayrollShadowMonth month,
        CancellationToken cancellationToken)
    {
        var employees = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .Where(item => item.ShadowMonthId == month.Id)
            .ToListAsync(cancellationToken);
        var included = employees
            .Where(item => item.EligibilityStatus == PayrollEligibilityStatus.Included)
            .ToList();
        return new PayrollShadowMonthSummary(
            month.Year,
            month.Month,
            month.Status,
            month.CreatedAtUtc,
            month.EvaluationDate,
            month.CalculationVersion,
            employees.Count,
            included.Count,
            employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.Excluded),
            employees.Count(item => item.EligibilityStatus == PayrollEligibilityStatus.NeedsDecision),
            included.Count(item => item.ReviewStatus == PayrollEmployeeReviewStatus.Pending),
            included.Count(item => item.ReviewStatus == PayrollEmployeeReviewStatus.NeedsFollowUp),
            included.Count(item => item.ReviewStatus == PayrollEmployeeReviewStatus.Accepted));
    }

    private static async Task ApplyEligibilityToOpenSnapshotsAsync(
        TimeControlDbContext context,
        PayrollEmployeeConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var openMonths = await context.PayrollShadowMonths
            .Where(item => item.Status != PayrollShadowMonthStatus.Finalized)
            .ToListAsync(cancellationToken);
        foreach (var month in openMonths)
        {
            if (!configuration.IsActiveFor(month.PeriodStart, month.PeriodEnd))
            {
                continue;
            }

            var employee = await context.PayrollShadowEmployeeResults.SingleOrDefaultAsync(item =>
                item.ShadowMonthId == month.Id && item.ResourceId == configuration.ResourceId, cancellationToken);
            if (employee is null)
            {
                continue;
            }

            employee.EligibilityStatus = configuration.EligibilityStatus;
            employee.EligibilityReason = configuration.ReasonCode;
            employee.ReviewStatus = configuration.EligibilityStatus switch
            {
                PayrollEligibilityStatus.Excluded => PayrollEmployeeReviewStatus.ExcludedFromPayroll,
                PayrollEligibilityStatus.Included when employee.ReviewStatus
                    == PayrollEmployeeReviewStatus.ExcludedFromPayroll =>
                    PayrollEmployeeReviewStatus.Pending,
                _ => employee.ReviewStatus,
            };
        }
    }

    private async Task AppendAuditAsync(
        TimeControlDbContext context,
        int shadowMonthId,
        string? resourceId,
        PayrollShadowAuditAction action,
        string actor,
        string? reasonCode,
        string? comment)
    {
        context.PayrollShadowReviewAudits.Add(new PayrollShadowReviewAudit
        {
            ShadowMonthId = shadowMonthId,
            ResourceId = resourceId,
            Action = action,
            Actor = actor.Trim(),
            TimestampUtc = timeProvider.GetUtcNow(),
            ReasonCode = reasonCode,
            Comment = comment,
        });
        await Task.CompletedTask;
    }
}
