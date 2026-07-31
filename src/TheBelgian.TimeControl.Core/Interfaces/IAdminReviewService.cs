using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IAdminReviewService
{
    string DataSourceName { get; }

    Task<IReadOnlyList<ReviewCase>> SearchAsync(
        AdminReviewFilter filter,
        CancellationToken cancellationToken);

    Task<ReviewCase?> GetAsync(
        long performanceId,
        CancellationToken cancellationToken);

    Task<AdminReviewDecisionAudit> RecordDecisionAsync(
        long performanceId,
        AdminReviewStatus decision,
        string reviewer,
        string? comment,
        string? chosenVisitCandidateId,
        IReadOnlyList<string>? chosenVisitSourceStopIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AdminReviewDecisionAudit>> GetAuditTrailAsync(
        long performanceId,
        CancellationToken cancellationToken);
}
