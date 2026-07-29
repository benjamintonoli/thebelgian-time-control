using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal static class LocationMatchingBenchmarkSampling
{
    public const int DevelopmentSeed = 20260729;
    public const int HoldoutSeed = 202607291;
    public const int ChallengeSeed = 202607292;
    public const int ReviewOrderSeed = 202607293;
    public const int MaxDevelopmentCases = 200;
    public const int HoldoutTargetCases = 300;
    public const int ChallengeMinCases = 75;
    public const int ChallengeMaxCases = 100;
    public const int MaxCasesPerLacleunikHoldout = 8;
    public const int MinUniqueHoldoutLocations = 100;
    public const int CalibrationSeed = 202607294;
    public const int CalibrationCaseCount = 30;
    public const int PureHoldoutSeed = 20251001;
    public const int RecoveryAuditSeed = 202607295;
    public const int RecoveryAuditMaxCases = 60;
    public const int RecoveryAuditControlTarget = 15;
    public const double SecondReviewFraction = 0.20;

    public static string DistanceBucket(double? meters) =>
        meters switch
        {
            null => "unknown",
            <= 100 => "0-100",
            <= 250 => "101-250",
            <= 500 => "251-500",
            _ => ">500",
        };

    public static string CandidateBucket(int count) =>
        count switch
        {
            <= 0 => "0",
            1 => "1",
            _ => "2+",
        };

    public static string Exposure(
        string? lacleunik,
        IReadOnlySet<string> historicallySeenLocations) =>
        string.IsNullOrWhiteSpace(lacleunik)
            ? "UnseenLocation"
            : historicallySeenLocations.Contains(lacleunik.Trim())
                ? "SeenLocation"
                : "UnseenLocation";

    public static List<LocationMatchingBenchmarkCase> SelectDevelopmentCases(
        IReadOnlyList<LocationMatchingBenchmarkCase> julyCore,
        IReadOnlyList<LocationMatchingBenchmarkCase> pool,
        int maxCount = MaxDevelopmentCases)
    {
        var selected = new Dictionary<long, LocationMatchingBenchmarkCase>();
        foreach (var item in julyCore)
        {
            selected[item.PerformanceId] = item with { DatasetRole = "development" };
        }

        if (selected.Count >= maxCount)
        {
            return selected.Values
                .OrderBy(item => item.Date)
                .ThenBy(item => item.Technician, StringComparer.Ordinal)
                .ThenBy(item => item.PerformanceId)
                .Take(maxCount)
                .ToList();
        }

        var remaining = pool
            .Where(item => !selected.ContainsKey(item.PerformanceId))
            .ToList();
        var buckets = remaining
            .GroupBy(item => DevelopmentBucketKey(item), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => Shuffle(group.ToList(), DevelopmentSeed ^ StableHash(group.Key)).ToList(),
                StringComparer.Ordinal);

        while (selected.Count < maxCount && buckets.Values.Any(list => list.Count > 0))
        {
            foreach (var key in buckets.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray())
            {
                if (selected.Count >= maxCount)
                {
                    break;
                }

                var list = buckets[key];
                if (list.Count == 0)
                {
                    continue;
                }

                var next = list[0];
                list.RemoveAt(0);
                selected[next.PerformanceId] = next with { DatasetRole = "development" };
            }
        }

        return selected.Values
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.Ordinal)
            .ThenBy(item => item.PerformanceId)
            .ToList();
    }

    public static (List<LocationMatchingBenchmarkCase> Cases, HoldoutSamplingManifest Manifest)
        SelectHoldoutCases(
            IReadOnlyList<LocationMatchingBenchmarkCase> eligible,
            IReadOnlySet<long> excludedIds,
            IReadOnlyList<string> completeMonths,
            int targetCount = HoldoutTargetCases,
            int seed = HoldoutSeed)
    {
        var candidates = eligible
            .Where(item => !excludedIds.Contains(item.PerformanceId))
            .Select(item => item with { DatasetRole = "holdout" })
            .ToList();
        var byBucket = candidates
            .GroupBy(item => HoldoutBucketKey(item), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => Shuffle(group.ToList(), seed ^ StableHash(group.Key)).ToList(),
                StringComparer.Ordinal);

        var selected = new List<LocationMatchingBenchmarkCase>();
        var perLocation = new Dictionary<string, int>(StringComparer.Ordinal);
        while (selected.Count < targetCount && byBucket.Values.Any(list => list.Count > 0))
        {
            var progressed = false;
            foreach (var key in byBucket.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray())
            {
                if (selected.Count >= targetCount)
                {
                    break;
                }

                var list = byBucket[key];
                while (list.Count > 0)
                {
                    var next = list[0];
                    list.RemoveAt(0);
                    var locationKey = string.IsNullOrWhiteSpace(next.Lacleunik)
                        ? "NONE:" + next.PerformanceId.ToString(CultureInfo.InvariantCulture)
                        : next.Lacleunik.Trim();
                    perLocation.TryGetValue(locationKey, out var used);
                    if (used >= MaxCasesPerLacleunikHoldout)
                    {
                        continue;
                    }

                    selected.Add(next);
                    perLocation[locationKey] = used + 1;
                    progressed = true;
                    break;
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        if (selected.Select(item => item.Lacleunik)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.Ordinal)
                .Count() < MinUniqueHoldoutLocations)
        {
            // Prefer filling unused locations first for uniqueness.
            var unused = candidates
                .Where(item => selected.All(selectedItem => selectedItem.PerformanceId != item.PerformanceId))
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Lacleunik) &&
                    !perLocation.ContainsKey(item.Lacleunik!))
                .OrderBy(item => item.Date)
                .ThenBy(item => item.PerformanceId)
                .ToList();
            foreach (var item in unused)
            {
                if (selected.Count >= targetCount)
                {
                    break;
                }

                selected.Add(item);
                perLocation[item.Lacleunik!] = 1;
            }
        }

        if (selected.Count < targetCount)
        {
            var remaining = Shuffle(
                candidates
                    .Where(item => selected.All(selectedItem => selectedItem.PerformanceId != item.PerformanceId))
                    .ToList(),
                seed ^ 17);
            foreach (var item in remaining)
            {
                if (selected.Count >= targetCount)
                {
                    break;
                }

                var locationKey = string.IsNullOrWhiteSpace(item.Lacleunik)
                    ? "NONE:" + item.PerformanceId.ToString(CultureInfo.InvariantCulture)
                    : item.Lacleunik.Trim();
                perLocation.TryGetValue(locationKey, out var used);
                if (used >= MaxCasesPerLacleunikHoldout)
                {
                    continue;
                }

                selected.Add(item);
                perLocation[locationKey] = used + 1;
            }
        }

        var uniqueCount = selected
            .Select(item => item.Lacleunik)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (selected.Count < targetCount && uniqueCount >= MinUniqueHoldoutLocations)
        {
            // Soft fill: uniqueness target already met; allow more per location to reach 300.
            var remaining = Shuffle(
                candidates
                    .Where(item => selected.All(selectedItem => selectedItem.PerformanceId != item.PerformanceId))
                    .ToList(),
                seed ^ 29);
            foreach (var item in remaining)
            {
                if (selected.Count >= targetCount)
                {
                    break;
                }

                selected.Add(item);
            }
        }

        selected = selected
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.Ordinal)
            .ThenBy(item => item.PerformanceId)
            .Take(targetCount)
            .ToList();

        var manifest = new HoldoutSamplingManifest
        {
            RandomSeed = seed,
            GeneratedAt = DateTimeOffset.UtcNow,
            Locked = true,
            TargetCaseCount = targetCount,
            MaxCasesPerLacleunik = MaxCasesPerLacleunikHoldout,
            MinUniqueLacleunik = MinUniqueHoldoutLocations,
            SelectedPerformanceIds = selected.Select(item => item.PerformanceId).ToArray(),
            CompleteMonthsUsed = completeMonths.ToArray(),
            CountsByTechnician = selected
                .GroupBy(item => item.Technician, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            CountsByMonth = selected
                .GroupBy(item => item.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            CountsByExposure = selected
                .GroupBy(item => item.LocationExposure ?? "UnseenLocation", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
        };
        return (selected, manifest);
    }

    public static List<LocationMatchingBenchmarkCase> SelectChallengeCases(
        IReadOnlyList<LocationMatchingBenchmarkCase> eligible,
        IReadOnlySet<long> excludedIds,
        int minCount = ChallengeMinCases,
        int maxCount = ChallengeMaxCases,
        int seed = ChallengeSeed)
    {
        var hard = eligible
            .Where(item => !excludedIds.Contains(item.PerformanceId))
            .Where(IsChallengeCase)
            .Select(item => item with { DatasetRole = "challenge" })
            .ToList();
        var shuffled = Shuffle(hard, seed);
        var take = Math.Clamp(shuffled.Count, 0, maxCount);
        if (take < minCount)
        {
            take = Math.Min(shuffled.Count, maxCount);
        }

        return shuffled
            .Take(Math.Max(take, 0))
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.Ordinal)
            .ThenBy(item => item.PerformanceId)
            .ToList();
    }

    public static bool IsChallengeCase(LocationMatchingBenchmarkCase item)
    {
        var bestDistance = item.Candidates
            .Where(candidate => candidate.DistanceMeters is not null)
            .Select(candidate => candidate.DistanceMeters!.Value)
            .DefaultIfEmpty(double.MaxValue)
            .Min();
        var competing = item.Candidates.Count(candidate => candidate.ExistingCandidateScore > 0) >= 2;
        var multiPerformanceSameStop = item.Candidates.Any(candidate =>
            candidate.OverlapMinutes > 0 &&
            (item.PreviousPerformance is not null || item.NextPerformance is not null));
        return competing ||
               bestDistance > 250 ||
               item.ExistingMatchStatus is "ManualReviewRequired" or "NoReliableMatch" or "AddressDataIssue" ||
               item.GeocodeQuality is GeocodeQualityClass.StreetOnly
                   or GeocodeQualityClass.LowConfidence
                   or GeocodeQualityClass.Unusable ||
               item.Candidates.Count == 0 ||
               multiPerformanceSameStop ||
               item.Candidates.Any(candidate =>
                   candidate.Explanation.Contains("parking", StringComparison.OrdinalIgnoreCase) ||
                   candidate.Explanation.Contains("toegang", StringComparison.OrdinalIgnoreCase) ||
                   candidate.Explanation.Contains("huisnummer", StringComparison.OrdinalIgnoreCase));
    }

    public static List<LocationMatchingBenchmarkCase> MarkSecondReviews(
        IReadOnlyList<LocationMatchingBenchmarkCase> cases,
        double fraction = SecondReviewFraction,
        int seed = ReviewOrderSeed)
    {
        var ordered = Shuffle(cases.ToList(), seed).ToList();
        var count = (int)Math.Ceiling(ordered.Count * fraction);
        var secondIds = ordered.Take(count).Select(item => item.PerformanceId).ToHashSet();
        return cases
            .Select(item => item with { RequiresSecondReview = secondIds.Contains(item.PerformanceId) })
            .ToList();
    }

    public static List<LocationMatchingBenchmarkCase> BlindReviewOrder(
        IReadOnlyList<LocationMatchingBenchmarkCase> cases,
        int seed = ReviewOrderSeed) =>
        Shuffle(cases.ToList(), seed);

    public static BenchmarkEvaluationScaffold BuildEvaluationScaffold(
        IReadOnlyList<LocationMatchingBenchmarkCase> cases)
    {
        var labeled = cases.Count(item => !string.IsNullOrWhiteSpace(item.Label));
        return new BenchmarkEvaluationScaffold
        {
            LabelsPresent = labeled > 0 && labeled == cases.Count,
            Status = labeled == 0
                ? "Geen labels aanwezig; precision/recall/coverage/F1 nog niet berekend."
                : labeled < cases.Count
                    ? $"Gedeeltelijk gelabeld ({labeled}/{cases.Count}); wacht op volledige labels."
                    : "Labels volledig; metrics kunnen berekend worden.",
            PreparedMetrics =
            [
                "precision",
                "recall",
                "coverage",
                "F1",
                "false positives",
                "false negatives",
                "Wilson 95% CI",
                "risk-coverage curve",
                "SeenLocation / UnseenLocation split",
                "challenge result (separate)",
            ],
        };
    }

    public static List<LocationMatchingBenchmarkCase> SelectCalibrationCases(
        IReadOnlyList<LocationMatchingBenchmarkCase> development,
        int count = CalibrationCaseCount,
        int seed = CalibrationSeed)
    {
        var pool = development
            .Where(item => !item.IsChallengeSubset)
            .ToList();
        var byBucket = pool
            .GroupBy(CalibrationBucketKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => Shuffle(group.ToList(), seed ^ StableHash(group.Key)).ToList(),
                StringComparer.Ordinal);

        var selected = new Dictionary<long, LocationMatchingBenchmarkCase>();
        while (selected.Count < count && byBucket.Values.Any(list => list.Count > 0))
        {
            var progressed = false;
            foreach (var key in byBucket.Keys.OrderBy(item => item, StringComparer.Ordinal).ToArray())
            {
                if (selected.Count >= count)
                {
                    break;
                }

                var list = byBucket[key];
                if (list.Count == 0)
                {
                    continue;
                }

                var next = list[0];
                list.RemoveAt(0);
                selected[next.PerformanceId] = next with
                {
                    IsCalibrationCase = true,
                    RequiresSecondReview = true,
                    DatasetRole = "development",
                };
                progressed = true;
            }

            if (!progressed)
            {
                break;
            }
        }

        if (selected.Count < count)
        {
            foreach (var item in Shuffle(pool, seed ^ 41))
            {
                if (selected.Count >= count)
                {
                    break;
                }

                if (selected.ContainsKey(item.PerformanceId))
                {
                    continue;
                }

                selected[item.PerformanceId] = item with
                {
                    IsCalibrationCase = true,
                    RequiresSecondReview = true,
                    DatasetRole = "development",
                };
            }
        }

        return selected.Values
            .OrderBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.Ordinal)
            .ThenBy(item => item.PerformanceId)
            .Take(count)
            .ToList();
    }

    public static BenchmarkLabelAgreement ComputeLabelAgreement(
        IReadOnlyList<LocationMatchingBenchmarkCase> cases)
    {
        var doubleLabeled = cases
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Label) &&
                !string.IsNullOrWhiteSpace(item.SecondReviewLabel))
            .ToArray();
        if (doubleLabeled.Length == 0)
        {
            return new BenchmarkLabelAgreement
            {
                CaseCount = cases.Count,
                DoubleLabeledCount = 0,
                ExactLabelAgreementCount = 0,
                ExactLabelAgreementRate = 0,
                ExpectedStopIdAgreementCount = 0,
                ExpectedStopIdAgreementRate = 0,
                ConflictCount = 0,
                CohensKappa = 0,
                Status = "Nog geen dubbele labels; agreement/kappa nog niet berekend.",
            };
        }

        var exact = doubleLabeled.Count(item =>
            string.Equals(item.Label, item.SecondReviewLabel, StringComparison.Ordinal));
        var stopAgree = doubleLabeled.Count(item =>
            string.Equals(item.ExpectedStopId, item.SecondReviewExpectedStopId, StringComparison.Ordinal));
        var conflicts = doubleLabeled.Length - exact;
        var kappa = CohensKappa(
            doubleLabeled.Select(item => item.Label!).ToArray(),
            doubleLabeled.Select(item => item.SecondReviewLabel!).ToArray());
        return new BenchmarkLabelAgreement
        {
            CaseCount = cases.Count,
            DoubleLabeledCount = doubleLabeled.Length,
            ExactLabelAgreementCount = exact,
            ExactLabelAgreementRate = Math.Round(exact / (double)doubleLabeled.Length, 4),
            ExpectedStopIdAgreementCount = stopAgree,
            ExpectedStopIdAgreementRate = Math.Round(stopAgree / (double)doubleLabeled.Length, 4),
            ConflictCount = conflicts,
            CohensKappa = Math.Round(kappa, 4),
            Status = doubleLabeled.Length < cases.Count
                ? $"Gedeeltelijk dubbel gelabeld ({doubleLabeled.Length}/{cases.Count})."
                : "Dubbele labeling volledig; agreement/kappa berekend.",
        };
    }

    public static string ComputeContentSha256(IReadOnlyList<LocationMatchingBenchmarkCase> cases)
    {
        var canonical = string.Join(
            '\n',
            cases
                .OrderBy(item => item.PerformanceId)
                .Select(item =>
                    string.Join(
                        '|',
                        item.PerformanceId.ToString(CultureInfo.InvariantCulture),
                        item.Technician,
                        item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        item.Lacleunik ?? string.Empty,
                        item.LocationExposure ?? string.Empty,
                        item.Candidates.Count.ToString(CultureInfo.InvariantCulture))));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static BenchmarkLeakageAudit AuditLeakage(
        IReadOnlyList<LocationMatchingBenchmarkCase> development,
        IReadOnlyList<LocationMatchingBenchmarkCase> holdout,
        IReadOnlyList<LocationMatchingBenchmarkCase> challenge,
        DateOnly historicalFrom,
        DateOnly historicalThrough)
    {
        static BenchmarkDatasetAuditRow Row(string name, IReadOnlyList<LocationMatchingBenchmarkCase> cases)
        {
            if (cases.Count == 0)
            {
                return new BenchmarkDatasetAuditRow
                {
                    Name = name,
                    PeriodFrom = "n/a",
                    PeriodThrough = "n/a",
                    CaseCount = 0,
                    UniquePerformanceIds = 0,
                    UniqueLacleuniks = 0,
                };
            }

            return new BenchmarkDatasetAuditRow
            {
                Name = name,
                PeriodFrom = cases.Min(item => item.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                PeriodThrough = cases.Max(item => item.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CaseCount = cases.Count,
                UniquePerformanceIds = cases.Select(item => item.PerformanceId).Distinct().Count(),
                UniqueLacleuniks = cases
                    .Select(item => item.Lacleunik)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.Ordinal)
                    .Count(),
            };
        }

        var devIds = development.Select(item => item.PerformanceId).ToHashSet();
        var holdIds = holdout.Select(item => item.PerformanceId).ToHashSet();
        var chalIds = challenge.Select(item => item.PerformanceId).ToHashSet();
        static string TechKey(LocationMatchingBenchmarkCase item) =>
            $"{item.Technician}|{item.Date:yyyy-MM-dd}|{item.PerformanceId}";
        var devKeys = development.Select(TechKey).ToHashSet(StringComparer.Ordinal);
        var holdKeys = holdout.Select(TechKey).ToHashSet(StringComparer.Ordinal);
        var holdInHist = holdout.Count(item => item.Date >= historicalFrom && item.Date <= historicalThrough);
        var holdMayJun = holdout.Count(item =>
            item.Date.Year == 2026 && item.Date.Month is 5 or 6);
        var holdJuly = holdout.Count(item => item.Date.Year == 2026 && item.Date.Month == 7);
        var mayJunBoth = holdMayJun > 0 &&
                         historicalFrom <= new DateOnly(2026, 6, 30) &&
                         historicalThrough >= new DateOnly(2026, 5, 1);
        var findings = new List<string>();
        if (mayJunBoth)
        {
            findings.Add(
                "LEAKAGE: mei en/of juni 2026 zitten in holdout én in het historical-learningvenster.");
        }

        if (holdJuly > 0)
        {
            findings.Add("LEAKAGE: juli 2026 holdout overlapt met eerdere parameter-/regelbeoordeling.");
        }

        if (holdInHist > 0)
        {
            findings.Add(
                $"LEAKAGE: {holdInHist} holdoutcases vallen in historical learning ({historicalFrom:yyyy-MM-dd}..{historicalThrough:yyyy-MM-dd}).");
        }

        if (devIds.Overlaps(holdIds) || holdIds.Overlaps(chalIds))
        {
            findings.Add("LEAKAGE: PerformanceId-overlap tussen datasets.");
        }

        if (findings.Count == 0)
        {
            findings.Add("Geen PerformanceId-/periode-leakage gevonden t.o.v. historical learning en juli-tuning.");
        }

        return new BenchmarkLeakageAudit
        {
            Datasets = [Row("development", development), Row("holdout", holdout), Row("challenge", challenge)],
            PerformanceIdOverlapDevHoldout = devIds.Count(holdIds.Contains),
            PerformanceIdOverlapDevChallenge = devIds.Count(chalIds.Contains),
            PerformanceIdOverlapHoldoutChallenge = holdIds.Count(chalIds.Contains),
            TechDatePerformanceOverlapDevHoldout = holdKeys.Count(devKeys.Contains),
            HoldoutInHistoricalLearningWindowCount = holdInHist,
            HoldoutInMayJun2026Count = holdMayJun,
            HoldoutInJuly2026Count = holdJuly,
            MayOrJuneUsedAsBothHistoricalAndHoldout = mayJunBoth,
            Findings = findings,
        };
    }

    private static double CohensKappa(string[] a, string[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        var labels = a.Concat(b).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        var n = a.Length;
        var matrix = new int[labels.Length, labels.Length];
        var index = labels
            .Select((label, i) => (label, i))
            .ToDictionary(item => item.label, item => item.i, StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            matrix[index[a[i]], index[b[i]]]++;
        }

        double agree = 0;
        for (var i = 0; i < labels.Length; i++)
        {
            agree += matrix[i, i];
        }

        agree /= n;
        double expected = 0;
        for (var i = 0; i < labels.Length; i++)
        {
            double row = 0;
            double col = 0;
            for (var j = 0; j < labels.Length; j++)
            {
                row += matrix[i, j];
                col += matrix[j, i];
            }

            expected += (row / n) * (col / n);
        }

        return Math.Abs(1 - expected) < 1e-12 ? 0 : (agree - expected) / (1 - expected);
    }

    private static string CalibrationBucketKey(LocationMatchingBenchmarkCase item)
    {
        var bestDistance = item.Candidates
            .Where(candidate => candidate.DistanceMeters is not null)
            .Select(candidate => candidate.DistanceMeters)
            .DefaultIfEmpty(null)
            .Min();
        return string.Join(
            '|',
            DistanceBucket(bestDistance),
            CandidateBucket(item.Candidates.Count),
            item.GeocodeQuality is GeocodeQualityClass.PreciseBuilding
                or GeocodeQualityClass.PreciseAmenity
                or GeocodeQualityClass.PartialAddress
                ? "high-geocode"
                : "low-geocode",
            item.LocationExposure ?? "UnseenLocation");
    }

    private static string DevelopmentBucketKey(LocationMatchingBenchmarkCase item)
    {
        var bestDistance = item.Candidates
            .Where(candidate => candidate.DistanceMeters is not null)
            .Select(candidate => candidate.DistanceMeters)
            .FirstOrDefault();
        return string.Join(
            '|',
            DistanceBucket(bestDistance),
            CandidateBucket(item.Candidates.Count),
            item.GeocodeQuality,
            item.ExistingMatchStatus,
            item.Technician);
    }

    private static string HoldoutBucketKey(LocationMatchingBenchmarkCase item) =>
        string.Join(
            '|',
            item.Technician,
            item.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            item.ActivityType ?? "Unknown",
            item.GeocodeQuality,
            CandidateBucket(item.Candidates.Count),
            item.LocationExposure ?? "UnseenLocation");

    private static List<T> Shuffle<T>(IReadOnlyList<T> source, int seed)
    {
        var list = source.ToList();
        var random = new Random(seed);
        for (var index = list.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (list[index], list[swap]) = (list[swap], list[index]);
        }

        return list;
    }

    public static List<RecoveryAuditClassifiedCase> SelectRecoveryAuditCases(
        IReadOnlyList<RecoveryAuditClassifiedCase> pool,
        int maxCount = RecoveryAuditMaxCases,
        int controlTarget = RecoveryAuditControlTarget,
        int seed = RecoveryAuditSeed)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        var recovery = pool.Where(item => item.UsedRecovery).ToList();
        var adaptiveAccepted = pool
            .Where(item => !item.UsedRecovery && item.AdaptiveAccepted)
            .ToList();
        var abstention = pool
            .Where(item =>
                !item.UsedRecovery &&
                !item.AdaptiveAccepted &&
                item.HybridAbstention)
            .ToList();

        var selected = new Dictionary<long, RecoveryAuditClassifiedCase>();
        foreach (var item in recovery.OrderBy(item => item.PerformanceId))
        {
            selected[item.PerformanceId] = TagRecoveryAuditStrata(item);
        }

        var remaining = Math.Max(0, maxCount - selected.Count);
        int positiveSlots;
        int negativeSlots;
        if (remaining >= 2 * controlTarget)
        {
            positiveSlots = controlTarget;
            negativeSlots = controlTarget;
        }
        else
        {
            positiveSlots = Math.Min(controlTarget, (remaining + 1) / 2);
            negativeSlots = Math.Min(controlTarget, remaining - positiveSlots);
        }

        foreach (var item in Shuffle(adaptiveAccepted, seed ^ 11).Take(positiveSlots))
        {
            selected[item.PerformanceId] = TagRecoveryAuditStrata(item);
        }

        foreach (var item in Shuffle(abstention, seed ^ 29).Take(negativeSlots))
        {
            selected[item.PerformanceId] = TagRecoveryAuditStrata(item);
        }

        // Top up only under-filled control strata, never beyond controlTarget.
        var positiveCount = selected.Values.Count(item =>
            item.Strata.Contains("AdaptiveAcceptedControl", StringComparer.Ordinal));
        var negativeCount = selected.Values.Count(item =>
            item.Strata.Contains("AbstentionControl", StringComparer.Ordinal));
        remaining = Math.Max(0, maxCount - selected.Count);
        if (remaining > 0 && positiveCount < controlTarget)
        {
            foreach (var item in Shuffle(adaptiveAccepted, seed ^ 47)
                         .Where(caseItem => !selected.ContainsKey(caseItem.PerformanceId))
                         .Take(Math.Min(controlTarget - positiveCount, remaining)))
            {
                selected[item.PerformanceId] = TagRecoveryAuditStrata(item);
            }
        }

        remaining = Math.Max(0, maxCount - selected.Count);
        negativeCount = selected.Values.Count(item =>
            item.Strata.Contains("AbstentionControl", StringComparer.Ordinal));
        if (remaining > 0 && negativeCount < controlTarget)
        {
            foreach (var item in Shuffle(abstention, seed ^ 53)
                         .Where(caseItem => !selected.ContainsKey(caseItem.PerformanceId))
                         .Take(Math.Min(controlTarget - negativeCount, remaining)))
            {
                selected[item.PerformanceId] = TagRecoveryAuditStrata(item);
            }
        }

        return selected.Values
            .OrderBy(item => item.PerformanceId)
            .ToList();
    }

    public static RecoveryAuditDistribution BuildRecoveryAuditDistribution(
        IReadOnlyList<RecoveryAuditClassifiedCase> selected) =>
        new()
        {
            RecoveryOnly = selected.Count(item => item.UsedRecovery),
            AdaptiveAcceptedControl = selected.Count(item =>
                item.Strata.Contains("AdaptiveAcceptedControl", StringComparer.Ordinal)),
            AbstentionControl = selected.Count(item =>
                item.Strata.Contains("AbstentionControl", StringComparer.Ordinal)),
            WeakOverlapRecovery = selected.Count(item =>
                item.Strata.Contains("WeakOverlapRecovery", StringComparer.Ordinal)),
            ProbableDistanceRecovery = selected.Count(item =>
                item.Strata.Contains("ProbableDistanceRecovery", StringComparer.Ordinal)),
            WeakGeocodeRecovery = selected.Count(item =>
                item.Strata.Contains("WeakGeocodeRecovery", StringComparer.Ordinal)),
            Total = selected.Count,
        };

    private static RecoveryAuditClassifiedCase TagRecoveryAuditStrata(
        RecoveryAuditClassifiedCase item)
    {
        var strata = new List<string>();
        if (item.UsedRecovery)
        {
            strata.Add("RecoveryOnly");
            if (item.SelectedOverlapMinutes is < 10 &&
                item.SelectedOverlapPercent is < 50)
            {
                strata.Add("WeakOverlapRecovery");
            }

            if (item.SelectedDistanceMeters is > 100 and <= 250)
            {
                strata.Add("ProbableDistanceRecovery");
            }

            if (item.GeocodeQuality is "LowConfidence" or "PartialAddress")
            {
                strata.Add("WeakGeocodeRecovery");
            }
        }
        else if (item.AdaptiveAccepted)
        {
            strata.Add("AdaptiveAcceptedControl");
        }
        else if (item.HybridAbstention)
        {
            strata.Add("AbstentionControl");
        }

        return item with { Strata = strata };
    }

    private static int StableHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return BitConverter.ToInt32(bytes, 0);
    }
}
