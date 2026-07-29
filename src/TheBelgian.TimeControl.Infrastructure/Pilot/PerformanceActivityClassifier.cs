using System.Globalization;
using System.Text;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class PerformanceActivityClassifier
{
    private static readonly string[] AbsenceMarkers =
    [
        "verlof", "ziekte", "afwezig", "recup", "feestdag", "klein verlet", "arbeidsongeschikt",
    ];

    private static readonly string[] BreakMarkers =
    [
        "pauze", "middagpauze", "lunch", "break", "rusttijd",
    ];

    private static readonly string[] RemoteMarkers =
    [
        "thuiswerk", "telewerk", "remote", "van thuis", "home office",
    ];

    private static readonly string[] AdministrationMarkers =
    [
        "administratie", "admin ", " administratie", "planning", "opvolging",
        "rapport", "facturatie", "bestelling", "mailbox", "mailen",
    ];

    private static readonly string[] OfficeMarkers =
    [
        "kantoor", "office", "intern overleg", "toolbox", "werkplaats intern",
    ];

    private static readonly string[] SiteMarkers =
    [
        "werf", "montage", "installatie", "plaatsing", "inbedrijfstelling", "commissioning",
    ];

    private static readonly string[] CustomerMarkers =
    [
        "onderhoud", "herstelling", "interventie", "service", "storing",
        "controle", "keuring", "nazicht", "reparatie", "klant",
    ];

    private static readonly string[] OtherNonLocationMarkers =
    [
        "opleiding", "training", "vorming", "vergadering", "meeting", "toolboxmeeting",
    ];

    public static PerformanceActivityClassification Classify(
        NormalizedPilotPerformance performance,
        string technicianName,
        PilotLocationResolution? resolution)
    {
        var text = Combine(
            performance.Description,
            performance.Comment,
            performance.ProjectName,
            performance.CustomerOrSiteName,
            performance.MainTaskExternalId);
        var (type, reason) = ResolveType(performance, text);
        var requiresGeo = RequiresGeographicMatch(type);
        var wasInDenominator = resolution is not null;
        var incorrectlyInDenominator = wasInDenominator && !requiresGeo;
        return new PerformanceActivityClassification(
            performance.ExternalId,
            performance.Date,
            technicianName,
            type,
            requiresGeo,
            performance.MainTaskExternalId,
            performance.Description,
            performance.ProjectNumber,
            performance.ProjectName,
            performance.WorkOrderNumber,
            performance.DeliveryAddressExternalId,
            reason,
            resolution?.MatchStatus,
            wasInDenominator,
            incorrectlyInDenominator);
    }

    internal static (PerformanceActivityType Type, string Reason) ResolveType(
        NormalizedPilotPerformance performance,
        string normalizedText)
    {
        if (ContainsAny(normalizedText, AbsenceMarkers))
        {
            return (PerformanceActivityType.Absence, "Afwezigheidsmarkering in omschrijving/project.");
        }

        if (ContainsAny(normalizedText, BreakMarkers))
        {
            return (PerformanceActivityType.Break, "Pauze-/breakmarkering in omschrijving.");
        }

        if (DailyTimelineFactory.IsTravelPerformance(
                new PlenionPerformance { Description = performance.Description }) ||
            ContainsAny(normalizedText, ["verplaats", "rijtijd", "transport", "onderweg"]))
        {
            return (PerformanceActivityType.Travel, "Verplaatsingsmarkering (HFDTAAK/OMSCHR).");
        }

        if (ContainsAny(normalizedText, RemoteMarkers))
        {
            return (PerformanceActivityType.RemoteWork, "Thuis-/remote-markering.");
        }

        if (ContainsAny(normalizedText, AdministrationMarkers))
        {
            return (
                PerformanceActivityType.Administration,
                "Administratieve markering; geen verplichte voertuigstop.");
        }

        if (ContainsAny(normalizedText, SiteMarkers))
        {
            return (
                PerformanceActivityType.SiteWork,
                "Werf-/installatiemarkering; koppelt aan werflocatie.");
        }

        if (ContainsAny(normalizedText, CustomerMarkers) ||
            HasCustomerLocation(performance))
        {
            return (
                PerformanceActivityType.CustomerWork,
                HasCustomerLocation(performance) &&
                !ContainsAny(normalizedText, CustomerMarkers)
                    ? "Klant-/leveradres aanwezig; behandeld als klantwerk."
                    : "Klantwerkmarkering in omschrijving.");
        }

        if (ContainsAny(normalizedText, OfficeMarkers))
        {
            return (
                PerformanceActivityType.OfficeWork,
                "Kantoormarkering; koppelt aan erkende kantoorlocatie.");
        }

        if (ContainsAny(normalizedText, OtherNonLocationMarkers))
        {
            return (
                PerformanceActivityType.OtherNonLocationBound,
                "Opleiding/vergadering zonder verplichte klantstop.");
        }

        return (
            PerformanceActivityType.Unknown,
            "Geen betrouwbare HFDTAAK/OMSCHR-regel; manuele classificatie nodig.");
    }

    public static bool RequiresGeographicMatch(PerformanceActivityType type) =>
        type is PerformanceActivityType.CustomerWork
            or PerformanceActivityType.SiteWork
            or PerformanceActivityType.OfficeWork;

    private static bool HasCustomerLocation(NormalizedPilotPerformance performance) =>
        !string.IsNullOrWhiteSpace(performance.DeliveryAddressExternalId) ||
        !string.IsNullOrWhiteSpace(performance.Street) ||
        !string.IsNullOrWhiteSpace(performance.CustomerOrSiteName);

    private static string Combine(params string?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            builder.Append(' ');
            builder.Append(part.ToLowerInvariant());
        }

        return builder.ToString();
    }

    private static bool ContainsAny(string text, IReadOnlyList<string> markers) =>
        markers.Any(marker =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
