using System.Collections.Concurrent;
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
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Review;

internal sealed class PayrollProject300WorkbenchService(
    IPayrollReviewQueueService reviewQueueService,
    IPayrollPerformanceSource performanceSource,
    IPayrollPlanningSource planningSource,
    IPayrollStandbyGpsSource gpsSource,
    PlenionPayrollReader plenionReader,
    PayrollProject300GpsCache gpsCache,
    PayrollProject300GpsContextCache gpsContextCache,
    PayrollProject300DaySourceCache daySourceCache,
    PayrollProject300QueueCache queueCache,
    PayrollProject300BonMemoCache bonMemoCache,
    PayrollProject300HfdCache hfdCache,
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IOptions<PayrollShadowOptions> shadowOptions,
    TimeProvider timeProvider,
    ILogger<PayrollProject300WorkbenchService> logger) : IPayrollProject300WorkbenchService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, byte> _prefetchInFlight = new(StringComparer.Ordinal);

    public Task<PayrollProject300WorkbenchPage> GetWorkbenchAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken) =>
        GetCoreDetailAsync(year, month, filter, selectedAdminCaseKey, cancellationToken);

    public async Task<PayrollProject300WorkbenchPage> GetCoreDetailAsync(
        int year,
        int month,
        PayrollReviewQueueFilter filter,
        string? selectedAdminCaseKey,
        CancellationToken cancellationToken)
    {
        EnsureEnabled();
        var scopedFilter = filter with { Category = PayrollReviewCategory.Project300 };
        var queue = await GetAdminQueueCachedAsync(year, month, scopedFilter, cancellationToken);
        var cases = queue.AdminCases;
        var selected = ResolveSelected(cases, selectedAdminCaseKey);

        if (selected is null)
        {
            var emptyPreviews = await BuildQueuePreviewsAsync(cases, cancellationToken);
            return new PayrollProject300WorkbenchPage(
                year,
                month,
                queue.AdminSummary,
                cases,
                null,
                null,
                new PayrollProject300WorkbenchMetrics(0, 0, 0, GpsDeferred: true),
                emptyPreviews);
        }

        var detailBundle = await LoadCoreDetailAsync(selected, cancellationToken);
        var previews = await BuildQueuePreviewsAsync(cases, cancellationToken);
        return new PayrollProject300WorkbenchPage(
            year,
            month,
            queue.AdminSummary,
            cases,
            selected.AdminCaseKey,
            detailBundle.Detail,
            detailBundle.Metrics,
            previews);
    }

    public async Task<PayrollProject300GpsLoadResult> GetGpsContextAsync(
        int year,
        int month,
        string adminCaseKey,
        bool prefetchNext = true,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        if (gpsContextCache.TryGet(adminCaseKey, out var cachedContext) && cachedContext is not null)
        {
            string? prefetchKey = null;
            var prefetchStarted = false;
            if (prefetchNext)
            {
                var queue = await GetAdminQueueCachedAsync(
                    year,
                    month,
                    new PayrollReviewQueueFilter(PayrollReviewCategory.Project300, Scope: PayrollReviewQueueScope.All),
                    cancellationToken);
                var selectedCached = FindCase(queue.AdminCases, adminCaseKey);
                if (selectedCached is not null)
                {
                    (prefetchKey, prefetchStarted) = TryStartPrefetch(queue.AdminCases, selectedCached);
                }
            }

            return new PayrollProject300GpsLoadResult(
                adminCaseKey,
                string.Empty,
                default,
                cachedContext,
                CacheHit: true,
                PowerFleetApiCalls: 0,
                PrefetchAdminCaseKey: prefetchKey,
                PrefetchStarted: prefetchStarted);
        }

        var queueFresh = await GetAdminQueueCachedAsync(
            year,
            month,
            new PayrollReviewQueueFilter(PayrollReviewCategory.Project300, Scope: PayrollReviewQueueScope.All),
            cancellationToken);
        var selected = FindCase(queueFresh.AdminCases, adminCaseKey);

        if (selected is null)
        {
            return new PayrollProject300GpsLoadResult(
                adminCaseKey,
                string.Empty,
                default,
                new PayrollProject300GpsContext(
                    false,
                    PayrollProject300WorkbenchBuilder.MissingGpsSummary,
                    [],
                    "Missing",
                    null,
                    []),
                CacheHit: false,
                PowerFleetApiCalls: 0,
                PrefetchAdminCaseKey: null,
                PrefetchStarted: false);
        }

        var (evidence, cacheHit, apiCalls) = await LoadGpsDayAsync(
            selected.ResourceId,
            selected.DisplayName ?? selected.ResourceId,
            selected.Date,
            cancellationToken);

        var (performances, _) = await LoadDaySourcesAsync(selected.ResourceId, selected.Date, cancellationToken);
        var gpsContext = PayrollProject300WorkbenchBuilder.BuildDetail(
            selected,
            performances,
            [],
            evidence,
            gpsPending: false).GpsContext;
        gpsContextCache.Set(selected.AdminCaseKey, gpsContext);

        var (prefetchKey2, prefetchStarted2) = prefetchNext
            ? TryStartPrefetch(queueFresh.AdminCases, selected)
            : (null, false);

        return new PayrollProject300GpsLoadResult(
            selected.AdminCaseKey,
            selected.ResourceId,
            selected.Date,
            gpsContext,
            cacheHit,
            apiCalls,
            prefetchKey2,
            prefetchStarted2);
    }

    public PayrollProject300GpsCacheHint GetGpsCacheHint(string resourceId, DateOnly workDate)
    {
        if (!gpsCache.TryGet(resourceId, workDate, out var evidence, out var hit) || !hit)
        {
            return PayrollProject300GpsCacheHint.Unknown;
        }

        if (evidence is null || !evidence.HasUsableTrips)
        {
            return PayrollProject300GpsCacheHint.Unavailable;
        }

        return PayrollProject300GpsCacheHint.Available;
    }

    public void InvalidateQueueCache(int year, int month) => queueCache.InvalidateMonth(year, month);

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

        var page = await GetCoreDetailAsync(
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

        var target = detail.CorrectionTargets.FirstOrDefault(item =>
            item.PerformanceId == performanceId
            && item.CorrectionCapability == PayrollProject300CorrectionCapability.SupportedVanTot);
        if (target is null)
        {
            var unsupported = detail.CorrectionTargets.FirstOrDefault(item =>
                item.PerformanceId == performanceId
                && item.CorrectionCapability == PayrollProject300CorrectionCapability.UnsupportedActivity);
            return new PayrollProject300ProposeCorrectionResult(
                false,
                unsupported?.CapabilityMessage
                    ?? "Deze prestatie kan nog niet veilig vanuit TimeControl aangepast worden.",
                null,
                "UnsupportedActivity");
        }

        if (string.IsNullOrWhiteSpace(target.ActivityType))
        {
            return new PayrollProject300ProposeCorrectionResult(false, "Activity type ontbreekt.", null, null);
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
        if (shadowMonth is null || shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            return new PayrollProject300ProposeCorrectionResult(
                false,
                shadowMonth is null ? "Shadow-maand niet gevonden." : "Maand is afgesloten.",
                null,
                "MonthFinalized");
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
            target.ActivityType,
            target.HfdTaakId);

        var evidence = new PayrollActionEvidenceSnapshot(
            findingKey,
            PayrollFindingType.Project300WithoutPlanning,
            PayrollFindingSeverity.Review,
            null,
            $"Workbench VAN/TOT voorstel PerformanceId={performanceId}; activity={target.ActivityType}; OMSCHR={booked.Description ?? "—"}.",
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
        existing.SourceRevision =
            $"p300:{performanceId}:{target.CurrentStart:O}:{target.CurrentEnd:O}:{target.ActivityType}:{target.HfdTaakId}";
        existing.Comment = reason.Trim();
        existing.UpdatedAtUtc = now;

        await context.SaveChangesAsync(cancellationToken);
        queueCache.InvalidateMonth(year, month);
        logger.LogInformation(
            "Created Project300 adjust proposal {ActionId} perf={PerformanceId} activity={Activity} (ExecutionEnabled gate applies).",
            existing.ActionId,
            performanceId,
            target.ActivityType);

        return new PayrollProject300ProposeCorrectionResult(
            true,
            "Correctievoorstel opgeslagen (niet uitgevoerd).",
            existing.ActionId,
            null);
    }

    private async Task<(PayrollProject300CaseDetail Detail, PayrollProject300WorkbenchMetrics Metrics)> LoadCoreDetailAsync(
        PayrollAdminCase adminCase,
        CancellationToken cancellationToken)
    {
        var (performances, planning) = await LoadDaySourcesAsync(
            adminCase.ResourceId,
            adminCase.Date,
            cancellationToken);

        var hfdById = await GetHfdDefinitionsAsync(cancellationToken);
        var activities = new Dictionary<long, PayrollProject300ResolvedActivity>();
        var probe = PayrollProject300WorkbenchBuilder.BuildDetail(
            adminCase,
            performances,
            planning,
            null,
            gpsPending: true);
        var byId = performances.ToDictionary(item => item.SourceEntryId);
        foreach (var row in probe.BookedRows)
        {
            if (!byId.TryGetValue(row.PerformanceId, out var perf))
            {
                continue;
            }

            hfdById.TryGetValue(perf.HfdTaakId ?? -1, out var def);
            var resolved = PayrollProject300ActivityResolver.Resolve(perf, def);
            activities[perf.SourceEntryId] = new PayrollProject300ResolvedActivity(
                perf.SourceEntryId,
                resolved.ActivityType,
                resolved.Supported,
                resolved.Message,
                resolved.FriendlyTaskName);
        }

        StandbyGpsDayEvidence? cachedGps = null;
        var cacheHit = gpsCache.TryGet(adminCase.ResourceId, adminCase.Date, out cachedGps, out var hit) && hit;

        var bonNr = probe.BookedRows
            .Select(item => item.BonNr)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            ?? adminCase.BonNr;
        var bonMemo = await ResolveBonMemoAsync(bonNr, cancellationToken);

        var detail = PayrollProject300WorkbenchBuilder.BuildDetail(
            adminCase,
            performances,
            planning,
            cacheHit ? cachedGps : null,
            gpsPending: !cacheHit,
            activities,
            bonMemo);

        if (cacheHit && !detail.GpsContext.IsLoading)
        {
            gpsContextCache.Set(adminCase.AdminCaseKey, detail.GpsContext);
        }

        var metrics = new PayrollProject300WorkbenchMetrics(
            1,
            1,
            PowerFleetApiCalls: 0,
            GpsDeferred: !cacheHit,
            GpsCacheHit: cacheHit);
        return (detail, metrics);
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildQueuePreviewsAsync(
        IReadOnlyList<PayrollAdminCase> cases,
        CancellationToken cancellationToken)
    {
        var bonNrs = cases
            .Select(item => item.BonNr)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await EnsureBonMemosCachedAsync(bonNrs, cancellationToken);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in cases)
        {
            string? bonMemo = null;
            if (!string.IsNullOrWhiteSpace(item.BonNr))
            {
                bonMemoCache.TryGet(item.BonNr, out bonMemo);
            }

            var prestDesc = item.PerformanceDescriptionSummary
                ?? item.Performances.Select(p => p.PerformanceDescription).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var prestMemo = item.Performances.Select(p => p.PerformanceMemo).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            var preview = PayrollProject300WorkbenchBuilder.BuildQueueTechnicianPreview(
                prestDesc,
                prestMemo,
                bonMemo);
            if (preview is not null)
            {
                map[item.AdminCaseKey] = preview;
            }
        }

        return map;
    }

    private async Task<string?> ResolveBonMemoAsync(string? bonNr, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bonNr))
        {
            return null;
        }

        if (bonMemoCache.TryGet(bonNr, out var cached))
        {
            return cached;
        }

        await EnsureBonMemosCachedAsync([bonNr.Trim()], cancellationToken);
        return bonMemoCache.TryGet(bonNr, out var memo) ? memo : null;
    }

    private async Task EnsureBonMemosCachedAsync(
        IReadOnlyList<string> bonNumbers,
        CancellationToken cancellationToken)
    {
        var missing = bonNumbers
            .Where(item => !string.IsNullOrWhiteSpace(item) && !bonMemoCache.TryGet(item, out _))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var loaded = await plenionReader.ReadBonTechnicianMemosAsync(missing, cancellationToken);
        bonMemoCache.SetMany(loaded);
        foreach (var bonNr in missing)
        {
            if (!loaded.ContainsKey(bonNr))
            {
                bonMemoCache.Set(bonNr, null);
            }
        }
    }

    private async Task<(IReadOnlyList<NormalizedPerformanceEntry> Performances, IReadOnlyList<PayrollPlanningReservation> Planning)>
        LoadDaySourcesAsync(string resourceId, DateOnly date, CancellationToken cancellationToken)
    {
        if (daySourceCache.TryGet(resourceId, date, out var cachedPerf, out var cachedPlan))
        {
            return (cachedPerf, cachedPlan);
        }

        var resourceIds = new[] { resourceId };
        var performances = await performanceSource.ReadPerformancesAsync(
            date,
            date,
            resourceIds,
            cancellationToken);
        var planning = await planningSource.ReadWorkReservationsAsync(
            date,
            date,
            resourceIds,
            cancellationToken);
        daySourceCache.Set(resourceId, date, performances, planning);
        return (performances, planning);
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

    private (string? PrefetchKey, bool PrefetchStarted) TryStartPrefetch(
        IReadOnlyList<PayrollAdminCase> cases,
        PayrollAdminCase selected)
    {
        var prefetchKey = FindNextUnresolvedKey(cases, selected.AdminCaseKey);
        if (prefetchKey is null)
        {
            return (null, false);
        }

        var next = FindCase(cases, prefetchKey);
        if (next is null
            || gpsCache.Has(next.ResourceId, next.Date)
            || !_prefetchInFlight.TryAdd(Key(next.ResourceId, next.Date), 0))
        {
            return (prefetchKey, false);
        }

        var prefetchResourceId = next.ResourceId;
        var prefetchName = next.DisplayName ?? next.ResourceId;
        var prefetchDate = next.Date;
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadGpsDayAsync(
                    prefetchResourceId,
                    prefetchName,
                    prefetchDate,
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogDebug(
                    exception,
                    "GPS prefetch failed for {ResourceId} {Date}",
                    prefetchResourceId,
                    prefetchDate);
            }
            finally
            {
                _prefetchInFlight.TryRemove(Key(prefetchResourceId, prefetchDate), out _);
            }
        }, CancellationToken.None);

        return (prefetchKey, true);
    }

    private async Task<(StandbyGpsDayEvidence? Evidence, bool CacheHit, int ApiCalls)> LoadGpsDayAsync(
        string resourceId,
        string displayName,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        if (gpsCache.TryGet(resourceId, date, out var cached, out var hit) && hit)
        {
            return (cached, true, 0);
        }

        var batch = await gpsSource.ReadStandbyGpsAsync(
            date,
            date,
            [(resourceId, displayName, date)],
            cancellationToken);
        var evidence = batch.Days.FirstOrDefault(item =>
            string.Equals(item.ResourceId, resourceId, StringComparison.Ordinal)
            && item.Date == date);
        gpsCache.Set(resourceId, date, evidence);
        return (evidence, false, batch.ApiCallCount);
    }

    private static PayrollAdminCase? FindCase(IReadOnlyList<PayrollAdminCase> cases, string adminCaseKey)
    {
        foreach (var item in cases)
        {
            if (string.Equals(item.AdminCaseKey, adminCaseKey, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    private static string? FindNextUnresolvedKey(IReadOnlyList<PayrollAdminCase> cases, string currentKey)
    {
        var seenCurrent = false;
        foreach (var item in cases)
        {
            if (string.Equals(item.AdminCaseKey, currentKey, StringComparison.Ordinal))
            {
                seenCurrent = true;
                continue;
            }

            if (seenCurrent && PayrollReviewCategories.IsUnresolved(item.WorkflowStatus))
            {
                return item.AdminCaseKey;
            }
        }

        foreach (var item in cases)
        {
            if (!string.Equals(item.AdminCaseKey, currentKey, StringComparison.Ordinal)
                && PayrollReviewCategories.IsUnresolved(item.WorkflowStatus))
            {
                return item.AdminCaseKey;
            }
        }

        return null;
    }

    private static PayrollAdminCase? ResolveSelected(
        IReadOnlyList<PayrollAdminCase> cases,
        string? selectedAdminCaseKey)
    {
        if (!string.IsNullOrWhiteSpace(selectedAdminCaseKey))
        {
            for (var i = 0; i < cases.Count; i++)
            {
                if (string.Equals(cases[i].AdminCaseKey, selectedAdminCaseKey, StringComparison.Ordinal))
                {
                    return cases[i];
                }
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

    private async Task<IReadOnlyDictionary<int, HfdTaakDefinition>> GetHfdDefinitionsAsync(
        CancellationToken cancellationToken)
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

    private static string Key(string resourceId, DateOnly date) => $"{resourceId}|{date:yyyyMMdd}";

    private void EnsureEnabled()
    {
        if (!shadowOptions.Value.Enabled)
        {
            throw new InvalidOperationException("Payroll shadow is disabled.");
        }
    }
}
