using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IAdminReviewService
{
    Task<IReadOnlyList<AdminReviewCase>> SearchAsync(
        AdminReviewFilter filter,
        CancellationToken cancellationToken);

    Task<AdminReviewCase?> GetAsync(
        long performanceId,
        string technician,
        DateOnly performanceDate,
        CancellationToken cancellationToken);

    Task<AdminReviewDecisionAudit> RecordDecisionAsync(
        long performanceId,
        string technician,
        DateOnly performanceDate,
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
