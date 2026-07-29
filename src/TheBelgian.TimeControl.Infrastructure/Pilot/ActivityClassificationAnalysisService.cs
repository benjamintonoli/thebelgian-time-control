using System.Globalization;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class ActivityClassificationAnalysisService
{
    public static ActivityClassificationAnalysisResult Analyze(BroaderValidationResult broader)
    {
        var classifications = new List<PerformanceActivityClassification>();
        var openCases = new List<PerformanceActivityClassification>();
        var locationBoundResolutions = new List<(PilotLocationResolution Resolution, bool Confirmable)>();

        foreach (var technician in broader.Technicians.Where(item =>
                     item.Processed && item.PilotResult is not null))
        {
            var pilot = technician.PilotResult!;
            var resolutionByPerformance = pilot.LocationResolutions
                .ToDictionary(item => item.PerformanceId);
            foreach (var performance in pilot.PlenionRecords)
            {
                resolutionByPerformance.TryGetValue(performance.ExternalId, out var resolution);
                var classification = PerformanceActivityClassifier.Classify(
                    performance,
                    technician.Technician?.Name ?? technician.Query,
                    resolution);
                classifications.Add(classification);

                if (resolution is null)
                {
                    continue;
                }

                var best = resolution.Candidates.Count > 0 ? resolution.Candidates[0] : null;
                var reliable = IsReliable(resolution.MatchStatus);
                var confirmable = CoverageGapAnalysisService.IsConfirmableAlias(best);
                if (!reliable && !confirmable)
                {
                    openCases.Add(classification);
                }

                if (classification.RequiresGeographicMatch)
                {
                    locationBoundResolutions.Add((resolution, confirmable));
                }
            }
        }

        var typeSummaries = Enum.GetValues<PerformanceActivityType>()
            .Select(activityType => BuildTypeSummary(activityType, classifications))
            .Where(summary =>
                summary.PerformanceCount > 0 ||
                summary.ActivityType == PerformanceActivityType.Unknown)
            .ToArray();
        var notLocationBoundOpen = openCases.Count(item => !item.RequiresGeographicMatch);
        var unknownOpen = openCases.Count(item =>
            item.ActivityType == PerformanceActivityType.Unknown);
        var locationBound = locationBoundResolutions.Select(item => item.Resolution).ToArray();
        var reliableLocationBound = locationBound.Count(item => IsReliable(item.MatchStatus));
        var remainingNoReliable = locationBound.Count(item =>
            item.MatchStatus == PilotLocationResolutionStatus.NoReliableMatch);
        var aliasFlippable = locationBoundResolutions.Count(item =>
            !IsReliable(item.Resolution.MatchStatus) && item.Confirmable);
        var correctedPercent = Percent(reliableLocationBound, locationBound.Length);
        var potentialPercent = Percent(
            reliableLocationBound + aliasFlippable,
            locationBound.Length);

        return new ActivityClassificationAnalysisResult
        {
            Classifications = classifications
                .OrderBy(item => item.Date)
                .ThenBy(item => item.PerformanceId)
                .ToArray(),
            TypeSummaries = typeSummaries,
            OpenCases = new OpenLocationCaseSummary(
                openCases.Count,
                notLocationBoundOpen,
                openCases.Count(item => item.RequiresGeographicMatch),
                unknownOpen,
                openCases
                    .OrderBy(item => item.ActivityType.ToString(), StringComparer.Ordinal)
                    .ThenBy(item => item.PerformanceId)
                    .ToArray()),
            CorrectedMatch = new CorrectedMatchSummary(
                locationBound.Length,
                reliableLocationBound,
                correctedPercent,
                remainingNoReliable,
                aliasFlippable,
                potentialPercent),
            AliasAdvice = BuildAdvice(
                notLocationBoundOpen,
                openCases.Count,
                correctedPercent,
                potentialPercent,
                aliasFlippable,
                remainingNoReliable),
        };
    }

    private static ActivityTypeSummary BuildTypeSummary(
        PerformanceActivityType type,
        IReadOnlyList<PerformanceActivityClassification> classifications)
    {
        var items = classifications.Where(item => item.ActivityType == type).ToArray();
        return new ActivityTypeSummary(
            type,
            items.Length,
            items.Select(item => item.MainTaskExternalId)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .Take(20)
                .ToArray(),
            items.Select(item => item.Description)
                .Where(description => !string.IsNullOrWhiteSpace(description))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(description => description, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .Take(20)
                .ToArray(),
            items.Count(item => item.RequiresGeographicMatch),
            items.Count(item => item.IncorrectlyInLocationDenominator),
            type == PerformanceActivityType.Unknown ? items.Length : 0);
    }

    private static bool IsReliable(PilotLocationResolutionStatus status) =>
        status is PilotLocationResolutionStatus.ConfirmedLocationMatch
            or PilotLocationResolutionStatus.ProbableLocationMatch;

    private static double Percent(int count, int total) =>
        total == 0 ? 0 : Math.Round(100d * count / total, 1);

    private static string BuildAdvice(
        int notLocationBoundOpen,
        int openCount,
        double correctedPercent,
        double potentialPercent,
        int aliasFlippable,
        int remainingNoReliable)
    {
        if (aliasFlippable > 0 && potentialPercent >= correctedPercent + 5)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"KnownLocationAlias blijft de juiste volgende implementatie voor de resterende locatiegebonden cases: {aliasFlippable} nabije paren tillen de gecorrigeerde graad van {correctedPercent}% naar {potentialPercent}%. {notLocationBoundOpen}/{openCount} open gevallen zijn niet locatiegebonden en horen uit de matchnoemer.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Eerst classificatie vastzetten: {notLocationBoundOpen}/{openCount} open gevallen zijn niet locatiegebonden. Daarna KnownLocationAlias voor de {remainingNoReliable} resterende NoReliableMatch op locatiegebonden werk.");
    }
}
