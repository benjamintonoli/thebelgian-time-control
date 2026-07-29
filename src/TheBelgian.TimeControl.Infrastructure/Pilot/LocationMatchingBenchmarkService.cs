using System.Globalization;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Benchmark dataset generation and persistence.
/// Matching optimization must only read the development set; never the locked holdout file.
/// </summary>
internal sealed class LocationMatchingBenchmarkService(
    IBroaderValidationPilotService broaderValidationPilotService,
    PilotPlenionReader plenionReader,
    PilotPowerfleetReader powerfleetReader,
    LocationResolutionPilotService locationResolutionPilotService)
{
    public const string DevelopmentFileName = "location-matching-development.json";
    public const string HoldoutFileName = "location-matching-holdout.json";
    public const string HoldoutManifestFileName = "location-matching-holdout-manifest.json";
    public const string ChallengeFileName = "location-matching-challenge.json";
    public const string CompletenessFileName = "location-matching-completeness.json";
    public const string LegacyBenchmarkFileName = "location-matching-benchmark.json";
    public const string RawPoolFileName = "location-matching-raw-pool.json";
    public const string CasePoolFileName = "location-matching-case-pool.json";
    public const string CalibrationFileName = "location-matching-calibration.json";
    public const string LeakageAuditFileName = "location-matching-leakage-audit.json";
    public static readonly DateOnly PureHoldoutFrom = new(2025, 10, 1);
    public static readonly DateOnly PureHoldoutThrough = new(2025, 12, 31);
    public static readonly DateOnly PureHoldoutHistoricalExclusive = new(2025, 10, 1);

    private static readonly string[] TechnicianNames =
    [
        "Filip Dekuyper",
        "Jonas Deklerck",
        "Jasper De Smet",
        "Jarno Vergauwen",
        "Dimitri Stiers",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<LocationMatchingBenchmarkResult> RunAsync(
        string docsPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(docsPath);

        var broader = await broaderValidationPilotService.RunAsync(
            new BroaderValidationRequest(
                TechnicianNames.Select(name => new BroaderValidationTechnicianRequest(name)).ToArray(),
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 28),
                5),
            cancellationToken);
        var julyCoreCases = BuildBenchmarkCases(CollectDeterministicCases(broader));
        var driverIds = broader.Technicians
            .Where(item => item.Processed && !string.IsNullOrWhiteSpace(item.DriverId))
            .ToDictionary(
                item => item.Technician?.Name ?? item.Query,
                item => item.DriverId!,
                StringComparer.OrdinalIgnoreCase);

        var completeness = new List<MonthTechnicianCompleteness>();
        var poolById = new Dictionary<long, LocationMatchingBenchmarkCase>();
        var locationsByMonth = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var monthLoadWarnings = new List<string>();

        foreach (var technicianName in TechnicianNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!driverIds.TryGetValue(technicianName, out var driverId))
            {
                monthLoadWarnings.Add($"{technicianName}: geen driverid; maanden overgeslagen.");
                continue;
            }

            for (var month = 1; month <= 7; month++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = new DateOnly(2026, month, 1);
                var through = new DateOnly(2026, month, DateTime.DaysInMonth(2026, month));
                if (month == 7)
                {
                    through = new DateOnly(2026, 7, 28);
                }

                var yearMonth = from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                var slice = await LoadMonthSliceAsync(
                    technicianName,
                    driverId,
                    from,
                    through,
                    monthLoadWarnings,
                    cancellationToken);
                completeness.Add(slice.Completeness);
                if (!locationsByMonth.TryGetValue(yearMonth, out var monthLocations))
                {
                    monthLocations = new HashSet<string>(StringComparer.Ordinal);
                    locationsByMonth[yearMonth] = monthLocations;
                }

                foreach (var lacleunik in slice.Cases
                             .Select(item => item.Lacleunik)
                             .Where(item => !string.IsNullOrWhiteSpace(item)))
                {
                    monthLocations.Add(lacleunik!);
                }

                foreach (var item in slice.Cases)
                {
                    poolById[item.PerformanceId] = item;
                }
            }
        }

        var completeMonths = SelectCompleteMonths(completeness);
        var historicallySeenBefore = BuildHistoricalSeenLookup(locationsByMonth);
        var annotatedPool = poolById.Values
            .Select(item =>
            {
                var monthKey = item.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                historicallySeenBefore.TryGetValue(monthKey, out var seenBefore);
                seenBefore ??= new HashSet<string>(StringComparer.Ordinal);
                return item with
                {
                    LocationExposure = LocationMatchingBenchmarkSampling.Exposure(
                        item.Lacleunik,
                        seenBefore),
                };
            })
            .ToList();
        var julyCoreAnnotated = julyCoreCases
            .Select(item =>
            {
                historicallySeenBefore.TryGetValue("2026-07", out var seenBefore);
                seenBefore ??= new HashSet<string>(StringComparer.Ordinal);
                var fromPool = annotatedPool.FirstOrDefault(poolItem =>
                    poolItem.PerformanceId == item.PerformanceId);
                return (fromPool ?? item) with
                {
                    LocationExposure = LocationMatchingBenchmarkSampling.Exposure(
                        item.Lacleunik,
                        seenBefore),
                    DatasetRole = "development",
                };
            })
            .ToList();

        var eligiblePool = annotatedPool
            .Where(item =>
            {
                var month = item.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                return completeness.Any(row =>
                    row.IsComplete &&
                    string.Equals(row.YearMonth, month, StringComparison.Ordinal) &&
                    string.Equals(row.Technician, item.Technician, StringComparison.Ordinal));
            })
            .ToList();

        // Prefer representative complete months, but keep other complete technician-months
        // so development (200) + holdout (300) remain feasible.
        var preferredPool = eligiblePool
            .Where(item =>
                completeMonths.Contains(item.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
            .ToList();
        if (preferredPool.Count >=
            LocationMatchingBenchmarkSampling.MaxDevelopmentCases +
            LocationMatchingBenchmarkSampling.HoldoutTargetCases)
        {
            eligiblePool = preferredPool;
        }

        var poolPath = Path.Combine(docsPath, CasePoolFileName);
        File.WriteAllText(poolPath, JsonSerializer.Serialize(eligiblePool, JsonOptions));
        File.WriteAllText(
            Path.Combine(docsPath, RawPoolFileName),
            JsonSerializer.Serialize(
                new
                {
                    JulyCore = julyCoreAnnotated,
                    Cases = annotatedPool,
                },
                JsonOptions));

        // Lock holdout before expanding development so the independent set reaches target size.
        var julyCoreIds = julyCoreAnnotated.Select(item => item.PerformanceId).ToHashSet();
        var holdoutPath = Path.Combine(docsPath, HoldoutFileName);
        var holdoutManifestPath = Path.Combine(docsPath, HoldoutManifestFileName);
        List<LocationMatchingBenchmarkCase> holdout;
        HoldoutSamplingManifest holdoutManifest;
        if (TryLoadLockedHoldout(holdoutPath, holdoutManifestPath, out var lockedHoldout, out var lockedManifest) &&
            lockedHoldout.Count >= LocationMatchingBenchmarkSampling.HoldoutTargetCases)
        {
            holdout = lockedHoldout;
            holdoutManifest = lockedManifest;
        }
        else
        {
            (holdout, holdoutManifest) = LocationMatchingBenchmarkSampling.SelectHoldoutCases(
                eligiblePool,
                julyCoreIds,
                completeMonths);
            WriteHoldoutLocked(holdoutPath, holdout);
            File.WriteAllText(
                holdoutManifestPath,
                JsonSerializer.Serialize(holdoutManifest, JsonOptions));
        }

        var holdoutIds = holdout.Select(item => item.PerformanceId).ToHashSet();
        var developmentPool = eligiblePool
            .Where(item => !holdoutIds.Contains(item.PerformanceId))
            .ToList();
        var development = LocationMatchingBenchmarkSampling.MarkSecondReviews(
            LocationMatchingBenchmarkSampling.SelectDevelopmentCases(
                julyCoreAnnotated,
                developmentPool));
        var developmentIds = development.Select(item => item.PerformanceId).ToHashSet();

        var challengeExclusive = LocationMatchingBenchmarkSampling.SelectChallengeCases(
            eligiblePool,
            developmentIds.Concat(holdoutIds).ToHashSet());
        var challenge = challengeExclusive.Count >= LocationMatchingBenchmarkSampling.ChallengeMinCases
            ? challengeExclusive
            : LocationMatchingBenchmarkSampling.SelectChallengeCases(
                eligiblePool
                    .Concat(holdout)
                    .GroupBy(item => item.PerformanceId)
                    .Select(group => group.First())
                    .ToList(),
                developmentIds);

        var completenessPath = Path.Combine(docsPath, CompletenessFileName);
        File.WriteAllText(
            completenessPath,
            JsonSerializer.Serialize(
                new
                {
                    CompleteMonths = completeMonths,
                    Warnings = monthLoadWarnings,
                    Rows = completeness
                        .OrderBy(item => item.YearMonth)
                        .ThenBy(item => item.Technician, StringComparer.Ordinal)
                        .ToArray(),
                },
                JsonOptions));

        var developmentPath = Path.Combine(docsPath, DevelopmentFileName);
        var developmentFile = new LocationMatchingDevelopmentFile
        {
            DatasetRole = "development",
            Cases = development,
            Evaluation = LocationMatchingBenchmarkSampling.BuildEvaluationScaffold(development),
        };
        File.WriteAllText(developmentPath, JsonSerializer.Serialize(developmentFile, JsonOptions));

        // Legacy unlabeled list kept for compatibility; identical to development cases.
        File.WriteAllText(
            Path.Combine(docsPath, LegacyBenchmarkFileName),
            JsonSerializer.Serialize(development, JsonOptions));

        var challengePath = Path.Combine(docsPath, ChallengeFileName);
        var challengeFile = new LocationMatchingChallengeFile
        {
            DatasetRole = "challenge",
            ExclusionNote =
                "Challenge cases are excluded from the general coverage percentage.",
            Cases = challenge,
            Evaluation = LocationMatchingBenchmarkSampling.BuildEvaluationScaffold(challenge),
        };
        File.WriteAllText(challengePath, JsonSerializer.Serialize(challengeFile, JsonOptions));

        var holdoutUnique = holdout
            .Select(item => item.Lacleunik)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var seenCount = holdout.Count(item =>
            string.Equals(item.LocationExposure, "SeenLocation", StringComparison.Ordinal));
        var unseenCount = holdout.Count - seenCount;
        var granularity = BuildPowerfleetGranularity(broader);
        var historical = new HistoricalClusterBenchmarkStatus
        {
            HistoryFrom = new DateOnly(2026, 1, 1),
            HistoryThrough = new DateOnly(2026, 6, 30),
            JulyUsedForLearning = false,
            LearnedClusterCount = 0,
            Warnings =
            [
                "Historical clustering not re-run during dataset generation; matching logic unchanged.",
            ],
        };
        var variants = new List<string>
        {
            "baseline",
            "adaptive matcher",
            "baseline + historical clusters",
        };
        if (granularity.HasIndividualPoints)
        {
            variants.Add("spatio-temporele stopvariant");
        }

        return new LocationMatchingBenchmarkResult
        {
            DeterministicDenominator = julyCoreAnnotated.Count,
            StablePerformanceIds = julyCoreAnnotated
                .Select(item => item.PerformanceId)
                .OrderBy(id => id)
                .ToArray(),
            PowerfleetGranularity = granularity,
            BenchmarkCaseCount = development.Count,
            HistoricalClustering = historical,
            VariantsReady = variants,
            NeedsForMeasuredMetrics =
                "Labels ontbreken; precision/recall/coverage/F1/Wilson/risk-coverage worden pas berekend na blinde development-review.",
            BenchmarkPath = developmentPath,
            CompleteMonths = completeMonths,
            DevelopmentCaseCount = development.Count,
            HoldoutCaseCount = holdout.Count,
            HoldoutUniqueLocationCount = holdoutUnique,
            ChallengeCaseCount = challenge.Count,
            SeenLocationCount = seenCount,
            UnseenLocationCount = unseenCount,
            DevelopmentPath = developmentPath,
            HoldoutPath = holdoutPath,
            ChallengePath = challengePath,
            CompletenessPath = completenessPath,
            BlindReviewerPath = "https://localhost:7211/Pilot/BenchmarkReview",
        };
    }

    /// <summary>
    /// Rebuilds independent holdout (Jasper 2025 Q4), reclassifies May–Jul as development,
    /// marks challenge as a hard development subset, and creates a 30-case calibration set.
    /// </summary>
    public async Task<LocationMatchingPurifyResult> PurifyAndCalibrateAsync(
        string docsPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(docsPath);
        var priorDevelopment = LoadDevelopmentCases(docsPath).ToList();
        var priorHoldout = LoadHoldoutCases(docsPath);
        var priorChallenge = LoadChallengeCases(docsPath);
        var priorLeakage = LocationMatchingBenchmarkSampling.AuditLeakage(
            priorDevelopment,
            priorHoldout,
            priorChallenge,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 6, 30));
        File.WriteAllText(
            Path.Combine(docsPath, LeakageAuditFileName),
            JsonSerializer.Serialize(priorLeakage, JsonOptions));

        // Development = existing 200 + former challenge (marked hard subset). May–Jul only for role.
        var developmentById = new Dictionary<long, LocationMatchingBenchmarkCase>();
        foreach (var item in priorDevelopment)
        {
            developmentById[item.PerformanceId] = item with
            {
                DatasetRole = "development",
                IsChallengeSubset = false,
                IsCalibrationCase = false,
                RequiresSecondReview = false,
            };
        }

        foreach (var item in priorChallenge)
        {
            developmentById[item.PerformanceId] = item with
            {
                DatasetRole = "development",
                IsChallengeSubset = true,
                IsCalibrationCase = false,
                RequiresSecondReview = false,
                Label = null,
                ExpectedStopId = null,
                ReviewerConfidence = null,
                ReviewerNote = null,
                SecondReviewLabel = null,
                SecondReviewExpectedStopId = null,
                SecondReviewerConfidence = null,
                SecondReviewerNote = null,
                AdjudicationStatus = null,
            };
        }

        // Absorb May–Jul cases from the leaked holdout into development (not independent).
        foreach (var item in priorHoldout.Where(item =>
                     item.Date.Year == 2026 && item.Date.Month is >= 5 and <= 7))
        {
            if (developmentById.ContainsKey(item.PerformanceId))
            {
                continue;
            }

            developmentById[item.PerformanceId] = item with
            {
                DatasetRole = "development",
                IsChallengeSubset = false,
                IsCalibrationCase = false,
                RequiresSecondReview = false,
                Label = null,
                ExpectedStopId = null,
                ReviewerConfidence = null,
                ReviewerNote = null,
                SecondReviewLabel = null,
                SecondReviewExpectedStopId = null,
                SecondReviewerConfidence = null,
                SecondReviewerNote = null,
                AdjudicationStatus = null,
            };
        }

        var challenge = developmentById.Values
            .Where(item => item.IsChallengeSubset)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.Ordinal)
            .ThenBy(item => item.PerformanceId)
            .Select(item => item with { DatasetRole = "challenge" })
            .ToList();

        var calibrationSeedPool = developmentById.Values
            .Where(item => !item.IsChallengeSubset)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.Ordinal)
            .ThenBy(item => item.PerformanceId)
            .Take(LocationMatchingBenchmarkSampling.MaxDevelopmentCases)
            .ToList();
        // Prefer the original primary development IDs when present.
        if (priorDevelopment.Count > 0)
        {
            var priorIds = priorDevelopment.Select(item => item.PerformanceId).ToHashSet();
            calibrationSeedPool = developmentById.Values
                .Where(item => priorIds.Contains(item.PerformanceId) && !item.IsChallengeSubset)
                .OrderBy(item => item.Date)
                .ThenBy(item => item.Technician, StringComparer.Ordinal)
                .ThenBy(item => item.PerformanceId)
                .ToList();
        }

        var calibration = LocationMatchingBenchmarkSampling.SelectCalibrationCases(calibrationSeedPool);
        var calibrationIds = calibration.Select(item => item.PerformanceId).ToHashSet();
        foreach (var id in calibrationIds)
        {
            developmentById[id] = developmentById[id] with
            {
                IsCalibrationCase = true,
                RequiresSecondReview = true,
            };
        }

        var development = developmentById.Values
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.Ordinal)
            .ThenBy(item => item.PerformanceId)
            .ToList();

        var broader = await broaderValidationPilotService.RunAsync(
            new BroaderValidationRequest(
                TechnicianNames.Select(name => new BroaderValidationTechnicianRequest(name)).ToArray(),
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 28),
                5),
            cancellationToken);
        var jasper = broader.Technicians.FirstOrDefault(item =>
            item.Processed &&
            string.Equals(item.Technician?.Name ?? item.Query, "Jasper De Smet", StringComparison.OrdinalIgnoreCase));
        if (jasper is null || string.IsNullOrWhiteSpace(jasper.DriverId))
        {
            throw new InvalidOperationException(
                "Kan geen pure holdout bouwen: Jasper De Smet driverid ontbreekt.");
        }

        var historicalSeen = await LoadHistoricalLocationsBeforeAsync(
            "Jasper De Smet",
            jasper.DriverId,
            PureHoldoutHistoricalExclusive,
            cancellationToken);
        var pureHoldoutPool = new List<LocationMatchingBenchmarkCase>();
        var warnings = new List<string>();
        for (var month = 10; month <= 12; month++)
        {
            var from = new DateOnly(2025, month, 1);
            var through = new DateOnly(2025, month, DateTime.DaysInMonth(2025, month));
            var slice = await LoadMonthSliceAsync(
                "Jasper De Smet",
                jasper.DriverId,
                from,
                through,
                warnings,
                cancellationToken);
            foreach (var item in slice.Cases)
            {
                pureHoldoutPool.Add(item with
                {
                    DatasetRole = "holdout",
                    LocationExposure = LocationMatchingBenchmarkSampling.Exposure(
                        item.Lacleunik,
                        historicalSeen),
                    IsChallengeSubset = false,
                    IsCalibrationCase = false,
                    RequiresSecondReview = false,
                });
            }
        }

        var excluded = development.Select(item => item.PerformanceId).ToHashSet();
        var (holdout, holdoutManifestBase) = LocationMatchingBenchmarkSampling.SelectHoldoutCases(
            pureHoldoutPool,
            excluded,
            ["2025-10", "2025-11", "2025-12"],
            targetCount: Math.Min(
                LocationMatchingBenchmarkSampling.HoldoutTargetCases,
                pureHoldoutPool.Count(item => !excluded.Contains(item.PerformanceId))),
            seed: LocationMatchingBenchmarkSampling.PureHoldoutSeed);
        var contentSha = LocationMatchingBenchmarkSampling.ComputeContentSha256(holdout);
        var holdoutManifest = new HoldoutSamplingManifest
        {
            RandomSeed = holdoutManifestBase.RandomSeed,
            GeneratedAt = DateTimeOffset.UtcNow,
            Locked = true,
            TargetCaseCount = LocationMatchingBenchmarkSampling.HoldoutTargetCases,
            MaxCasesPerLacleunik = holdoutManifestBase.MaxCasesPerLacleunik,
            MinUniqueLacleunik = holdoutManifestBase.MinUniqueLacleunik,
            SelectedPerformanceIds = holdout.Select(item => item.PerformanceId).ToArray(),
            CompleteMonthsUsed = holdoutManifestBase.CompleteMonthsUsed,
            CountsByTechnician = holdoutManifestBase.CountsByTechnician,
            CountsByMonth = holdoutManifestBase.CountsByMonth,
            CountsByExposure = holdoutManifestBase.CountsByExposure,
            HoldoutPeriodFrom = PureHoldoutFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            HoldoutPeriodThrough = PureHoldoutThrough.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            HistoricalFeaturesThroughExclusive =
                PureHoldoutHistoricalExclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ContentSha256 = contentSha,
            IndependenceNote =
                "Pure holdout: Jasper De Smet 2025-10..2025-12 only. Outside Jan–Jul 2026 tuning/learning. " +
                "300 not reachable without leakage; using largest pure set.",
        };

        WriteHoldoutLocked(Path.Combine(docsPath, HoldoutFileName), holdout);
        File.WriteAllText(
            Path.Combine(docsPath, HoldoutManifestFileName),
            JsonSerializer.Serialize(holdoutManifest, JsonOptions));

        var developmentPath = Path.Combine(docsPath, DevelopmentFileName);
        File.WriteAllText(
            developmentPath,
            JsonSerializer.Serialize(
                new LocationMatchingDevelopmentFile
                {
                    DatasetRole = "development",
                    Cases = development,
                    Evaluation = LocationMatchingBenchmarkSampling.BuildEvaluationScaffold(development),
                },
                JsonOptions));
        File.WriteAllText(
            Path.Combine(docsPath, LegacyBenchmarkFileName),
            JsonSerializer.Serialize(
                development.Where(item => item.IsCalibrationCase).ToArray(),
                JsonOptions));

        File.WriteAllText(
            Path.Combine(docsPath, ChallengeFileName),
            JsonSerializer.Serialize(
                new LocationMatchingChallengeFile
                {
                    DatasetRole = "challenge",
                    ExclusionNote =
                        "Gemarkeerde moeilijke subset van development; geen onafhankelijke holdout. " +
                        "Niet gebruiken voor algemene coverage%.",
                    Cases = challenge,
                    Evaluation = LocationMatchingBenchmarkSampling.BuildEvaluationScaffold(challenge),
                },
                JsonOptions));

        var calibrationPath = Path.Combine(docsPath, CalibrationFileName);
        var calibrationOnDisk = development.Where(item => item.IsCalibrationCase).ToList();
        File.WriteAllText(
            calibrationPath,
            JsonSerializer.Serialize(
                new LocationMatchingCalibrationFile
                {
                    DatasetRole = "calibration",
                    RandomSeed = LocationMatchingBenchmarkSampling.CalibrationSeed,
                    Cases = calibrationOnDisk,
                    Agreement = LocationMatchingBenchmarkSampling.ComputeLabelAgreement(calibrationOnDisk),
                },
                JsonOptions));

        return new LocationMatchingPurifyResult
        {
            PriorLeakage = priorLeakage,
            DevelopmentRole =
                $"Primary development labeling set plus absorbed May–Jul leaked holdout cases " +
                $"and marked challenge subset (n={development.Count}; primary seed={calibrationSeedPool.Count}).",
            ChallengeRole =
                "Gemarkeerde moeilijke subset van development; niet onafhankelijk; niet voor coverage%.",
            HoldoutPeriod = "2025-10-01..2025-12-31 (Jasper De Smet only)",
            PureHoldoutCaseCount = holdout.Count,
            HoldoutUniqueLocationCount = holdout
                .Select(item => item.Lacleunik)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .Count(),
            CalibrationCaseCount = calibrationOnDisk.Count,
            CalibrationReviewerPath = "https://localhost:7211/Pilot/BenchmarkReview",
            HoldoutContentSha256 = contentSha,
            DevelopmentCaseCount = development.Count,
            ChallengeCaseCount = challenge.Count,
        };
    }

    public static IReadOnlyList<LocationMatchingBenchmarkCase> LoadHoldoutCases(string docsPath)
    {
        var path = Path.Combine(docsPath, HoldoutFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<LocationMatchingHoldoutFile>(
                File.ReadAllText(path),
                JsonOptions);
            return file?.Cases ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<LocationMatchingBenchmarkCase> LoadChallengeCases(string docsPath)
    {
        var path = Path.Combine(docsPath, ChallengeFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<LocationMatchingChallengeFile>(
                File.ReadAllText(path),
                JsonOptions);
            return file?.Cases ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<LocationMatchingBenchmarkCase> LoadCalibrationCases(string docsPath)
    {
        var path = Path.Combine(docsPath, CalibrationFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var file = JsonSerializer.Deserialize<LocationMatchingCalibrationFile>(
                File.ReadAllText(path),
                JsonOptions);
            return file?.Cases ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void SaveCalibrationAndDevelopmentCases(
        string docsPath,
        IReadOnlyList<LocationMatchingBenchmarkCase> calibrationLabeled)
    {
        var development = LoadDevelopmentCases(docsPath).ToList();
        var byId = calibrationLabeled.ToDictionary(item => item.PerformanceId);
        for (var index = 0; index < development.Count; index++)
        {
            if (!byId.TryGetValue(development[index].PerformanceId, out var labeled))
            {
                continue;
            }

            development[index] = development[index] with
            {
                Label = labeled.Label,
                ExpectedStopId = labeled.ExpectedStopId,
                ReviewerConfidence = labeled.ReviewerConfidence,
                ReviewerNote = labeled.ReviewerNote,
                SecondReviewLabel = labeled.SecondReviewLabel,
                SecondReviewExpectedStopId = labeled.SecondReviewExpectedStopId,
                SecondReviewerConfidence = labeled.SecondReviewerConfidence,
                SecondReviewerNote = labeled.SecondReviewerNote,
                AdjudicationStatus = labeled.AdjudicationStatus,
                RequiresSecondReview = true,
                IsCalibrationCase = true,
            };
        }

        SaveDevelopmentCases(docsPath, development);
        var calibration = development.Where(item => item.IsCalibrationCase).ToList();
        File.WriteAllText(
            Path.Combine(docsPath, CalibrationFileName),
            JsonSerializer.Serialize(
                new LocationMatchingCalibrationFile
                {
                    DatasetRole = "calibration",
                    RandomSeed = LocationMatchingBenchmarkSampling.CalibrationSeed,
                    Cases = calibration,
                    Agreement = LocationMatchingBenchmarkSampling.ComputeLabelAgreement(calibration),
                },
                JsonOptions));
    }

    private async Task<HashSet<string>> LoadHistoricalLocationsBeforeAsync(
        string technicianName,
        string driverId,
        DateOnly beforeExclusive,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        // Prefer up to three months immediately before the holdout window.
        var cursor = beforeExclusive.AddMonths(-1);
        for (var i = 0; i < 3; i++)
        {
            var from = new DateOnly(cursor.Year, cursor.Month, 1);
            var through = new DateOnly(cursor.Year, cursor.Month, DateTime.DaysInMonth(cursor.Year, cursor.Month));
            if (through >= beforeExclusive)
            {
                through = beforeExclusive.AddDays(-1);
            }

            if (through < from)
            {
                break;
            }

            var slice = await LoadMonthSliceAsync(
                technicianName,
                driverId,
                from,
                through,
                warnings,
                cancellationToken);
            foreach (var lacleunik in slice.Cases
                         .Select(item => item.Lacleunik)
                         .Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                seen.Add(lacleunik!);
            }

            cursor = from.AddMonths(-1);
        }

        return seen;
    }

    /// <summary>
    /// Optimization and review tooling may load development cases only.
    /// </summary>
    public LocationMatchingBenchmarkResult ResampleFromSavedPool(string docsPath)
    {
        var rawPath = Path.Combine(docsPath, RawPoolFileName);
        var completenessPath = Path.Combine(docsPath, CompletenessFileName);
        var reportPath = Path.Combine(docsPath, "location-matching-benchmark-report.json");
        using var rawDoc = JsonDocument.Parse(File.ReadAllText(rawPath));
        var annotatedPool = rawDoc.RootElement.GetProperty("Cases")
            .Deserialize<List<LocationMatchingBenchmarkCase>>(JsonOptions)
            ?? throw new InvalidOperationException("Raw pool Cases ontbreekt.");
        var julyCoreAnnotated = rawDoc.RootElement.GetProperty("JulyCore")
            .Deserialize<List<LocationMatchingBenchmarkCase>>(JsonOptions)
            ?? throw new InvalidOperationException("Raw pool JulyCore ontbreekt.");
        using var completenessDoc = JsonDocument.Parse(File.ReadAllText(completenessPath));
        var completeness = completenessDoc.RootElement.GetProperty("Rows")
            .Deserialize<List<MonthTechnicianCompleteness>>(JsonOptions)
            ?? throw new InvalidOperationException("Completeness Rows ontbreekt.");
        var previous = File.Exists(reportPath)
            ? JsonSerializer.Deserialize<LocationMatchingBenchmarkResult>(
                File.ReadAllText(reportPath),
                JsonOptions)
            : null;

        var completeMonths = SelectCompleteMonths(completeness);
        var eligiblePool = annotatedPool
            .Where(item =>
            {
                var month = item.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                return completeness.Any(row =>
                    row.IsComplete &&
                    string.Equals(row.YearMonth, month, StringComparison.Ordinal) &&
                    string.Equals(row.Technician, item.Technician, StringComparison.Ordinal));
            })
            .ToList();
        var preferredPool = eligiblePool
            .Where(item =>
                completeMonths.Contains(item.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture)))
            .ToList();
        if (preferredPool.Count >=
            LocationMatchingBenchmarkSampling.MaxDevelopmentCases +
            LocationMatchingBenchmarkSampling.HoldoutTargetCases)
        {
            eligiblePool = preferredPool;
        }

        File.WriteAllText(
            Path.Combine(docsPath, CasePoolFileName),
            JsonSerializer.Serialize(eligiblePool, JsonOptions));

        var julyCoreIds = julyCoreAnnotated.Select(item => item.PerformanceId).ToHashSet();
        var holdoutPath = Path.Combine(docsPath, HoldoutFileName);
        var holdoutManifestPath = Path.Combine(docsPath, HoldoutManifestFileName);
        var (holdout, holdoutManifest) = LocationMatchingBenchmarkSampling.SelectHoldoutCases(
            eligiblePool,
            julyCoreIds,
            completeMonths);
        WriteHoldoutLocked(holdoutPath, holdout);
        File.WriteAllText(
            holdoutManifestPath,
            JsonSerializer.Serialize(holdoutManifest, JsonOptions));

        var holdoutIds = holdout.Select(item => item.PerformanceId).ToHashSet();
        var development = LocationMatchingBenchmarkSampling.MarkSecondReviews(
            LocationMatchingBenchmarkSampling.SelectDevelopmentCases(
                julyCoreAnnotated,
                eligiblePool.Where(item => !holdoutIds.Contains(item.PerformanceId)).ToList()));
        var developmentIds = development.Select(item => item.PerformanceId).ToHashSet();
        var challengeExclusive = LocationMatchingBenchmarkSampling.SelectChallengeCases(
            eligiblePool,
            developmentIds.Concat(holdoutIds).ToHashSet());
        var challenge = challengeExclusive.Count >= LocationMatchingBenchmarkSampling.ChallengeMinCases
            ? challengeExclusive
            : LocationMatchingBenchmarkSampling.SelectChallengeCases(
                eligiblePool
                    .Concat(holdout)
                    .GroupBy(item => item.PerformanceId)
                    .Select(group => group.First())
                    .ToList(),
                developmentIds);

        File.WriteAllText(
            completenessPath,
            JsonSerializer.Serialize(
                new
                {
                    CompleteMonths = completeMonths,
                    Warnings = Array.Empty<string>(),
                    Rows = completeness
                        .OrderBy(item => item.YearMonth)
                        .ThenBy(item => item.Technician, StringComparer.Ordinal)
                        .ToArray(),
                },
                JsonOptions));

        var developmentPath = Path.Combine(docsPath, DevelopmentFileName);
        File.WriteAllText(
            developmentPath,
            JsonSerializer.Serialize(
                new LocationMatchingDevelopmentFile
                {
                    DatasetRole = "development",
                    Cases = development,
                    Evaluation = LocationMatchingBenchmarkSampling.BuildEvaluationScaffold(development),
                },
                JsonOptions));
        File.WriteAllText(
            Path.Combine(docsPath, LegacyBenchmarkFileName),
            JsonSerializer.Serialize(development, JsonOptions));

        var challengePath = Path.Combine(docsPath, ChallengeFileName);
        File.WriteAllText(
            challengePath,
            JsonSerializer.Serialize(
                new LocationMatchingChallengeFile
                {
                    DatasetRole = "challenge",
                    ExclusionNote =
                        "Challenge cases are excluded from the general coverage percentage.",
                    Cases = challenge,
                    Evaluation = LocationMatchingBenchmarkSampling.BuildEvaluationScaffold(challenge),
                },
                JsonOptions));

        var holdoutUnique = holdout
            .Select(item => item.Lacleunik)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Count();
        var seenCount = holdout.Count(item =>
            string.Equals(item.LocationExposure, "SeenLocation", StringComparison.Ordinal));
        return new LocationMatchingBenchmarkResult
        {
            DeterministicDenominator = julyCoreAnnotated.Count,
            StablePerformanceIds = julyCoreAnnotated
                .Select(item => item.PerformanceId)
                .OrderBy(id => id)
                .ToArray(),
            PowerfleetGranularity = previous?.PowerfleetGranularity ?? new PowerfleetGranularitySummary
            {
                ReportParameters = [],
                AvailableFields = [],
                HasVendorStops = true,
                HasTripStartEndCoordinates = true,
                HasIndividualPoints = false,
                HasTimestamps = true,
                HasSpeed = true,
                HasIgnition = false,
                HasGpsValidityOrAccuracy = false,
                Limitation = "Resample zonder live Powerfleet-herlezing.",
            },
            BenchmarkCaseCount = development.Count,
            HistoricalClustering = previous?.HistoricalClustering ?? new HistoricalClusterBenchmarkStatus
            {
                HistoryFrom = new DateOnly(2026, 1, 1),
                HistoryThrough = new DateOnly(2026, 6, 30),
                JulyUsedForLearning = false,
                LearnedClusterCount = 0,
                Warnings = ["Resample from saved pool."],
            },
            VariantsReady = previous?.VariantsReady ??
            [
                "baseline",
                "adaptive matcher",
                "baseline + historical clusters",
            ],
            NeedsForMeasuredMetrics =
                "Labels ontbreken; precision/recall/coverage/F1/Wilson/risk-coverage worden pas berekend na blinde development-review.",
            BenchmarkPath = developmentPath,
            CompleteMonths = completeMonths,
            DevelopmentCaseCount = development.Count,
            HoldoutCaseCount = holdout.Count,
            HoldoutUniqueLocationCount = holdoutUnique,
            ChallengeCaseCount = challenge.Count,
            SeenLocationCount = seenCount,
            UnseenLocationCount = holdout.Count - seenCount,
            DevelopmentPath = developmentPath,
            HoldoutPath = holdoutPath,
            ChallengePath = challengePath,
            CompletenessPath = completenessPath,
            BlindReviewerPath = "https://localhost:7211/Pilot/BenchmarkReview",
        };
    }

    /// <summary>
    /// Optimization and review tooling may load development cases only.
    /// </summary>
    public static IReadOnlyList<LocationMatchingBenchmarkCase> LoadDevelopmentCases(string docsPath)
    {
        var path = Path.Combine(docsPath, DevelopmentFileName);
        if (!File.Exists(path))
        {
            var legacy = Path.Combine(docsPath, LegacyBenchmarkFileName);
            if (!File.Exists(legacy))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<LocationMatchingBenchmarkCase>>(
                       File.ReadAllText(legacy),
                       JsonOptions) ??
                   [];
        }

        var file = JsonSerializer.Deserialize<LocationMatchingDevelopmentFile>(
            File.ReadAllText(path),
            JsonOptions);
        return file?.Cases ?? [];
    }

    public static void SaveDevelopmentCases(
        string docsPath,
        IReadOnlyList<LocationMatchingBenchmarkCase> cases)
    {
        var path = Path.Combine(docsPath, DevelopmentFileName);
        var file = new LocationMatchingDevelopmentFile
        {
            DatasetRole = "development",
            Cases = cases.ToArray(),
            Evaluation = LocationMatchingBenchmarkSampling.BuildEvaluationScaffold(cases),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
        File.WriteAllText(
            Path.Combine(docsPath, LegacyBenchmarkFileName),
            JsonSerializer.Serialize(cases, JsonOptions));
    }

    private static bool TryLoadLockedHoldout(
        string holdoutPath,
        string manifestPath,
        out List<LocationMatchingBenchmarkCase> cases,
        out HoldoutSamplingManifest manifest)
    {
        cases = [];
        manifest = null!;
        if (!File.Exists(holdoutPath) || !File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var holdoutFile = JsonSerializer.Deserialize<LocationMatchingHoldoutFile>(
                File.ReadAllText(holdoutPath),
                JsonOptions);
            var loadedManifest = JsonSerializer.Deserialize<HoldoutSamplingManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
            if (holdoutFile is not { Locked: true } || loadedManifest is not { Locked: true })
            {
                return false;
            }

            cases = holdoutFile.Cases.ToList();
            manifest = loadedManifest;
            return cases.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WriteHoldoutLocked(
        string path,
        IReadOnlyList<LocationMatchingBenchmarkCase> cases)
    {
        var file = new LocationMatchingHoldoutFile
        {
            Locked = true,
            DoNotUseForOptimization = true,
            Warning =
                "LOCKED HOLDOUT. Do not read this file for matcher parameter optimization. Use development only.",
            Cases = cases.ToArray(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
    }

    private static List<string> SelectCompleteMonths(
        IReadOnlyList<MonthTechnicianCompleteness> rows)
    {
        return rows
            .GroupBy(item => item.YearMonth, StringComparer.Ordinal)
            .Where(group =>
            {
                var techRows = group.ToArray();
                var completeCount = techRows.Count(item => item.IsComplete);
                var locationBound = techRows.Sum(item => item.LocationBoundPerformances);
                // Majority complete, or enough volume with at least 3 complete technicians.
                return completeCount >= 4 ||
                       (completeCount >= 3 && locationBound >= 80);
            })
            .Select(group => group.Key)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToList();
    }

    private static Dictionary<string, HashSet<string>> BuildHistoricalSeenLookup(
        IReadOnlyDictionary<string, HashSet<string>> locationsByMonth)
    {
        var orderedMonths = locationsByMonth.Keys
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var cumulative = new HashSet<string>(StringComparer.Ordinal);
        foreach (var month in orderedMonths)
        {
            result[month] = new HashSet<string>(cumulative, StringComparer.Ordinal);
            foreach (var location in locationsByMonth[month])
            {
                cumulative.Add(location);
            }
        }

        return result;
    }

    private async Task<MonthSliceResult> LoadMonthSliceAsync(
        string technicianName,
        string driverId,
        DateOnly from,
        DateOnly through,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var yearMonth = from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var request = new ReadOnlyPilotRequest(
            technicianName,
            from,
            through,
            PowerfleetDriverId: driverId,
            DriverOnlyLinking: true,
            ResolveAllLocations: true,
            MaximumPerformances: 500,
            MaximumTrips: 1000);
        try
        {
            var plenion = await plenionReader.ReadAsync(request, cancellationToken);
            var powerfleet = await powerfleetReader.ReadAsync(request, cancellationToken);
            var matchedTrips = powerfleet.NormalizedRecords
                .Where(trip => string.Equals(trip.DriverId, driverId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(trip => trip.StartDateTime)
                .ToArray();
            var issues = plenion.Issues.Concat(powerfleet.Issues).ToList();
            var powerfleetFailed = powerfleet.Issues.Any(issue =>
                issue.Message.Contains("tripid", StringComparison.OrdinalIgnoreCase) ||
                (issue.Category.Equals("Powerfleet", StringComparison.OrdinalIgnoreCase) &&
                 issue.Message.Contains("geen", StringComparison.OrdinalIgnoreCase)));
            if (matchedTrips.Length == 0 && powerfleet.NormalizedRecords.Count == 0)
            {
                powerfleetFailed = true;
            }

            var stops = PilotLocationMatcher.ReconstructStops(matchedTrips, issues);
            var resolutions = await locationResolutionPilotService.ResolveAsync(
                plenion.NormalizedRecords,
                stops,
                true,
                cancellationToken);
            var resolutionById = resolutions.ToDictionary(item => item.PerformanceId);
            var cases = new List<LocationMatchingBenchmarkCase>();
            var uniqueLocations = new HashSet<string>(StringComparer.Ordinal);
            var withCandidates = 0;
            var locationBound = 0;
            foreach (var performance in plenion.NormalizedRecords
                         .OrderBy(item => item.Date)
                         .ThenBy(item => item.StartDateTime))
            {
                if (!resolutionById.TryGetValue(performance.ExternalId, out var resolution))
                {
                    continue;
                }

                var classification = PerformanceActivityClassifier.Classify(
                    performance,
                    technicianName,
                    resolution);
                if (!classification.RequiresGeographicMatch)
                {
                    continue;
                }

                locationBound++;
                if (!string.IsNullOrWhiteSpace(performance.DeliveryAddressExternalId))
                {
                    uniqueLocations.Add(performance.DeliveryAddressExternalId);
                }

                if (resolution.Candidates.Count > 0)
                {
                    withCandidates++;
                }

                var dayPerformances = plenion.NormalizedRecords
                    .Where(item => item.Date == performance.Date)
                    .OrderBy(item => item.StartDateTime)
                    .ToArray();
                cases.Add(BuildCase(
                    technicianName,
                    performance,
                    resolution,
                    classification.ActivityType.ToString(),
                    dayPerformances));
            }

            var missingDriver = issues.Count(issue =>
                issue.Category.Equals("MissingDriver", StringComparison.Ordinal));
            var isComplete = !powerfleetFailed &&
                             locationBound > 0 &&
                             matchedTrips.Length > 0 &&
                             missingDriver <= Math.Max(3, matchedTrips.Length / 5);
            var notes = powerfleetFailed
                ? "Powerfleet-slice onbruikbaar of zonder ritten."
                : isComplete
                    ? "Voldoende complete data."
                    : "Onvoldoende locatiegebonden prestaties, ritten of kandidaatstops.";
            if (!isComplete)
            {
                warnings.Add($"{technicianName} {yearMonth}: {notes}");
            }

            return new MonthSliceResult(
                new MonthTechnicianCompleteness
                {
                    Technician = technicianName,
                    YearMonth = yearMonth,
                    LocationBoundPerformances = locationBound,
                    PowerfleetTrips = matchedTrips.Length,
                    PerformancesWithCandidateStops = withCandidates,
                    UniqueLacleunikCount = uniqueLocations.Count,
                    MissingDriverTripCount = missingDriver,
                    IsComplete = isComplete,
                    Notes = notes,
                },
                cases);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"{technicianName} {yearMonth}: slice mislukt ({exception.Message}).");
            return new MonthSliceResult(
                new MonthTechnicianCompleteness
                {
                    Technician = technicianName,
                    YearMonth = yearMonth,
                    LocationBoundPerformances = 0,
                    PowerfleetTrips = 0,
                    PerformancesWithCandidateStops = 0,
                    UniqueLacleunikCount = 0,
                    MissingDriverTripCount = 0,
                    IsComplete = false,
                    Notes = $"Slice mislukt: {exception.Message}",
                },
                []);
        }
    }

    private static List<DeterministicCase> CollectDeterministicCases(BroaderValidationResult broader)
    {
        var cases = new List<DeterministicCase>();
        foreach (var technician in broader.Technicians.Where(item => item.Processed && item.PilotResult is not null))
        {
            var pilot = technician.PilotResult!;
            var resolutionById = pilot.LocationResolutions.ToDictionary(item => item.PerformanceId);
            foreach (var performance in pilot.PlenionRecords.OrderBy(item => item.Date).ThenBy(item => item.StartDateTime))
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

                cases.Add(new DeterministicCase(
                    technician.Technician?.Name ?? technician.Query,
                    performance,
                    resolution,
                    classification.ActivityType.ToString(),
                    pilot.PlenionRecords.Where(item => item.Date == performance.Date)
                        .OrderBy(item => item.StartDateTime)
                        .ToArray()));
            }
        }

        return cases
            .OrderBy(item => item.Performance.Date)
            .ThenBy(item => item.Technician)
            .ThenBy(item => item.Performance.StartDateTime)
            .ThenBy(item => item.Performance.ExternalId)
            .ToList();
    }

    private static List<LocationMatchingBenchmarkCase> BuildBenchmarkCases(
        IReadOnlyList<DeterministicCase> cases) =>
        cases
            .Select(item => BuildCase(
                item.Technician,
                item.Performance,
                item.Resolution,
                item.ActivityType,
                item.DayPerformances))
            .ToList();

    private static LocationMatchingBenchmarkCase BuildCase(
        string technician,
        NormalizedPilotPerformance performance,
        PilotLocationResolution resolution,
        string activityType,
        NormalizedPilotPerformance[] dayPerformances)
    {
        var geocodeQuality = GeocodeQualityClassifier.Classify(resolution.Geocoding);
        var index = Array.FindIndex(
            dayPerformances,
            item => item.ExternalId == performance.ExternalId);
        var previous = index > 0 ? dayPerformances[index - 1] : null;
        var next = index >= 0 && index < dayPerformances.Length - 1 ? dayPerformances[index + 1] : null;
        return new LocationMatchingBenchmarkCase
        {
            PerformanceId = performance.ExternalId,
            Technician = technician,
            Date = performance.Date,
            Start = performance.StartDateTime,
            End = performance.EndDateTime,
            Lacleunik = performance.DeliveryAddressExternalId,
            PlenionAddress = resolution.OriginalAddress,
            GeocodeQuality = geocodeQuality,
            ExistingMatchStatus = resolution.MatchStatus.ToString(),
            ActivityType = activityType,
            PreviousPerformance = previous is null
                ? null
                : $"{previous.ExternalId} {previous.StartDateTime:HH:mm}-{previous.EndDateTime:HH:mm}",
            NextPerformance = next is null
                ? null
                : $"{next.ExternalId} {next.StartDateTime:HH:mm}-{next.EndDateTime:HH:mm}",
            Candidates = resolution.Candidates
                .OrderByDescending(candidate => candidate.TotalScore)
                .ThenBy(candidate => candidate.Stop.Arrival)
                .Select(candidate => new LocationMatchingBenchmarkCandidate
                {
                    StopId = candidate.Stop.StopId,
                    Address = candidate.Stop.Address,
                    DistanceMeters = candidate.DistanceMeters,
                    Arrival = candidate.Stop.Arrival,
                    Departure = candidate.Stop.Departure,
                    OverlapMinutes = candidate.TimeOverlapMinutes,
                    StartDifferenceMinutes = candidate.StartDifferenceMinutes,
                    EndDifferenceMinutes = candidate.EndDifferenceMinutes,
                    ExistingCandidateStatus = candidate.MatchStatus.ToString(),
                    ExistingCandidateScore = candidate.TotalScore,
                    Explanation = candidate.Explanation,
                })
                .ToArray(),
            Label = null,
        };
    }

    private static PowerfleetGranularitySummary BuildPowerfleetGranularity(
        BroaderValidationResult broader)
    {
        var pilot = broader.Technicians
            .Where(item => item.Processed && item.PilotResult is not null)
            .Select(item => item.PilotResult!)
            .First();
        var reportParametersObservation = pilot.SourceObservations
            .FirstOrDefault(item => item.StartsWith("Powerfleet rapportparameters:", StringComparison.Ordinal));
        var reportParameters = reportParametersObservation is null
            ? Array.Empty<string>()
            : reportParametersObservation
                .Split(':', 2)[1]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var rawFields = pilot.RawPowerfleetRecords
            .SelectMany(item => item.Fields.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var hasIndividualPoints = rawFields.Any(field =>
            field.Contains("point", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("gpspoint", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("timestamp", StringComparison.OrdinalIgnoreCase) ||
            field.Equals("latitude", StringComparison.OrdinalIgnoreCase) ||
            field.Equals("longitude", StringComparison.OrdinalIgnoreCase));
        var hasIgnition = rawFields.Any(field =>
            field.Contains("ignition", StringComparison.OrdinalIgnoreCase));
        var hasAccuracy = rawFields.Any(field =>
            field.Contains("accuracy", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("gpsvalid", StringComparison.OrdinalIgnoreCase) ||
            field.Contains("hdop", StringComparison.OrdinalIgnoreCase));
        return new PowerfleetGranularitySummary
        {
            ReportParameters = reportParameters,
            AvailableFields = rawFields,
            HasVendorStops = true,
            HasTripStartEndCoordinates = rawFields.Contains("startlatitude", StringComparer.OrdinalIgnoreCase) &&
                                         rawFields.Contains("startlongitude", StringComparer.OrdinalIgnoreCase) &&
                                         rawFields.Contains("endlatitude", StringComparer.OrdinalIgnoreCase) &&
                                         rawFields.Contains("endlongitude", StringComparer.OrdinalIgnoreCase),
            HasIndividualPoints = hasIndividualPoints,
            HasTimestamps = rawFields.Contains("startdate", StringComparer.OrdinalIgnoreCase) &&
                            rawFields.Contains("enddate", StringComparer.OrdinalIgnoreCase),
            HasSpeed = rawFields.Contains("maxspeed", StringComparer.OrdinalIgnoreCase) ||
                       rawFields.Contains("avgspeed", StringComparer.OrdinalIgnoreCase),
            HasIgnition = hasIgnition,
            HasGpsValidityOrAccuracy = hasAccuracy,
            Limitation = hasIndividualPoints
                ? "Rapport bevat ruwe punten; spatio-temporele stopdetectie kan worden vergeleken met vendor stops."
                : "Rapport bevat vendor-trip/stopinformatie met start- en eindcoÃ¶rdinaten, maar geen individuele GPS-punten of expliciete ignition/accuracy-velden.",
        };
    }

    private sealed record DeterministicCase(
        string Technician,
        NormalizedPilotPerformance Performance,
        PilotLocationResolution Resolution,
        string ActivityType,
        NormalizedPilotPerformance[] DayPerformances);

    private sealed record MonthSliceResult(
        MonthTechnicianCompleteness Completeness,
        IReadOnlyList<LocationMatchingBenchmarkCase> Cases);
}
