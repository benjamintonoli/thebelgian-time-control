using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class ActivityClassificationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ToMarkdown(ActivityClassificationAnalysisResult result)
    {
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "# Activity-classification pilot",
            string.Empty,
            "## Verdeling per prestatietype",
            string.Empty,
            "| Type | Aantal | HFDTAAK | Omschrijvingen | Geo vereist | Onterecht in noemer | Unknown |",
            "|---|---:|---|---|---:|---:|---:|",
        };

        foreach (var summary in result.TypeSummaries)
        {
            lines.Add(
                string.Create(
                    culture,
                    $"| {summary.ActivityType} | {summary.PerformanceCount} | " +
                    $"{string.Join(", ", summary.MainTaskCodes)} | " +
                    $"{string.Join(" / ", summary.Descriptions.Take(5))} | " +
                    $"{summary.RequiresGeographicMatchCount} | " +
                    $"{summary.IncorrectlyInLocationDenominatorCount} | " +
                    $"{summary.UnknownCount} |"));
        }

        lines.Add(string.Empty);
        lines.Add("## Open locatiegevallen (niet-betrouwbaar en niet-aliasbaar ≤500 m)");
        lines.Add(string.Empty);
        lines.Add(string.Create(
            culture,
            $"- Open: {result.OpenCases.OpenCaseCount}"));
        lines.Add(string.Create(
            culture,
            $"- Niet locatiegebonden: {result.OpenCases.NotLocationBoundCount}"));
        lines.Add(string.Create(
            culture,
            $"- Nog wel locatiegebonden: {result.OpenCases.StillLocationBoundCount}"));
        lines.Add(string.Create(
            culture,
            $"- Unknown: {result.OpenCases.UnknownCount}"));
        lines.Add(string.Empty);
        lines.Add("## Gecorrigeerde matchgraad (alleen CustomerWork/SiteWork/OfficeWork)");
        lines.Add(string.Empty);
        lines.Add(string.Create(
            culture,
            $"- Locatiegebonden resoluties: {result.CorrectedMatch.LocationBoundResolutionCount}"));
        lines.Add(string.Create(
            culture,
            $"- Betrouwbaar: {result.CorrectedMatch.ReliableLocationBoundCount} ({result.CorrectedMatch.CorrectedReliablePercent}%)"));
        lines.Add(string.Create(
            culture,
            $"- Resterende NoReliableMatch: {result.CorrectedMatch.RemainingNoReliableMatchCount}"));
        lines.Add(string.Create(
            culture,
            $"- Alias-flipbaar (≤500 m): {result.CorrectedMatch.AliasFlippableLocationBoundCount}"));
        lines.Add(string.Create(
            culture,
            $"- Potentieel na KnownLocationAlias: {result.CorrectedMatch.PotentialReliablePercentAfterAliases}%"));
        lines.Add(string.Empty);
        lines.Add("## Advies");
        lines.Add(string.Empty);
        lines.Add(result.AliasAdvice);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToJson(ActivityClassificationAnalysisResult result) =>
        JsonSerializer.Serialize(
            new
            {
                result.TypeSummaries,
                result.OpenCases.OpenCaseCount,
                result.OpenCases.NotLocationBoundCount,
                result.OpenCases.StillLocationBoundCount,
                result.OpenCases.UnknownCount,
                result.CorrectedMatch,
                result.AliasAdvice,
                OpenCases = result.OpenCases.Cases.Select(item => new
                {
                    item.PerformanceId,
                    item.Date,
                    item.TechnicianName,
                    item.ActivityType,
                    item.RequiresGeographicMatch,
                    item.MainTaskExternalId,
                    item.Description,
                    item.LocationMatchStatus,
                    item.IncorrectlyInLocationDenominator,
                    item.Reason,
                }),
            },
            JsonOptions);
}
