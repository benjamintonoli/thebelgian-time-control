using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Adaptive matching analysis on live broader-validation observations.
/// Must never read location-matching-holdout.json for parameter optimization.
/// </summary>
internal static class AdaptiveLocationValidationService
{
    private static readonly JsonSerializerOptions SampleJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static AdaptiveLocationValidationResult Analyze(
        BroaderValidationResult broader,
        string docsPath)
    {
        var distanceCalculator = new HaversineDistanceCalculator();
        var defaultOptions = new AdaptiveLocationMatchingOptions();
        defaultOptions.Validate();
        var locationBound = CollectLocationBoundCases(broader);
        var baseline = BaselineVariant(locationBound);
        var observations = BuildObservations(locationBound, defaultOptions, distanceCalculator);
        var clusters = HistoricalLocationClusterLearner.Learn(
            observations,
            defaultOptions,
            distanceCalculator);
        var clusterMap = clusters.ToDictionary(
            item => item.PlenionLocationKey,
            StringComparer.Ordinal);
        var withoutLearning = RunVariant(
            "adaptive-without-learning",
            locationBound,
            defaultOptions,
            distanceCalculator,
            new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
            enableLearning: false);
        var withLearning = RunVariant(
            "adaptive-with-learning",
            locationBound,
            defaultOptions,
            distanceCalculator,
            clusterMap,
            enableLearning: true);
        var experiments = RunExperiments(locationBound, distanceCalculator);
        var selected = SelectConfiguration(experiments, withLearning, defaultOptions);
        var samplePath = WriteStratifiedSample(
            docsPath,
            selected.WithLearning,
            locationBound,
            selected.Options,
            distanceCalculator,
            clusterMap);
        var largestGain = DescribeLargestGain(baseline, withoutLearning, selected.WithLearning);
        var targetOk = selected.WithLearning.ReliableCoveragePercent >= 80 &&
                       selected.EstimatedPrecisionPercent >= 95;
        var responsible = selected.PreferredForPrecision || targetOk;
        return new AdaptiveLocationValidationResult
        {
            Baseline = baseline,
            AdaptiveWithoutLearning = withoutLearning,
            AdaptiveWithLearning = selected.WithLearning,
            Experiments = experiments,
            SelectedConfiguration = selected,
            LearnedClusterCount = clusters.Count,
            PrecisionKind = "estimated",
            PrecisionPercent = selected.EstimatedPrecisionPercent,
            LargestGainRules = largestGain,
            TargetEightyPercentResponsible = responsible &&
                selected.WithLearning.ReliableCoveragePercent >= 80,
            RecommendedNextStep = responsible &&
                selected.WithLearning.ReliableCoveragePercent >= 80
                ? "Neem de geselecteerde adaptieve configuratie over in de locatiematcher en valideer precision op een manueel gelabelde steekproef van 50–100 cases."
                : "Behoud de precisieveilige configuratie en breid historische learning uit over een langere periode (meer werkdagen per LACLEUNIK) vóór verdere drempelversoepeling.",
            StratifiedSamplePath = samplePath,
        };
    }

    private static List<LocationBoundCase> CollectLocationBoundCases(
        BroaderValidationResult broader)
    {
        var cases = new List<LocationBoundCase>();
        foreach (var technician in broader.Technicians.Where(item =>
                     item.Processed && item.PilotResult is not null))
        {
            var pilot = technician.PilotResult!;
            var resolutionById = pilot.LocationResolutions.ToDictionary(item => item.PerformanceId);
            foreach (var performance in pilot.PlenionRecords)
            {
                if (!resolutionById.TryGetValue(performance.ExternalId, out var resolution))
                {
                    continue;
                }

                var classification = PerformanceActivityClassifier.Classify(
                    performance,
                    technician.Technician?.Name ?? technician.Query,
                    resolution);
                if (!classification.RequiresGeographicMatch)
                {
                    continue;
                }

                cases.Add(new LocationBoundCase(
                    performance,
                    technician.Technician?.Name ?? technician.Query,
                    resolution,
                    pilot.PlenionRecords.Where(item => item.Date == performance.Date).ToArray(),
                    pilot.PowerfleetStops.Where(stop => stop.Date == performance.Date).ToArray()));
            }
        }

        return cases;
    }

