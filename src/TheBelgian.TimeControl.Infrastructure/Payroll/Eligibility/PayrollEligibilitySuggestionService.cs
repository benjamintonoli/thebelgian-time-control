using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Eligibility;

public static class PayrollEligibilitySuggestionService
{
    public static (PayrollEligibilityStatus? SuggestedEligibility, string? SuggestedReason) Suggest(
        PayrollEmployeeCandidate candidate,
        DateOnly periodStart)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.EmploymentEndDate is not null && candidate.EmploymentEndDate < periodStart)
        {
            return (PayrollEligibilityStatus.Excluded, "Uit dienst vóór begin van de loonperiode.");
        }

        if (ContainsMarker(candidate.DisplayName, "(OA)"))
        {
            return (PayrollEligibilityStatus.Excluded, "Naam suggereert onderaannemer (OA).");
        }

        if (ContainsMarker(candidate.DisplayName, "stagiair")
            || ContainsMarker(candidate.DisplayName, "intern"))
        {
            return (PayrollEligibilityStatus.Excluded, "Naam suggereert stagiair/intern.");
        }

        if (candidate.AcertaIdentityStatus == AcertaIdentityStatus.Missing)
        {
            return (null, "Acerta-identiteit ontbreekt in bron.");
        }

        return (null, null);
    }

    public static (PayrollEligibilityStatus? SuggestedEligibility, string? SuggestedReason) SuggestPowerBiPresence(
        PayrollEmployeeCandidate candidate,
        DateOnly periodStart,
        bool presentInPowerBiOverview)
    {
        var baseSuggestion = Suggest(candidate, periodStart);
        if (!presentInPowerBiOverview)
        {
            return baseSuggestion;
        }

        var reason = baseSuggestion.SuggestedReason is null
            ? "Aanwezig in ververst Power BI-overzicht (referentie, geen beslissing)."
            : $"{baseSuggestion.SuggestedReason} Aanwezig in ververst Power BI-overzicht (referentie).";
        return (baseSuggestion.SuggestedEligibility, reason);
    }

    private static bool ContainsMarker(string? value, string marker) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(marker, StringComparison.OrdinalIgnoreCase);
}
