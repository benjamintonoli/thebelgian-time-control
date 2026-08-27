using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

internal sealed class DailyReviewService(
    DailyAuditReviewCaseProvider provider,
    DailyReviewRepository repository,
    TimeProvider timeProvider) : IDailyReviewService
{
    public async Task<DailyReviewCockpit> GetCockpitAsync(
        DailyReviewFilter filter,
        string? selectedCaseId,
        CancellationToken cancellationToken)
    {
        var all = await LoadWithDecisionsAsync(cancellationToken);
        var filtered = Filter(all, filter).ToArray();
        var selected = !string.IsNullOrWhiteSpace(selectedCaseId)
            ? all.FirstOrDefault(item => item.CaseId == selectedCaseId)
            : filtered.FirstOrDefault();
        var recent = selected is null
            ? []
            : all.Where(item =>
                    item.CaseId != selected.CaseId &&
                    string.Equals(item.Technician, selected.Technician, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Date)
                .Take(8)
                .ToArray();
        return new DailyReviewCockpit(filtered, selected, recent, Counts(all));
    }

    public async Task<DailyReviewCase?> GetCaseAsync(
        string caseId,
        CancellationToken cancellationToken) =>
        (await LoadWithDecisionsAsync(cancellationToken))
        .FirstOrDefault(item => item.CaseId == caseId);

    public async Task<DailyReviewActionAudit> SaveDecisionAsync(
        SaveDailyReviewDecision request,
        CancellationToken cancellationToken)
    {
        var reviewCase = await GetCaseAsync(request.CaseId, cancellationToken)
            ?? throw new InvalidOperationException("Reviewcase niet gevonden.");
        Validate(request);
        var now = timeProvider.GetUtcNow();
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
            EvidenceSnapshotJson = reviewCase.EvidenceSnapshotJson,
            AlgorithmVersion = reviewCase.AlgorithmVersion,
        };
        DailyCorrectionProposal? proposal = null;
        if (request.Status == DailyReviewWorkflowStatus.PendingCorrection)
        {
            proposal = new DailyCorrectionProposal
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
                Status = DailyReviewWorkflowStatus.PendingCorrection.ToString(),
            };
        }

        return await repository.SaveAsync(action, proposal, cancellationToken);
    }

    public Task<IReadOnlyList<DailyReviewActionAudit>> GetAuditTrailAsync(
        string caseId,
        CancellationToken cancellationToken) => repository.ListAsync(caseId, cancellationToken);

    public async Task<GeneratedFactualReport> GenerateFactualReportAsync(
        IReadOnlyList<string> caseIds,
        string generatedBy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(generatedBy))
        {
            throw new InvalidOperationException("Vul in wie het feitenrapport selecteert.");
        }

        var selectedIds = caseIds.Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var all = await LoadWithDecisionsAsync(cancellationToken);
        var cases = selectedIds.Select(id => all.FirstOrDefault(item => item.CaseId == id))
            .Where(item => item is not null)
            .Cast<DailyReviewCase>()
            .ToArray();
        if (cases.Length != selectedIds.Length)
        {
            throw new InvalidOperationException("Een of meer geselecteerde cases bestaan niet.");
        }

        if (cases.Any(item => item.Decision.Status == DailyReviewWorkflowStatus.Open))
        {
            throw new InvalidOperationException(
                "Een feitenrapport bevat alleen expliciet beoordeelde cases.");
        }

        var now = timeProvider.GetUtcNow();
        var content = DailyFactualReportBuilder.Build(cases, generatedBy.Trim(), now);
        var id = await repository.SaveReportAsync(
            cases[0].Technician,
            selectedIds,
            content,
            generatedBy.Trim(),
            now,
            cancellationToken);
        var safeName = string.Concat(cases[0].Technician.Where(char.IsLetterOrDigit));
        return new GeneratedFactualReport(
            id,
            $"time-control-feiten-{safeName}-{now:yyyyMMdd-HHmm}.txt",
            content);
    }

    private async Task<IReadOnlyList<DailyReviewCase>> LoadWithDecisionsAsync(
        CancellationToken cancellationToken)
    {
        var source = await provider.GetCasesAsync(cancellationToken);
        var latest = await repository.LatestAsync(
            source.Select(item => item.CaseId).ToArray(),
            cancellationToken);
        return source.Select(item => latest.TryGetValue(item.CaseId, out var action)
                ? item with { Decision = ToDecision(action) }
                : item)
            .ToArray();
    }

    private static DailyReviewDecision ToDecision(DailyReviewActionAudit action) => new(
        Enum.TryParse<DailyReviewWorkflowStatus>(action.Decision, out var status)
            ? status
            : DailyReviewWorkflowStatus.Open,
        Enum.TryParse<ReviewFeedbackReason>(action.DecisionReason, out var reason)
            ? reason
            : null,
        action.Notes,
        action.ReviewedBy,
        action.ReviewedAt,
        null,
        null);

    private static IEnumerable<DailyReviewCase> Filter(
        IReadOnlyList<DailyReviewCase> source,
        DailyReviewFilter filter)
    {
        var query = source.AsEnumerable();
        query = filter.View switch
        {
            DailyReviewQueueView.Open => query.Where(item =>
                item.Decision.Status == DailyReviewWorkflowStatus.Open),
            DailyReviewQueueView.ToReview => query.Where(item =>
                item.Decision.Status is DailyReviewWorkflowStatus.PendingCorrection or
                    DailyReviewWorkflowStatus.AwaitingExplanation or
                    DailyReviewWorkflowStatus.EscalatedForManagementReview),
            DailyReviewQueueView.Completed => query.Where(item =>
                item.Decision.Status == DailyReviewWorkflowStatus.ResolvedNoAction),
            _ => query,
        };
        if (!string.IsNullOrWhiteSpace(filter.Technician))
        {
            query = query.Where(item => item.Technician.Contains(
                filter.Technician.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }

        if (filter.Date is { } date)
        {
            query = query.Where(item => item.Date == date);
        }

        if (filter.Evidence is { } evidence)
        {
            query = query.Where(item => item.EvidenceLevel == evidence);
        }

        if (filter.EscalatedOnly)
        {
            query = query.Where(item =>
                item.Decision.Status == DailyReviewWorkflowStatus.EscalatedForManagementReview);
        }

        query = filter.Boundary switch
        {
            DailyReviewBoundaryFilter.Start => query.Where(item =>
                item.First.SignedDifferenceMinutes is not null),
            DailyReviewBoundaryFilter.End => query.Where(item =>
                item.Last.SignedDifferenceMinutes is not null),
            _ => query,
        };
        return filter.Sort switch
        {
            DailyReviewSort.DateAscending => query.OrderBy(item => item.Date)
                .ThenBy(item => item.Technician),
            DailyReviewSort.DateDescending => query.OrderByDescending(item => item.Date)
                .ThenBy(item => item.Technician),
            DailyReviewSort.Technician => query.OrderBy(item => item.Technician)
                .ThenBy(item => item.Date),
            _ => query.OrderByDescending(item => item.MaximumAbsoluteDifferenceMinutes)
                .ThenBy(item => item.Date),
        };
    }

    private static DailyReviewCounts Counts(IReadOnlyList<DailyReviewCase> cases) => new(
        Open: cases.Count(item => item.Decision.Status == DailyReviewWorkflowStatus.Open),
        ToReview: cases.Count(item => item.Decision.Status is
            DailyReviewWorkflowStatus.PendingCorrection or
            DailyReviewWorkflowStatus.AwaitingExplanation or
            DailyReviewWorkflowStatus.EscalatedForManagementReview),
        Completed: cases.Count(item =>
            item.Decision.Status == DailyReviewWorkflowStatus.ResolvedNoAction),
        Total: cases.Count);

    private static void Validate(SaveDailyReviewDecision request)
    {
        if (request.Status == DailyReviewWorkflowStatus.Open)
        {
            throw new InvalidOperationException("Open is geen adminbeslissing.");
        }

        if (string.IsNullOrWhiteSpace(request.Reviewer))
        {
            throw new InvalidOperationException("Reviewer is verplicht.");
        }

        if (request.Reason is null)
        {
            throw new InvalidOperationException("Selecteer een reden.");
        }

        if (request.Status == DailyReviewWorkflowStatus.PendingCorrection &&
            request.ProposedStart is null && request.ProposedEnd is null)
        {
            throw new InvalidOperationException(
                "Vul minstens één voorgestelde gecorrigeerde tijd in.");
        }

        if (request.Status is DailyReviewWorkflowStatus.AwaitingExplanation or
                DailyReviewWorkflowStatus.EscalatedForManagementReview &&
            string.IsNullOrWhiteSpace(request.Notes))
        {
            throw new InvalidOperationException("Deze actie vereist een korte notitie.");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
