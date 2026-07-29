using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Evaluates baseline / adaptive / baseline+historical on the 30 SingleReviewerReferenceSet cases.
/// Does not read locked holdout and does not change matcher thresholds.
/// </summary>
internal sealed class CalibrationSingleReviewerEvaluationService(
    IBroaderValidationPilotService broaderValidationPilotService,
    PilotPlenionReader plenionReader,
    PilotPowerfleetReader powerfleetReader,
    LocationResolutionPilotService locationResolutionPilotService,
    IDistanceCalculator distanceCalculator)
{
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

    public async Task<CalibrationSingleReviewerEvaluationResult> EvaluateAsync(
        string docsPath,
        CancellationToken cancellationToken)
    {
        var calibration = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath)
            .Where(item => !string.IsNullOrWhiteSpace(item.Label))
            .OrderBy(item => item.PerformanceId)
            .ToArray();
        if (calibration.Length != LocationMatchingBenchmarkSampling.CalibrationCaseCount)
        {
            throw new InvalidOperationException(
                $"SingleReviewerReferenceSet verwacht {LocationMatchingBenchmarkSampling.CalibrationCaseCount} gelabelde cases; gevonden {calibration.Length}.");
        }

        var options = new AdaptiveLocationMatchingOptions();
        options.Validate();
        var broader = await broaderValidationPilotService.RunAsync(
            new BroaderValidationRequest(
                TechnicianNames.Select(name => new BroaderValidationTechnicianRequest(name)).ToArray(),
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 28),
                5),
            cancellationToken);
        var driverIds = broader.Technicians
            .Where(item => item.Processed && !string.IsNullOrWhiteSpace(item.DriverId))
            .ToDictionary(
                item => item.Technician?.Name ?? item.Query,
                item => item.DriverId!,
                StringComparer.OrdinalIgnoreCase);

        var neededMonths = TechnicianNames
            .SelectMany(technician => Enumerable.Range(1, 7).Select(month => (technician, Year: 2026, Month: month)))
            .OrderBy(item => item.technician, StringComparer.Ordinal)
            .ThenBy(item => item.Year)
            .ThenBy(item => item.Month)
            .ToList();

        var warnings = new List<string>();
        var liveByPerformance = new Dictionary<long, LiveCaseContext>();
        var learningObservations = new List<(
            NormalizedPilotPerformance Performance,
            string TechnicianName,
            PilotLocationResolution? Resolution,
            IReadOnlyList<MergedPilotStop> DayStops)>();
        var calibrationIds = calibration.Select(item => item.PerformanceId).ToHashSet();

        foreach (var (technician, year, month) in neededMonths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!driverIds.TryGetValue(technician, out var driverId))
            {
                warnings.Add($"{technician}: geen driverid.");
                continue;
            }

            var from = new DateOnly(year, month, 1);
            var through = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            if (year == 2026 && month == 7)
            {
                through = new DateOnly(2026, 7, 28);
            }

            var slice = await LoadSliceAsync(
                technician,
                driverId,
                from,
                through,
                warnings,
                cancellationToken);
            foreach (var context in slice)
            {
                liveByPerformance[context.Performance.ExternalId] = context;
                var inLearningWindow =
                    context.Performance.Date >= new DateOnly(2026, 1, 1) &&
                    context.Performance.Date <= new DateOnly(2026, 6, 30);
                if (inLearningWindow && !calibrationIds.Contains(context.Performance.ExternalId))
                {
                    learningObservations.Add((
                        context.Performance,
                        context.TechnicianName,
                        context.Resolution,
                        MergedStopBuilder.Merge(context.DayStops, options, distanceCalculator)));
                }
            }
        }

        var clusters = HistoricalLocationClusterLearner.Learn(
            learningObservations,
            options,
            distanceCalculator);
        var clustersByLocation = clusters.ToDictionary(
            item => item.PlenionLocationKey,
            StringComparer.Ordinal);

        var baseline = EvaluateVariant(
            "baseline",
            calibration,
            item => PredictBaseline(item));
        var adaptive = EvaluateVariant(
            "adaptive",
            calibration,
            item => PredictAdaptive(
                item,
                liveByPerformance,
                options,
                clustersByLocation,
                enableLearning: false));
        var hybrid = EvaluateVariant(
            "hybrid",
            calibration,
            item => PredictHybrid(
                item,
                liveByPerformance,
                options,
                clustersByLocation));

        var variants = new[] { baseline, adaptive, hybrid };
        var gapAnalysis = BuildGapAnalysis(
            calibration,
            liveByPerformance,
            options,
            clustersByLocation);
        var recoveredIds = gapAnalysis
            .Where(item => item.HybridRecovered)
            .Select(item => item.PerformanceId)
            .OrderBy(id => id)
            .ToArray();
        var (criteriaMet, criteriaNotes) = EvaluateHybridAcceptance(
            hybrid,
            recoveredIds,
            gapAnalysis);
        var developmentSanity = EvaluateDevelopmentSanity(
            docsPath,
            liveByPerformance,
            options,
            clustersByLocation,
            warnings);

        var best = criteriaMet ? "hybrid" : SelectBest(variants);
        var causes = SummarizeErrorCauses(variants.First(item => item.Name == best));
        var result = new CalibrationSingleReviewerEvaluationResult
        {
            ReferenceSet = "SingleReviewerReferenceSet (reviewer 1, n=30)",
            CaseCount = calibration.Length,
            HighConfidenceCaseCount = calibration.Count(item =>
                string.Equals(item.ReviewerConfidence, "High", StringComparison.Ordinal)),
            Variants = variants,
            BestVariant = best,
            MainErrorCauses = causes,
            RecommendedNextStep = criteriaMet
                ? "Breid SingleReviewerReferenceSet labeling uit op development voordat holdout wordt geopend."
                : RecommendNextStep(hybrid),
            LearnedClusterCount = clusters.Count,
            GapAnalysis = gapAnalysis,
            RecoveredPerformanceIds = recoveredIds,
            HybridAcceptanceCriteriaMet = criteriaMet,
            HybridAcceptanceNotes = criteriaNotes,
            DevelopmentSanityCheck = developmentSanity,
        };

        File.WriteAllText(
            Path.Combine(docsPath, "calibration-single-reviewer-eval.json"),
            JsonSerializer.Serialize(result, JsonOptions));
        if (warnings.Count > 0)
        {
            File.WriteAllText(
                Path.Combine(docsPath, "calibration-single-reviewer-eval-warnings.json"),
                JsonSerializer.Serialize(warnings, JsonOptions));
        }

        return result;
    }

    private static CalibrationVariantMetrics EvaluateVariant(
        string name,
        IReadOnlyList<LocationMatchingBenchmarkCase> cases,
        Func<LocationMatchingBenchmarkCase, Prediction> predict)
    {
        var scored = cases.Select(item => ScoreCase(item, predict(item))).ToArray();
        return new CalibrationVariantMetrics
        {
            Name = name,
            AllCases = ToSlice(scored),
            HighConfidenceOnly = ToSlice(
                scored.Where(item =>
                    string.Equals(item.Confidence, "High", StringComparison.Ordinal))
                    .ToArray()),
            Errors = scored
                .Where(item => item.Error is not null)
                .Select(item => item.Error!)
                .OrderBy(item => item.PerformanceId)
                .ToArray(),
        };
    }

    private static ScoredCase ScoreCase(
        LocationMatchingBenchmarkCase item,
        Prediction prediction)
    {
        var label = item.Label!;
        var confidence = item.ReviewerConfidence ?? "Medium";
        var expected = string.IsNullOrWhiteSpace(item.ExpectedStopId)
            ? null
            : item.ExpectedStopId.Trim();
        var error = (CalibrationCaseError?)null;
        var correctAccepted = false;
        var falsePositive = false;
        var falseNegative = false;
        var wrongStop = false;

        if (string.Equals(label, "CorrectCandidate", StringComparison.Ordinal))
        {
            if (!prediction.Accepted)
            {
                falseNegative = true;
                error = Error(item, prediction, "FN: matcher onthield zich bij CorrectCandidate.");
            }
            else if (StopMatches(expected, prediction.StopId, prediction.SourceStopIds))
            {
                correctAccepted = true;
            }
            else
            {
                falsePositive = true;
                wrongStop = true;
                error = Error(
                    item,
                    prediction,
                    $"FP: verkeerde StopId (verwacht {expected ?? "null"}, kreeg {prediction.StopId ?? "null"}).");
            }
        }
        else if (string.Equals(label, "NoValidCandidate", StringComparison.Ordinal) ||
                 string.Equals(label, "Ambiguous", StringComparison.Ordinal))
        {
            if (prediction.Accepted)
            {
                falsePositive = true;
                error = Error(
                    item,
                    prediction,
                    $"FP: matcher accepteerde stop bij {label} ({prediction.StopId ?? "null"}).");
            }
        }
        else
        {
            error = Error(item, prediction, $"Onbekend label: {label}.");
        }

        return new ScoredCase(
            confidence,
            prediction.Accepted,
            correctAccepted,
            falsePositive,
            falseNegative,
            wrongStop,
            error);
    }

    private static CalibrationMetricSlice ToSlice(IReadOnlyList<ScoredCase> scored)
    {
        var accepted = scored.Count(item => item.Accepted);
        var correctAccepted = scored.Count(item => item.CorrectAccepted);
        var fp = scored.Count(item => item.FalsePositive);
        var fn = scored.Count(item => item.FalseNegative);
        var wrong = scored.Count(item => item.WrongStopId);
        return new CalibrationMetricSlice
        {
            CaseCount = scored.Count,
            AcceptedMatches = accepted,
            CorrectAcceptedMatches = correctAccepted,
            Precision = Round(accepted == 0 ? 0 : correctAccepted / (double)accepted),
            Coverage = Round(scored.Count == 0 ? 0 : accepted / (double)scored.Count),
            FalsePositives = fp,
            FalseNegatives = fn,
            WrongStopIdChoices = wrong,
        };
    }

    private static Prediction PredictBaseline(LocationMatchingBenchmarkCase item)
    {
        var accepted = item.ExistingMatchStatus is "ConfirmedLocationMatch" or "ProbableLocationMatch";
        if (!accepted)
        {
            return new Prediction(false, item.ExistingMatchStatus, null, [], UsedRecovery: false);
        }

        var top = item.Candidates
            .OrderByDescending(candidate => candidate.ExistingCandidateScore)
            .ThenBy(candidate => candidate.Arrival)
            .FirstOrDefault();
        return new Prediction(
            true,
            item.ExistingMatchStatus,
            top?.StopId,
            top is null ? [] : [top.StopId],
            UsedRecovery: false);
    }

    private Prediction PredictAdaptive(
        LocationMatchingBenchmarkCase item,
        IReadOnlyDictionary<long, LiveCaseContext> liveByPerformance,
        AdaptiveLocationMatchingOptions options,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clusters,
        bool enableLearning)
    {
        if (!liveByPerformance.TryGetValue(item.PerformanceId, out var live))
        {
            return new Prediction(false, "MissingLiveData", null, [], UsedRecovery: false);
        }

        var merged = MergedStopBuilder.Merge(live.DayStops, options, distanceCalculator);
        var result = AdaptiveLocationMatcher.Match(
            live.Performance,
            live.TechnicianName,
            live.Resolution,
            merged,
            live.SameDayPerformances,
            clusters,
            options,
            distanceCalculator,
            enableLearning);
        var accepted = result.Decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable;
        var sources = result.Selected?.Stop.SourceStopIds.ToArray() ?? [];
        var stopId = sources.Length > 0 ? sources[0] : result.Selected?.Stop.MergedStopId;
        return new Prediction(
            accepted,
            result.Decision.ToString(),
            stopId,
            sources,
            UsedRecovery: false);
    }

    private Prediction PredictHybrid(
        LocationMatchingBenchmarkCase item,
        IReadOnlyDictionary<long, LiveCaseContext> liveByPerformance,
        AdaptiveLocationMatchingOptions options,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clusters)
    {
        if (!liveByPerformance.TryGetValue(item.PerformanceId, out var live))
        {
            return new Prediction(false, "MissingLiveData", null, [], UsedRecovery: false);
        }

        var merged = MergedStopBuilder.Merge(live.DayStops, options, distanceCalculator);
        var result = PrecisionPreservingHybridMatcher.Match(
            live.Performance,
            live.TechnicianName,
            live.Resolution,
            merged,
            live.SameDayPerformances,
            clusters,
            options,
            distanceCalculator);
        var accepted = result.Decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable;
        var sources = result.Selected?.Stop.SourceStopIds.ToArray() ?? [];
        var stopId = sources.Length > 0 ? sources[0] : result.Selected?.Stop.MergedStopId;
        return new Prediction(
            accepted,
            result.UsedRecovery ? "RecoveredProbable" : result.Decision.ToString(),
            stopId,
            sources,
            result.UsedRecovery);
    }

    private CalibrationGapCaseAnalysis[] BuildGapAnalysis(
        IReadOnlyList<LocationMatchingBenchmarkCase> calibration,
        IReadOnlyDictionary<long, LiveCaseContext> liveByPerformance,
        AdaptiveLocationMatchingOptions options,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clusters)
    {
        long[] focusIds =
        [
            279763, 279971, 280191, 280278, 280279, 280344, 280347, 280198,
        ];
        return focusIds
            .Select(id =>
            {
                var item = calibration.First(caseItem => caseItem.PerformanceId == id);
                var baselineAccepted = item.ExistingMatchStatus is "ConfirmedLocationMatch"
                    or "ProbableLocationMatch";
                AdaptiveMatchResult? adaptiveResult = null;
                AdaptiveMatchResult? hybridResult = null;
                if (liveByPerformance.TryGetValue(id, out var live))
                {
                    var merged = MergedStopBuilder.Merge(live.DayStops, options, distanceCalculator);
                    adaptiveResult = AdaptiveLocationMatcher.Match(
                        live.Performance,
                        live.TechnicianName,
                        live.Resolution,
                        merged,
                        live.SameDayPerformances,
                        clusters,
                        options,
                        distanceCalculator,
                        enableLearning: false);
                    hybridResult = PrecisionPreservingHybridMatcher.Match(
                        live.Performance,
                        live.TechnicianName,
                        live.Resolution,
                        merged,
                        live.SameDayPerformances,
                        clusters,
                        options,
                        distanceCalculator);
                }

                var top = adaptiveResult is { Candidates.Count: > 0 }
                    ? adaptiveResult.Candidates[0]
                    : ToOfflineCandidate(item);
                var second = adaptiveResult is { Candidates.Count: > 1 }
                    ? adaptiveResult.Candidates[1]
                    : null;
                var isRecoverableGap = baselineAccepted &&
                    adaptiveResult?.Decision == AdaptiveMatchDecision.Unresolved;
                return new CalibrationGapCaseAnalysis
                {
                    PerformanceId = id,
                    Label = item.Label ?? string.Empty,
                    BaselineAccepted = baselineAccepted,
                    AdaptiveUnresolved = adaptiveResult?.Decision == AdaptiveMatchDecision.Unresolved,
                    IsRecoverableGap = isRecoverableGap,
                    DistanceMeters = top?.DistanceMeters,
                    OverlapMinutes = top?.OverlapMinutes ?? 0,
                    OverlapPercent = top?.OverlapPercent ?? 0,
                    ArrivalVersusStartMinutes = top?.ArrivalDifferenceMinutes ?? 0,
                    DepartureVersusEndMinutes = top?.DepartureDifferenceMinutes ?? 0,
                    GeocodeQuality = adaptiveResult?.GeocodeQuality.ToString()
                        ?? item.GeocodeQuality.ToString(),
                    CompetingCandidateCount = Math.Max(0, (adaptiveResult?.Candidates.Count ?? item.Candidates.Count) - 1),
                    ScoreMarginVsSecond = second is null || top is null
                        ? null
                        : Math.Round(top.TotalScore - second.TotalScore, 1),
                    PreviousPerformance = item.PreviousPerformance,
                    NextPerformance = item.NextPerformance,
                    AdaptiveAbstentionReason = DescribeAbstention(adaptiveResult, top),
                    HybridRecovered = hybridResult?.UsedRecovery == true,
                    HybridRecoveryReason = hybridResult?.RecoveryReason,
                };
            })
            .ToArray();
    }

    private static AdaptiveMatchCandidate? ToOfflineCandidate(LocationMatchingBenchmarkCase item)
    {
        var top = item.Candidates
            .OrderByDescending(candidate => candidate.ExistingCandidateScore)
            .ThenBy(candidate => candidate.Arrival)
            .FirstOrDefault();
        if (top is null)
        {
            return null;
        }

        var duration = Math.Max(1, (item.End - item.Start).TotalMinutes);
        var overlapPercent = 100d * top.OverlapMinutes / duration;
        var zone = top.DistanceMeters switch
        {
            null => AdaptiveDistanceZone.Unknown,
            <= 100 => AdaptiveDistanceZone.Strong0To100,
            <= 250 => AdaptiveDistanceZone.Probable101To250,
            <= 500 => AdaptiveDistanceZone.Learned251To500,
            _ => AdaptiveDistanceZone.Beyond500,
        };
        return new AdaptiveMatchCandidate(
            new MergedPilotStop(
                top.StopId,
                item.Date,
                top.Arrival,
                top.Departure,
                Math.Max(1, (int)(top.Departure - top.Arrival).TotalMinutes),
                top.Address,
                null,
                null,
                null,
                null,
                [top.StopId],
                false),
            top.DistanceMeters,
            zone,
            top.OverlapMinutes,
            overlapPercent,
            top.StartDifferenceMinutes,
            top.EndDifferenceMinutes,
            Math.Max(1, (int)(top.Departure - top.Arrival).TotalMinutes),
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            top.ExistingCandidateScore,
            null,
            top.Explanation);
    }

    private static string DescribeAbstention(
        AdaptiveMatchResult? adaptive,
        AdaptiveMatchCandidate? top)
    {
        if (adaptive is null)
        {
            return "MissingLiveData";
        }

        if (adaptive.Decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable)
        {
            return $"Accepted as {adaptive.Decision}";
        }

        if (top is null)
        {
            return $"{adaptive.Decision}: geen kandidaten.";
        }

        if (top.OverlapMinutes <= 0)
        {
            return $"{adaptive.Decision}: geen positieve tijdsoverlap (arrival vs performance).";
        }

        if (top.DistanceZone == AdaptiveDistanceZone.Learned251To500 &&
            top.HistoricalClusterId is null)
        {
            return $"{adaptive.Decision}: zone 251-500 zonder historical cluster.";
        }

        if (top.DistanceZone == AdaptiveDistanceZone.Probable101To250 &&
            top.HasCompetingPerformanceOverlap)
        {
            return $"{adaptive.Decision}: probable-afstand met concurrerende prestatie-overlap.";
        }

        if (top.OverlapMinutes < 5 || top.OverlapPercent < 20)
        {
            return $"{adaptive.Decision}: onvoldoende strongTime (overlap {top.OverlapMinutes} min / {top.OverlapPercent:0.#}%).";
        }

        if (!adaptive.UsedAsPrecisePoint)
        {
            return $"{adaptive.Decision}: niet-precise geocode ({adaptive.GeocodeQuality}) met score {top.TotalScore} onder bevestigingsdrempel.";
        }

        return $"{adaptive.Decision}: {adaptive.Assessment}";
    }

    private static (bool Met, string Notes) EvaluateHybridAcceptance(
        CalibrationVariantMetrics hybrid,
        IReadOnlyList<long> recoveredIds,
        IReadOnlyList<CalibrationGapCaseAnalysis> gapAnalysis)
    {
        var all = hybrid.AllCases;
        var fp280198 = hybrid.Errors.Any(item => item.PerformanceId == 280198);
        var recoverable = gapAnalysis.Where(item => item.IsRecoverableGap).ToArray();
        var recoveredRecoverable = recoverable.Count(item => item.HybridRecovered);
        var notes = new List<string>();
        if (all.FalsePositives != 0)
        {
            notes.Add($"FP={all.FalsePositives} (vereist 0)");
        }

        if (Math.Abs(all.Precision - 1.0) > 0.0001)
        {
            notes.Add($"Precision={all.Precision} (vereist 1.000)");
        }

        if (all.CorrectAcceptedMatches < 10)
        {
            notes.Add($"CorrectAccepted={all.CorrectAcceptedMatches} (vereist >=10)");
        }

        if (all.Coverage < 0.333)
        {
            notes.Add($"Coverage={all.Coverage} (vereist >=0.333)");
        }

        if (all.WrongStopIdChoices != 0)
        {
            notes.Add($"WrongStopId={all.WrongStopIdChoices} (vereist 0)");
        }

        if (fp280198)
        {
            notes.Add("280198 werd geaccepteerd (moet NoValidCandidate blijven)");
        }

        if (recoveredRecoverable < recoverable.Length)
        {
            notes.Add(
                $"Herstelbare gaps {recoveredRecoverable}/{recoverable.Length} hersteld " +
                $"(ids {string.Join(',', recoverable.Select(item => item.PerformanceId))}).");
        }

        _ = recoveredIds;
        return notes.Count == 0
            ? (true, "Alle hybrid-acceptatiecriteria gehaald.")
            : (false, string.Join(" ", notes));
    }

    private DevelopmentHybridSanityCheck EvaluateDevelopmentSanity(
        string docsPath,
        IReadOnlyDictionary<long, LiveCaseContext> liveByPerformance,
        AdaptiveLocationMatchingOptions options,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clusters,
        List<string> warnings)
    {
        var development = LocationMatchingBenchmarkService.LoadDevelopmentCases(docsPath)
            .Where(item => !item.IsChallengeSubset)
            .OrderBy(item => item.PerformanceId)
            .ToArray();
        var accepted = 0;
        var unresolved = 0;
        var ambiguous = 0;
        var recoveryOnly = 0;
        var byDistance = new Dictionary<string, int>(StringComparer.Ordinal);
        var byOverlap = new Dictionary<string, int>(StringComparer.Ordinal);
        var missingLive = 0;
        var recoveryFar = 0;
        var recoveryWeakOverlap = 0;

        foreach (var item in development)
        {
            if (!liveByPerformance.TryGetValue(item.PerformanceId, out var live))
            {
                missingLive++;
                unresolved++;
                continue;
            }

            var merged = MergedStopBuilder.Merge(live.DayStops, options, distanceCalculator);
            var adaptive = AdaptiveLocationMatcher.Match(
                live.Performance,
                live.TechnicianName,
                live.Resolution,
                merged,
                live.SameDayPerformances,
                clusters,
                options,
                distanceCalculator,
                enableLearning: false);
            var hybrid = PrecisionPreservingHybridMatcher.Match(
                live.Performance,
                live.TechnicianName,
                live.Resolution,
                merged,
                live.SameDayPerformances,
                clusters,
                options,
                distanceCalculator);

            switch (hybrid.Decision)
            {
                case AdaptiveMatchDecision.Confirmed:
                case AdaptiveMatchDecision.Probable:
                    accepted++;
                    break;
                case AdaptiveMatchDecision.Ambiguous:
                    ambiguous++;
                    break;
                default:
                    unresolved++;
                    break;
            }

            if (!hybrid.UsedRecovery)
            {
                continue;
            }

            recoveryOnly++;
            var zone = hybrid.Selected?.DistanceZone.ToString() ?? "Unknown";
            byDistance[zone] = byDistance.GetValueOrDefault(zone) + 1;
            var overlapZone = ClassifyOverlapZone(hybrid.Selected);
            byOverlap[overlapZone] = byOverlap.GetValueOrDefault(overlapZone) + 1;
            if (hybrid.Selected?.DistanceMeters is > 200)
            {
                recoveryFar++;
            }

            if (hybrid.Selected is { OverlapMinutes: < 10, OverlapPercent: < 50 })
            {
                recoveryWeakOverlap++;
            }

            _ = adaptive;
        }

        if (missingLive > 0)
        {
            warnings.Add($"Development sanity: {missingLive} cases zonder live context.");
        }

        var risks = new List<string>();
        if (recoveryFar > 0)
        {
            risks.Add($"{recoveryFar} recovery-matches met afstand >200 m.");
        }

        if (recoveryWeakOverlap > 0)
        {
            risks.Add($"{recoveryWeakOverlap} recovery-matches met zwakke overlap (<10 min en <50%).");
        }

        if (missingLive > 0)
        {
            risks.Add($"{missingLive} developmentcases zonder live data (geteld als Unresolved).");
        }

        if (risks.Count == 0)
        {
            risks.Add("Geen opvallende risicopatronen in recovery-matches.");
        }

        return new DevelopmentHybridSanityCheck
        {
            CaseCount = development.Length,
            Accepted = accepted,
            Unresolved = unresolved,
            Ambiguous = ambiguous,
            RecoveryOnlyMatches = recoveryOnly,
            RecoveryByDistanceZone = byDistance,
            RecoveryByOverlapZone = byOverlap,
            NotableRiskPatterns = risks,
        };
    }

    private static string ClassifyOverlapZone(AdaptiveMatchCandidate? selected)
    {
        if (selected is null)
        {
            return "None";
        }

        if (selected.OverlapPercent >= 50 || selected.OverlapMinutes >= 30)
        {
            return "StrongOverlap";
        }

        if (selected.OverlapPercent >= 30 || selected.OverlapMinutes >= 4)
        {
            return "ModerateOverlap";
        }

        return "WeakOverlap";
    }

    private async Task<List<LiveCaseContext>> LoadSliceAsync(
        string technicianName,
        string driverId,
        DateOnly from,
        DateOnly through,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
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
            var stops = PilotLocationMatcher.ReconstructStops(matchedTrips, issues);
            var resolutions = await locationResolutionPilotService.ResolveAsync(
                plenion.NormalizedRecords,
                stops,
                true,
                cancellationToken);
            var resolutionById = resolutions.ToDictionary(item => item.PerformanceId);
            var result = new List<LiveCaseContext>();
            foreach (var performance in plenion.NormalizedRecords)
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

                var dayStops = stops.Where(stop => stop.Date == performance.Date).ToArray();
                var sameDay = plenion.NormalizedRecords
                    .Where(item => item.Date == performance.Date)
                    .OrderBy(item => item.StartDateTime)
                    .ToArray();
                result.Add(new LiveCaseContext(
                    performance,
                    technicianName,
                    resolution,
                    sameDay,
                    dayStops));
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            warnings.Add($"{technicianName} {from:yyyy-MM}: {exception.Message}");
            return [];
        }
    }

    private static string SelectBest(IReadOnlyList<CalibrationVariantMetrics> variants) =>
        variants
            .OrderBy(item => item.AllCases.FalsePositives)
            .ThenByDescending(item => item.AllCases.Precision)
            .ThenByDescending(item => item.AllCases.Coverage)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .First()
            .Name;

    private static string[] SummarizeErrorCauses(CalibrationVariantMetrics best)
    {
        if (best.Errors.Count == 0)
        {
            return ["Geen fouten op de 30 SingleReviewerReferenceSet-cases."];
        }

        return best.Errors
            .GroupBy(item =>
            {
                if (item.Reason.StartsWith("FP: verkeerde StopId", StringComparison.Ordinal))
                {
                    return "Verkeerde StopId bij CorrectCandidate";
                }

                if (item.Reason.StartsWith("FP: matcher accepteerde", StringComparison.Ordinal))
                {
                    return "Acceptatie bij NoValidCandidate/Ambiguous";
                }

                if (item.Reason.StartsWith("FN:", StringComparison.Ordinal))
                {
                    return "Onthouding bij CorrectCandidate";
                }

                return "Overig";
            })
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} ({group.Count()})")
            .ToArray();
    }

    private static string RecommendNextStep(CalibrationVariantMetrics best)
    {
        if (best.AllCases.FalsePositives > 0)
        {
            return "Verlaag false positives op de SingleReviewerReferenceSet door acceptatieregels te verscherpen voor concurrerende stops en AddressDataIssue-cases, zonder holdout te raken.";
        }

        if (best.AllCases.FalseNegatives > 0)
        {
            return "Verhoog recall op CorrectCandidate-cases met onthouding via gerichte historical-cluster of overlapregels, enkel op development/kalibratie.";
        }

        return "Breid labeling uit voorbij de 30-case kalibratie zodra precision stabiel blijft.";
    }

    private static bool StopMatches(
        string? expectedStopId,
        string? predictedStopId,
        IReadOnlyList<string> sourceStopIds)
    {
        if (string.IsNullOrWhiteSpace(expectedStopId))
        {
            return false;
        }

        if (string.Equals(expectedStopId, predictedStopId, StringComparison.Ordinal))
        {
            return true;
        }

        return sourceStopIds.Any(id => string.Equals(id, expectedStopId, StringComparison.Ordinal));
    }

    private static CalibrationCaseError Error(
        LocationMatchingBenchmarkCase item,
        Prediction prediction,
        string reason) =>
        new()
        {
            PerformanceId = item.PerformanceId,
            Label = item.Label!,
            ExpectedStopId = item.ExpectedStopId,
            ReviewerConfidence = item.ReviewerConfidence ?? "Medium",
            Reason = reason,
            PredictedDecision = prediction.Decision,
            PredictedStopId = prediction.StopId,
        };

    private static double Round(double value) =>
        Math.Round(value, 4);

    private sealed record Prediction(
        bool Accepted,
        string Decision,
        string? StopId,
        IReadOnlyList<string> SourceStopIds,
        bool UsedRecovery);

    private sealed record ScoredCase(
        string Confidence,
        bool Accepted,
        bool CorrectAccepted,
        bool FalsePositive,
        bool FalseNegative,
        bool WrongStopId,
        CalibrationCaseError? Error);

    private sealed record LiveCaseContext(
        NormalizedPilotPerformance Performance,
        string TechnicianName,
        PilotLocationResolution Resolution,
        IReadOnlyList<NormalizedPilotPerformance> SameDayPerformances,
        IReadOnlyList<PilotStop> DayStops);
}
