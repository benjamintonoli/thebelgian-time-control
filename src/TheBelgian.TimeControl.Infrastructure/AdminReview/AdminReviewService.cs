using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

/// <summary>
/// Read-only admin review orchestration. Never writes to Plenion.
/// </summary>
internal sealed class AdminReviewService(
    IReviewCaseProvider caseProvider,
    IReviewExplanationService explanationService,
    AdminReviewDecisionRepository decisionRepository,
    TimeProvider timeProvider) : IAdminReviewService
{
    public string DataSourceName => caseProvider.ProviderName;

    public async Task<AdminReviewSearchResult> SearchAsync(
        AdminReviewFilter filter,
        CancellationToken cancellationToken)
    {
        EnsureSafeProvider();
        var cases = await LoadWithAdminOverlayAsync(cancellationToken);
        return SpotcheckPriorityCalculator.ApplyFilterAndPage(
            cases,
            filter,
            caseProvider.UniqueCaseCount,
            caseProvider.DuplicatesRemoved,
            caseProvider.RawCaseCount);
    }

    public async Task<ReviewCase?> GetAsync(
        long performanceId,
        CancellationToken cancellationToken)
    {
        EnsureSafeProvider();
        var cases = await LoadWithAdminOverlayAsync(cancellationToken);
        var match = cases.FirstOrDefault(item => item.PerformanceId == performanceId);
        if (match is null)
        {
            return null;
        }

        var explanation = explanationService.Explain(match.Source, match.Matcher);
        return match with { DeterministicExplanation = explanation };
    }

    public async Task<AdminReviewDecisionAudit> RecordDecisionAsync(
        long performanceId,
        AdminReviewStatus decision,
        string reviewer,
        string? comment,
        string? chosenVisitCandidateId,
        IReadOnlyList<string>? chosenVisitSourceStopIds,
        CancellationToken cancellationToken)
    {
        EnsureSafeProvider();
        var current = await GetAsync(performanceId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Reviewcase {performanceId} niet gevonden.");

        AdminReviewDecisionRules.Validate(
            decision,
            reviewer,
            comment,
            current.Matcher.ProposedVisit?.VisitCandidateId,
            chosenVisitCandidateId);

        IReadOnlyList<string>? chosenStops = chosenVisitSourceStopIds;
        if (chosenStops is null && !string.IsNullOrWhiteSpace(chosenVisitCandidateId))
        {
            chosenStops = current.Matcher.CandidateVisits
                .FirstOrDefault(item =>
                    string.Equals(
                        item.VisitCandidateId,
                        chosenVisitCandidateId,
                        StringComparison.Ordinal))
                ?.ConstituentStopIds;
        }

        if (decision == AdminReviewStatus.Confirmed &&
            string.IsNullOrWhiteSpace(chosenVisitCandidateId) &&
            current.Matcher.ProposedVisit is not null)
        {
            chosenVisitCandidateId = current.Matcher.ProposedVisit.VisitCandidateId;
            chosenStops = current.Matcher.ProposedVisit.ConstituentStopIds;
        }

        var audit = new AdminReviewDecisionAudit
        {
            PerformanceId = performanceId,
            OriginalMatcherStatus = current.Matcher.MatcherStatus,
            ProposedVisitCandidateId = current.Matcher.ProposedVisit?.VisitCandidateId,
            ProposedVisitSourceStopIdsJson = AdminReviewDecisionRepository.SerializeStopIds(
                current.Matcher.ProposedVisit?.ConstituentStopIds),
            ChosenVisitCandidateId = chosenVisitCandidateId,
            ChosenVisitSourceStopIdsJson =
                AdminReviewDecisionRepository.SerializeStopIds(chosenStops),
            Decision = decision.ToString(),
            ReasonOrComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            Reviewer = reviewer.Trim(),
            DecidedAt = timeProvider.GetUtcNow(),
            MatcherCommit = current.Matcher.MatcherCommit,
            ConfigurationHash = current.Matcher.ConfigurationHash,
        };

        // Append-only: never update prior rows; never mutate SourceEvidence or MatcherAssessment.
        return await decisionRepository.AppendAsync(audit, cancellationToken);
    }

    public Task<IReadOnlyList<AdminReviewDecisionAudit>> GetAuditTrailAsync(
        long performanceId,
        CancellationToken cancellationToken) =>
        decisionRepository.ListForPerformanceAsync(performanceId, cancellationToken);

    private async Task<IReadOnlyList<ReviewCase>> LoadWithAdminOverlayAsync(
        CancellationToken cancellationToken)
    {
        var baseCases = await caseProvider.GetCasesAsync(cancellationToken);
        var latest = await decisionRepository.LatestByPerformanceAsync(
            baseCases.Select(item => item.PerformanceId).ToArray(),
            cancellationToken);

        var withAdmin = baseCases
            .Select(item =>
            {
                if (!latest.TryGetValue(item.PerformanceId, out var audit))
                {
                    return item;
                }

                return item with
                {
                    Admin = new AdminDecision(
                        Status: Enum.TryParse<AdminReviewStatus>(audit.Decision, out var status)
                            ? status
                            : AdminReviewStatus.Pending,
                        Reviewer: audit.Reviewer,
                        Comment: audit.ReasonOrComment,
                        ChosenVisitCandidateId: audit.ChosenVisitCandidateId,
                        ChosenVisitSourceStopIds:
                            AdminReviewDecisionRepository.DeserializeStopIds(
                                audit.ChosenVisitSourceStopIdsJson)),
                };
            })
            .ToArray();

        var recurringIds = RecurringConfirmedPatternDetector.DetectPerformanceIds(withAdmin);
        return withAdmin
            .Select(item => SpotcheckPriorityCalculator.WithDerivedFields(
                item,
                recurringPattern: recurringIds.Contains(item.PerformanceId)))
            .ToArray();
    }

    private void EnsureSafeProvider()
    {
        if (caseProvider.LoadsLockedHoldout)
        {
            throw new InvalidOperationException(
                "Admin Review mag locked holdoutbestanden niet laden.");
        }

        if (MatcherUsagePolicy.PlenionWritebackAllowed)
        {
            throw new InvalidOperationException(
                "Plenion-writeback is niet toegestaan in Admin Review.");
        }
    }
}
