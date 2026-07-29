using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class AdaptiveLocationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string ToMarkdown(AdaptiveLocationValidationResult result)
    {
        var culture = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            "# Adaptive location matching pilot",
            string.Empty,
            "## Coverage-vergelijking (locatiegebonden noemer)",
            string.Empty,
            FormatVariant(result.Baseline, culture),
            FormatVariant(result.AdaptiveWithoutLearning, culture),
            FormatVariant(result.AdaptiveWithLearning, culture),
            string.Empty,
            string.Create(culture, $"- Geleerde clusters: {result.LearnedClusterCount}"),
            string.Create(
                culture,
                $"- Precision ({result.PrecisionKind}): {result.PrecisionPercent}%"),
            string.Create(
                culture,
                $"- Ambiguous (selected): {result.AdaptiveWithLearning.Ambiguous}"),
            string.Create(
                culture,
                $"- Unresolved (selected): {result.AdaptiveWithLearning.Unresolved}"),
            string.Create(culture, $"- Grootste winst: {result.LargestGainRules}"),
            string.Create(
                culture,
                $"- 80% verantwoord haalbaar: {result.TargetEightyPercentResponsible}"),
            string.Create(culture, $"- Steekproef: {result.StratifiedSamplePath}"),
            string.Empty,
            "## Geselecteerde configuratie",
            string.Empty,
            $"- Naam: {result.SelectedConfiguration.Name}",
            string.Create(
                culture,
                $"- Coverage: {result.SelectedConfiguration.WithLearning.ReliableCoveragePercent}%"),
            string.Create(
                culture,
                $"- Estimated precision: {result.SelectedConfiguration.EstimatedPrecisionPercent}%"),
            string.Empty,
            "## Volgende stap",
            string.Empty,
            result.RecommendedNextStep,
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToJson(AdaptiveLocationValidationResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);

    private static string FormatVariant(
        AdaptiveMatcherVariantResult variant,
        CultureInfo culture) =>
        string.Create(
            culture,
            $"- {variant.Name}: Confirmed {variant.Confirmed}, Probable {variant.Probable}, " +
            $"Ambiguous {variant.Ambiguous}, Unresolved {variant.Unresolved}, " +
            $"coverage {variant.ReliableCoveragePercent}%, clusters {variant.LinksViaHistoricalClusters}, " +
            $"FP-risk {variant.EstimatedFalsePositiveRisk}");
}
