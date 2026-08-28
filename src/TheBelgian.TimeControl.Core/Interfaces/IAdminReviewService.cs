using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IAdminReviewService
{
    string DataSourceName { get; }

    bool IsLivePilot { get; }

    LivePilotSummary? LivePilotSummary { get; }

    Task<AdminReviewSearchResult> SearchAsync(
        AdminReviewFilter filter,
        CancellationToken cancellationToken);

    Task<ReviewCase?> GetAsync(
        long performanceId,
        CancellationToken cancellationToken);

    Task RecordCaseOpenedAsync(
        long performanceId,
        CancellationToken cancellationToken);

    Task<AdminReviewDecisionAudit> RecordDecisionAsync(
        long performanceId,
        AdminReviewStatus decision,
        string reviewer,
        string? reviewerSubject,
        string? comment,
        string? chosenVisitCandidateId,
        IReadOnlyList<string>? chosenVisitSourceStopIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AdminReviewDecisionAudit>> GetAuditTrailAsync(
        long performanceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AdminReviewSessionMetric>> GetSessionMetricsAsync(
        long performanceId,
        CancellationToken cancellationToken);
}
