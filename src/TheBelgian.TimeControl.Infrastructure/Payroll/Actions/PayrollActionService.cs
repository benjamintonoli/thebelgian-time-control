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
            gateMessage);
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

        var finding = await ResolvePrimaryFindingAsync(context, action, cancellationToken);
        var employee = await context.PayrollShadowEmployeeResults.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ShadowMonthId == action.ShadowMonthId && item.ResourceId == action.ResourceId,
                cancellationToken);
        var performances = await performanceSource.ReadPerformancesAsync(
            month.PeriodStart,
            month.PeriodEnd,
            [action.ResourceId],
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
        PayrollActionCreateProposal? storedCreate = null;
        PayrollActionAdjustProposal? storedAdjust = null;
        if (action.ActionType == PayrollProposedActionType.CreateMissingPerformance)
        {
            storedCreate = DeserializeCreate(action.ProposalSnapshotJson);
            if (storedCreate is not null)
            {
                eligibilityContext = eligibilityContext with
                {
                    ProvenMainTaskId = storedCreate.MainTaskId,
                    IntervalSemanticsOverride = storedCreate.IntervalSemantics,
                };
            }
        }
        else
        {
            storedAdjust = DeserializeAdjust(action.ProposalSnapshotJson);
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
            action.ActionId.ToString("N"));

        var response = await createClient.CreateAsync(command, cancellationToken);
        if (string.Equals(response.Status, "contract_unproven", StringComparison.OrdinalIgnoreCase))
        {
            return await MarkFailedAsync(context, action, actor,
                response.Message ?? "PWS create-contract is unproven; niet Applied.",
                cancellationToken,
                response.Reference);
        }

        if (!IsSuccessStatus(response.Status) || response.PerformanceId is null)
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
            cancellationToken);

        return new PayrollActionExecutionResult(
            action.ActionId,
            action.Status,
            message ?? "Actie uitgevoerd.",
            reference,
            performanceId);
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
            // Never copy peer MainTaskId — ProvenMainTaskId stays null unless independently proven later.
            return new PayrollActionEligibilityContext(
                included,
                finalized,
                hasMatch,
                ProvenMainTaskId: null,
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
