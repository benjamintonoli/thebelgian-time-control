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
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

internal sealed class PayrollIntelligenceWorkbenchService(
    IPayrollReviewQueueService reviewQueueService,
    IPayrollPerformanceSource performanceSource,
    IPayrollStandbyGpsSource gpsSource,
    PlenionPayrollReader plenionReader,
    PayrollIntelligenceQueueCache queueCache,
    PayrollIntelligenceDayCache dayCache,
    PayrollIntelligenceGpsCache gpsCache,
    PayrollIntelligenceGpsContextCache gpsContextCache,
    PayrollIntelligenceHfdCache hfdCache,
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IOptions<PayrollShadowOptions> shadowOptions,
    TimeProvider timeProvider,
    ILogger<PayrollIntelligenceWorkbenchService> logger) : IPayrollIntelligenceWorkbenchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PayrollIntelligenceWorkbenchPage> GetShellAsync(
        int year,
        int month,
        PayrollReviewCategory category,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        EnsureCategory(category);
        var scoped = filter with { Category = category };
        var queue = await GetAdminQueueCachedAsync(year, month, scoped, cancellationToken);
        var selected = ResolveSelected(queue.AdminCases, selectedAdminCaseKey);
        return new PayrollIntelligenceWorkbenchPage(
            year,
            month,
            category,
            queue.AdminSummary,
            queue.AdminCases,
            selected?.AdminCaseKey,
            Detail: null,
            GpsDeferred: true);
    }

    public async Task<PayrollIntelligenceWorkbenchPage> GetCoreDetailAsync(
        int year,
        int month,
        PayrollReviewCategory category,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        EnsureCategory(category);
        var scoped = filter with { Category = category };
        var queue = await GetAdminQueueCachedAsync(year, month, scoped, cancellationToken);
        var selected = ResolveSelected(queue.AdminCases, selectedAdminCaseKey);
        if (selected is null)
        {
            return new PayrollIntelligenceWorkbenchPage(
                year,
                month,
                category,
                queue.AdminSummary,
                queue.AdminCases,
                null,
                null,
                GpsDeferred: true);
        }

        var detail = category == PayrollReviewCategory.Overlap
            ? await BuildOverlapCoreAsync(year, month, selected, cancellationToken)
            : await BuildMissingCoreAsync(year, month, selected, cancellationToken);

        return new PayrollIntelligenceWorkbenchPage(
            year,
            month,
            category,
            queue.AdminSummary,
            queue.AdminCases,
            selected.AdminCaseKey,
            detail,
            GpsDeferred: true);
    }

    public async Task<PayrollIntelligenceGpsLoadResult> GetGpsContextAsync(
        int year,
        int month,
        PayrollReviewCategory category,
        string adminCaseKey,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        EnsureCategory(category);

        if (gpsContextCache.TryGet(adminCaseKey, out var cached) && cached is not null)
        {
            return new PayrollIntelligenceGpsLoadResult(
                adminCaseKey,
                string.Empty,
                default,
                cached,
                CacheHit: true,
                OverlapWithGps: null,
                TravelModeDutch: null,
                GpsSiteSummary: null);
        }

        var queue = await GetAdminQueueCachedAsync(
            year,
            month,
            new PayrollReviewQueueFilter(category, Scope: PayrollReviewQueueScope.All),
            cancellationToken);
        var selected = FindCase(queue.AdminCases, adminCaseKey);
        if (selected is null)
        {
            var missing = new PayrollIntelligenceGpsContext(
                false,
                false,
                PayrollIntelligenceWorkbenchBuilder.NoGpsSummary,
                []);
            return new PayrollIntelligenceGpsLoadResult(
                adminCaseKey, string.Empty, default, missing, false, null, null, null);
        }

        var (evidence, cacheHit) = await LoadGpsAsync(
            selected.ResourceId,
            selected.DisplayName ?? selected.ResourceId,
            selected.Date,
            cancellationToken);
        var context = PayrollIntelligenceWorkbenchBuilder.BuildGpsContext(evidence, gpsPending: false);
        gpsContextCache.Set(adminCaseKey, context);

        OverlapAnalysis? overlap = null;
        string? travelNl = null;
        string? siteSummary = null;
        if (category == PayrollReviewCategory.Overlap)
        {
            var day = await LoadDayAsync(selected.ResourceId, selected.Date, cancellationToken);
            var finding = await LoadPrimaryFindingAsync(year, month, selected, cancellationToken);
            var related = finding is null
                ? []
                : PayrollIntelligenceWorkbenchBuilder.ParseRelatedIds(finding.RelatedPerformanceIdsJson);
            if (related.Count == 0)
            {
                related = selected.Performances
                    .Where(item => item.PerformanceId is > 0)
                    .Select(item => item.PerformanceId!.Value)
                    .Take(2)
                    .ToArray();
            }

            overlap = PayrollIntelligenceWorkbenchBuilder.AnalyzeFromDay(day, related, evidence);
        }
        else
        {
            var finding = await LoadPrimaryFindingAsync(year, month, selected, cancellationToken);
            if (finding is not null)
            {
                travelNl = MissingTechnicianControl.TravelModeDutch(
                    PayrollIntelligenceWorkbenchBuilder.ParseTravelMode(finding.Evidence));
                var peerRows = await LoadPeerRowsAsync(finding, cancellationToken);
                var peer = peerRows.Count > 0 ? peerRows[0] : null;
                var derived = MissingTechnicianControl.TryDeriveSitePresence(
                    new PlannedWorkGroup(
                        finding.Date,
                        0,
                        finding.SuggestedProjectId,
                        null,
                        finding.SuggestedPayableStart is { } s
                            ? TimeOnly.FromTimeSpan(s.TimeOfDay)
                            : null,
                        finding.SuggestedPayableEnd is { } e
                            ? TimeOnly.FromTimeSpan(e.TimeOfDay)
                            : null,
                        null,
                        [finding.ResourceId],
                        []),
                    evidence,
                    peer);
                if (derived.Arrival is not null)
                {
                    siteSummary = derived.Departure is null
                        ? $"Aankomst {derived.Arrival:HH:mm} (vertrek onbekend)"
                        : $"Werf {derived.Arrival:HH:mm}–{derived.Departure:HH:mm}";
                }
            }
        }

        return new PayrollIntelligenceGpsLoadResult(
            selected.AdminCaseKey,
            selected.ResourceId,
            selected.Date,
            context,
            cacheHit,
            overlap,
            travelNl,
            siteSummary);
    }

    public PayrollIntelligenceGpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate)
    {
        if (!gpsCache.TryGet(resourceId, workDate, out var evidence, out var hit) || !hit)
        {
            return PayrollIntelligenceGpsCacheHint.Unknown;
        }

        return evidence is null || !evidence.HasUsableTrips
            ? PayrollIntelligenceGpsCacheHint.Unavailable
            : PayrollIntelligenceGpsCacheHint.Available;
    }

    public void InvalidateQueueCache(int year, int month) =>
        queueCache.InvalidateMonth(year, month);

    public async Task<PayrollIntelligenceProposeResult> ProposeAdjustAsync(
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
            return new PayrollIntelligenceProposeResult(false, "Reden is verplicht.", null, null);
        }

        if (newEnd <= newStart)
        {
            return new PayrollIntelligenceProposeResult(
                false, "Voorstelduur moet positief zijn.", null, "NonPositiveDuration");
        }

        var page = await GetCoreDetailAsync(
            year,
            month,
            PayrollReviewCategory.Overlap,
            new PayrollReviewQueueFilter(PayrollReviewCategory.Overlap, Scope: PayrollReviewQueueScope.All),
            adminCaseKey,
            cancellationToken);
        var overlap = page.Detail?.Overlap;
        if (overlap is null || page.Detail is null)
        {
            return new PayrollIntelligenceProposeResult(false, "Overlap-case niet gevonden.", null, null);
        }

        var side = overlap.A.PerformanceId == performanceId
            ? overlap.A
            : overlap.B.PerformanceId == performanceId
                ? overlap.B
                : null;
        if (side is null || !side.AdjustSupported || string.IsNullOrWhiteSpace(side.ActivityType))
        {
            return new PayrollIntelligenceProposeResult(
                false,
                side?.CapabilityMessage ?? "Prestatie niet aanpasbaar.",
                null,
                "UnsupportedActivity");
        }

        if (side.Start is null || side.End is null)
        {
            return new PayrollIntelligenceProposeResult(false, "Huidige VAN/TOT ontbreekt.", null, null);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await RequireOpenShadowAsync(context, year, month, cancellationToken);
        if (shadowMonth is null)
        {
            return new PayrollIntelligenceProposeResult(false, "Shadow-maand niet gevonden of afgesloten.", null, "MonthFinalized");
        }

        var date = page.Detail.AdminCase.Date;
        var actionKey = $"overlap-adjust:{page.Detail.AdminCase.ResourceId}:{date:yyyyMMdd}:{performanceId}";
        var findingKey = FirstFindingKey(page.Detail.AdminCase) ?? actionKey;
        var findingId = FirstFindingId(page.Detail.AdminCase);
        var proposedStart = new DateTimeOffset(date.ToDateTime(newStart), side.Start.Value.Offset);
        var proposedEnd = new DateTimeOffset(date.ToDateTime(newEnd), side.End.Value.Offset);

        var adjust = new PayrollActionAdjustProposal(
            performanceId,
            side.Start.Value,
            side.End.Value,
            proposedStart,
            proposedEnd,
            side.ActivityType,
            side.HfdTaakId);

        var evidence = new PayrollActionEvidenceSnapshot(
            findingKey,
            PayrollFindingType.OverlappingPerformances,
            page.Detail.AdminCase.Severity,
            overlap.Kind.ToString(),
            $"Overlap adjust PerformanceId={performanceId}; kind={overlap.Kind}.",
            "Overlap tijdscorrectie (voorstel)",
            reason.Trim(),
            [performanceId],
            proposedStart,
            proposedEnd,
            (decimal)(proposedEnd - proposedStart).TotalHours,
            side.ProjectId,
            side.BonNr,
            page.Detail.AdminCase.FindingKeys.ToArray(),
            page.Detail.AdminCase.FindingIds.ToArray());

        var actionId = await UpsertProposalAsync(
            context,
            shadowMonth.Id,
            actionKey,
            findingId == 0 ? null : findingId,
            page.Detail.AdminCase.ResourceId,
            PayrollProposedActionType.AdjustExistingPerformanceTime,
            evidence,
            adjust,
            $"overlap-adjust:{performanceId}:{side.Start:O}:{side.End:O}:{side.ActivityType}",
            reason.Trim(),
            actor,
            cancellationToken);

        InvalidateQueueCache(year, month);
        return new PayrollIntelligenceProposeResult(true, "Correctievoorstel opgeslagen (niet uitgevoerd).", actionId, null);
    }

    public async Task<PayrollIntelligenceProposeResult> ProposeDeleteAsync(
        int year,
        int month,
        string adminCaseKey,
        long performanceId,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "Prestatie verwijderen (overlap workbench).";
        }

        var page = await GetCoreDetailAsync(
            year,
            month,
            PayrollReviewCategory.Overlap,
            new PayrollReviewQueueFilter(PayrollReviewCategory.Overlap, Scope: PayrollReviewQueueScope.All),
            adminCaseKey,
            cancellationToken);
        var overlap = page.Detail?.Overlap;
        if (overlap is null || page.Detail is null)
        {
            return new PayrollIntelligenceProposeResult(false, "Overlap-case niet gevonden.", null, null);
        }

        var side = overlap.A.PerformanceId == performanceId
            ? overlap.A
            : overlap.B.PerformanceId == performanceId
                ? overlap.B
                : null;
        if (side is null || !side.DeleteSupported || side.Start is null || side.End is null)
        {
            return new PayrollIntelligenceProposeResult(
                false,
                side?.CapabilityMessage ?? "Prestatie niet verwijderbaar.",
                null,
                "UnsupportedDelete");
        }

        if (string.IsNullOrWhiteSpace(side.ProjectId) || side.HfdTaakId is null or <= 0)
        {
            return new PayrollIntelligenceProposeResult(false, "Project of HFDTAAK ontbreekt.", null, "IncompleteTarget");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await RequireOpenShadowAsync(context, year, month, cancellationToken);
        if (shadowMonth is null)
        {
            return new PayrollIntelligenceProposeResult(false, "Shadow-maand niet gevonden of afgesloten.", null, "MonthFinalized");
        }

        var date = page.Detail.AdminCase.Date;
        var actionKey = $"overlap-delete:{page.Detail.AdminCase.ResourceId}:{date:yyyyMMdd}:{performanceId}";
        var findingKey = FirstFindingKey(page.Detail.AdminCase) ?? actionKey;
        var findingId = FirstFindingId(page.Detail.AdminCase);

        var delete = new PayrollActionDeleteProposal(
            performanceId,
            date,
            side.Start.Value,
            side.End.Value,
            side.AtlHours,
            page.Detail.AdminCase.ResourceId,
            side.ProjectId.Trim(),
            string.IsNullOrWhiteSpace(side.BonNr) ? null : side.BonNr.Trim(),
            side.HfdTaakId,
            side.ActivityType,
            side.Description,
            side.Memo,
            null,
            ProjectLabel: "overlap");

        var evidence = new PayrollActionEvidenceSnapshot(
            findingKey,
            PayrollFindingType.OverlappingPerformances,
            page.Detail.AdminCase.Severity,
            overlap.Kind.ToString(),
            $"Overlap delete PerformanceId={performanceId}; kind={overlap.Kind}.",
            "Overlap prestatie verwijderen (voorstel)",
            reason.Trim(),
            [performanceId],
            side.Start,
            side.End,
            side.AtlHours,
            side.ProjectId,
            side.BonNr,
            page.Detail.AdminCase.FindingKeys.ToArray(),
            page.Detail.AdminCase.FindingIds.ToArray());

        var actionId = await UpsertProposalAsync(
            context,
            shadowMonth.Id,
            actionKey,
            findingId == 0 ? null : findingId,
            page.Detail.AdminCase.ResourceId,
            PayrollProposedActionType.DeleteExistingPerformance,
            evidence,
            delete,
            $"overlap-delete:{performanceId}:{side.Start:O}:{side.End:O}:{side.ProjectId}",
            reason.Trim(),
            actor,
            cancellationToken);

        InvalidateQueueCache(year, month);
        return new PayrollIntelligenceProposeResult(true, "Verwijderingsvoorstel opgeslagen (niet uitgevoerd).", actionId, null);
    }

    public async Task<PayrollIntelligenceProposeResult> ProposeCreateAsync(
        int year,
        int month,
        string adminCaseKey,
        TimeOnly start,
        TimeOnly endTime,
        string reason,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "Ontbrekende prestatie aanmaken (missing workbench).";
        }

        if (endTime <= start)
        {
            return new PayrollIntelligenceProposeResult(false, "VAN/TOT ongeldig.", null, "NonPositiveDuration");
        }

        var page = await GetCoreDetailAsync(
            year,
            month,
            PayrollReviewCategory.MissingPerformance,
            new PayrollReviewQueueFilter(PayrollReviewCategory.MissingPerformance, Scope: PayrollReviewQueueScope.All),
            adminCaseKey,
            cancellationToken);
        var missing = page.Detail?.Missing;
        if (missing is null || page.Detail is null)
        {
            return new PayrollIntelligenceProposeResult(false, "Missing-case niet gevonden.", null, null);
        }

        if (!missing.CanProposeCreate || missing.SuggestedMainTaskId is null or <= 0
            || string.IsNullOrWhiteSpace(missing.SuggestedProjectId))
        {
            return new PayrollIntelligenceProposeResult(
                false,
                missing.CreateBlockReason ?? "Create niet beschikbaar voor deze case.",
                null,
                "CreateBlocked");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await RequireOpenShadowAsync(context, year, month, cancellationToken);
        if (shadowMonth is null)
        {
            return new PayrollIntelligenceProposeResult(false, "Shadow-maand niet gevonden of afgesloten.", null, "MonthFinalized");
        }

        var date = page.Detail.AdminCase.Date;
        var findingKey = FirstFindingKey(page.Detail.AdminCase)
            ?? $"missing-tech:{page.Detail.AdminCase.ResourceId}:{date:yyyyMMdd}";
        var actionKey = $"missing-create:{page.Detail.AdminCase.ResourceId}:{date:yyyyMMdd}:{findingKey}";
        var findingId = FirstFindingId(page.Detail.AdminCase);
        var offset = TimeSpan.Zero;
        var proposedStart = new DateTimeOffset(date.ToDateTime(start), offset);
        var proposedEnd = new DateTimeOffset(date.ToDateTime(endTime), offset);
        var hours = Math.Round((decimal)(proposedEnd - proposedStart).TotalHours, 2, MidpointRounding.AwayFromZero);

        var create = new PayrollActionCreateProposal(
            page.Detail.AdminCase.ResourceId,
            date,
            proposedStart,
            proposedEnd,
            hours,
            missing.SuggestedProjectId.Trim(),
            string.IsNullOrWhiteSpace(missing.SuggestedBonNr) ? null : missing.SuggestedBonNr.Trim(),
            missing.SuggestedMainTaskId.Value,
            PayrollIntervalSemantics.PayableWork);

        var evidence = new PayrollActionEvidenceSnapshot(
            findingKey,
            PayrollFindingType.MissingPlannedTechnicianPerformance,
            page.Detail.AdminCase.Severity,
            missing.EvidenceClass,
            missing.FindingEvidence,
            "Ontbrekende prestatie aanmaken (voorstel)",
            reason.Trim(),
            missing.PeerPerformanceId is > 0 ? [missing.PeerPerformanceId.Value] : [],
            proposedStart,
            proposedEnd,
            hours,
            missing.SuggestedProjectId,
            missing.SuggestedBonNr,
            page.Detail.AdminCase.FindingKeys.ToArray(),
            page.Detail.AdminCase.FindingIds.ToArray());

        var actionId = await UpsertProposalAsync(
            context,
            shadowMonth.Id,
            actionKey,
            findingId == 0 ? null : findingId,
            page.Detail.AdminCase.ResourceId,
            PayrollProposedActionType.CreateMissingPerformance,
            evidence,
            create,
            $"missing-create:{proposedStart:O}:{proposedEnd:O}:{missing.SuggestedMainTaskId}",
            reason.Trim(),
            actor,
            cancellationToken);

        InvalidateQueueCache(year, month);
        return new PayrollIntelligenceProposeResult(true, "Create-voorstel opgeslagen (niet uitgevoerd).", actionId, null);
    }

    private async Task<PayrollIntelligenceCaseDetail> BuildOverlapCoreAsync(
        int year,
        int month,
        PayrollAdminCase adminCase,
        CancellationToken cancellationToken)
    {
        var day = await LoadDayAsync(adminCase.ResourceId, adminCase.Date, cancellationToken);
        var finding = await LoadPrimaryFindingAsync(year, month, adminCase, cancellationToken);
        var related = finding is null
            ? adminCase.Performances.Where(item => item.PerformanceId is > 0).Select(item => item.PerformanceId!.Value).Take(2).ToArray()
            : PayrollIntelligenceWorkbenchBuilder.ParseRelatedIds(finding.RelatedPerformanceIdsJson);
        if (related.Count < 2)
        {
            related = adminCase.Performances
                .Where(item => item.PerformanceId is > 0)
                .Select(item => item.PerformanceId!.Value)
                .Distinct()
                .Take(2)
                .ToArray();
        }

        gpsCache.TryGet(adminCase.ResourceId, adminCase.Date, out var cachedGps, out var gpsHit);
        var analysis = PayrollIntelligenceWorkbenchBuilder.AnalyzeFromDay(
            day,
            related,
            gpsHit ? cachedGps : null);

        var hfd = await GetHfdAsync(cancellationToken);
        var activities = new Dictionary<long, (string? ActivityType, bool Supported, string Message)>();
        foreach (var perf in new[] { analysis.A, analysis.B }.DistinctBy(item => item.SourceEntryId))
        {
            hfd.TryGetValue(perf.HfdTaakId ?? -1, out var def);
            var resolved = PayrollProject100ActivityResolver.Resolve(perf, def);
            activities[perf.SourceEntryId] = (resolved.ActivityType, resolved.Supported, resolved.Message);
        }

        string? deleteImpact = null;
        string? adjustImpact = null;
        if (analysis.TargetPerformanceId is { } targetId)
        {
            deleteImpact = OverlapPayrollImpactCalculator.ForDelete(day, targetId).SummaryNl;
            if (analysis.ProposedStart is not null && analysis.ProposedEnd is not null)
            {
                adjustImpact = OverlapPayrollImpactCalculator
                    .ForAdjust(day, targetId, analysis.ProposedStart.Value, analysis.ProposedEnd.Value)
                    .SummaryNl;
            }
        }

        return PayrollIntelligenceWorkbenchBuilder.BuildOverlapDetail(
            adminCase,
            analysis,
            activities,
            deleteImpact,
            adjustImpact,
            gpsHit ? cachedGps : null,
            gpsPending: !gpsHit);
    }

    private async Task<PayrollIntelligenceCaseDetail> BuildMissingCoreAsync(
        int year,
        int month,
        PayrollAdminCase adminCase,
        CancellationToken cancellationToken)
    {
        var finding = await LoadPrimaryFindingAsync(year, month, adminCase, cancellationToken);
        if (finding is null)
        {
            finding = new PayrollFindingRecord
            {
                FindingKey = FirstFindingKey(adminCase) ?? adminCase.AdminCaseKey,
                ResourceId = adminCase.ResourceId,
                Date = adminCase.Date,
                FindingType = PayrollFindingType.MissingPlannedTechnicianPerformance,
                Severity = adminCase.Severity,
                Title = adminCase.IssueSummary,
                Description = adminCase.IssueSummary,
                Evidence = adminCase.EvidenceSummary ?? string.Empty,
                SuggestedAction = adminCase.ActionabilityHint,
                GpsClassification = adminCase.FriendlyState,
            };
        }

        var peerRows = await LoadPeerRowsAsync(finding, cancellationToken);
        gpsCache.TryGet(adminCase.ResourceId, adminCase.Date, out var cachedGps, out var gpsHit);
        return PayrollIntelligenceWorkbenchBuilder.BuildMissingDetail(
            adminCase,
            finding,
            peerRows,
            gpsHit ? cachedGps : null,
            gpsPending: !gpsHit);
    }

    private async Task<IReadOnlyList<NormalizedPerformanceEntry>> LoadPeerRowsAsync(
        PayrollFindingRecord finding,
        CancellationToken cancellationToken)
    {
        var related = PayrollIntelligenceWorkbenchBuilder.ParseRelatedIds(finding.RelatedPerformanceIdsJson);
        var resources = new HashSet<string>(StringComparer.Ordinal) { finding.ResourceId };
        var match = System.Text.RegularExpressions.Regex.Match(
            finding.Evidence ?? string.Empty,
            @"peers=\[([^\s#]+)#",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (match.Success)
        {
            resources.Add(match.Groups[1].Value);
        }

        var rows = new List<NormalizedPerformanceEntry>();
        foreach (var resourceId in resources)
        {
            rows.AddRange(await LoadDayAsync(resourceId, finding.Date, cancellationToken));
        }

        if (related.Count > 0)
        {
            return rows.Where(item => related.Contains(item.SourceEntryId) || item.ResourceId != finding.ResourceId)
                .ToList();
        }

        return rows.Where(item => item.ResourceId != finding.ResourceId).ToList();
    }

    private async Task<IReadOnlyList<NormalizedPerformanceEntry>> LoadDayAsync(
        string resourceId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (dayCache.TryGet(resourceId, date, out var cached))
        {
            return cached;
        }

        var rows = await performanceSource.ReadPerformancesAsync(
            date,
            date,
            [resourceId],
            cancellationToken);
        dayCache.Set(resourceId, date, rows);
        return rows;
    }

    private async Task<(StandbyGpsDayEvidence? Evidence, bool CacheHit)> LoadGpsAsync(
        string resourceId,
        string displayName,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (gpsCache.TryGet(resourceId, date, out var cached, out var hit) && hit)
        {
            return (cached, true);
        }

        var batch = await gpsSource.ReadStandbyGpsAsync(
            date,
            date,
            [(resourceId, displayName, date)],
            cancellationToken);
        StandbyGpsDayEvidence? evidence = null;
        foreach (var item in batch.Days)
        {
            if (string.Equals(item.ResourceId, resourceId, StringComparison.Ordinal) && item.Date == date)
            {
                evidence = item;
                break;
            }
        }

        gpsCache.Set(resourceId, date, evidence);
        return (evidence, false);
    }

    private async Task<PayrollFindingRecord?> LoadPrimaryFindingAsync(
        int year,
        int month,
        PayrollAdminCase adminCase,
        CancellationToken cancellationToken)
    {
        // Prefer year/month from caller for shadow lookup when adminCase.Date month differs rarely.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadow = await context.PayrollShadowMonths
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadow is null)
        {
            return null;
        }

        var keys = adminCase.FindingKeys;
        if (keys.Count == 0)
        {
            return null;
        }

        return await context.PayrollFindingRecords
            .AsNoTracking()
            .Where(item => item.ShadowMonthId == shadow.Id && keys.Contains(item.FindingKey))
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<int, HfdTaakDefinition>> GetHfdAsync(CancellationToken cancellationToken)
    {
        if (hfdCache.TryGet(out var cached))
        {
            return cached;
        }

        var defs = await plenionReader.ReadHfdTaakDefinitionsAsync(cancellationToken);
        var map = (IReadOnlyDictionary<int, HfdTaakDefinition>)defs.ToDictionary(item => item.Id);
        hfdCache.Set(map);
        return map;
    }

    private async Task<PayrollAdminQueuePage> GetAdminQueueCachedAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        CancellationToken cancellationToken)
    {
        if (queueCache.TryGet(year, month, filter, out var cached) && cached is not null)
        {
            return cached;
        }

        var page = await reviewQueueService.GetAdminQueueAsync(year, month, filter, cancellationToken);
        queueCache.Set(year, month, filter, page);
        return page;
    }

    private async Task<Guid> UpsertProposalAsync<TProposal>(
        TimeControlDbContext context,
        int shadowMonthId,
        string actionKey,
        int? findingId,
        string resourceId,
        PayrollProposedActionType actionType,
        PayrollActionEvidenceSnapshot evidence,
        TProposal proposal,
        string sourceRevision,
        string comment,
        string actor,
        CancellationToken cancellationToken)
    {
        var existing = await context.PayrollProposedActionRecords
            .Where(item => item.ShadowMonthId == shadowMonthId && item.FindingKey == actionKey)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        if (existing is null
            || existing.Status is PayrollProposedActionStatus.Applied
                or PayrollProposedActionStatus.Cancelled
                or PayrollProposedActionStatus.Failed)
        {
            existing = new PayrollProposedActionRecord
            {
                ActionId = Guid.NewGuid(),
                ShadowMonthId = shadowMonthId,
                FindingKey = actionKey,
                FindingId = findingId,
                ResourceId = resourceId,
                CreatedAtUtc = now,
                CreatedBy = actor,
            };
            context.PayrollProposedActionRecords.Add(existing);
        }

        existing.ActionType = actionType;
        existing.Status = PayrollProposedActionStatus.ReadyForApproval;
        existing.BlockReason = null;
        existing.EvidenceSnapshotJson = JsonSerializer.Serialize(evidence, JsonOptions);
        existing.ProposalSnapshotJson = JsonSerializer.Serialize(proposal, JsonOptions);
        existing.SourceRevision = sourceRevision;
        existing.Comment = comment;
        existing.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Intelligence workbench proposal {ActionId} type={ActionType} key={Key}",
            existing.ActionId,
            actionType,
            actionKey);
        return existing.ActionId;
    }

    private static async Task<PayrollShadowMonth?> RequireOpenShadowAsync(
        TimeControlDbContext context,
        int year,
        int month,
        CancellationToken cancellationToken)
    {
        var shadow = await context.PayrollShadowMonths
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadow is null || shadow.Status == PayrollShadowMonthStatus.Finalized)
        {
            return null;
        }

        return shadow;
    }

    private static string? FirstFindingKey(PayrollAdminCase adminCase) =>
        adminCase.FindingKeys.Count > 0 ? adminCase.FindingKeys[0] : null;

    private static int FirstFindingId(PayrollAdminCase adminCase) =>
        adminCase.FindingIds.Count > 0 ? adminCase.FindingIds[0] : 0;

    private static PayrollAdminCase? ResolveSelected(
        IReadOnlyList<PayrollAdminCase> cases,
        string? selectedAdminCaseKey)
    {
        if (cases.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selectedAdminCaseKey))
        {
            var match = FindCase(cases, selectedAdminCaseKey);
            if (match is not null)
            {
                return match;
            }
        }

        return cases.FirstOrDefault(item => PayrollReviewCategories.IsUnresolved(item.WorkflowStatus))
            ?? cases[0];
    }

    private static PayrollAdminCase? FindCase(IReadOnlyList<PayrollAdminCase> cases, string adminCaseKey) =>
        cases.FirstOrDefault(item => string.Equals(item.AdminCaseKey, adminCaseKey, StringComparison.Ordinal));

    private static void EnsureCategory(PayrollReviewCategory category)
    {
        if (category is not (PayrollReviewCategory.Overlap or PayrollReviewCategory.MissingPerformance))
        {
            throw new InvalidOperationException($"Unsupported intelligence workbench category: {category}");
        }
    }

    private void EnsureEnabled()
    {
        if (!shadowOptions.Value.Enabled)
        {
            throw new InvalidOperationException("Payroll shadow is disabled.");
        }
    }
}
