using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

public static class AdminReviewDecisionRules
{
    public static void Validate(
        AdminReviewStatus decision,
        string? reviewer,
        string? comment,
        string? proposedVisitCandidateId = null,
        string? chosenVisitCandidateId = null)
    {
        if (decision == AdminReviewStatus.Pending)
        {
            throw new InvalidOperationException(
                "Pending is de startstatus; sla geen Pending-beslissing op.");
        }

        if (string.IsNullOrWhiteSpace(reviewer))
        {
            throw new InvalidOperationException(
                "Bevestiging of andere adminbeslissing vereist een reviewer.");
        }

        if (decision == AdminReviewStatus.Rejected && string.IsNullOrWhiteSpace(comment))
        {
            throw new InvalidOperationException(
                "Rejected vereist een reden (opmerking).");
        }

        var choseDifferent =
            !string.IsNullOrWhiteSpace(chosenVisitCandidateId) &&
            !string.Equals(
                chosenVisitCandidateId.Trim(),
                proposedVisitCandidateId?.Trim(),
                StringComparison.Ordinal);
        if (choseDifferent && string.IsNullOrWhiteSpace(comment))
        {
            throw new InvalidOperationException(
                "Selectie van een andere kandidaat vereist een opmerking.");
        }
    }

    /// <summary>
    /// Every new case starts as Pending until an admin acts.
    /// </summary>
    public static AdminReviewStatus InitialReviewStatus() => AdminReviewStatus.Pending;
}
