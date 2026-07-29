using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class LocationMatchingBenchmarkReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ToMarkdown(LocationMatchingBenchmarkResult result)
    {
        var lines = new List<string>
        {
            "# Location-matching benchmark",
            string.Empty,
            "## Dataset splits",
            string.Empty,
            $"- Complete months: {string.Join(", ", result.CompleteMonths)}",
            $"- Development cases: {result.DevelopmentCaseCount}",
            $"- Holdout cases: {result.HoldoutCaseCount}",
            $"- Holdout unique LACLEUNIK: {result.HoldoutUniqueLocationCount}",
            $"- Challenge cases: {result.ChallengeCaseCount}",
            $"- Holdout SeenLocation: {result.SeenLocationCount}",
            $"- Holdout UnseenLocation: {result.UnseenLocationCount}",
            $"- Blind reviewer: {result.BlindReviewerPath}",
            string.Empty,
            "## Paths",
            string.Empty,
            $"- Completeness: `{result.CompletenessPath}`",
            $"- Development: `{result.DevelopmentPath}`",
            $"- Holdout (locked): `{result.HoldoutPath}`",
            $"- Challenge: `{result.ChallengePath}`",
            string.Empty,
            "## Powerfleet-granulariteit",
            string.Empty,
            $"- Parameters: {string.Join(", ", result.PowerfleetGranularity.ReportParameters)}",
            $"- Vendor stops: {result.PowerfleetGranularity.HasVendorStops}",
            $"- Start/eindcoördinaten: {result.PowerfleetGranularity.HasTripStartEndCoordinates}",
            $"- Individuele punten: {result.PowerfleetGranularity.HasIndividualPoints}",
            $"- Timestamps: {result.PowerfleetGranularity.HasTimestamps}",
            $"- Snelheid: {result.PowerfleetGranularity.HasSpeed}",
            $"- Ignition: {result.PowerfleetGranularity.HasIgnition}",
            $"- GPS-validiteit/accuracy: {result.PowerfleetGranularity.HasGpsValidityOrAccuracy}",
            $"- Beperking: {result.PowerfleetGranularity.Limitation}",
            string.Empty,
            "## Variants",
            string.Empty,
        };
        lines.AddRange(result.VariantsReady.Select(variant => $"- {variant}"));
        lines.Add(string.Empty);
        lines.Add("## Historical split");
        lines.Add(string.Empty);
        lines.Add(
            string.Create(
                CultureInfo.InvariantCulture,
                $"- Periode: {result.HistoricalClustering.HistoryFrom:dd/MM/yyyy} t.e.m. {result.HistoricalClustering.HistoryThrough:dd/MM/yyyy}"));
        lines.Add($"- Juli gebruikt voor learning: {result.HistoricalClustering.JulyUsedForLearning}");
        if (result.HistoricalClustering.Warnings.Count > 0)
        {
            lines.Add($"- Waarschuwingen: {string.Join(" | ", result.HistoricalClustering.Warnings)}");
        }

        lines.Add(string.Empty);
        lines.Add("## Evaluatie");
        lines.Add(string.Empty);
        lines.Add($"- {result.NeedsForMeasuredMetrics}");
        lines.Add(
            "- Voorbereide metrics: precision, recall, coverage, F1, FP, FN, Wilson 95% CI, risk-coverage, Seen/Unseen, challenge apart.");
        return string.Join(Environment.NewLine, lines);
    }

    public static string ToJson(LocationMatchingBenchmarkResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);
}
