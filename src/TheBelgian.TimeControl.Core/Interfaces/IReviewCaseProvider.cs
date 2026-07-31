using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Interfaces;

/// <summary>
/// Supplies review cases without coupling UI to JSON, ODBC, Powerfleet, or Geoapify.
/// </summary>
public interface IReviewCaseProvider
{
    string ProviderName { get; }

    /// <summary>Must remain false for all Admin Review providers.</summary>
    bool LoadsLockedHoldout { get; }

    Task<IReadOnlyList<ReviewCase>> GetCasesAsync(CancellationToken cancellationToken);

    Task<ReviewCase?> GetByPerformanceIdAsync(
        long performanceId,
        CancellationToken cancellationToken);
}
