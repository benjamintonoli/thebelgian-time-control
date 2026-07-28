using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class CoverageGapReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ToMarkdown(CoverageGapAnalysisResult result)
    {
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "# Coverage-gap-analyse",
            string.Empty,
            "## 1. Medewerkerkoppeling",
            string.Empty,
            result.LinkingModelDescription.Trim(),
            string.Empty,
            "| Technieker | IDRESOURCE | RESCODE | OMSCHR | driverid | drivername | Sleutel | objectname (info) | kenteken (info) |",
            "|---|---|---|---|---|---|---|---|---|",
        };

        foreach (var link in result.EmployeeLinks)
        {
            lines.Add(
                "| " + link.PlenionOmschr +
                " | " + link.PlenionIdResource +
                " | " + link.PlenionResCode +
                " | " + link.PlenionOmschr +
                " | " + link.PowerfleetDriverId +
                " | " + link.PowerfleetDriverName +
                " | " + link.LinkingKey +
                " | " + string.Join(", ", link.InformativeObjectNames) +
                " | " + string.Join(", ", link.InformativePlates) +
                " |");
        }

        lines.Add(string.Empty);
        lines.Add("## 2. Oorzaak huidige betrouwbare graad");
        lines.Add(string.Empty);
        lines.Add(string.Create(
            culture,
            $"- Locatieresoluties: {result.MatchBreakdown.TotalLocationResolutions}"));
        lines.Add(string.Create(
            culture,
            $"- Betrouwbaar (Confirmed+Probable): {result.MatchBreakdown.ReliableCount} ({result.MatchBreakdown.ReliablePercent}%)"));
        lines.Add(string.Create(
            culture,
            $"- Onbetrouwbaar: {result.MatchBreakdown.UnreliableCount} ({result.MatchBreakdown.UnreliablePercent}%)"));
        lines.Add(string.Create(
            culture,
            $"- Confirmed {result.MatchBreakdown.ConfirmedCount}; Probable {result.MatchBreakdown.ProbableCount}; Manual {result.MatchBreakdown.ManualReviewCount}; None {result.MatchBreakdown.NoReliableMatchCount}; AddressIssue {result.MatchBreakdown.AddressDataIssueCount}"));
        lines.Add("- " + result.MatchBreakdown.PrimaryCause);
        lines.Add(string.Empty);
        lines.Add("## 3. Onbetrouwbare locatiegroepen");
        lines.Add(string.Empty);
        lines.Add(string.Create(
            culture,
            $"Unieke probleemlocaties (Plenion-sleutel): {result.AliasProjection.UniqueProblemLocations}"));
        lines.Add(string.Create(
            culture,
            $"Unieke bevestigbare alias-paren: {result.AliasProjection.UniqueConfirmableAliases}"));
        lines.Add(string.Empty);
        lines.Add(
            "| Plenion LACLEUNIK | Plenion-adres | Powerfleet-stop | Coördinaten | Prestaties | Techniekers | Gem. afstand | Gem. overlap | Status | Reden |");
        lines.Add("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var group in result.UnreliableGroups)
        {
            lines.Add(FormatGroupRow(group, culture));
        }

        lines.Add(string.Empty);
        lines.Add("## 4. Top 20 locatiebevestigingen");
        lines.Add(string.Empty);
        lines.Add(string.Create(
            culture,
            $"Winst bij top 20: {result.AliasProjection.Top20GainPerformances} prestaties"));
        lines.Add(string.Empty);
        var rank = 1;
        foreach (var group in result.TopConfirmations)
        {
            lines.Add(string.Create(
                culture,
                $"{rank}. +{group.PerformanceCount} — LACLEUNIK {group.PlenionLacleunik} / {group.PlenionAddress} ↔ {group.PowerfleetStopAddress} ({group.AverageDistanceMeters} m, {group.DominantMatchStatus})"));
            rank++;
        }

        lines.Add(string.Empty);
        lines.Add("## 5. Alias-projectie en advies");
        lines.Add(string.Empty);
        lines.Add(string.Create(
            culture,
            $"- Prestaties flipbaar via alias: {result.AliasProjection.PerformancesFlippedIfAllAliasesConfirmed}"));
        lines.Add(string.Create(
            culture,
            $"- Zonder stopkandidaat: {result.AliasProjection.UnreliableWithoutCandidateStop}"));
        lines.Add(string.Create(
            culture,
            $"- Potentiële betrouwbare graad na bevestiging: {result.AliasProjection.PotentialReliablePercentAfterAliasConfirmation}%"));
        lines.Add("- " + result.AliasTableAdvice);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToJson(CoverageGapAnalysisResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);

    private static string FormatGroupRow(
        CoverageGapLocationGroup group,
        CultureInfo culture) =>
        string.Create(
            culture,
            $"| {group.PlenionLacleunik} | {group.PlenionAddress} | {group.PowerfleetStopAddress} | " +
            $"{FormatCoordinates(group.PowerfleetLatitude, group.PowerfleetLongitude)} | " +
            $"{group.PerformanceCount} | {string.Join(", ", group.Technicians)} | " +
            $"{FormatDistance(group.AverageDistanceMeters)} | {group.AverageTimeOverlapMinutes} min | " +
            $"{group.DominantMatchStatus} | {group.UncertaintyReason} |");

    private static string FormatCoordinates(double? latitude, double? longitude) =>
        latitude is null || longitude is null
            ? "n.v.t."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{latitude:0.######}, {longitude:0.######}");

    private static string FormatDistance(double? meters) =>
        meters is null
            ? "n.v.t."
            : string.Create(CultureInfo.InvariantCulture, $"{meters:0.#} m");
}
