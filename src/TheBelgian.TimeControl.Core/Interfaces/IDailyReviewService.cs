using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IDailyReviewService
{
    Task<DailyReviewCockpit> GetCockpitAsync(
        DailyReviewFilter filter,
        string? selectedCaseId,
        CancellationToken cancellationToken);

    Task<DailyReviewCase?> GetCaseAsync(string caseId, CancellationToken cancellationToken);

    Task<DailyReviewActionAudit> SaveDecisionAsync(
        SaveDailyReviewDecision request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DailyReviewActionAudit>> GetAuditTrailAsync(
        string caseId,
        CancellationToken cancellationToken);

    Task<GeneratedFactualReport> GenerateFactualReportAsync(
        IReadOnlyList<string> caseIds,
        string generatedBy,
        CancellationToken cancellationToken);
}
