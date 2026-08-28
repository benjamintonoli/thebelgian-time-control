using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

public interface IMonthlyReviewService
{
    ReviewMonth GetDefaultMonth(DateTimeOffset now);

    Task<MonthlyReviewCockpit> GetCockpitAsync(
        ReviewMonth month,
        DailyReviewFilter filter,
        string? selectedCaseId,
        CancellationToken cancellationToken);

    Task<MonthlyPrepareResult> PrepareAsync(
        ReviewMonth month,
        string actor,
        string? existingEvidenceJsonPath,
        bool refresh,
        CancellationToken cancellationToken);

    Task<DailyReviewActionAudit> SaveDecisionAsync(
        ReviewMonth month,
        SaveDailyReviewDecision request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DailyReviewActionAudit>> GetAuditTrailAsync(
        string caseId,
        CancellationToken cancellationToken);

    Task<DailyCorrectionProposal?> GetLatestCorrectionProposalAsync(
        string caseId,
        CancellationToken cancellationToken);

    Task<CorrectionExecutionAvailability> GetCorrectionExecutionAvailabilityAsync(
        CancellationToken cancellationToken);

    Task<CorrectionExecutionResult> ExecuteCorrectionAsync(
        ReviewMonth month,
        long proposalId,
        string executedBy,
        CancellationToken cancellationToken);

    Task<CorrectionExecutionResult> ExecuteDirectCorrectionAsync(
        ReviewMonth month,
        ExecuteDirectCorrectionRequest request,
        CancellationToken cancellationToken);

    Task<MonthlyReviewPeriod> FinalizeAsync(
        ReviewMonth month,
        string finalizedBy,
        bool confirmOpenCases,
        CancellationToken cancellationToken);

    Task<string> BuildHtmlReportAsync(
        ReviewMonth month,
        CancellationToken cancellationToken);
}
