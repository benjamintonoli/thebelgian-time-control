using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.AdminReview;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Persistence;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Actions;

internal sealed class PayrollActionService(
    IDbContextFactory<TimeControlDbContext> contextFactory,
    IPayrollShadowService shadowService,
    IPayrollPerformanceSource performanceSource,
    IPayrollStandbyGpsSource standbyGpsSource,
    IPlenionCorrectionClient correctionClient,
    IPlenionPerformanceCreateClient createClient,
    IPlenionPerformanceDeleteClient deleteClient,
    IOptions<PayrollActionsOptions> actionsOptions,
    IOptions<PayrollShadowOptions> shadowOptions,
    IOptions<TimeControlCorrectionWriteOptions> writeOptions,
    TimeProvider timeProvider) : IPayrollActionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<PayrollProposedActionStatus> TerminalStatuses =
    [
        PayrollProposedActionStatus.Applied,
        PayrollProposedActionStatus.Cancelled,
    ];

    public async Task<IReadOnlyList<PayrollProposedActionRecord>> ProposeFromFindingsAsync(
        int year,
        int month,
        string? resourceId,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureActionsEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await RequireMonthAsync(context, year, month, cancellationToken);
        var findingsQuery = context.PayrollFindingRecords
            .Where(item => item.ShadowMonthId == shadowMonth.Id);
        if (!string.IsNullOrWhiteSpace(resourceId))
        {
            findingsQuery = findingsQuery.Where(item => item.ResourceId == resourceId);
        }

        var findings = await findingsQuery.ToListAsync(cancellationToken);
        if (findings.Count == 0)
        {
            return [];
        }

        var employees = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        var employeeByResource = employees.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);

        var resourceIds = findings.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).ToArray();
        var performances = await performanceSource.ReadPerformancesAsync(
            shadowMonth.PeriodStart,
            shadowMonth.PeriodEnd,
            resourceIds,
            cancellationToken);

        var gpsByResourceDate = await LoadStandbyGpsLookupAsync(
            findings,
            employeeByResource,
            shadowMonth,
            cancellationToken);

        var existingActions = await context.PayrollProposedActionRecords
            .Where(item => item.ShadowMonthId == shadowMonth.Id)
            .ToListAsync(cancellationToken);
        var byFindingKey = existingActions
            .GroupBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Id).First(), StringComparer.Ordinal);

        var now = timeProvider.GetUtcNow();
        var results = new List<PayrollProposedActionRecord>();
        var consumedFindingKeys = new HashSet<string>(StringComparer.Ordinal);

        var standbyGroups = findings
            .Where(item => item.FindingType is PayrollFindingType.StandbyStartMismatch
                or PayrollFindingType.StandbyEndMismatch)
            .Select(item => (Finding: item, PerfId: SingleRelatedPerformanceId(item)))
            .Where(item => item.PerfId is not null)
            .GroupBy(item => (item.Finding.ResourceId, item.Finding.Date, PerformanceId: item.PerfId!.Value))
            .ToList();

        foreach (var group in standbyGroups)
        {
            var groupFindings = group
                .Select(item => item.Finding)
                .OrderBy(item => item.FindingType)
                .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
                .ToList();
            foreach (var finding in groupFindings)
            {
                consumedFindingKeys.Add(finding.FindingKey);
            }

            employeeByResource.TryGetValue(group.Key.ResourceId, out var employee);
            gpsByResourceDate.TryGetValue((group.Key.ResourceId, group.Key.Date), out var dayTrips);
            var primary = groupFindings[0];
            var eligibilityContext = BuildStandbyEligibilityContext(
                primary,
                groupFindings,
                employee,
                shadowMonth,
                performances,
                dayTrips ?? [],
                findings);
            var eligibility = PayrollActionEligibility.EvaluateStandbyAdjustGroup(groupFindings, eligibilityContext);
            var actionKey = PayrollStandbyActivityTypes.AdjustActionKey(
                group.Key.ResourceId,
                group.Key.Date,
                group.Key.PerformanceId);

            CancelLegacyStandbyMemberActions(
                byFindingKey,
                groupFindings,
                actionKey,
                now,
                results);

            UpsertAction(
                context,
                byFindingKey,
                results,
                primary,
                eligibility,
                actionKey,
                shadowMonth.Id,
                actor,
                now);
        }

        foreach (var finding in findings
                     .Where(item => !consumedFindingKeys.Contains(item.FindingKey))
                     .OrderBy(item => item.FindingKey, StringComparer.Ordinal))
        {
            employeeByResource.TryGetValue(finding.ResourceId, out var employee);
            gpsByResourceDate.TryGetValue((finding.ResourceId, finding.Date), out var dayTrips);
            var contextForFinding = BuildEligibilityContext(
                finding,
                employee,
                shadowMonth,
                performances,
                dayTrips,
                findings);
            var eligibility = PayrollActionEligibility.Evaluate(finding, contextForFinding);

            if (byFindingKey.TryGetValue(finding.FindingKey, out var existing)
                && !TerminalStatuses.Contains(existing.Status))
            {
                if (existing.Status == PayrollProposedActionStatus.ReadyForApproval
                    && contextForFinding.HasMatchingExistingPerformance
                    && eligibility.ActionType == PayrollProposedActionType.CreateMissingPerformance)
                {
                    existing.Status = PayrollProposedActionStatus.Stale;
                    existing.BlockReason =
                        "Bron gewijzigd: matching prestatie bestaat nu. Nieuw voorstel vereist.";
                    existing.UpdatedAtUtc = now;
                    existing.FindingId = finding.Id;
                    results.Add(existing);
                    continue;
                }

                ApplyEligibility(existing, finding, eligibility, actor, now, isNew: false);
                results.Add(existing);
            }
            else if (byFindingKey.TryGetValue(finding.FindingKey, out var terminal)
                     && terminal.Status == PayrollProposedActionStatus.Applied)
            {
                results.Add(terminal);
            }
            else
            {
                var created = new PayrollProposedActionRecord
                {
                    ActionId = Guid.NewGuid(),
                    ShadowMonthId = shadowMonth.Id,
                    CreatedAtUtc = now,
                    CreatedBy = actor,
                };
                ApplyEligibility(created, finding, eligibility, actor, now, isNew: true);
                context.PayrollProposedActionRecords.Add(created);
                results.Add(created);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        return results
            .GroupBy(item => item.ActionId)
            .Select(group => group.First())
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<PayrollProposedActionRecord>> ListActionsAsync(
        int year,
        int month,
        string? resourceId,
        CancellationToken cancellationToken)
    {
        EnsureActionsEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadowMonth is null)
        {
            return [];
        }

        var query = context.PayrollProposedActionRecords.AsNoTracking()
            .Where(item => item.ShadowMonthId == shadowMonth.Id);
        if (!string.IsNullOrWhiteSpace(resourceId))
        {
            query = query.Where(item => item.ResourceId == resourceId);
        }

        return await query
            .OrderBy(item => item.ResourceId)
            .ThenBy(item => item.FindingKey)
            .ToListAsync(cancellationToken);
    }

    public async Task<PayrollProposedActionRecord?> GetActionAsync(
        Guid actionId,
        CancellationToken cancellationToken)
    {
        EnsureActionsEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.PayrollProposedActionRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ActionId == actionId, cancellationToken);
    }

    public async Task<PayrollActionConfirmationView?> PrepareConfirmationAsync(
        Guid actionId,
        CancellationToken cancellationToken)
    {
        EnsureActionsEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var action = await context.PayrollProposedActionRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ActionId == actionId, cancellationToken);
        if (action is null)
        {
            return null;
        }

        var month = await context.PayrollShadowMonths.AsNoTracking()
            .SingleAsync(item => item.Id == action.ShadowMonthId, cancellationToken);
        var employee = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ShadowMonthId == action.ShadowMonthId && item.ResourceId == action.ResourceId,
                cancellationToken);

        var evidence = DeserializeEvidence(action.EvidenceSnapshotJson);
        var create = action.ActionType == PayrollProposedActionType.CreateMissingPerformance
            ? DeserializeCreate(action.ProposalSnapshotJson)
            : null;
        var adjust = action.ActionType == PayrollProposedActionType.AdjustExistingPerformanceTime
            ? DeserializeAdjust(action.ProposalSnapshotJson)
            : null;
        var delete = action.ActionType == PayrollProposedActionType.DeleteExistingPerformance
            ? DeserializeDelete(action.ProposalSnapshotJson)
            : null;

        var executionEnabled = actionsOptions.Value.ExecutionEnabled;
        string? gateMessage = null;
        var canExecute = action.Status == PayrollProposedActionStatus.ReadyForApproval;
        if (!executionEnabled)
        {
            canExecute = false;
            gateMessage = "Uitvoering staat uit (PayrollActions:ExecutionEnabled=false). Voorstel is wel zichtbaar.";
        }
        else if (action.Status != PayrollProposedActionStatus.ReadyForApproval)
        {
            canExecute = false;
            gateMessage = action.BlockReason ?? $"Status {action.Status} laat geen uitvoering toe.";
        }
        else if (!writeOptions.Value.Enabled)
        {
            canExecute = false;
            gateMessage = "PlenionWriteService-correcties/writes staan uit.";
        }
        else if (!IsFamilyExecuteEnabled(action.ActionType, out var familyGate))
        {
            canExecute = false;
            gateMessage = familyGate;
        }

        return new PayrollActionConfirmationView(
            action.ActionId,
            action.ShadowMonthId,
            month.Year,
            month.Month,
            action.ResourceId,
            employee?.DisplayNameSnapshot,
            action.ActionType,
            action.Status,
            action.BlockReason,
            evidence,
            create,
            adjust,
            string.IsNullOrWhiteSpace(action.Comment)
                ? PayrollActionEligibility.DefaultComment(action.ActionType)
                : action.Comment,
            executionEnabled,
            canExecute,
            gateMessage,
            delete);
    }

    public async Task<PayrollActionExecutionResult> ExecuteAsync(
        Guid actionId,
        string comment,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureActionsEnabled();
        if (!actionsOptions.Value.ExecutionEnabled)
        {
            throw new InvalidOperationException(
                "PayrollActions:ExecutionEnabled=false; uitvoering is uitgeschakeld.");
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new InvalidOperationException("Een korte reden/commentaar is verplicht voor uitvoering.");
        }

        if (!writeOptions.Value.Enabled)
        {
            throw new InvalidOperationException("TimeControlCorrectionWrites:Enabled=false; schrijven is uitgeschakeld.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var action = await context.PayrollProposedActionRecords
            .SingleOrDefaultAsync(item => item.ActionId == actionId, cancellationToken)
            ?? throw new InvalidOperationException("Payrollactie niet gevonden.");

        if (action.Status != PayrollProposedActionStatus.ReadyForApproval)
        {
            throw new InvalidOperationException($"Actie status {action.Status} kan niet worden uitgevoerd.");
        }

        if (!IsFamilyExecuteEnabled(action.ActionType, out var familyGate))
        {
            throw new InvalidOperationException(familyGate);
        }

        var month = await context.PayrollShadowMonths
            .SingleAsync(item => item.Id == action.ShadowMonthId, cancellationToken);
        if (month.Status == PayrollShadowMonthStatus.Finalized)
        {
            action.Status = PayrollProposedActionStatus.Stale;
            action.BlockReason = "Maand is afgesloten sinds het voorstel.";
            action.UpdatedAtUtc = timeProvider.GetUtcNow();
            await context.SaveChangesAsync(cancellationToken);
            return new PayrollActionExecutionResult(
                action.ActionId, action.Status, action.BlockReason, null, null);
        }

        var performances = await performanceSource.ReadPerformancesAsync(
            month.PeriodStart,
            month.PeriodEnd,
            [action.ResourceId],
            cancellationToken);

        var storedCreate = action.ActionType == PayrollProposedActionType.CreateMissingPerformance
            ? DeserializeCreate(action.ProposalSnapshotJson)
            : null;
        var storedAdjust = action.ActionType == PayrollProposedActionType.AdjustExistingPerformanceTime
            ? DeserializeAdjust(action.ProposalSnapshotJson)
            : null;
        var storedDelete = action.ActionType == PayrollProposedActionType.DeleteExistingPerformance
            ? DeserializeDelete(action.ProposalSnapshotJson)
            : null;

        var isWorkbench = PayrollActionEligibility.IsWorkbenchOrSnapshottedAction(
            action.FindingKey,
            action.ActionType,
            storedAdjust);

        if (action.ActionType == PayrollProposedActionType.DeleteExistingPerformance)
        {
            if (storedDelete is null)
            {
                MarkStale(action, "Delete-voorstel ontbreekt; nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }

            var liveDelete = performances.FirstOrDefault(item => item.SourceEntryId == storedDelete.PerformanceId);
            if (liveDelete is null)
            {
                MarkStale(action, "Prestatie bestaat niet meer in Plenion; nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }

            if (!MatchesDeleteSnapshot(storedDelete, liveDelete))
            {
                MarkStale(action, "Prestatie wijkt af van snapshot (VAN/TOT/dossier); nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }

            var nowDelete = timeProvider.GetUtcNow();
            action.Status = PayrollProposedActionStatus.Executing;
            action.Comment = comment.Trim();
            action.ApprovedAtUtc = nowDelete;
            action.ApprovedBy = actor;
            action.UpdatedAtUtc = nowDelete;
            await context.SaveChangesAsync(cancellationToken);

            try
            {
                return await ExecuteDeleteAsync(context, action, month, storedDelete, actor, cancellationToken);
            }
            catch (Exception exception)
            {
                return await MarkFailedAsync(context, action, actor, exception.Message, cancellationToken);
            }
        }

        if (action.ActionType == PayrollProposedActionType.AdjustExistingPerformanceTime && isWorkbench)
        {
            if (storedAdjust is null)
            {
                MarkStale(action, "Correctievoorstel ontbreekt; nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }

            var liveAdjust = performances.FirstOrDefault(item => item.SourceEntryId == storedAdjust.PerformanceId);
            if (liveAdjust is null || liveAdjust.Start is null || liveAdjust.End is null)
            {
                MarkStale(action, "Prestatie bestaat niet meer in Plenion; nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }

            if (liveAdjust.Start.Value.TimeOfDay != storedAdjust.CurrentStart.TimeOfDay
                || liveAdjust.End.Value.TimeOfDay != storedAdjust.CurrentEnd.TimeOfDay)
            {
                MarkStale(action, "Huidige VAN/TOT wijkt af van snapshot; nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }

            if (storedAdjust.ExpectedMainTaskExternalId is long expectedTask
                && liveAdjust.HfdTaakId is int liveTask
                && expectedTask != liveTask)
            {
                MarkStale(action, "Taaktype wijkt af van snapshot; nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }

            var nowAdjust = timeProvider.GetUtcNow();
            action.Status = PayrollProposedActionStatus.Executing;
            action.Comment = comment.Trim();
            action.ApprovedAtUtc = nowAdjust;
            action.ApprovedBy = actor;
            action.UpdatedAtUtc = nowAdjust;
            await context.SaveChangesAsync(cancellationToken);

            try
            {
                return await ExecuteAdjustAsync(context, action, month, storedAdjust, actor, cancellationToken);
            }
            catch (Exception exception)
            {
                return await MarkFailedAsync(context, action, actor, exception.Message, cancellationToken);
            }
        }

        var finding = await ResolvePrimaryFindingAsync(context, action, cancellationToken);
        var employee = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ShadowMonthId == action.ShadowMonthId && item.ResourceId == action.ResourceId,
                cancellationToken);

        if (finding is null)
        {
            MarkStale(action, "Bevinding bestaat niet meer na rebuild; nieuw voorstel vereist.");
            await context.SaveChangesAsync(cancellationToken);
            return FailResult(action, action.BlockReason!);
        }

        var monthFindings = await context.PayrollFindingRecords.AsNoTracking()
            .Where(item => item.ShadowMonthId == action.ShadowMonthId && item.ResourceId == action.ResourceId)
            .ToListAsync(cancellationToken);
        var gpsLookup = await LoadStandbyGpsLookupAsync(
            monthFindings,
            employee is null
                ? new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
                : new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
                {
                    [employee.ResourceId] = employee,
                },
            month,
            cancellationToken);
        gpsLookup.TryGetValue((finding.ResourceId, finding.Date), out var dayTrips);

        var eligibilityContext = BuildEligibilityContext(
            finding,
            employee,
            month,
            performances,
            dayTrips,
            monthFindings);
        if (action.ActionType == PayrollProposedActionType.CreateMissingPerformance
            && storedCreate is not null)
        {
            eligibilityContext = eligibilityContext with
            {
                ProvenMainTaskId = storedCreate.MainTaskId,
                IntervalSemanticsOverride = storedCreate.IntervalSemantics,
            };
        }

        if (action.ActionType == PayrollProposedActionType.CreateMissingPerformance
            && eligibilityContext.HasMatchingExistingPerformance)
        {
            MarkStale(action, "Bron gewijzigd: matching prestatie bestaat nu. Nieuw voorstel vereist.");
            await context.SaveChangesAsync(cancellationToken);
            return FailResult(action, action.BlockReason!);
        }

        PayrollActionEligibilityResult eligibility;
        if (action.ActionType == PayrollProposedActionType.AdjustExistingPerformanceTime
            && storedAdjust is not null)
        {
            var groupFindings = monthFindings
                .Where(item => item.FindingType is PayrollFindingType.StandbyStartMismatch
                    or PayrollFindingType.StandbyEndMismatch)
                .Where(item => SingleRelatedPerformanceId(item) == storedAdjust.PerformanceId)
                .OrderBy(item => item.FindingType)
                .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
                .ToList();
            eligibility = groupFindings.Count > 0
                ? PayrollActionEligibility.EvaluateStandbyAdjustGroup(groupFindings, eligibilityContext)
                : PayrollActionEligibility.Evaluate(finding, eligibilityContext);
        }
        else
        {
            eligibility = PayrollActionEligibility.Evaluate(finding, eligibilityContext);
        }
        if (eligibility.Status != PayrollProposedActionStatus.ReadyForApproval)
        {
            MarkStale(action, eligibility.BlockReason
                ?? "Bron of voorstel is gewijzigd; nieuw voorstel vereist.");
            await context.SaveChangesAsync(cancellationToken);
            return FailResult(action, action.BlockReason!);
        }

        if (storedCreate is not null && eligibility.CreateProposal is not null)
        {
            var proposed = eligibility.CreateProposal;
            if (storedCreate.MainTaskId != proposed.MainTaskId
                || storedCreate.ProjectId != proposed.ProjectId
                || storedCreate.Start != proposed.Start
                || storedCreate.End != proposed.End
                || !string.Equals(storedCreate.BonNr, proposed.BonNr, StringComparison.Ordinal))
            {
                MarkStale(action, "Voorstel wijkt af van actuele bevinding; nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }
        }

        if (storedAdjust is not null && eligibility.AdjustProposal is not null)
        {
            var proposed = eligibility.AdjustProposal;
            if (storedAdjust.PerformanceId != proposed.PerformanceId
                || storedAdjust.ProposedStart != proposed.ProposedStart
                || storedAdjust.ProposedEnd != proposed.ProposedEnd
                || storedAdjust.CurrentStart != proposed.CurrentStart
                || storedAdjust.CurrentEnd != proposed.CurrentEnd)
            {
                MarkStale(action, "Correctievoorstel wijkt af van actuele bron; nieuw voorstel vereist.");
                await context.SaveChangesAsync(cancellationToken);
                return FailResult(action, action.BlockReason!);
            }
        }

        // Prefer the snapshotted proposal the admin reviewed.
        var createProposal = storedCreate ?? eligibility.CreateProposal;
        var adjustProposal = storedAdjust ?? eligibility.AdjustProposal;
        var now = timeProvider.GetUtcNow();
        action.Status = PayrollProposedActionStatus.Executing;
        action.Comment = comment.Trim();
        action.ApprovedAtUtc = now;
        action.ApprovedBy = actor;
        action.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            if (action.ActionType == PayrollProposedActionType.CreateMissingPerformance)
            {
                return await ExecuteCreateAsync(context, action, month, createProposal!, actor, cancellationToken);
            }

            return await ExecuteAdjustAsync(context, action, month, adjustProposal!, actor, cancellationToken);
        }
        catch (Exception exception)
        {
            action.Status = PayrollProposedActionStatus.Failed;
            action.ExecutionResult = exception.Message;
            action.ExecutedAtUtc = timeProvider.GetUtcNow();
            action.ExecutedBy = actor;
            action.UpdatedAtUtc = action.ExecutedAtUtc;
            await context.SaveChangesAsync(cancellationToken);
            return new PayrollActionExecutionResult(
                action.ActionId,
                action.Status,
                exception.Message,
                action.PwsReference,
                action.ResultPerformanceId);
        }
    }

    public async Task<PayrollActionProposeResult> ProposeDeleteForPerformanceAsync(
        int year,
        int month,
        string resourceId,
        DateOnly workDate,
        long performanceId,
        string reason,
        string actor,
        PayrollFindingType findingType,
        string? actionKey = null,
        string? sourceFindingKey = null,
        int? sourceFindingId = null,
        IReadOnlyList<string>? sourceFindingKeys = null,
        IReadOnlyList<int>? sourceFindingIds = null,
        string? prestOmschr = null,
        string? prestMemo = null,
        string? bonTechnicianRemark = null,
        string? projectLabel = null,
        string? expectedActivityType = null,
        CancellationToken cancellationToken = default)
    {
        EnsureActionsEnabled();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new PayrollActionProposeResult(false, "Reden is verplicht.", null, null);
        }

        if (performanceId <= 0)
        {
            return new PayrollActionProposeResult(false, "PerformanceId is verplicht.", null, null);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shadowMonth = await context.PayrollShadowMonths
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        if (shadowMonth is null || shadowMonth.Status == PayrollShadowMonthStatus.Finalized)
        {
            return new PayrollActionProposeResult(
                false,
                shadowMonth is null ? "Shadow-maand niet gevonden." : "Maand is afgesloten.",
                null,
                "MonthFinalized");
        }

        var performances = await performanceSource.ReadPerformancesAsync(
            shadowMonth.PeriodStart,
            shadowMonth.PeriodEnd,
            [resourceId],
            cancellationToken);
        var live = performances.FirstOrDefault(item =>
            item.SourceEntryId == performanceId
            && string.Equals(item.ResourceId, resourceId, StringComparison.Ordinal)
            && item.Date == workDate);
        if (live is null || live.Start is null || live.End is null)
        {
            return new PayrollActionProposeResult(
                false,
                "Prestatie niet gevonden of zonder VAN/TOT.",
                null,
                "IncompleteTarget");
        }

        if (live.HfdTaakId is null or <= 0)
        {
            return new PayrollActionProposeResult(
                false,
                "Prestatie heeft geen IDHFDTAAK; delete is niet veilig.",
                null,
                "MissingMainTaskId");
        }

        var projectId = live.ProjectId
            ?? live.ProjectNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return new PayrollActionProposeResult(
                false,
                "ProjectId ontbreekt; delete is niet veilig.",
                null,
                "IncompleteTarget");
        }

        var prefix = findingType is PayrollFindingType.Project200WithoutPlanning
            or PayrollFindingType.Project200ExceedsPlanning
            ? "p200-delete"
            : "p300-delete";
        var resolvedActionKey = string.IsNullOrWhiteSpace(actionKey)
            ? $"{prefix}:{resourceId}:{workDate:yyyyMMdd}:{performanceId}"
            : actionKey.Trim();

        var delete = new PayrollActionDeleteProposal(
            performanceId,
            workDate,
            live.Start.Value,
            live.End.Value,
            live.AtlHoursRaw,
            resourceId,
            projectId.Trim(),
            string.IsNullOrWhiteSpace(live.BonNr) ? null : live.BonNr.Trim(),
            live.HfdTaakId,
            expectedActivityType,
            prestOmschr ?? live.Description,
            prestMemo ?? live.Memo,
            bonTechnicianRemark,
            projectLabel);

        var evidenceKey = sourceFindingKey ?? resolvedActionKey;
        var evidence = new PayrollActionEvidenceSnapshot(
            evidenceKey,
            findingType,
            PayrollFindingSeverity.Review,
            null,
            $"Delete-voorstel PerformanceId={performanceId}; project={projectId}; OMSCHR={prestOmschr ?? live.Description ?? "—"}.",
            "Prestatie verwijderen (voorstel)",
            reason.Trim(),
            [performanceId],
            live.Start,
            live.End,
            live.AtlHoursRaw,
            projectId,
            live.BonNr,
            sourceFindingKeys ?? (sourceFindingKey is null ? null : [sourceFindingKey]),
            sourceFindingIds ?? (sourceFindingId is > 0 ? [sourceFindingId.Value] : null));

        var existing = await context.PayrollProposedActionRecords
            .Where(item => item.ShadowMonthId == shadowMonth.Id && item.FindingKey == resolvedActionKey)
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
                ShadowMonthId = shadowMonth.Id,
                FindingKey = resolvedActionKey,
                FindingId = sourceFindingId is > 0 ? sourceFindingId : null,
                ResourceId = resourceId,
                CreatedAtUtc = now,
                CreatedBy = actor,
            };
            context.PayrollProposedActionRecords.Add(existing);
        }

        existing.ActionType = PayrollProposedActionType.DeleteExistingPerformance;
        existing.Status = PayrollProposedActionStatus.ReadyForApproval;
        existing.BlockReason = null;
        existing.EvidenceSnapshotJson = JsonSerializer.Serialize(evidence, JsonOptions);
        existing.ProposalSnapshotJson = JsonSerializer.Serialize(delete, JsonOptions);
        existing.SourceRevision =
            $"delete:{performanceId}:{live.Start:O}:{live.End:O}:{projectId}:{live.HfdTaakId}";
        existing.Comment = reason.Trim();
        existing.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);

        return new PayrollActionProposeResult(
            true,
            "Verwijderingsvoorstel opgeslagen (niet uitgevoerd).",
            existing.ActionId,
            null);
    }

    public async Task CancelAsync(
        Guid actionId,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureActionsEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var action = await context.PayrollProposedActionRecords
            .SingleOrDefaultAsync(item => item.ActionId == actionId, cancellationToken)
            ?? throw new InvalidOperationException("Payrollactie niet gevonden.");

        if (action.Status is PayrollProposedActionStatus.Applied or PayrollProposedActionStatus.Executing)
        {
            throw new InvalidOperationException("Deze actie kan niet meer worden geannuleerd.");
        }

        action.Status = PayrollProposedActionStatus.Cancelled;
        action.UpdatedAtUtc = timeProvider.GetUtcNow();
        action.ExecutedBy = actor;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PayrollActionProposeResult> UpdateCreateProposalAsync(
        Guid actionId,
        TimeOnly? start,
        TimeOnly? endTime,
        int? mainTaskId,
        string actor,
        CancellationToken cancellationToken)
    {
        EnsureActionsEnabled();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var action = await context.PayrollProposedActionRecords
            .SingleOrDefaultAsync(item => item.ActionId == actionId, cancellationToken);
        if (action is null)
        {
            return new PayrollActionProposeResult(false, "Payrollactie niet gevonden.", null, "NotFound");
        }

        if (action.ActionType != PayrollProposedActionType.CreateMissingPerformance)
        {
            return new PayrollActionProposeResult(false, "Alleen create-voorstellen zijn bewerkbaar.", action.ActionId, "WrongType");
        }

        if (action.Status is PayrollProposedActionStatus.Applied
            or PayrollProposedActionStatus.Executing
            or PayrollProposedActionStatus.Cancelled)
        {
            return new PayrollActionProposeResult(false, "Dit voorstel kan niet meer worden bewerkt.", action.ActionId, "Terminal");
        }

        var evidence = DeserializeEvidence(action.EvidenceSnapshotJson);
        var existing = DeserializeCreate(action.ProposalSnapshotJson);
        if (existing is null)
        {
            if (evidence.SuggestedPayableStart is null
                || evidence.SuggestedPayableEnd is null
                || string.IsNullOrWhiteSpace(evidence.SuggestedProjectId)
                || string.IsNullOrWhiteSpace(action.ResourceId))
            {
                return new PayrollActionProposeResult(
                    false,
                    "Create-target is onvolledig; VAN/TOT/project ontbreken in evidence.",
                    action.ActionId,
                    "IncompleteTarget");
            }

            existing = new PayrollActionCreateProposal(
                action.ResourceId,
                DateOnly.FromDateTime(evidence.SuggestedPayableStart.Value.DateTime),
                evidence.SuggestedPayableStart.Value,
                evidence.SuggestedPayableEnd.Value,
                evidence.SuggestedPayableHours
                    ?? Math.Round(
                        (decimal)(evidence.SuggestedPayableEnd.Value - evidence.SuggestedPayableStart.Value).TotalHours,
                        2,
                        MidpointRounding.AwayFromZero),
                evidence.SuggestedProjectId.Trim(),
                string.IsNullOrWhiteSpace(evidence.SuggestedBonNr) ? null : evidence.SuggestedBonNr.Trim(),
                mainTaskId ?? 0,
                PayrollIntervalSemantics.PayableWork);
        }

        var newStart = start is null
            ? existing.Start
            : new DateTimeOffset(existing.Date.ToDateTime(start.Value), existing.Start.Offset);
        var newEnd = endTime is null
            ? existing.End
            : new DateTimeOffset(existing.Date.ToDateTime(endTime.Value), existing.End.Offset);
        if (newEnd <= newStart)
        {
            return new PayrollActionProposeResult(false, "TOT moet na VAN liggen.", action.ActionId, "InvalidInterval");
        }

        var resolvedMainTask = mainTaskId ?? existing.MainTaskId;
        if (!PayrollCreateAllowedMainTasks.IsAllowed(resolvedMainTask))
        {
            return new PayrollActionProposeResult(
                false,
                PayrollCreateAllowedMainTasks.RejectReason(resolvedMainTask),
                action.ActionId,
                "ACTIVITY_NOT_ALLOWED");
        }

        var hours = Math.Round((decimal)(newEnd - newStart).TotalHours, 2, MidpointRounding.AwayFromZero);
        var updated = existing with
        {
            Start = newStart,
            End = newEnd,
            Hours = hours,
            MainTaskId = resolvedMainTask,
        };

        var now = timeProvider.GetUtcNow();
        action.ProposalSnapshotJson = JsonSerializer.Serialize(updated, JsonOptions);
        action.SourceRevision = PayrollActionEligibility.ComputeSourceRevision(evidence, updated, null);
        action.Status = PayrollProposedActionStatus.ReadyForApproval;
        action.BlockReason = null;
        action.UpdatedAtUtc = now;
        action.Comment = string.IsNullOrWhiteSpace(action.Comment)
            ? $"Admin-edit door {actor}"
            : action.Comment;
        await context.SaveChangesAsync(cancellationToken);

        return new PayrollActionProposeResult(true, "Create-voorstel bijgewerkt.", action.ActionId, null);
    }

    private async Task<PayrollActionExecutionResult> ExecuteCreateAsync(
        TimeControlDbContext context,
        PayrollProposedActionRecord action,
        PayrollShadowMonth month,
        PayrollActionCreateProposal proposal,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!await createClient.IsAvailableAsync(cancellationToken))
        {
            return await MarkFailedAsync(context, action, actor,
                "PlenionWriteService create-endpoint is niet beschikbaar.", cancellationToken);
        }

        var command = new PlenionPerformanceCreateCommand(
            action.ActionId.ToString("N"),
            proposal.ResourceId,
            proposal.Date,
            proposal.Start.TimeOfDay,
            proposal.End.TimeOfDay,
            proposal.ProjectId,
            proposal.BonNr,
            proposal.MainTaskId,
            action.Comment ?? PayrollActionEligibility.DefaultComment(action.ActionType),
            actor,
            action.FindingKey,
            action.ActionId.ToString("N"),
            DryRun: false);

        var response = await createClient.CreateAsync(command, cancellationToken);
        if (string.Equals(response.Status, "contract_unproven", StringComparison.OrdinalIgnoreCase)
            || string.Equals(response.Status, "create_contract_unproven", StringComparison.OrdinalIgnoreCase))
        {
            return await MarkFailedAsync(context, action, actor,
                response.Message ?? "PWS create-contract is unproven; niet Applied.",
                cancellationToken,
                response.Reference);
        }

        if (string.Equals(response.Status, "already_exists", StringComparison.OrdinalIgnoreCase))
        {
            return await MarkFailedAsync(
                context,
                action,
                actor,
                response.Message ?? "Equivalente prestatie bestaat al (stale); geen TimeControl create-claim.",
                cancellationToken,
                response.Reference,
                response.PerformanceId);
        }

        if ((!IsSuccessStatus(response.Status)
                && !string.Equals(response.Status, "already_applied", StringComparison.OrdinalIgnoreCase))
            || response.PerformanceId is null)
        {
            return await MarkFailedAsync(context, action, actor,
                response.Message ?? "Create mislukt.",
                cancellationToken,
                response.Reference);
        }

        // Re-read Plenion to verify persisted state before Applied.
        var performances = await performanceSource.ReadPerformancesAsync(
            month.PeriodStart,
            month.PeriodEnd,
            [action.ResourceId],
            cancellationToken);
        var verified = performances.Any(item => item.SourceEntryId == response.PerformanceId.Value);
        if (!verified && !writeOptions.Value.UseMock)
        {
            return await MarkFailedAsync(context, action, actor,
                "Create niet geverifieerd na teruglezen uit Plenion.",
                cancellationToken,
                response.Reference,
                response.PerformanceId);
        }

        return await MarkAppliedAsync(
            context,
            action,
            month,
            actor,
            response.Message,
            response.Reference,
            response.PerformanceId,
            cancellationToken);
    }

    private async Task<PayrollActionExecutionResult> ExecuteAdjustAsync(
        TimeControlDbContext context,
        PayrollProposedActionRecord action,
        PayrollShadowMonth month,
        PayrollActionAdjustProposal proposal,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!await correctionClient.IsAvailableAsync(cancellationToken))
        {
            return await MarkFailedAsync(context, action, actor,
                "PlenionWriteService correctie-endpoint is niet beschikbaar.", cancellationToken);
        }

        var command = new PlenionCorrectionCommand(
            [
                new PlenionCorrectionItem(
                    proposal.PerformanceId,
                    proposal.CurrentStart.TimeOfDay,
                    proposal.CurrentEnd.TimeOfDay,
                    proposal.ProposedStart.TimeOfDay,
                    proposal.ProposedEnd.TimeOfDay,
                    proposal.ExpectedActivityType ?? string.Empty,
                    proposal.ExpectedMainTaskExternalId)
            ],
            action.Comment ?? PayrollActionEligibility.DefaultComment(action.ActionType),
            actor,
            action.ActionId.ToString("N"),
            action.ActionId.ToString("N"));

        var response = await correctionClient.ExecuteAsync(command, cancellationToken);
        if (!IsSuccessStatus(response.Status))
        {
            return await MarkFailedAsync(context, action, actor,
                response.Message ?? "Correctie mislukt.",
                cancellationToken,
                response.Reference,
                proposal.PerformanceId);
        }

        return await MarkAppliedAsync(
            context,
            action,
            month,
            actor,
            response.Message,
            response.Reference,
            proposal.PerformanceId,
            cancellationToken);
    }

    private async Task<PayrollActionExecutionResult> ExecuteDeleteAsync(
        TimeControlDbContext context,
        PayrollProposedActionRecord action,
        PayrollShadowMonth month,
        PayrollActionDeleteProposal proposal,
        string actor,
        CancellationToken cancellationToken)
    {
        if (!await deleteClient.IsAvailableAsync(cancellationToken))
        {
            return await MarkFailedAsync(context, action, actor,
                "PlenionWriteService delete-endpoint is niet beschikbaar.", cancellationToken);
        }

        if (!TryParsePositiveLong(proposal.ResourceId, out var expectedResourceId)
            || !TryParsePositiveLong(proposal.ProjectId, out var expectedProjectId)
            || proposal.ExpectedMainTaskExternalId is null or <= 0)
        {
            return await MarkFailedAsync(context, action, actor,
                "Delete-snapshot mist resource/project/taak-id voor PWS-contract.",
                cancellationToken);
        }

        long? expectedBonNr = null;
        if (!string.IsNullOrWhiteSpace(proposal.BonNr)
            && long.TryParse(proposal.BonNr.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var bonParsed)
            && bonParsed > 0)
        {
            expectedBonNr = bonParsed;
        }

        var actionKey = action.ActionId.ToString("N");
        var command = new PlenionPerformanceDeleteCommand(
            actionKey,
            actionKey,
            DryRun: false,
            proposal.PerformanceId,
            expectedResourceId,
            proposal.Date,
            expectedProjectId,
            expectedBonNr,
            proposal.CurrentStart.TimeOfDay,
            proposal.CurrentEnd.TimeOfDay,
            proposal.ExpectedMainTaskExternalId.Value,
            action.Comment ?? PayrollActionEligibility.DefaultComment(action.ActionType),
            actor,
            actionKey);

        var response = await deleteClient.DeleteAsync(command, cancellationToken);
        if (!IsDeleteSuccessStatus(response))
        {
            return await MarkFailedAsync(context, action, actor,
                response.Message ?? "Delete mislukt.",
                cancellationToken,
                response.Reference,
                proposal.PerformanceId);
        }

        var performances = await performanceSource.ReadPerformancesAsync(
            month.PeriodStart,
            month.PeriodEnd,
            [action.ResourceId],
            cancellationToken);
        var stillPresent = performances.Any(item => item.SourceEntryId == proposal.PerformanceId);
        if (stillPresent && !writeOptions.Value.UseMock && !response.AlreadyApplied)
        {
            return await MarkFailedAsync(context, action, actor,
                "Delete niet geverifieerd: prestatie bestaat nog in Plenion.",
                cancellationToken,
                response.Reference,
                proposal.PerformanceId);
        }

        return await MarkAppliedAsync(
            context,
            action,
            month,
            actor,
            response.Message,
            response.Reference,
            response.DeletedPerformanceId ?? proposal.PerformanceId,
            cancellationToken);
    }

    private static bool IsDeleteSuccessStatus(PlenionPerformanceDeleteResponse response) =>
        response.Deleted
        || response.AlreadyApplied
        || string.Equals(response.Status, "success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(response.Status, "already_applied", StringComparison.OrdinalIgnoreCase)
        || string.Equals(response.Status, "AlreadyApplied", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesDeleteSnapshot(
        PayrollActionDeleteProposal snapshot,
        NormalizedPerformanceEntry live)
    {
        if (live.Start is null || live.End is null)
        {
            return false;
        }

        if (live.Start.Value.TimeOfDay != snapshot.CurrentStart.TimeOfDay
            || live.End.Value.TimeOfDay != snapshot.CurrentEnd.TimeOfDay)
        {
            return false;
        }

        if (snapshot.ExpectedMainTaskExternalId is long expectedTask
            && live.HfdTaakId is int liveTask
            && expectedTask != liveTask)
        {
            return false;
        }

        var liveProject = live.ProjectId
            ?? live.ProjectNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(snapshot.ProjectId, liveProject, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var snapBon = string.IsNullOrWhiteSpace(snapshot.BonNr) ? null : snapshot.BonNr.Trim();
        var liveBon = string.IsNullOrWhiteSpace(live.BonNr) ? null : live.BonNr.Trim();
        if (snapBon is "0")
        {
            snapBon = null;
        }

        if (liveBon is "0")
        {
            liveBon = null;
        }

        return string.Equals(snapBon, liveBon, StringComparison.Ordinal);
    }

    private bool IsFamilyExecuteEnabled(PayrollProposedActionType actionType, out string gateMessage)
    {
        switch (actionType)
        {
            case PayrollProposedActionType.AdjustExistingPerformanceTime:
                if (!actionsOptions.Value.AdjustTimeEnabled)
                {
                    gateMessage = "Uitvoering van tijdscorrecties staat uit (PayrollActions:AdjustTimeEnabled=false).";
                    return false;
                }

                break;
            case PayrollProposedActionType.DeleteExistingPerformance:
                if (!actionsOptions.Value.DeletePerformanceEnabled)
                {
                    gateMessage = "Uitvoering van verwijderen staat uit (PayrollActions:DeletePerformanceEnabled=false).";
                    return false;
                }

                break;
            case PayrollProposedActionType.CreateMissingPerformance:
                if (!actionsOptions.Value.CreatePerformanceEnabled)
                {
                    gateMessage = "Uitvoering van create staat uit (PayrollActions:CreatePerformanceEnabled=false).";
                    return false;
                }

                break;
        }

        gateMessage = string.Empty;
        return true;
    }

    private static bool TryParsePositiveLong(string? value, out long parsed)
    {
        parsed = 0;
        return !string.IsNullOrWhiteSpace(value)
            && long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out parsed)
            && parsed > 0;
    }

    private async Task<PayrollActionExecutionResult> MarkAppliedAsync(
        TimeControlDbContext context,
        PayrollProposedActionRecord action,
        PayrollShadowMonth month,
        string actor,
        string? message,
        string? reference,
        long? performanceId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        action.Status = PayrollProposedActionStatus.Applied;
        action.PwsReference = reference;
        action.ResultPerformanceId = performanceId;
        action.ExecutionResult = message;
        action.ExecutedAtUtc = now;
        action.ExecutedBy = actor;
        action.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);

        await shadowService.RebuildSnapshotAsync(
            month.Year,
            month.Month,
            month.EvaluationDate,
            actor,
            cancellationToken,
            limitToResourceIds: ResolveRebuildResourceIds(action));

        return new PayrollActionExecutionResult(
            action.ActionId,
            action.Status,
            message ?? "Actie uitgevoerd.",
            reference,
            performanceId);
    }

    private static string[]? ResolveRebuildResourceIds(PayrollProposedActionRecord action)
    {
        if (string.IsNullOrWhiteSpace(action.ResourceId))
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal) { action.ResourceId.Trim() };
        if (action.ActionType == PayrollProposedActionType.CreateMissingPerformance)
        {
            try
            {
                var evidence = DeserializeEvidence(action.EvidenceSnapshotJson);
                foreach (var peer in PayrollActionEligibility.ParsePeerResourceIds(evidence.Evidence))
                {
                    ids.Add(peer);
                }
            }
            catch
            {
                // Keep action resource only when evidence snapshot is unreadable.
            }
        }

        return ids.Count == 0 ? null : ids.ToArray();
    }

    private async Task<PayrollActionExecutionResult> MarkFailedAsync(
        TimeControlDbContext context,
        PayrollProposedActionRecord action,
        string actor,
        string message,
        CancellationToken cancellationToken,
        string? reference = null,
        long? performanceId = null)
    {
        var now = timeProvider.GetUtcNow();
        action.Status = PayrollProposedActionStatus.Failed;
        action.ExecutionResult = message;
        action.PwsReference = reference;
        action.ResultPerformanceId = performanceId;
        action.ExecutedAtUtc = now;
        action.ExecutedBy = actor;
        action.UpdatedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
        return new PayrollActionExecutionResult(
            action.ActionId, action.Status, message, reference, performanceId);
    }

    private static void MarkStale(PayrollProposedActionRecord action, string reason)
    {
        action.Status = PayrollProposedActionStatus.Stale;
        action.BlockReason = reason;
        action.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static PayrollActionExecutionResult FailResult(PayrollProposedActionRecord action, string message) =>
        new(action.ActionId, action.Status, message, action.PwsReference, action.ResultPerformanceId);

    private static bool IsSuccessStatus(string? status) =>
        string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "already_applied", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "AlreadyApplied", StringComparison.OrdinalIgnoreCase);

    private static void ApplyEligibility(
        PayrollProposedActionRecord record,
        PayrollFindingRecord finding,
        PayrollActionEligibilityResult eligibility,
        string actor,
        DateTimeOffset now,
        bool isNew,
        string? actionKeyOverride = null)
    {
        record.FindingKey = actionKeyOverride
            ?? eligibility.EvidenceSnapshot.FindingKey
            ?? finding.FindingKey;
        record.FindingId = finding.Id;
        record.ResourceId = finding.ResourceId;
        record.ActionType = eligibility.ActionType;
        record.Status = eligibility.Status;
        record.BlockReason = eligibility.BlockReason;
        record.EvidenceSnapshotJson = JsonSerializer.Serialize(eligibility.EvidenceSnapshot, JsonOptions);
        record.ProposalSnapshotJson = eligibility.CreateProposal is not null
            ? JsonSerializer.Serialize(eligibility.CreateProposal, JsonOptions)
            : eligibility.AdjustProposal is not null
                ? JsonSerializer.Serialize(eligibility.AdjustProposal, JsonOptions)
                : eligibility.DeleteProposal is not null
                    ? JsonSerializer.Serialize(eligibility.DeleteProposal, JsonOptions)
                    : "{}";
        record.SourceRevision = eligibility.SourceRevision;
        record.UpdatedAtUtc = now;
        if (isNew)
        {
            record.CreatedBy = actor;
            record.CreatedAtUtc = now;
        }
    }

    private void UpsertAction(
        TimeControlDbContext context,
        Dictionary<string, PayrollProposedActionRecord> byFindingKey,
        List<PayrollProposedActionRecord> results,
        PayrollFindingRecord primaryFinding,
        PayrollActionEligibilityResult eligibility,
        string actionKey,
        int shadowMonthId,
        string actor,
        DateTimeOffset now)
    {
        if (byFindingKey.TryGetValue(actionKey, out var existing)
            && !TerminalStatuses.Contains(existing.Status))
        {
            ApplyEligibility(existing, primaryFinding, eligibility, actor, now, isNew: false, actionKey);
            results.Add(existing);
            return;
        }

        if (byFindingKey.TryGetValue(actionKey, out var terminal)
            && terminal.Status == PayrollProposedActionStatus.Applied)
        {
            results.Add(terminal);
            return;
        }

        var created = new PayrollProposedActionRecord
        {
            ActionId = Guid.NewGuid(),
            ShadowMonthId = shadowMonthId,
            CreatedAtUtc = now,
            CreatedBy = actor,
        };
        ApplyEligibility(created, primaryFinding, eligibility, actor, now, isNew: true, actionKey);
        context.PayrollProposedActionRecords.Add(created);
        byFindingKey[actionKey] = created;
        results.Add(created);
    }

    private static void CancelLegacyStandbyMemberActions(
        Dictionary<string, PayrollProposedActionRecord> byFindingKey,
        IReadOnlyList<PayrollFindingRecord> groupFindings,
        string actionKey,
        DateTimeOffset now,
        List<PayrollProposedActionRecord> results)
    {
        foreach (var finding in groupFindings)
        {
            if (string.Equals(finding.FindingKey, actionKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (!byFindingKey.TryGetValue(finding.FindingKey, out var legacy)
                || TerminalStatuses.Contains(legacy.Status)
                || legacy.Status == PayrollProposedActionStatus.Cancelled)
            {
                continue;
            }

            legacy.Status = PayrollProposedActionStatus.Cancelled;
            legacy.BlockReason =
                "Vervangen door geaggregeerde wachtdienstcorrectie voor dezelfde prestatie.";
            legacy.UpdatedAtUtc = now;
            results.Add(legacy);
        }
    }

    private async Task<Dictionary<(string ResourceId, DateOnly Date), IReadOnlyList<StandbyGpsTripEvidence>>>
        LoadStandbyGpsLookupAsync(
            IReadOnlyList<PayrollFindingRecord> findings,
            Dictionary<string, PayrollShadowEmployeeResult> employeeByResource,
            PayrollShadowMonth month,
            CancellationToken cancellationToken)
    {
        var standbyDates = findings
            .Where(item => item.FindingType is PayrollFindingType.StandbyStartMismatch
                or PayrollFindingType.StandbyEndMismatch)
            .Select(item => (item.ResourceId, item.Date))
            .Distinct()
            .ToList();
        if (standbyDates.Count == 0)
        {
            return new Dictionary<(string, DateOnly), IReadOnlyList<StandbyGpsTripEvidence>>();
        }

        var requests = standbyDates
            .Select(item =>
            {
                employeeByResource.TryGetValue(item.ResourceId, out var employee);
                var display = employee?.DisplayNameSnapshot ?? item.ResourceId;
                return (item.ResourceId, display, item.Date);
            })
            .ToArray();

        var batch = await standbyGpsSource.ReadStandbyGpsAsync(
            month.PeriodStart,
            month.PeriodEnd,
            requests,
            cancellationToken);

        return batch.Days
            .GroupBy(item => (item.ResourceId, item.Date))
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<StandbyGpsTripEvidence>)group.First().Trips);
    }

    private static long? SingleRelatedPerformanceId(PayrollFindingRecord finding)
    {
        var related = PayrollActionEligibility.ParseRelatedIds(finding.RelatedPerformanceIdsJson);
        return related.Count == 1 ? related[0] : null;
    }

    private async Task<PayrollFindingRecord?> ResolvePrimaryFindingAsync(
        TimeControlDbContext context,
        PayrollProposedActionRecord action,
        CancellationToken cancellationToken)
    {
        var byKey = await context.PayrollFindingRecords.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ShadowMonthId == action.ShadowMonthId && item.FindingKey == action.FindingKey,
                cancellationToken);
        if (byKey is not null)
        {
            return byKey;
        }

        if (action.FindingId is int findingId)
        {
            var byId = await context.PayrollFindingRecords.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ShadowMonthId == action.ShadowMonthId && item.Id == findingId,
                    cancellationToken);
            if (byId is not null)
            {
                return byId;
            }
        }

        var evidence = DeserializeEvidence(action.EvidenceSnapshotJson);
        string? sourceKey = null;
        if (evidence.SourceFindingKeys is { Count: > 0 })
        {
            sourceKey = evidence.SourceFindingKeys[0];
        }

        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            return await context.PayrollFindingRecords.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.ShadowMonthId == action.ShadowMonthId && item.FindingKey == sourceKey,
                    cancellationToken);
        }

        return null;
    }

    private static PayrollActionEligibilityContext BuildEligibilityContext(
        PayrollFindingRecord finding,
        PayrollShadowEmployeeResult? employee,
        PayrollShadowMonth month,
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<StandbyGpsTripEvidence>? dayTrips = null,
        IReadOnlyList<PayrollFindingRecord>? monthFindings = null)
    {
        var included = employee?.EligibilityStatus == PayrollEligibilityStatus.Included;
        var finalized = month.Status == PayrollShadowMonthStatus.Finalized;
        var related = PayrollActionEligibility.ParseRelatedIds(finding.RelatedPerformanceIdsJson);

        if (finding.FindingType == PayrollFindingType.MissingPlannedTechnicianPerformance)
        {
            var hasMatch = HasMatchingMissingTechPerformance(finding, performances);
            // Planning-proven HFDTAAK only — never copy peer MainTaskId.
            return new PayrollActionEligibilityContext(
                included,
                finalized,
                hasMatch,
                ProvenMainTaskId: PayrollActionEligibility.ParseSuggestedHfdTaakId(finding.Evidence),
                null,
                null,
                null);
        }

        if (finding.FindingType is PayrollFindingType.StandbyStartMismatch
            or PayrollFindingType.StandbyEndMismatch)
        {
            return BuildStandbyEligibilityContext(
                finding,
                [finding],
                employee,
                month,
                performances,
                dayTrips ?? [],
                monthFindings ?? [finding]);
        }

        return new PayrollActionEligibilityContext(
            included,
            finalized,
            false,
            null,
            null,
            null,
            null);
    }

    private static PayrollActionEligibilityContext BuildStandbyEligibilityContext(
        PayrollFindingRecord primary,
        IReadOnlyList<PayrollFindingRecord> groupFindings,
        PayrollShadowEmployeeResult? employee,
        PayrollShadowMonth month,
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<StandbyGpsTripEvidence> dayTrips,
        IReadOnlyList<PayrollFindingRecord> monthFindings)
    {
        var included = employee?.EligibilityStatus == PayrollEligibilityStatus.Included;
        var finalized = month.Status == PayrollShadowMonthStatus.Finalized;
        var related = groupFindings
            .SelectMany(item => PayrollActionEligibility.ParseRelatedIds(item.RelatedPerformanceIdsJson))
            .Distinct()
            .ToList();

        NormalizedPerformanceEntry? target = null;
        if (related.Count == 1)
        {
            target = performances.FirstOrDefault(item => item.SourceEntryId == related[0]);
        }

        var activityType = PayrollStandbyActivityTypes.FromMainTaskExternalId(target?.HfdTaakId);
        var proposedStart = groupFindings
            .Select(item => item.SuggestedPayableStart)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .DefaultIfEmpty()
            .Min();
        var proposedEnd = groupFindings
            .Select(item => item.SuggestedPayableEnd)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .DefaultIfEmpty()
            .Max();

        var hasDossierAmbiguity = related.Count == 1
            && monthFindings.Any(item =>
                item.FindingType == PayrollFindingType.StandbyPossibleWrongDossier
                && PayrollActionEligibility.ParseRelatedIds(item.RelatedPerformanceIdsJson)
                    .Contains(related[0]));

        var hasConflict = target is not null
            && proposedStart != default
            && proposedEnd != default
            && performances.Any(item =>
                item.SourceEntryId != target.SourceEntryId
                && string.Equals(item.ResourceId, primary.ResourceId, StringComparison.Ordinal)
                && item.Date == primary.Date
                && !item.IsCalendarSynthetic
                && !item.IsAbsence
                && item.HfdTaakId != PayrollStandbyActivityTypes.WaitingMainTaskExternalId
                && item.Start is not null
                && item.End is not null
                && item.Start < proposedEnd
                && item.End > proposedStart);

        return new PayrollActionEligibilityContext(
            included,
            finalized,
            HasMatchingExistingPerformance: false,
            ProvenMainTaskId: null,
            target?.Start,
            target?.End,
            target?.SourceEntryId,
            ExistingActivityType: activityType,
            ExistingMainTaskExternalId: target?.HfdTaakId,
            StandbyDayTrips: dayTrips,
            HasRelatedDossierAmbiguity: hasDossierAmbiguity,
            HasConflictingPerformance: hasConflict);
    }

    private static bool HasMatchingMissingTechPerformance(
        PayrollFindingRecord finding,
        IReadOnlyList<NormalizedPerformanceEntry> performances)
    {
        if (finding.SuggestedPayableStart is null || finding.SuggestedPayableEnd is null)
        {
            return performances.Any(item =>
                string.Equals(item.ResourceId, finding.ResourceId, StringComparison.Ordinal)
                && item.Date == finding.Date
                && !item.IsCalendarSynthetic
                && !item.IsAbsence
                && !item.IsTravel
                && item.HfdTaakId != 5
                && item.HfdTaakId != 23
                && (string.IsNullOrWhiteSpace(finding.SuggestedProjectId)
                    || string.Equals(item.ProjectId, finding.SuggestedProjectId, StringComparison.OrdinalIgnoreCase)));
        }

        return performances.Any(item =>
            string.Equals(item.ResourceId, finding.ResourceId, StringComparison.Ordinal)
            && item.Date == finding.Date
            && !item.IsCalendarSynthetic
            && !item.IsAbsence
            && !item.IsTravel
            && item.HfdTaakId != 5
            && item.HfdTaakId != 23
            && item.Start is not null
            && item.End is not null
            && item.Start < finding.SuggestedPayableEnd
            && item.End > finding.SuggestedPayableStart
            && (string.IsNullOrWhiteSpace(finding.SuggestedProjectId)
                || string.Equals(item.ProjectId, finding.SuggestedProjectId, StringComparison.OrdinalIgnoreCase)));
    }

    private static PayrollActionEvidenceSnapshot DeserializeEvidence(string json) =>
        JsonSerializer.Deserialize<PayrollActionEvidenceSnapshot>(json, JsonOptions)
        ?? new PayrollActionEvidenceSnapshot(
            string.Empty,
            PayrollFindingType.MissingPlannedTechnicianPerformance,
            PayrollFindingSeverity.Info,
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            null,
            null,
            null,
            null,
            null);

    private static PayrollActionCreateProposal? DeserializeCreate(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "{}"
            ? null
            : JsonSerializer.Deserialize<PayrollActionCreateProposal>(json, JsonOptions);

    private static PayrollActionAdjustProposal? DeserializeAdjust(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "{}"
            ? null
            : JsonSerializer.Deserialize<PayrollActionAdjustProposal>(json, JsonOptions);

    private static PayrollActionDeleteProposal? DeserializeDelete(string json) =>
        string.IsNullOrWhiteSpace(json) || json == "{}"
            ? null
            : JsonSerializer.Deserialize<PayrollActionDeleteProposal>(json, JsonOptions);

    private static async Task<PayrollShadowMonth> RequireMonthAsync(
        TimeControlDbContext context,
        int year,
        int month,
        CancellationToken cancellationToken)
    {
        var shadowMonth = await context.PayrollShadowMonths
            .SingleOrDefaultAsync(item => item.Year == year && item.Month == month, cancellationToken);
        return shadowMonth
            ?? throw new InvalidOperationException($"Geen payroll shadow-maand voor {year}-{month:00}.");
    }

    private void EnsureActionsEnabled()
    {
        if (!shadowOptions.Value.Enabled)
        {
            throw new InvalidOperationException("PayrollShadow:Enabled=false.");
        }

        if (!actionsOptions.Value.Enabled)
        {
            throw new InvalidOperationException("PayrollActions:Enabled=false.");
        }
    }
}
