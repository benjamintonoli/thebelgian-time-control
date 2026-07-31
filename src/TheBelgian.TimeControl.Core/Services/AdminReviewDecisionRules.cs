using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

public static class AdminReviewDecisionRules
{
    public static void Validate(
        AdminReviewStatus decision,
        string? reviewer,
        string? comment)
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
    }

    /// <summary>
    /// Matcher acceptances always surface as Pending until an admin acts.
    /// </summary>
    public static AdminReviewStatus InitialReviewStatus(bool matcherProposedAcceptance) =>
        AdminReviewStatus.Pending;
}
