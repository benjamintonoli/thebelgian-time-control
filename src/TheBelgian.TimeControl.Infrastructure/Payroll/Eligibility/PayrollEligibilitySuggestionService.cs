using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Legacy;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Eligibility;

public static class PayrollEligibilitySuggestionService
{
    public static (PayrollEligibilityStatus? SuggestedEligibility, string? SuggestedReason) Suggest(
        PayrollEmployeeCandidate candidate,
        DateOnly periodStart)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!PayrollRosterEmploymentWindow.IsAutoEligibleOn(candidate.EmploymentEndDate, periodStart))
        {
            return (PayrollEligibilityStatus.Excluded, "Uit dienst buiten payroll-grace (volledige maand na uitdienst).");
        }

        if (LegacyPayrollNameMarkers.IsLegacyOaMarker(candidate.DisplayName))
        {
            return (PayrollEligibilityStatus.Excluded, "Legacy naammarker OA (voorstel, geen beslissing).");
        }

        if (LegacyPayrollNameMarkers.IsLegacyStagiairMarker(candidate.DisplayName))
        {
            return (PayrollEligibilityStatus.Excluded, "Legacy naammarker stagiair (voorstel, geen beslissing).");
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
}