    private static AdaptiveMatcherVariantResult BaselineVariant(
        IReadOnlyList<LocationBoundCase> cases)
    {
        var confirmed = cases.Count(item =>
            item.Resolution.MatchStatus ==
            PilotLocationResolutionStatus.ConfirmedLocationMatch);
        var probable = cases.Count(item =>
            item.Resolution.MatchStatus ==
            PilotLocationResolutionStatus.ProbableLocationMatch);
        var ambiguous = cases.Count(item =>
            item.Resolution.MatchStatus ==
            PilotLocationResolutionStatus.ManualReviewRequired);
        var unresolved = cases.Count - confirmed - probable - ambiguous;
        return BuildVariantResult(
            "baseline",
            confirmed,
            probable,
            ambiguous,
            unresolved,
            cases.Count,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["Strong0To100"] = 0,
                ["Probable101To250"] = 0,
                ["Learned251To500"] = 0,
                ["Beyond500"] = 0,
            },
            0,
            ambiguous,
            unresolved);
    }

    private static List<(
        NormalizedPilotPerformance Performance,
        string TechnicianName,
        PilotLocationResolution? Resolution,
        IReadOnlyList<MergedPilotStop> DayStops)> BuildObservations(
        IReadOnlyList<LocationBoundCase> cases,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator) =>
        cases.Select(item => (
                item.Performance,
                item.TechnicianName,
                (PilotLocationResolution?)item.Resolution,
                MergedStopBuilder.Merge(item.DayStops, options, distanceCalculator)))
            .ToList();

    private static AdaptiveMatcherVariantResult RunVariant(
        string name,
        IReadOnlyList<LocationBoundCase> cases,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clusters,
        bool enableLearning)
    {
        var results = cases.Select(item =>
            {
                var merged = MergedStopBuilder.Merge(
                    item.DayStops,
                    options,
                    distanceCalculator);
                return AdaptiveLocationMatcher.Match(
                    item.Performance,
                    item.TechnicianName,
                    item.Resolution,
                    merged,
                    item.SameDayPerformances,
                    clusters,
                    options,
                    distanceCalculator,
                    enableLearning);
            })
            .ToArray();
        return Summarize(name, results);
    }

    private static AdaptiveMatcherVariantResult Summarize(
        string name,
        AdaptiveMatchResult[] results)
    {
        var confirmed = results.Count(item => item.Decision == AdaptiveMatchDecision.Confirmed);
        var probable = results.Count(item => item.Decision == AdaptiveMatchDecision.Probable);
        var ambiguous = results.Count(item => item.Decision == AdaptiveMatchDecision.Ambiguous);
        var unresolved = results.Count(item => item.Decision == AdaptiveMatchDecision.Unresolved);
        var zones = results
            .Where(item => item.Selected is not null)
            .GroupBy(item => item.DistanceZone.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var zone in new[]
                 {
                     "Strong0To100", "Probable101To250", "Learned251To500", "Beyond500",
                 })
        {
            zones.TryAdd(zone, 0);
        }

        var competing = results.Count(item =>
            item.Candidates.Count > 1 &&
            item.Decision == AdaptiveMatchDecision.Ambiguous);
        var estimatedFp = results.Count(item =>
            item.Selected is { DistanceMeters: > 250 } ||
            (item.Decision == AdaptiveMatchDecision.Confirmed &&
             item.GeocodeQuality is GeocodeQualityClass.StreetOnly
                 or GeocodeQualityClass.LowConfidence));
        return BuildVariantResult(
            name,
            confirmed,
            probable,
            ambiguous,
            unresolved,
            results.Length,
            zones,
            results.Count(item => item.UsedHistoricalCluster),
            competing,
            estimatedFp);
    }

    private static AdaptiveMatcherVariantResult BuildVariantResult(
        string name,
        int confirmed,
        int probable,
        int ambiguous,
        int unresolved,
        int total,
        IReadOnlyDictionary<string, int> zones,
        int viaClusters,
        int competing,
        int estimatedFp) =>
        new(
            name,
            confirmed,
            probable,
            ambiguous,
            unresolved,
            total,
            total == 0
                ? 0
                : Math.Round(100d * (confirmed + probable) / total, 1),
            zones,
            viaClusters,
            competing,
            estimatedFp);

    private static AdaptiveParameterExperiment[] RunExperiments(
        IReadOnlyList<LocationBoundCase> cases,
        IDistanceCalculator distanceCalculator)
    {
        var grids = new (string Name, AdaptiveLocationMatchingOptions Options)[]
        {
            ("default", new AdaptiveLocationMatchingOptions()),
            ("strict-time", new AdaptiveLocationMatchingOptions
            {
                MinimumOverlapMinutes = 10,
                MinimumOverlapPercent = 30,
                MinimumScoreMargin = 10,
                ConfirmedMinimumScore = 75,
            }),
            ("wider-probable", new AdaptiveLocationMatchingOptions
            {
                ProbableDistanceMeters = 300,
                MinimumOverlapPercent = 15,
                ProbableMinimumScore = 50,
            }),
            ("learning-easier", new AdaptiveLocationMatchingOptions
            {
                MinimumDistinctWorkdays = 2,
                MinimumDominancePercentage = 70,
                MaximumLearnedClusterDistanceMeters = 500,
            }),
            ("balanced-coverage", new AdaptiveLocationMatchingOptions
            {
                ProbableDistanceMeters = 300,
                MinimumOverlapMinutes = 3,
                MinimumOverlapPercent = 10,
                MinimumScoreMargin = 5,
                ConfirmedMinimumScore = 60,
                ProbableMinimumScore = 45,
                MinimumDistinctWorkdays = 2,
                MinimumDominancePercentage = 70,
            }),
            ("precision-first", new AdaptiveLocationMatchingOptions
            {
                MinimumOverlapMinutes = 8,
                MinimumOverlapPercent = 25,
                MinimumScoreMargin = 12,
                ConfirmedMinimumScore = 78,
                ProbableMinimumScore = 60,
                MinimumDistinctWorkdays = 3,
                MinimumDominancePercentage = 85,
            }),
        };

        return grids.Select(grid =>
            {
                grid.Options.Validate();
                var observations = BuildObservations(cases, grid.Options, distanceCalculator);
                var clusters = HistoricalLocationClusterLearner.Learn(
                    observations,
                    grid.Options,
                    distanceCalculator);
                var map = clusters.ToDictionary(
                    item => item.PlenionLocationKey,
                    StringComparer.Ordinal);
                var result = RunVariant(
                    grid.Name,
                    cases,
                    grid.Options,
                    distanceCalculator,
                    map,
                    enableLearning: true);
                var estimatedPrecision = EstimatePrecision(result);
                return new AdaptiveParameterExperiment(
                    grid.Name,
                    grid.Options,
                    result,
                    estimatedPrecision,
                    result.ReliableCoveragePercent >= 80,
                    estimatedPrecision >= 95);
            })
            .ToArray();
    }

    private static AdaptiveParameterExperiment SelectConfiguration(
        IReadOnlyList<AdaptiveParameterExperiment> experiments,
        AdaptiveMatcherVariantResult defaultWithLearning,
        AdaptiveLocationMatchingOptions defaultOptions)
    {
        var preciseEnough = experiments
            .Where(item => item.EstimatedPrecisionPercent >= 95)
            .OrderByDescending(item => item.WithLearning.ReliableCoveragePercent)
            .ThenByDescending(item => item.EstimatedPrecisionPercent)
            .FirstOrDefault();
        if (preciseEnough is not null)
        {
            return preciseEnough;
        }

        return experiments
                   .OrderByDescending(item => item.EstimatedPrecisionPercent)
                   .ThenByDescending(item => item.WithLearning.ReliableCoveragePercent)
                   .FirstOrDefault()
               ?? new AdaptiveParameterExperiment(
                   "default",
                   defaultOptions,
                   defaultWithLearning,
                   EstimatePrecision(defaultWithLearning),
                   defaultWithLearning.ReliableCoveragePercent >= 80,
                   false);
    }

    private static double EstimatePrecision(AdaptiveMatcherVariantResult result)
    {
        if (result.Confirmed + result.Probable == 0)
        {
            return 100;
        }

        var risk = Math.Min(
            result.Confirmed + result.Probable,
            result.EstimatedFalsePositiveRisk);
        return Math.Round(
            100d * (result.Confirmed + result.Probable - risk) /
            (result.Confirmed + result.Probable),
            1);
    }

    private static string DescribeLargestGain(
        AdaptiveMatcherVariantResult baseline,
        AdaptiveMatcherVariantResult withoutLearning,
        AdaptiveMatcherVariantResult withLearning)
    {
        var directGain = withoutLearning.ReliableCoveragePercent - baseline.ReliableCoveragePercent;
        var learningGain = withLearning.ReliableCoveragePercent -
                           withoutLearning.ReliableCoveragePercent;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Tijd+overlappercentage/marge (+{directGain:0.#} pp zonder learning); " +
            $"historische clusters (+{learningGain:0.#} pp); " +
            $"geocodekwaliteit voorkomt street-only als precies punt.");
    }

    private static string WriteStratifiedSample(
        string docsPath,
        AdaptiveMatcherVariantResult selectedSummary,
        IReadOnlyList<LocationBoundCase> cases,
        AdaptiveLocationMatchingOptions options,
        IDistanceCalculator distanceCalculator,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clusters)
    {
        Directory.CreateDirectory(docsPath);
        var path = Path.Combine(docsPath, "adaptive-location-validation-sample.json");
        var results = cases.Select(item =>
            {
                var merged = MergedStopBuilder.Merge(item.DayStops, options, distanceCalculator);
                return AdaptiveLocationMatcher.Match(
                    item.Performance,
                    item.TechnicianName,
                    item.Resolution,
                    merged,
                    item.SameDayPerformances,
                    clusters,
                    options,
                    distanceCalculator,
                    enableLearning: true);
            })
            .ToArray();
        var sample = results
            .GroupBy(item => item.Decision)
            .SelectMany(group => group.Take(20))
            .Take(100)
            .Select(item => new
            {
                item.PerformanceId,
                item.Date,
                Technician = Hash(item.TechnicianName),
                item.Decision,
                item.GeocodeQuality,
                item.DistanceZone,
                item.UsedHistoricalCluster,
                DistanceMeters = item.Selected?.DistanceMeters,
                OverlapMinutes = item.Selected?.OverlapMinutes,
                OverlapPercent = item.Selected?.OverlapPercent,
                Score = item.Selected?.TotalScore,
                item.Assessment,
            })
            .ToArray();
        _ = selectedSummary;
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                sample,
                SampleJsonOptions));
        return path;
    }

    private static string Hash(string value)
    {
        var hash = value.GetHashCode(StringComparison.Ordinal);
        return "tech-" + Math.Abs(hash).ToString("X", CultureInfo.InvariantCulture);
    }

    private sealed record LocationBoundCase(
        NormalizedPilotPerformance Performance,
        string TechnicianName,
        PilotLocationResolution Resolution,
        IReadOnlyList<NormalizedPilotPerformance> SameDayPerformances,
        IReadOnlyList<PilotStop> DayStops);
}
