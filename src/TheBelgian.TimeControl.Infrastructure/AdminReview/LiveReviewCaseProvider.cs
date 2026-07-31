using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

/// <summary>
/// Architecture placeholder for a future live Plenion/Powerfleet-backed provider.
/// Not enabled in the MVP; OfflineReviewCaseProvider remains the active source.
/// </summary>
internal sealed class LiveReviewCaseProvider : IReviewCaseProvider
{
    public const bool IsEnabled = false;

    public string ProviderName => "LiveReviewCaseProvider";

    public bool LoadsLockedHoldout => false;

    public Task<IReadOnlyList<ReviewCase>> GetCasesAsync(CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            "LiveReviewCaseProvider is prepared but not enabled in this MVP. " +
            "Use OfflineReviewCaseProvider.");
    }

    public Task<ReviewCase?> GetByPerformanceIdAsync(
        long performanceId,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            "LiveReviewCaseProvider is prepared but not enabled in this MVP. " +
            "Use OfflineReviewCaseProvider.");
    }
}
