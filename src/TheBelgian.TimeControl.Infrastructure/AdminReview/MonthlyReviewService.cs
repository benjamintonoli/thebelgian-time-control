using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Persistence;
using TheBelgian.TimeControl.Infrastructure.Pilot;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed class MonthlyReviewService(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    DailyHoursAuditService auditService,
    DailyReviewRepository reviewRepository,
    VehicleAssignmentSyncHistoryService vehicleSyncHistory,
    IPlenionCorrectionClient plenionCorrectionClient,
    IOptions<TimeControlCorrectionWriteOptions> correctionOptions,
    TimeProvider timeProvider) : IMonthlyReviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public ReviewMonth GetDefaultMonth(DateTimeOffset now)
    {
        var current = new DateOnly(now.Year, now.Month, 1);
        var offset = now.Day >= 15 ? -1 : -2;
        var selected = current.AddMonths(offset);
        return new ReviewMonth(selected.Year, selected.Month);
    }

    public async Task<MonthlyPrepareResult> PrepareAsync(
        ReviewMonth month,
        string actor,
        string? existingEvidenceJsonPath,
        bool refresh,
        CancellationToken cancellationToken)
    {
        ValidateMonth(month);
        if (string.IsNullOrWhiteSpace(actor))
            throw new InvalidOperationException("Actor is verplicht.");

        await using (var check = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var finalized = await check.MonthlyReviewPeriods.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Year == month.Year && item.Month == month.Month,
                    cancellationToken);
            if (finalized?.Status == MonthlyReviewStatus.Finalized)
            {
                return new MonthlyPrepareResult(
                    finalized,
                    await check.MonthlyReviewCaseSnapshots.CountAsync(item =>
                        item.MonthlyReviewPeriodId == finalized.Id && item.IsActive, cancellationToken),
                    0, 0, 0, "FinalizedSnapshot");
            }
        }

        string json;
        string evidenceSource;
        DailyHoursAuditResult? liveResult = null;
        if (!string.IsNullOrWhiteSpace(existingEvidenceJsonPath))
        {
            var fullPath = Path.GetFullPath(existingEvidenceJsonPath);
            json = await File.ReadAllTextAsync(fullPath, cancellationToken);
            evidenceSource = fullPath;
        }
        else
        {
            var baseName = $"timecontrol-monthly-review-{month.Key}";
            var csv = Path.Combine(Path.GetTempPath(), baseName + ".csv");
            var diagnostics = Path.Combine(Path.GetTempPath(), baseName + ".json");
            liveResult = await auditService.RunAsync(new DailyHoursAuditRequest(
                month.FirstDay, month.LastDay, csv, diagnostics, null, true), cancellationToken);
            json = await File.ReadAllTextAsync(diagnostics, cancellationToken);
            evidenceSource = diagnostics;
        }

        var now = timeProvider.GetUtcNow();
        var cases = DailyReviewCaseMapper.Map(json, now)
            .Where(item => item.Date >= month.FirstDay && item.Date <= month.LastDay)
            .ToArray();
        var summary = BuildSummary(json, cases, liveResult);
        var lastVehicleSync = await vehicleSyncHistory
            .LastSuccessfulVehicleAssignmentSyncAtAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var period = await context.MonthlyReviewPeriods.SingleOrDefaultAsync(item =>
            item.Year == month.Year && item.Month == month.Month, cancellationToken);
        if (period is null)
        {
            period = new MonthlyReviewPeriod
            {
                Year = month.Year,
                Month = month.Month,
                Status = MonthlyReviewStatus.WaitingForData,
                CreatedAt = now,
            };
            context.MonthlyReviewPeriods.Add(period);
            await context.SaveChangesAsync(cancellationToken);
        }

        if (period.Status == MonthlyReviewStatus.Finalized)
            throw new InvalidOperationException("Een afgesloten maand wordt niet vernieuwd.");

        var existing = await context.MonthlyReviewCaseSnapshots
            .Where(item => item.MonthlyReviewPeriodId == period.Id)
            .ToDictionaryAsync(item => item.CaseId, StringComparer.Ordinal, cancellationToken);
        var reviewedIds = (await context.DailyReviewActionAudits.AsNoTracking()
                .Where(item => cases.Select(value => value.CaseId).Contains(item.CaseId))
                .Select(item => item.CaseId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var newCases = 0;
        var changedCases = 0;
        var unchangedCases = 0;
        foreach (var reviewCase in cases)
        {
            var hash = Hash(reviewCase.EvidenceSnapshotJson);
            var caseJson = JsonSerializer.Serialize(reviewCase, JsonOptions);
            if (!existing.TryGetValue(reviewCase.CaseId, out var snapshot))
            {
                context.MonthlyReviewCaseSnapshots.Add(new MonthlyReviewCaseSnapshot
                {
                    MonthlyReviewPeriodId = period.Id,
                    CaseId = reviewCase.CaseId,
                    Technician = reviewCase.Technician,
                    Date = reviewCase.Date,
                    EvidenceHash = hash,
                    EvidenceSnapshotJson = reviewCase.EvidenceSnapshotJson,
                    CaseJson = caseJson,
                    UpdatedAt = now,
                });
                newCases++;
                continue;
            }

            snapshot.IsActive = true;
            snapshot.UpdatedAt = now;
            if (snapshot.EvidenceHash == hash)
            {
                unchangedCases++;
                continue;
            }

            snapshot.PreviousEvidenceSnapshotJson = snapshot.EvidenceSnapshotJson;
            snapshot.EvidenceSnapshotJson = reviewCase.EvidenceSnapshotJson;
            snapshot.EvidenceHash = hash;
            snapshot.CaseJson = caseJson;
            snapshot.NeedsReReview = reviewedIds.Contains(reviewCase.CaseId);
            changedCases++;
        }

        var currentIds = cases.Select(item => item.CaseId).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in existing.Values.Where(item => !currentIds.Contains(item.CaseId)))
        {
            stale.IsActive = false;
            stale.UpdatedAt = now;
        }

        period.Status = period.Status == MonthlyReviewStatus.WaitingForData
            ? MonthlyReviewStatus.ReadyForReview
            : period.Status;
        period.PreparedAt ??= now;
        period.LastRefreshedAt = now;
        period.AlgorithmVersion = DailyReviewCaseMapper.AlgorithmVersion;
        period.SourceCutoffAt = now;
        period.LastVehicleSyncAt = lastVehicleSync;
        period.SummaryJson = JsonSerializer.Serialize(summary, JsonOptions);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MonthlyPrepareResult(
            period, cases.Length, newCases, changedCases, unchangedCases, evidenceSource);
    }

    public async Task<MonthlyReviewCockpit> GetCockpitAsync(
        ReviewMonth month,
        DailyReviewFilter filter,
        string? selectedCaseId,
        CancellationToken cancellationToken)
    {
        ValidateMonth(month);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var period = await context.MonthlyReviewPeriods.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == month.Year && item.Month == month.Month,
                cancellationToken) ?? WaitingPeriod(month);
        var snapshots = period.Id == 0
            ? []
            : await context.MonthlyReviewCaseSnapshots.AsNoTracking()
                .Where(item => item.MonthlyReviewPeriodId == period.Id && item.IsActive)
                .ToArrayAsync(cancellationToken);
        var caseIds = snapshots.Select(item => item.CaseId).ToArray();
        var latest = await reviewRepository.LatestAsync(caseIds, cancellationToken);
        var all = snapshots.Select(snapshot =>
        {
            var value = JsonSerializer.Deserialize<DailyReviewCase>(snapshot.CaseJson, JsonOptions)
                ?? throw new InvalidDataException($"Snapshot {snapshot.CaseId} is ongeldig.");
            value = value.TripContext is null
                ? value with
                {
                    TripContext = DailyReviewTripContextMapper.Map(
                        value.EvidenceSnapshotJson, value.First, value.Last),
                }
                : value;
            if (snapshot.NeedsReReview ||
                latest.TryGetValue(snapshot.CaseId, out var stale) &&
                stale.EvidenceSnapshotJson != snapshot.EvidenceSnapshotJson)
            {
                return value with { Decision = new DailyReviewDecision(
                    DailyReviewWorkflowStatus.NeedsReReview, null,
                    "Gegevens gewijzigd — opnieuw controleren", null, null, null, null) };
            }

            return latest.TryGetValue(snapshot.CaseId, out var action)
                ? value with { Decision = ToDecision(action) }
                : value;
        }).ToArray();
        var filtered = Filter(all, filter).ToArray();
        var selected = !string.IsNullOrWhiteSpace(selectedCaseId)
            ? all.FirstOrDefault(item => item.CaseId == selectedCaseId)
            : filtered.FirstOrDefault();
        var recent = selected is null ? [] : all.Where(item =>
                item.CaseId != selected.CaseId && item.Technician.Equals(
                    selected.Technician, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Date).Take(8).ToArray();
        var summary = DeserializeSummary(period.SummaryJson);
        var counts = Counts(all);
        return new MonthlyReviewCockpit(
            period,
            new DailyReviewCockpit(filtered, selected, recent, counts),
            summary with { CorrectionProposals = await context.DailyCorrectionProposals
                .CountAsync(item => caseIds.Contains(item.CaseId), cancellationToken) },
            AddMonths(month, -1),
            AddMonths(month, 1));
    }

    public async Task<DailyReviewActionAudit> SaveDecisionAsync(
        ReviewMonth month,
        SaveDailyReviewDecision request,
        CancellationToken cancellationToken)
    {
        ValidateDecision(request);
        var now = timeProvider.GetUtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var period = await context.MonthlyReviewPeriods.SingleAsync(item =>
            item.Year == month.Year && item.Month == month.Month, cancellationToken);
        if (period.Status == MonthlyReviewStatus.Finalized)
            throw new InvalidOperationException("Deze maand is afgesloten en alleen-lezen.");
        var snapshot = await context.MonthlyReviewCaseSnapshots.SingleAsync(item =>
            item.MonthlyReviewPeriodId == period.Id && item.CaseId == request.CaseId && item.IsActive,
            cancellationToken);
        var reviewCase = JsonSerializer.Deserialize<DailyReviewCase>(snapshot.CaseJson, JsonOptions)
            ?? throw new InvalidDataException("Reviewcase-snapshot is ongeldig.");
        var action = new DailyReviewActionAudit
        {
            CaseId = reviewCase.CaseId,
            Technician = reviewCase.Technician,
            Date = reviewCase.Date,
            Decision = request.Status.ToString(),
            DecisionReason = request.Reason?.ToString(),
            Notes = Normalize(request.Notes),
            ReviewedBy = request.Reviewer.Trim(),
            ReviewedAt = now,
            EvidenceSnapshotJson = snapshot.EvidenceSnapshotJson,
            AlgorithmVersion = period.AlgorithmVersion,
        };
        context.DailyReviewActionAudits.Add(action);
        if (request.Status == DailyReviewWorkflowStatus.PendingCorrection)
        {
            context.DailyCorrectionProposals.Add(CreateApprovedProposal(
                reviewCase, snapshot.EvidenceSnapshotJson, request, now));
        }

        snapshot.NeedsReReview = false;
        period.Status = MonthlyReviewStatus.InReview;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return action;
    }

    public Task<IReadOnlyList<DailyReviewActionAudit>> GetAuditTrailAsync(
        string caseId,
        CancellationToken cancellationToken) => reviewRepository.ListAsync(caseId, cancellationToken);

    public async Task<DailyCorrectionProposal?> GetLatestCorrectionProposalAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DailyCorrectionProposals.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<CorrectionExecutionAvailability> GetCorrectionExecutionAvailabilityAsync(
        CancellationToken cancellationToken)
    {
        var options = correctionOptions.Value;
        if (!options.Enabled)
            return new CorrectionExecutionAvailability(false, false,
                "Plenion-correcties zijn momenteel uitgeschakeld.");
        var reachable = await plenionCorrectionClient.IsAvailableAsync(cancellationToken);
        return new CorrectionExecutionAvailability(true, reachable,
            reachable
                ? "PlenionWriteService is bereikbaar."
                : "PlenionWriteService is niet bereikbaar. Correcties kunnen niet worden uitgevoerd.");
    }

    public async Task<CorrectionExecutionResult> ExecuteCorrectionAsync(
        ReviewMonth month,
        long proposalId,
        string executedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executedBy))
            throw new InvalidOperationException("Uitvoerder is verplicht.");
        var availability = await GetCorrectionExecutionAvailabilityAsync(cancellationToken);
        if (!availability.CanExecute) throw new InvalidOperationException(availability.Message);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var proposal = await context.DailyCorrectionProposals.SingleAsync(
            item => item.Id == proposalId, cancellationToken);
        if (proposal.Status == CorrectionProposalStatuses.Executed)
            return new CorrectionExecutionResult(proposal.Status, "Correctie was al uitgevoerd.", proposal);
        if (proposal.Status != CorrectionProposalStatuses.Approved)
            throw new InvalidOperationException("Alleen een goedgekeurd correctievoorstel kan worden uitgevoerd.");
        var period = await context.MonthlyReviewPeriods.SingleAsync(item =>
            item.Year == month.Year && item.Month == month.Month, cancellationToken);
        if (period.Status == MonthlyReviewStatus.Finalized)
            throw new InvalidOperationException("Deze maand is afgesloten en alleen-lezen.");

        var claimed = await context.DailyCorrectionProposals
            .Where(item => item.Id == proposalId && item.Status == CorrectionProposalStatuses.Approved)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.Status, CorrectionProposalStatuses.Executing), cancellationToken);
        if (claimed != 1)
            throw new InvalidOperationException("De correctie wordt al uitgevoerd of is ondertussen gewijzigd.");
        proposal.Status = CorrectionProposalStatuses.Executing;

        var command = BuildCorrectionCommand(proposal, month, executedBy);
        PlenionCorrectionResponse response;
        try
        {
            response = await plenionCorrectionClient.ExecuteAsync(command, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            proposal.Status = CorrectionProposalStatuses.Failed;
            proposal.ErrorMessage = exception.Message;
            await context.SaveChangesAsync(cancellationToken);
            return new CorrectionExecutionResult(proposal.Status,
                "PlenionWriteService kon de correctie niet uitvoeren.", proposal);
        }

        proposal.PlenionWriteReference = response.Reference;
        proposal.PlenionWriteResponse = JsonSerializer.Serialize(response, JsonOptions);
        proposal.ErrorMessage = response.Status is "success" or "already_applied" ? null : response.Message;
        if (response.Status is "success" or "already_applied")
        {
            try
            {
                ApplyExecutedValues(proposal, response);
            }
            catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
            {
                proposal.Status = CorrectionProposalStatuses.WriteVerificationFailed;
                proposal.ErrorMessage = exception.Message;
                await context.SaveChangesAsync(cancellationToken);
                return new CorrectionExecutionResult(proposal.Status,
                    "De teruggelezen Plenion-tijden konden niet worden geverifieerd.", proposal);
            }
            proposal.Status = CorrectionProposalStatuses.Executed;
            proposal.ExecutedBy = executedBy.Trim();
            proposal.ExecutedAt = timeProvider.GetUtcNow();
            var executedSnapshot = await context.MonthlyReviewCaseSnapshots
                .SingleAsync(item => item.MonthlyReviewPeriodId == period.Id && item.CaseId == proposal.CaseId,
                    cancellationToken);
            executedSnapshot.NeedsReReview = false;
            context.DailyReviewActionAudits.Add(new DailyReviewActionAudit
            {
                CaseId = proposal.CaseId,
                Technician = executedSnapshot.Technician,
                Date = executedSnapshot.Date,
                Decision = DailyReviewWorkflowStatus.CorrectionExecuted.ToString(),
                DecisionReason = proposal.Reason,
                Notes = "Correctie uitgevoerd via PlenionWriteService.",
                ReviewedBy = executedBy.Trim(),
                ReviewedAt = proposal.ExecutedAt.Value,
                EvidenceSnapshotJson = proposal.PlenionWriteResponse,
                AlgorithmVersion = period.AlgorithmVersion,
            });
        }
        else if (response.Status == "conflict")
        {
            proposal.Status = CorrectionProposalStatuses.Conflict;
            var snapshot = await context.MonthlyReviewCaseSnapshots.SingleAsync(item =>
                item.MonthlyReviewPeriodId == period.Id && item.CaseId == proposal.CaseId, cancellationToken);
            snapshot.NeedsReReview = true;
            context.DailyReviewActionAudits.Add(new DailyReviewActionAudit
            {
                CaseId = proposal.CaseId,
                Technician = snapshot.Technician,
                Date = snapshot.Date,
                Decision = DailyReviewWorkflowStatus.NeedsReReview.ToString(),
                DecisionReason = proposal.Reason,
                Notes = response.Message,
                ReviewedBy = executedBy.Trim(),
                ReviewedAt = timeProvider.GetUtcNow(),
                EvidenceSnapshotJson = snapshot.EvidenceSnapshotJson,
                AlgorithmVersion = period.AlgorithmVersion,
            });
        }
        else
        {
            proposal.Status = response.Status == "verification_failed"
                ? CorrectionProposalStatuses.WriteVerificationFailed
                : CorrectionProposalStatuses.Failed;
        }

        await context.SaveChangesAsync(cancellationToken);
        return new CorrectionExecutionResult(proposal.Status, response.Message, proposal);
    }

    public async Task<CorrectionExecutionResult> ExecuteDirectCorrectionAsync(
        ReviewMonth month,
        ExecuteDirectCorrectionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Reviewer))
            throw new InvalidOperationException("Uitvoerder is verplicht.");
        if (request.ProposedStart is null && request.ProposedEnd is null)
            throw new InvalidOperationException("Kies eerst een nieuwe start- en/of eindtijd.");

        var availability = await GetCorrectionExecutionAvailabilityAsync(cancellationToken);
        if (!availability.CanExecute)
            throw new InvalidOperationException(availability.Message);

        var now = timeProvider.GetUtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var period = await context.MonthlyReviewPeriods.SingleAsync(item =>
            item.Year == month.Year && item.Month == month.Month, cancellationToken);
        if (period.Status == MonthlyReviewStatus.Finalized)
            throw new InvalidOperationException("Deze maand is afgesloten en alleen-lezen.");

        var snapshot = await context.MonthlyReviewCaseSnapshots.SingleAsync(item =>
            item.MonthlyReviewPeriodId == period.Id && item.CaseId == request.CaseId && item.IsActive,
            cancellationToken);
        var reviewCase = JsonSerializer.Deserialize<DailyReviewCase>(snapshot.CaseJson, JsonOptions)
            ?? throw new InvalidDataException("Reviewcase-snapshot is ongeldig.");

        if (!DailyReviewDisplay.IsDirectCorrectionActionable(reviewCase))
            throw new InvalidOperationException("Geen betrouwbare GPS-correctie beschikbaar.");

        var proposedStart = NormalizeProposedTime(
            request.ProposedStart, reviewCase.First, "start");
        var proposedEnd = NormalizeProposedTime(
            request.ProposedEnd, reviewCase.Last, "einde");
        if (proposedStart is null && proposedEnd is null)
            throw new InvalidOperationException("Kies eerst een nieuwe start- en/of eindtijd.");

        var latest = await context.DailyCorrectionProposals
            .Where(item => item.CaseId == request.CaseId)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest?.Status == CorrectionProposalStatuses.Executing)
            throw new InvalidOperationException("De correctie wordt al uitgevoerd of is ondertussen gewijzigd.");
        if (latest?.Status == CorrectionProposalStatuses.Executed &&
            SameTargets(latest, proposedStart, proposedEnd))
        {
            await transaction.CommitAsync(cancellationToken);
            return new CorrectionExecutionResult(
                latest.Status, "Correctie was al uitgevoerd.", latest);
        }

        var saveRequest = new SaveDailyReviewDecision(
            request.CaseId,
            DailyReviewWorkflowStatus.PendingCorrection,
            request.Reason,
            request.Reviewer,
            request.Notes,
            proposedStart,
            proposedEnd);
        var proposal = CreateApprovedProposal(
            reviewCase, snapshot.EvidenceSnapshotJson, saveRequest, now);
        context.DailyCorrectionProposals.Add(proposal);
        context.DailyReviewActionAudits.Add(new DailyReviewActionAudit
        {
            CaseId = reviewCase.CaseId,
            Technician = reviewCase.Technician,
            Date = reviewCase.Date,
            Decision = DailyReviewWorkflowStatus.PendingCorrection.ToString(),
            DecisionReason = request.Reason.ToString(),
            Notes = Normalize(request.Notes),
            ReviewedBy = request.Reviewer.Trim(),
            ReviewedAt = now,
            EvidenceSnapshotJson = snapshot.EvidenceSnapshotJson,
            AlgorithmVersion = period.AlgorithmVersion,
        });
        snapshot.NeedsReReview = false;
        period.Status = MonthlyReviewStatus.InReview;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ExecuteCorrectionAsync(
            month, proposal.Id, request.Reviewer, cancellationToken);
    }

    private static DateTimeOffset? NormalizeProposedTime(
        DateTimeOffset? proposed,
        DailyReviewBoundaryEvidence boundary,
        string sideLabel)
    {
        if (proposed is null)
            return null;
        if (!DailyReviewDisplay.CanCorrectBoundary(boundary))
            throw new InvalidOperationException(
                $"De {sideLabel}boundary is niet betrouwbaar genoeg voor een GPS-correctie.");
        if (!DailyReviewDisplay.IsMeaningfulTimeChange(
                boundary.PlenionTime, TimeOnly.FromDateTime(proposed.Value.DateTime)))
            return null;
        return proposed;
    }

    private static bool SameTargets(
        DailyCorrectionProposal proposal,
        DateTimeOffset? proposedStart,
        DateTimeOffset? proposedEnd) =>
        SameClock(proposal.ProposedStart, proposedStart) &&
        SameClock(proposal.ProposedEnd, proposedEnd);

    private static bool SameClock(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;
        return left.Value.Hour == right.Value.Hour && left.Value.Minute == right.Value.Minute;
    }

    private DailyCorrectionProposal CreateApprovedProposal(
        DailyReviewCase reviewCase,
        string evidenceSnapshotJson,
        SaveDailyReviewDecision request,
        DateTimeOffset now)
    {
        if (request.ProposedStart is not null && !reviewCase.First.IsReliable)
            throw new InvalidOperationException("De startboundary is niet betrouwbaar genoeg voor een GPS-correctie.");
        if (request.ProposedEnd is not null && !reviewCase.Last.IsReliable)
            throw new InvalidOperationException("De eindboundary is niet betrouwbaar genoeg voor een GPS-correctie.");
        var firstRecord = FindPerformance(evidenceSnapshotJson, reviewCase.First.PerformanceId);
        var lastRecord = reviewCase.Last.PerformanceId == reviewCase.First.PerformanceId
            ? firstRecord
            : FindPerformance(evidenceSnapshotJson, reviewCase.Last.PerformanceId);
        EnsureLocationBound(firstRecord, reviewCase.First.PerformanceId, request.ProposedStart is not null);
        EnsureLocationBound(lastRecord, reviewCase.Last.PerformanceId, request.ProposedEnd is not null);
        return new DailyCorrectionProposal
        {
            CaseId = reviewCase.CaseId,
            OriginalStart = reviewCase.First.PlenionTime,
            OriginalEnd = reviewCase.Last.PlenionTime,
            ProposedStart = request.ProposedStart,
            ProposedEnd = request.ProposedEnd,
            Reason = request.Reason!.Value.ToString(),
            Notes = Normalize(request.Notes),
            ProposedBy = request.Reviewer.Trim(),
            CreatedAt = now,
            Status = CorrectionProposalStatuses.Approved,
            FirstPerformanceId = reviewCase.First.PerformanceId,
            LastPerformanceId = reviewCase.Last.PerformanceId,
            FirstActivityType = firstRecord.ActivityType,
            LastActivityType = lastRecord.ActivityType,
            FirstMainTaskExternalId = firstRecord.MainTaskExternalId,
            LastMainTaskExternalId = lastRecord.MainTaskExternalId,
            FirstRecordOriginalStart = firstRecord.Start,
            FirstRecordOriginalEnd = firstRecord.End,
            LastRecordOriginalStart = lastRecord.Start,
            LastRecordOriginalEnd = lastRecord.End,
        };
    }

    public async Task<MonthlyReviewPeriod> FinalizeAsync(
        ReviewMonth month,
        string finalizedBy,
        bool confirmOpenCases,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(finalizedBy))
            throw new InvalidOperationException("Vul in wie de maand afsluit.");
        var cockpit = await GetCockpitAsync(month,
            new DailyReviewFilter(DailyReviewQueueView.All), null, cancellationToken);
        if (cockpit.Period.Status == MonthlyReviewStatus.Finalized)
            return cockpit.Period;
        var open = cockpit.Review.Cases.Count(IsOrdinaryOpenCase);
        if (open > 0 && !confirmOpenCases)
            throw new InvalidOperationException(
                $"De maand heeft nog {open} gewone reviewcase(s). Bevestig bewust om toch af te sluiten.");

        var now = timeProvider.GetUtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var period = await context.MonthlyReviewPeriods.SingleAsync(item =>
            item.Year == month.Year && item.Month == month.Month, cancellationToken);
        var snapshots = await context.MonthlyReviewCaseSnapshots.AsNoTracking()
            .Where(item => item.MonthlyReviewPeriodId == period.Id)
            .ToArrayAsync(cancellationToken);
        var caseIds = snapshots.Select(item => item.CaseId).ToArray();
        var actions = await context.DailyReviewActionAudits.AsNoTracking()
            .Where(item => caseIds.Contains(item.CaseId)).ToArrayAsync(cancellationToken);
        period.Status = MonthlyReviewStatus.Finalized;
        period.FinalizedAt = now;
        period.FinalizedBy = finalizedBy.Trim();
        period.SourceCutoffAt = now;
        period.FinalSnapshotJson = JsonSerializer.Serialize(new
        {
            Period = new { period.Year, period.Month, period.AlgorithmVersion, period.SourceCutoffAt },
            Cases = snapshots,
            Actions = actions,
        }, JsonOptions);
        await context.SaveChangesAsync(cancellationToken);
        return period;
    }

    public async Task<string> BuildHtmlReportAsync(
        ReviewMonth month,
        CancellationToken cancellationToken)
    {
        var cockpit = await GetCockpitAsync(month,
            new DailyReviewFilter(DailyReviewQueueView.All), null, cancellationToken);
        var final = cockpit.Period.Status == MonthlyReviewStatus.Finalized;
        var cases = cockpit.Review.Cases;
        var title = CultureInfo.GetCultureInfo("nl-BE").DateTimeFormat
            .GetMonthName(month.Month) + " " + month.Year;
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"nl\"><head><meta charset=\"utf-8\"><title>Urencontrole ")
            .Append(WebUtility.HtmlEncode(title)).Append("</title></head><body>");
        if (!final) html.Append("<h1>VOORLOPIG — maand nog niet afgesloten</h1>");
        html.Append("<h1>Urencontrole ").Append(WebUtility.HtmlEncode(title)).Append("</h1>")
            .Append("<p>Status: ").Append(final ? "Definitief" : "Voorlopig").Append("</p>")
            .Append("<p>Afgesloten: ").Append(cockpit.Period.FinalizedAt?.ToLocalTime()
                .ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—")
            .Append(" · Reviewer: ").Append(WebUtility.HtmlEncode(cockpit.Period.FinalizedBy ?? "—")).Append("</p>")
            .Append("<h2>Samenvatting</h2><ul>")
            .Append("<li>Werkdagen: ").Append(cockpit.Summary.Workdays).Append("</li>")
            .Append("<li>Controleerbare dagen: ").Append(cockpit.Summary.AssessableDays).Append("</li>")
            .Append("<li>Niet controleerbare dagen: ").Append(cockpit.Summary.DataQualityCases).Append("</li>")
            .Append("<li>Geen Track &amp; Trace: ").Append(cockpit.Summary.NoTrackAndTrace).Append("</li>")
            .Append("<li>Afwijkingen: ").Append(cockpit.Summary.Deviations)
            .Append("; &gt;5: ").Append(cockpit.Summary.DeviationsOver5)
            .Append("; &gt;15: ").Append(cockpit.Summary.DeviationsOver15)
            .Append("; &gt;30: ").Append(cockpit.Summary.DeviationsOver30).Append("</li>")
            .Append("<li>Bevestigde positieve minuten: ")
            .Append(cockpit.Summary.ConfirmedPositiveMinutes).Append("</li></ul>")
            .Append("<h2>Per technieker</h2><ul>");
        foreach (var group in cases.GroupBy(item => item.Technician).OrderBy(item => item.Key))
            html.Append("<li>").Append(WebUtility.HtmlEncode(group.Key)).Append(": ")
                .Append(group.Count()).Append(" case(s), ")
                .Append(group.Sum(item => item.TotalPositiveMinutes).ToString("0", CultureInfo.InvariantCulture))
                .Append(" positieve minuten</li>");
        html.Append("</ul><h2>Bevestigde materiële afwijkingen</h2>");
        AppendCases(html, cases.Where(item => item.EvidenceLevel != DailyReviewEvidenceLevel.Insufficient &&
            item.TotalPositiveMinutes > 5));
        html.Append("<h2>Administratieve correcties</h2>");
        AppendCases(html, cases.Where(item => item.Decision.Status == DailyReviewWorkflowStatus.PendingCorrection));
        html.Append("<h2>Cases met onvoldoende bewijs</h2>");
        AppendCases(html, cases.Where(item => item.EvidenceLevel == DailyReviewEvidenceLevel.Insufficient));
        html.Append("</body></html>");
        return html.ToString();
    }

    private static void AppendCases(StringBuilder html, IEnumerable<DailyReviewCase> cases)
    {
        html.Append("<ul>");
        foreach (var item in cases)
            html.Append("<li>").Append(item.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))
                .Append(" — ").Append(WebUtility.HtmlEncode(item.Technician))
                .Append(" — ").Append(item.TotalPositiveMinutes.ToString("0", CultureInfo.InvariantCulture))
                .Append(" min — ").Append(WebUtility.HtmlEncode(item.Customer)).Append("</li>");
        html.Append("</ul>");
    }

    private static MonthlyReviewSummary BuildSummary(
        string json,
        DailyReviewCase[] cases,
        DailyHoursAuditResult? live)
    {
        if (live is not null)
        {
            return new MonthlyReviewSummary(
                live.TechnicianDays,
                live.DaysWithValidVehicleAssignment,
                live.AmbiguousVehicleAssignments + live.InsufficientVehicleAssignments,
                live.ExcludedNoTrackAndTrace,
                live.ConfirmedDeviations,
                live.ConfirmedDeviationsOver5,
                live.ConfirmedDeviationsOver15,
                live.ConfirmedDeviationsOver30,
                live.ConfirmedEffectiveDeviationMinutes);
        }

        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.EnumerateArray().ToArray();
        var noTrack = rows.Count(item => Contains(item, "NoTrackAndTrace"));
        var insufficient = cases.Count(item => item.EvidenceLevel == DailyReviewEvidenceLevel.Insufficient);
        var confirmed = rows.Select(item => JsonNumber(item, "TotalConfirmedDeviation"))
            .Where(item => item > 0).ToArray();
        return new MonthlyReviewSummary(
            rows.Length,
            Math.Max(0, rows.Length - insufficient - noTrack),
            insufficient,
            noTrack,
            confirmed.Length,
            confirmed.Count(item => item > 5),
            confirmed.Count(item => item > 15),
            confirmed.Count(item => item > 30),
            (int)Math.Round(confirmed.Sum()));
    }

    private static bool Contains(JsonElement element, string text) =>
        element.GetRawText().Contains(text, StringComparison.OrdinalIgnoreCase);

    private static double JsonNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : 0;

    private static IEnumerable<DailyReviewCase> Filter(
        IReadOnlyList<DailyReviewCase> source,
        DailyReviewFilter filter)
    {
        var query = source.AsEnumerable();
        query = filter.View switch
        {
            DailyReviewQueueView.Open => query.Where(item => IsOrdinary(item) &&
                item.TotalPositiveMinutes > 5 &&
                item.Decision.Status is (DailyReviewWorkflowStatus.Open or DailyReviewWorkflowStatus.NeedsReReview)),
            DailyReviewQueueView.ToReview => query.Where(item => IsOrdinary(item) && item.Decision.Status is
                DailyReviewWorkflowStatus.PendingCorrection or DailyReviewWorkflowStatus.AwaitingExplanation or
                DailyReviewWorkflowStatus.EscalatedForManagementReview),
            DailyReviewQueueView.Completed => query.Where(item => IsOrdinary(item) &&
                item.Decision.Status is DailyReviewWorkflowStatus.ResolvedNoAction or
                    DailyReviewWorkflowStatus.CorrectionExecuted),
            DailyReviewQueueView.DataQuality => query.Where(IsDataQuality),
            DailyReviewQueueView.NotApplicable => query.Where(IsNoTrackAndTrace),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(filter.Technician))
            query = query.Where(item => item.Technician.Contains(filter.Technician.Trim(),
                StringComparison.OrdinalIgnoreCase));
        if (filter.Date is { } date) query = query.Where(item => item.Date == date);
        if (filter.Evidence is { } evidence) query = query.Where(item => item.EvidenceLevel == evidence);
        if (filter.EscalatedOnly) query = query.Where(item =>
            item.Decision.Status == DailyReviewWorkflowStatus.EscalatedForManagementReview);
        query = filter.Boundary switch
        {
            DailyReviewBoundaryFilter.Start => query.Where(item => item.First.SignedDifferenceMinutes is not null),
            DailyReviewBoundaryFilter.End => query.Where(item => item.Last.SignedDifferenceMinutes is not null),
            _ => query,
        };
        return filter.Sort switch
        {
            DailyReviewSort.DateAscending => query.OrderBy(item => item.Date).ThenBy(item => item.Technician),
            DailyReviewSort.DateDescending => query.OrderByDescending(item => item.Date).ThenBy(item => item.Technician),
            DailyReviewSort.Technician => query.OrderBy(item => item.Technician).ThenBy(item => item.Date),
            _ => query.OrderByDescending(item => item.MaximumAbsoluteDifferenceMinutes).ThenBy(item => item.Date),
        };
    }

    private static DailyReviewCounts Counts(DailyReviewCase[] cases) => new(
        cases.Count(item => IsOrdinary(item) && item.TotalPositiveMinutes > 5 &&
            item.Decision.Status is (DailyReviewWorkflowStatus.Open or DailyReviewWorkflowStatus.NeedsReReview)),
        cases.Count(item => IsOrdinary(item) && item.Decision.Status is
            DailyReviewWorkflowStatus.PendingCorrection or DailyReviewWorkflowStatus.AwaitingExplanation or
            DailyReviewWorkflowStatus.EscalatedForManagementReview),
        cases.Count(item => IsOrdinary(item) && item.Decision.Status is
            DailyReviewWorkflowStatus.ResolvedNoAction or DailyReviewWorkflowStatus.CorrectionExecuted),
        cases.Length,
        cases.Count(IsDataQuality),
        cases.Count(IsNoTrackAndTrace));

    private static bool IsOrdinary(DailyReviewCase item) =>
        !IsDataQuality(item) && !IsNoTrackAndTrace(item);
    private static bool IsDataQuality(DailyReviewCase item) =>
        !IsNoTrackAndTrace(item) && (item.EvidenceLevel == DailyReviewEvidenceLevel.Insufficient ||
        item.AuditReviewStatus.Contains("Insufficient", StringComparison.OrdinalIgnoreCase) ||
        item.AuditReviewStatus.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase));
    private static bool IsNoTrackAndTrace(DailyReviewCase item) =>
        item.AuditReviewStatus.Contains("NoTrackAndTrace", StringComparison.OrdinalIgnoreCase) ||
        item.First.TechnicalReason?.Contains("NoTrackAndTrace", StringComparison.OrdinalIgnoreCase) == true ||
        item.Last.TechnicalReason?.Contains("NoTrackAndTrace", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsOrdinaryOpenCase(DailyReviewCase item) => IsOrdinary(item) &&
        item.TotalPositiveMinutes > 5 &&
        item.Decision.Status is (DailyReviewWorkflowStatus.Open or DailyReviewWorkflowStatus.NeedsReReview);

    private static DailyReviewDecision ToDecision(DailyReviewActionAudit action) => new(
        Enum.TryParse<DailyReviewWorkflowStatus>(action.Decision, out var status)
            ? status : DailyReviewWorkflowStatus.Open,
        Enum.TryParse<ReviewFeedbackReason>(action.DecisionReason, out var reason) ? reason : null,
        action.Notes, action.ReviewedBy, action.ReviewedAt, null, null);

    private static MonthlyReviewSummary DeserializeSummary(string json) =>
        JsonSerializer.Deserialize<MonthlyReviewSummary>(json, JsonOptions) ?? new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static MonthlyReviewPeriod WaitingPeriod(ReviewMonth month) => new()
    {
        Year = month.Year,
        Month = month.Month,
        Status = MonthlyReviewStatus.WaitingForData,
        AlgorithmVersion = DailyReviewCaseMapper.AlgorithmVersion,
    };

    private static ReviewMonth AddMonths(ReviewMonth month, int value)
    {
        var date = month.FirstDay.AddMonths(value);
        return new ReviewMonth(date.Year, date.Month);
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static PerformanceSnapshot FindPerformance(string evidenceJson, long performanceId)
    {
        using var document = JsonDocument.Parse(evidenceJson);
        if (!document.RootElement.TryGetProperty("Performances", out var performances))
            throw new InvalidDataException("De evidence bevat geen Plenion-prestaties.");
        foreach (var performance in performances.EnumerateArray())
        {
            if (!performance.TryGetProperty("PerformanceId", out var id) || id.GetInt64() != performanceId)
                continue;
            var activityType = performance.GetProperty("ActivityType").GetString() ?? string.Empty;
            var start = DateTimeOffset.Parse(performance.GetProperty("Start").GetString()!, CultureInfo.InvariantCulture);
            var end = DateTimeOffset.Parse(performance.GetProperty("End").GetString()!, CultureInfo.InvariantCulture);
            long? mainTask = null;
            if (performance.TryGetProperty("MainTaskExternalId", out var task) &&
                task.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (task.ValueKind == JsonValueKind.Number) mainTask = task.GetInt64();
                else if (long.TryParse(task.GetString(), NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out var parsed)) mainTask = parsed;
            }
            return new PerformanceSnapshot(start, end, activityType, mainTask);
        }
        throw new InvalidDataException($"Prestatie {performanceId} ontbreekt in de evidence.");
    }

    internal static void EnsureLocationBound(
        PerformanceSnapshot performance,
        long performanceId,
        bool correctionRequested)
    {
        if (correctionRequested &&
            (!Enum.TryParse<PerformanceActivityType>(performance.ActivityType, out var activityType) ||
             !PerformanceActivityClassifier.RequiresGeographicMatch(activityType)))
            throw new InvalidOperationException(
                $"Prestatie {performanceId} is geen locatiegebonden klantprestatie.");
    }

    internal static PlenionCorrectionCommand BuildCorrectionCommand(
        DailyCorrectionProposal proposal,
        ReviewMonth month,
        string executedBy)
    {
        if (proposal.FirstRecordOriginalStart is null || proposal.FirstRecordOriginalEnd is null ||
            proposal.LastRecordOriginalStart is null || proposal.LastRecordOriginalEnd is null)
            throw new InvalidOperationException("Het correctievoorstel mist de originele recordtijden.");

        var changes = new Dictionary<long, PlenionCorrectionItem>();
        if (proposal.ProposedStart is not null)
        {
            changes[proposal.FirstPerformanceId] = new PlenionCorrectionItem(
                proposal.FirstPerformanceId,
                proposal.FirstRecordOriginalStart.Value.TimeOfDay,
                proposal.FirstRecordOriginalEnd.Value.TimeOfDay,
                proposal.ProposedStart.Value.TimeOfDay,
                null,
                proposal.FirstActivityType,
                proposal.FirstMainTaskExternalId);
        }
        if (proposal.ProposedEnd is not null)
        {
            if (changes.TryGetValue(proposal.LastPerformanceId, out var same))
            {
                changes[proposal.LastPerformanceId] = same with
                {
                    NewEnd = proposal.ProposedEnd.Value.TimeOfDay
                };
            }
            else
            {
                changes[proposal.LastPerformanceId] = new PlenionCorrectionItem(
                    proposal.LastPerformanceId,
                    proposal.LastRecordOriginalStart.Value.TimeOfDay,
                    proposal.LastRecordOriginalEnd.Value.TimeOfDay,
                    null,
                    proposal.ProposedEnd.Value.TimeOfDay,
                    proposal.LastActivityType,
                    proposal.LastMainTaskExternalId);
            }
        }

        return new PlenionCorrectionCommand(
            changes.Values.OrderBy(item => item.PerformanceId).ToArray(),
            proposal.Reason,
            executedBy.Trim(),
            proposal.CaseId,
            $"{month.Key}:{proposal.CaseId}:{proposal.Id}");
    }

    private static void ApplyExecutedValues(
        DailyCorrectionProposal proposal,
        PlenionCorrectionResponse response)
    {
        if (proposal.ProposedStart is not null)
        {
            var item = response.Performances.Single(value =>
                value.PerformanceId == proposal.FirstPerformanceId);
            proposal.ExecutedStart = AtTime(proposal.OriginalStart, item.CurrentStart ??
                throw new InvalidDataException("PWS gaf geen teruggelezen starttijd terug."));
        }
        if (proposal.ProposedEnd is not null)
        {
            var item = response.Performances.Single(value =>
                value.PerformanceId == proposal.LastPerformanceId);
            proposal.ExecutedEnd = AtTime(proposal.OriginalEnd, item.CurrentEnd ??
                throw new InvalidDataException("PWS gaf geen teruggelezen eindtijd terug."));
        }
    }

    private static DateTimeOffset AtTime(DateTimeOffset source, TimeSpan time) =>
        new(source.Year, source.Month, source.Day, time.Hours, time.Minutes, time.Seconds, source.Offset);

    internal sealed record PerformanceSnapshot(
        DateTimeOffset Start,
        DateTimeOffset End,
        string ActivityType,
        long? MainTaskExternalId);

    private static void ValidateMonth(ReviewMonth month)
    {
        if (month.Year is < 2000 or > 2200 || month.Month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month));
    }

    private static void ValidateDecision(SaveDailyReviewDecision request)
    {
        if (request.Status is DailyReviewWorkflowStatus.Open or DailyReviewWorkflowStatus.NeedsReReview)
            throw new InvalidOperationException("Dit is geen adminbeslissing.");
        if (string.IsNullOrWhiteSpace(request.Reviewer))
            throw new InvalidOperationException("Reviewer is verplicht.");
        if (request.Reason is null)
            throw new InvalidOperationException("Selecteer een reden.");
        if (request.Status == DailyReviewWorkflowStatus.PendingCorrection &&
            request.ProposedStart is null && request.ProposedEnd is null)
            throw new InvalidOperationException("Kies minstens een nieuwe start- of eindtijd.");
        if (request.Status is DailyReviewWorkflowStatus.AwaitingExplanation or
                DailyReviewWorkflowStatus.EscalatedForManagementReview &&
            string.IsNullOrWhiteSpace(request.Notes))
            throw new InvalidOperationException("Deze actie vereist een korte notitie.");
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
