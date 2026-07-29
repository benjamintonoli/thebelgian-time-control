using System.Globalization;
using System.Text;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

/// <summary>
/// Independent recovery-audit set over development cases outside the 30 calibration cases.
/// Never reads locked holdout. Does not change matcher thresholds.
/// </summary>
internal sealed class LocationMatchingRecoveryAuditService(
    IBroaderValidationPilotService broaderValidationPilotService,
    PilotPlenionReader plenionReader,
    PilotPowerfleetReader powerfleetReader,
    LocationResolutionPilotService locationResolutionPilotService,
    IDistanceCalculator distanceCalculator)
{
    public const string PackMarkdownFileName = "recovery-audit-pack.md";
    public const string LabelsFileName = "recovery-audit-labels.json";
    public const string SetFileName = "recovery-audit-set.json";
    public const string EvaluationFileName = "recovery-audit-evaluation.json";

    private static readonly string[] TechnicianNames =
    [
        "Filip Dekuyper",
        "Jonas Deklerck",
        "Jasper De Smet",
        "Jarno Vergauwen",
        "Dimitri Stiers",
    ];

    private static readonly string[] AllowedLabels =
    [
        "CorrectCandidate",
        "NoValidCandidate",
        "Ambiguous",
    ];

    private static readonly string[] AllowedConfidence =
    [
        "High",
        "Medium",
        "Low",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<RecoveryAuditExportResult> ExportAsync(
        string docsPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(docsPath);
        var options = new AdaptiveLocationMatchingOptions();
        options.Validate();

        var calibrationIds = LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath)
            .Select(item => item.PerformanceId)
            .ToHashSet();
        var pool = LocationMatchingBenchmarkService.LoadDevelopmentCases(docsPath)
            .Where(item => !item.IsChallengeSubset && !calibrationIds.Contains(item.PerformanceId))
            .OrderBy(item => item.PerformanceId)
            .ToArray();
        if (pool.Length == 0)
        {
            throw new InvalidOperationException(
                "Geen developmentcases buiten kalibratie beschikbaar voor recovery-audit.");
        }

        var liveByPerformance = await LoadLiveContextsAsync(docsPath, options, cancellationToken);
        var emptyClusters = new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal);
        var classified = new List<RecoveryAuditClassifiedCase>(pool.Length);
        foreach (var item in pool)
        {
            classified.Add(Classify(item, liveByPerformance, options, emptyClusters));
        }

        var selected = LocationMatchingBenchmarkSampling.SelectRecoveryAuditCases(classified);
        var distribution = LocationMatchingBenchmarkSampling.BuildRecoveryAuditDistribution(selected);
        var auditCases = selected.Select(ToAuditCase).ToArray();
        var orderedForBlind = LocationMatchingBenchmarkSampling.BlindReviewOrder(
            auditCases.Select(item => ToBenchmarkShape(item)).ToArray(),
            LocationMatchingBenchmarkSampling.RecoveryAuditSeed);
        var orderIndex = orderedForBlind
            .Select((item, index) => (item.PerformanceId, Index: index))
            .ToDictionary(item => item.PerformanceId, item => item.Index);
        auditCases = auditCases
            .OrderBy(item => orderIndex[item.PerformanceId])
            .ToArray();

        var set = new RecoveryAuditSetFile
        {
            DatasetRole = "recovery-audit",
            ExportedAt = DateTimeOffset.UtcNow,
            RandomSeed = LocationMatchingBenchmarkSampling.RecoveryAuditSeed,
            CaseCount = auditCases.Length,
            Distribution = distribution,
            BlindNote =
                "Blind pack: no matcher variant, status, score, recovery reason, or prediction. " +
                "Candidate order is by arrival then StopId. Over-represents recovery cases; " +
                "do not use for production coverage estimates.",
            Cases = auditCases,
        };

        var setPath = Path.Combine(docsPath, SetFileName);
        var markdownPath = Path.Combine(docsPath, PackMarkdownFileName);
        var labelsPath = Path.Combine(docsPath, LabelsFileName);
        File.WriteAllText(setPath, JsonSerializer.Serialize(set, JsonOptions), Encoding.UTF8);
        File.WriteAllText(markdownPath, ToBlindMarkdown(set), Encoding.UTF8);
        File.WriteAllText(
            labelsPath,
            JsonSerializer.Serialize(
                auditCases.Select(item => new CalibrationLabelEntry
                {
                    PerformanceId = item.PerformanceId,
                    Label = null,
                    ExpectedStopId = null,
                    ReviewerConfidence = null,
                    ReviewerNote = null,
                }).ToArray(),
                JsonOptions),
            Encoding.UTF8);

        return new RecoveryAuditExportResult
        {
            MarkdownPath = markdownPath,
            LabelsPath = labelsPath,
            SetPath = setPath,
            CaseCount = auditCases.Length,
            NewRecoveryOnlyCount = distribution.RecoveryOnly,
            Distribution = distribution,
        };
    }

    public RecoveryAuditLabelImportResult ImportLabels(string docsPath, string labelsPath)
    {
        if (!File.Exists(labelsPath))
        {
            throw new FileNotFoundException("Labelbestand niet gevonden.", labelsPath);
        }

        var setPath = Path.Combine(docsPath, SetFileName);
        if (!File.Exists(setPath))
        {
            throw new FileNotFoundException(
                "Recovery-auditset ontbreekt; exporteer eerst met --export-recovery-audit.",
                setPath);
        }

        var set = JsonSerializer.Deserialize<RecoveryAuditSetFile>(
                      File.ReadAllText(setPath),
                      JsonOptions) ??
                  throw new InvalidOperationException("Recovery-auditset is ongeldig.");
        var entries = JsonSerializer.Deserialize<List<CalibrationLabelEntry>>(
                          File.ReadAllText(labelsPath),
                          JsonOptions) ??
                      [];
        ValidateLabelFile(entries, set.Cases);

        var byId = entries.ToDictionary(item => item.PerformanceId);
        var updated = set.Cases.Select(item =>
        {
            var entry = byId[item.PerformanceId];
            return item with
            {
                Label = entry.Label,
                ExpectedStopId = string.IsNullOrWhiteSpace(entry.ExpectedStopId)
                    ? null
                    : entry.ExpectedStopId.Trim(),
                ExpectedVisitStopIds = entry.ExpectedVisitStopIds is { Count: > 0 }
                    ? entry.ExpectedVisitStopIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id.Trim())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                    : null,
                ReviewerConfidence = entry.ReviewerConfidence,
                ReviewerNote = entry.ReviewerNote,
            };
        }).ToArray();

        var labeledCount = updated.Count(item => !string.IsNullOrWhiteSpace(item.Label));
        var saved = new RecoveryAuditSetFile
        {
            DatasetRole = set.DatasetRole,
            ExportedAt = set.ExportedAt,
            RandomSeed = set.RandomSeed,
            CaseCount = updated.Length,
            Distribution = set.Distribution,
            BlindNote = set.BlindNote,
            Cases = updated,
        };
        File.WriteAllText(setPath, JsonSerializer.Serialize(saved, JsonOptions), Encoding.UTF8);
        File.WriteAllText(labelsPath, JsonSerializer.Serialize(entries, JsonOptions), Encoding.UTF8);

        return new RecoveryAuditLabelImportResult
        {
            ImportedCount = entries.Count,
            LabeledCount = labeledCount,
            LabelsPath = labelsPath,
            SetPath = setPath,
        };
    }

    public async Task<RecoveryAuditEvaluationResult> EvaluateAsync(
        string docsPath,
        CancellationToken cancellationToken)
    {
        var setPath = Path.Combine(docsPath, SetFileName);
        if (!File.Exists(setPath))
        {
            throw new FileNotFoundException(
                "Recovery-auditset ontbreekt; exporteer eerst met --export-recovery-audit.",
                setPath);
        }

        var set = JsonSerializer.Deserialize<RecoveryAuditSetFile>(
                      File.ReadAllText(setPath),
                      JsonOptions) ??
                  throw new InvalidOperationException("Recovery-auditset is ongeldig.");

        var options = new AdaptiveLocationMatchingOptions();
        options.Validate();
        var liveByPerformance = await LoadLiveContextsAsync(docsPath, options, cancellationToken);
        var emptyClusters = new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal);
        var refreshed = new List<RecoveryAuditCase>(set.Cases.Count);
        var multiFragmentVisits = 0;
        var usedOffline = liveByPerformance.Count == 0;
        foreach (var item in set.Cases)
        {
            RecoveryAuditCase refreshedCase;
            if (usedOffline)
            {
                refreshedCase = RescoreOffline(item, options);
            }
            else
            {
                var source = ToBenchmarkShape(item) with
                {
                    Label = item.Label,
                    ExpectedStopId = item.ExpectedStopId,
                    ExpectedVisitStopIds = item.ExpectedVisitStopIds,
                    ReviewerConfidence = item.ReviewerConfidence,
                    ReviewerNote = item.ReviewerNote,
                    Lacleunik = item.Lacleunik,
                };
                var classified = Classify(source, liveByPerformance, options, emptyClusters);
                refreshedCase = item with
                {
                    AdaptiveDecision = classified.AdaptiveDecision,
                    HybridDecision = classified.HybridDecision,
                    UsedRecovery = classified.UsedRecovery,
                    SelectedStopId = classified.SelectedStopId,
                    SelectedSourceStopIds = classified.SelectedSourceStopIds,
                    SelectedDistanceMeters = classified.SelectedDistanceMeters,
                    SelectedOverlapMinutes = classified.SelectedOverlapMinutes,
                    SelectedOverlapPercent = classified.SelectedOverlapPercent,
                    DistanceZone = classified.DistanceZone,
                    GeocodeQuality = classified.GeocodeQuality,
                    Strata = classified.Strata.Count > 0 ? classified.Strata : item.Strata,
                };
            }

            if ((refreshedCase.SelectedSourceStopIds?.Count ?? 0) > 1)
            {
                multiFragmentVisits++;
            }

            refreshed.Add(refreshedCase);
        }

        File.WriteAllText(
            setPath,
            JsonSerializer.Serialize(
                new RecoveryAuditSetFile
                {
                    DatasetRole = set.DatasetRole,
                    ExportedAt = set.ExportedAt,
                    RandomSeed = set.RandomSeed,
                    CaseCount = refreshed.Count,
                    Distribution = set.Distribution,
                    BlindNote = set.BlindNote,
                    Cases = refreshed,
                },
                JsonOptions));

        var labeled = refreshed.Where(item => !string.IsNullOrWhiteSpace(item.Label)).ToArray();
        if (labeled.Length == 0)
        {
            var pending = new RecoveryAuditEvaluationResult
            {
                CaseCount = set.CaseCount,
                LabeledCount = 0,
                LabelsComplete = false,
                Status =
                    "Geen labels aanwezig; vul docs/recovery-audit-labels.json en importeer met " +
                    "--import-recovery-audit-labels.",
                Notes =
                [
                    "Auditset oververtegenwoordigt recoverycases; niet gebruiken voor coverage-schatting.",
                    $"MultiFragmentVisits={multiFragmentVisits}",
                ],
            };
            File.WriteAllText(
                Path.Combine(docsPath, EvaluationFileName),
                JsonSerializer.Serialize(pending, JsonOptions));
            return pending;
        }

        var scored = labeled.Select(ScoreCase).ToArray();
        var errors = scored.Where(item => item.Error is not null).Select(item => item.Error!).ToArray();
        var recoveryOnly = scored
            .Where(item => item.Case.UsedRecovery)
            .ToArray();
        var weakOverlap = scored
            .Where(item =>
                item.Case.UsedRecovery &&
                item.Case.SelectedOverlapMinutes is < 10 &&
                item.Case.SelectedOverlapPercent is < 50)
            .ToArray();
        var byDistance = scored
            .GroupBy(item => item.Case.DistanceZone ?? "Unknown", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ToSlice(group.ToArray()),
                StringComparer.Ordinal);
        var byGeocode = scored
            .GroupBy(item => item.Case.GeocodeQuality ?? "Unknown", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => ToSlice(group.ToArray()),
                StringComparer.Ordinal);

        var recoverySlice = ToSlice(recoveryOnly);
        var notes = new List<string>
        {
            "Auditset oververtegenwoordigt recoverycases; niet gebruiken voor coverage-schatting.",
            $"MultiFragmentVisits={multiFragmentVisits}",
        };
        if (usedOffline)
        {
            notes.Add(
                "Live Plenion onbereikbaar; audit herscoord offline via VisitCandidate-heuristiek op opgeslagen kandidaten.");
        }
        if (recoveryOnly.Length > 0 && recoverySlice.Precision < 0.95)
        {
            notes.Add(
                $"Acceptatiedoel niet gehaald: recovery-only precision {recoverySlice.Precision:0.###} < 0.95.");
        }

        if (weakOverlap.Any(item => item.FalsePositive || item.WrongStopId))
        {
            notes.Add("Zwakke-overlap recovery bevat FP of verkeerde StopId; systematisch risico controleren.");
        }

        var neighborWrongStop = errors.Where(item =>
            item.Reason.Contains("verkeerde VisitCandidate", StringComparison.Ordinal) ||
            item.Reason.Contains("verkeerde StopId", StringComparison.Ordinal)).ToArray();
        if (neighborWrongStop.Length > 0)
        {
            notes.Add(
                $"{neighborWrongStop.Length} verkeerde VisitCandidate; controleer buurprestaties/fragmentatie.");
        }

        var allLabeled = ToSlice(scored);
        if (allLabeled.Precision < 0.95)
        {
            notes.Add(
                $"Acceptatiedoel niet gehaald: audit hybrid precision {allLabeled.Precision:0.###} < 0.95.");
        }

        var result = new RecoveryAuditEvaluationResult
        {
            CaseCount = set.CaseCount,
            LabeledCount = labeled.Length,
            LabelsComplete = labeled.Length == set.CaseCount,
            Status = labeled.Length == set.CaseCount
                ? "Labels volledig; recovery-auditmetrics berekend."
                : $"Gedeeltelijk gelabeld ({labeled.Length}/{set.CaseCount}).",
            RecoveryOnly = recoverySlice,
            WeakOverlapRecovery = ToSlice(weakOverlap),
            ByDistanceZone = byDistance,
            ByGeocodeQuality = byGeocode,
            AllLabeledHybrid = allLabeled,
            Errors = errors.OrderBy(item => item.PerformanceId).ToArray(),
            Notes = notes,
        };
        File.WriteAllText(
            Path.Combine(docsPath, EvaluationFileName),
            JsonSerializer.Serialize(result, JsonOptions));
        return result;
    }

    [Obsolete("Use EvaluateAsync for live re-score after matcher changes.")]
    public RecoveryAuditEvaluationResult Evaluate(string docsPath) =>
        EvaluateAsync(docsPath, CancellationToken.None).GetAwaiter().GetResult();

    internal static void ValidateLabelFile(
        IReadOnlyList<CalibrationLabelEntry> entries,
        IReadOnlyList<RecoveryAuditCase> auditCases)
    {
        var errors = new List<string>();
        if (entries.Count != auditCases.Count)
        {
            errors.Add($"Verwacht {auditCases.Count} labels, gevonden {entries.Count}.");
        }

        var expectedIds = auditCases.Select(item => item.PerformanceId).ToHashSet();
        var seen = new HashSet<long>();
        var stopIdsByPerformance = auditCases.ToDictionary(
            item => item.PerformanceId,
            item => item.Candidates
                .Select(candidate => candidate.StopId)
                .ToHashSet(StringComparer.Ordinal));

        foreach (var entry in entries)
        {
            if (!seen.Add(entry.PerformanceId))
            {
                errors.Add($"Dubbele PerformanceId: {entry.PerformanceId}.");
            }

            if (!expectedIds.Contains(entry.PerformanceId))
            {
                errors.Add($"Onbekende PerformanceId: {entry.PerformanceId}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Label) ||
                !AllowedLabels.Contains(entry.Label, StringComparer.Ordinal))
            {
                errors.Add(
                    $"PerformanceId {entry.PerformanceId}: Label moet CorrectCandidate, NoValidCandidate of Ambiguous zijn.");
            }

            if (string.IsNullOrWhiteSpace(entry.ReviewerConfidence) ||
                !AllowedConfidence.Contains(entry.ReviewerConfidence, StringComparer.Ordinal))
            {
                errors.Add(
                    $"PerformanceId {entry.PerformanceId}: ReviewerConfidence moet High, Medium of Low zijn.");
            }

            var stopId = string.IsNullOrWhiteSpace(entry.ExpectedStopId)
                ? null
                : entry.ExpectedStopId.Trim();
            var visitIds = entry.ExpectedVisitStopIds is { Count: > 0 }
                ? entry.ExpectedVisitStopIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
            if (string.Equals(entry.Label, "CorrectCandidate", StringComparison.Ordinal))
            {
                if (visitIds.Length == 0 && stopId is null)
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: CorrectCandidate vereist ExpectedStopId of ExpectedVisitStopIds.");
                }

                foreach (var visitId in visitIds)
                {
                    if (!stopIdsByPerformance[entry.PerformanceId].Contains(visitId))
                    {
                        errors.Add(
                            $"PerformanceId {entry.PerformanceId}: ExpectedVisitStopId '{visitId}' bestaat niet bij deze case.");
                    }
                }

                if (stopId is not null &&
                    visitIds.Length == 0 &&
                    !stopIdsByPerformance[entry.PerformanceId].Contains(stopId))
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: ExpectedStopId '{stopId}' bestaat niet bij deze case.");
                }
            }
            else if (entry.Label is "NoValidCandidate" or "Ambiguous")
            {
                if (stopId is not null || visitIds.Length > 0)
                {
                    errors.Add(
                        $"PerformanceId {entry.PerformanceId}: {entry.Label} vereist ExpectedStopId/ExpectedVisitStopIds = null.");
                }
            }
        }

        foreach (var missing in expectedIds.Where(id => !seen.Contains(id)).OrderBy(id => id))
        {
            errors.Add($"Ontbrekende PerformanceId: {missing}.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Labelimport geweigerd (niets opgeslagen):" +
                Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(item => "- " + item)));
        }
    }

    private static RecoveryAuditCase RescoreOffline(
        RecoveryAuditCase item,
        AdaptiveLocationMatchingOptions options)
    {
        var visits = OfflineVisitMerge.Merge(item.Candidates, options);
        var performanceMinutes = Math.Max(
            1,
            (int)Math.Round((item.End - item.Start).TotalMinutes, MidpointRounding.AwayFromZero));
        if (visits.Count == 0)
        {
            return item with
            {
                HybridDecision = "Unresolved",
                UsedRecovery = false,
                SelectedStopId = null,
                SelectedSourceStopIds = [],
            };
        }

        var best = visits
            .Select(visit =>
            {
                var overlap = OfflineVisitMerge.OverlapMinutes(
                    item.Start,
                    item.End,
                    visit.Arrival,
                    visit.Departure);
                return (
                    Visit: visit,
                    Overlap: overlap,
                    OverlapPercent: 100d * overlap / performanceMinutes,
                    Distance: visit.DistanceMeters ?? double.MaxValue);
            })
            .OrderByDescending(entry => entry.Overlap)
            .ThenBy(entry => entry.Distance)
            .First();

        var sources = best.Visit.StopIds;
        var distanceZone = best.Distance switch
        {
            <= 100 => "Strong0To100",
            <= 250 => "Probable101To250",
            <= 500 => "Learned251To500",
            _ when best.Distance < double.MaxValue => "Beyond500",
            _ => item.DistanceZone ?? "Unknown",
        };

        var adaptiveAccepted = item.AdaptiveDecision is "Confirmed" or "Probable"
            or "ConfirmedLocationMatch" or "ProbableLocationMatch";
        string hybridDecision;
        var usedRecovery = false;
        if (adaptiveAccepted)
        {
            hybridDecision = item.AdaptiveDecision is "Confirmed" or "ConfirmedLocationMatch"
                ? "Confirmed"
                : "Probable";
        }
        else if (CanRecoverOffline(item, best.Visit, best.Overlap, best.OverlapPercent, best.Distance, options))
        {
            usedRecovery = true;
            hybridDecision = "RecoveredProbable";
        }
        else
        {
            hybridDecision = item.AdaptiveDecision is "Ambiguous" ? "Ambiguous" : "Unresolved";
        }

        return item with
        {
            HybridDecision = hybridDecision,
            UsedRecovery = usedRecovery,
            SelectedStopId = sources[0],
            SelectedSourceStopIds = sources,
            SelectedDistanceMeters = best.Distance < double.MaxValue ? best.Distance : null,
            SelectedOverlapMinutes = best.Overlap,
            SelectedOverlapPercent = Math.Round(best.OverlapPercent, 1),
            DistanceZone = distanceZone,
        };
    }

    private static bool CanRecoverOffline(
        RecoveryAuditCase item,
        OfflineVisitMerge.Visit visit,
        int overlap,
        double overlapPercent,
        double distance,
        AdaptiveLocationMatchingOptions options)
    {
        if (visit.Arrival >= item.End || visit.Departure <= item.Start || overlap <= 0)
        {
            return false;
        }

        if (distance > options.RecoveryMaximumDistanceMeters)
        {
            return false;
        }

        if (string.Equals(item.GeocodeQuality, "Unusable", StringComparison.Ordinal))
        {
            return false;
        }

        var shortChain = OfflineVisitMerge.MeetsShortChain(item, visit, overlap, options);
        var overlapEnough =
            overlap >= options.RecoveryMinimumOverlapMinutes ||
            overlapPercent >= options.RecoveryMinimumOverlapPercent;
        return overlapEnough || shortChain;
    }

    private RecoveryAuditClassifiedCase Classify(
        LocationMatchingBenchmarkCase item,
        IReadOnlyDictionary<long, LiveCaseContext> liveByPerformance,
        AdaptiveLocationMatchingOptions options,
        IReadOnlyDictionary<string, HistoricalLocationCluster> clusters)
    {
        if (!liveByPerformance.TryGetValue(item.PerformanceId, out var live))
        {
            return new RecoveryAuditClassifiedCase(
                item.PerformanceId,
                item,
                UsedRecovery: false,
                AdaptiveAccepted: false,
                HybridAbstention: true,
                AdaptiveDecision: "MissingLiveData",
                HybridDecision: "Unresolved",
                SelectedStopId: null,
                SelectedSourceStopIds: [],
                SelectedDistanceMeters: null,
                SelectedOverlapMinutes: null,
                SelectedOverlapPercent: null,
                DistanceZone: null,
                GeocodeQuality: item.GeocodeQuality.ToString(),
                Strata: []);
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
        var adaptiveAccepted = adaptive.Decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable;
        var hybridAccepted = hybrid.Decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable;
        var selected = hybrid.Selected ??
                       (adaptive.Candidates.Count > 0 ? adaptive.Candidates[0] : null);
        var sources = selected?.Stop.SourceStopIds.ToArray() ?? [];
        return new RecoveryAuditClassifiedCase(
            item.PerformanceId,
            item,
            hybrid.UsedRecovery,
            adaptiveAccepted,
            !hybridAccepted && hybrid.Decision is AdaptiveMatchDecision.Ambiguous
                or AdaptiveMatchDecision.Unresolved,
            adaptive.Decision.ToString(),
            hybrid.UsedRecovery ? "RecoveredProbable" : hybrid.Decision.ToString(),
            sources.Length > 0 ? sources[0] : selected?.Stop.MergedStopId,
            sources,
            selected?.DistanceMeters,
            selected?.OverlapMinutes,
            selected?.OverlapPercent,
            selected?.DistanceZone.ToString(),
            hybrid.GeocodeQuality.ToString(),
            Strata: []);
    }

    private async Task<Dictionary<long, LiveCaseContext>> LoadLiveContextsAsync(
        string docsPath,
        AdaptiveLocationMatchingOptions options,
        CancellationToken cancellationToken)
    {
        _ = docsPath;
        _ = options;
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
        if (driverIds.Count == 0)
        {
            var cached = BroaderValidationCache.TryLoad(
                BroaderValidationCache.DefaultPath(docsPath));
            if (cached is not null)
            {
                foreach (var item in cached.Technicians.Where(tech =>
                             tech.Processed && !string.IsNullOrWhiteSpace(tech.DriverId)))
                {
                    driverIds[item.Technician?.Name ?? item.Query] = item.DriverId!;
                }
            }
        }

        var liveByPerformance = new Dictionary<long, LiveCaseContext>();
        foreach (var technician in TechnicianNames)
        {
            if (!driverIds.TryGetValue(technician, out var driverId))
            {
                continue;
            }

            for (var month = 1; month <= 7; month++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var from = new DateOnly(2026, month, 1);
                var through = month == 7
                    ? new DateOnly(2026, 7, 28)
                    : new DateOnly(2026, month, DateTime.DaysInMonth(2026, month));
                foreach (var context in await LoadSliceAsync(
                             technician,
                             driverId,
                             from,
                             through,
                             cancellationToken))
                {
                    liveByPerformance[context.Performance.ExternalId] = context;
                }
            }
        }

        return liveByPerformance;
    }

    private async Task<List<LiveCaseContext>> LoadSliceAsync(
        string technicianName,
        string driverId,
        DateOnly from,
        DateOnly through,
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
            _ = exception;
            return [];
        }
    }

    private static RecoveryAuditCase ToAuditCase(RecoveryAuditClassifiedCase item) =>
        new()
        {
            PerformanceId = item.PerformanceId,
            Technician = item.Source.Technician,
            Date = item.Source.Date,
            Start = item.Source.Start,
            End = item.Source.End,
            Lacleunik = item.Source.Lacleunik,
            PlenionAddress = item.Source.PlenionAddress,
            PreviousPerformance = item.Source.PreviousPerformance,
            NextPerformance = item.Source.NextPerformance,
            Candidates = item.Source.Candidates,
            Strata = item.Strata,
            AdaptiveDecision = item.AdaptiveDecision,
            HybridDecision = item.HybridDecision,
            UsedRecovery = item.UsedRecovery,
            SelectedStopId = item.SelectedStopId,
            SelectedSourceStopIds = item.SelectedSourceStopIds,
            SelectedDistanceMeters = item.SelectedDistanceMeters,
            SelectedOverlapMinutes = item.SelectedOverlapMinutes,
            SelectedOverlapPercent = item.SelectedOverlapPercent,
            DistanceZone = item.DistanceZone,
            GeocodeQuality = item.GeocodeQuality,
            Label = null,
            ExpectedStopId = null,
            ExpectedVisitStopIds = null,
            ReviewerConfidence = null,
            ReviewerNote = null,
        };

    private static LocationMatchingBenchmarkCase ToBenchmarkShape(RecoveryAuditCase item) =>
        new()
        {
            PerformanceId = item.PerformanceId,
            Technician = item.Technician,
            Date = item.Date,
            Start = item.Start,
            End = item.End,
            Lacleunik = item.Lacleunik,
            PlenionAddress = item.PlenionAddress,
            GeocodeQuality = GeocodeQualityClass.PartialAddress,
            ExistingMatchStatus = "NoReliableMatch",
            PreviousPerformance = item.PreviousPerformance,
            NextPerformance = item.NextPerformance,
            Candidates = item.Candidates,
        };

    private static string ToBlindMarkdown(RecoveryAuditSetFile pack)
    {
        var lines = new List<string>
        {
            "# Recovery audit pack",
            string.Empty,
            $"Cases: {pack.CaseCount}",
            $"ExportedAt: {pack.ExportedAt:O}",
            string.Empty,
            pack.BlindNote,
            string.Empty,
        };

        for (var index = 0; index < pack.Cases.Count; index++)
        {
            var item = pack.Cases[index];
            var performanceMinutes = Math.Max(
                1,
                (int)Math.Round((item.End - item.Start).TotalMinutes, MidpointRounding.AwayFromZero));
            lines.Add($"## Case {index + 1}");
            lines.Add(string.Empty);
            lines.Add($"- PerformanceId: `{item.PerformanceId}`");
            lines.Add($"- Technician: {item.Technician}");
            lines.Add($"- Date: {item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
            lines.Add($"- Plenion address: {item.PlenionAddress}");
            lines.Add($"- Performance time: {item.Start:HH:mm} â€“ {item.End:HH:mm}");
            lines.Add($"- Previous performance: {(item.PreviousPerformance ?? "â€”")}");
            lines.Add($"- Next performance: {(item.NextPerformance ?? "â€”")}");
            lines.Add(string.Empty);

            var candidates = item.Candidates
                .OrderBy(candidate => candidate.Arrival)
                .ThenBy(candidate => candidate.StopId, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
            {
                lines.Add("No candidate stops.");
                lines.Add(string.Empty);
                continue;
            }

            lines.Add(
                "| StopId | Address | Distance m | Arrival | Departure | Dwell min | Overlap min | Overlap % | Î” start | Î” end |");
            lines.Add("| --- | --- | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var candidate in candidates)
            {
                var dwell = Math.Max(
                    0,
                    (int)Math.Round(
                        (candidate.Departure - candidate.Arrival).TotalMinutes,
                        MidpointRounding.AwayFromZero));
                var overlapPercent = Math.Round(100d * candidate.OverlapMinutes / performanceMinutes, 1);
                lines.Add(
                    $"| `{candidate.StopId}` | {EscapePipe(candidate.Address ?? "â€”")} | " +
                    $"{FormatDistance(candidate.DistanceMeters)} | {candidate.Arrival:HH:mm} | " +
                    $"{candidate.Departure:HH:mm} | {dwell} | {candidate.OverlapMinutes} | " +
                    $"{overlapPercent.ToString("0.0", CultureInfo.InvariantCulture)} | " +
                    $"{candidate.StartDifferenceMinutes} | {candidate.EndDifferenceMinutes} |");
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static ScoredAuditCase ScoreCase(RecoveryAuditCase item)
    {
        var accepted = item.HybridDecision is "Confirmed" or "Probable" or "RecoveredProbable";
        var predictedStop = item.SelectedStopId;
        var predictedSources = item.SelectedSourceStopIds ?? [];
        var expected = string.IsNullOrWhiteSpace(item.ExpectedStopId)
            ? null
            : item.ExpectedStopId.Trim();
        var label = item.Label!;
        var correctAccepted = false;
        var falsePositive = false;
        var falseNegative = false;
        var wrongStop = false;
        RecoveryAuditCaseError? error = null;
        var stratum = item.Strata.Count > 0 ? item.Strata[0] : "Unknown";

        if (string.Equals(label, "CorrectCandidate", StringComparison.Ordinal))
        {
            if (!accepted)
            {
                falseNegative = true;
                error = new RecoveryAuditCaseError
                {
                    PerformanceId = item.PerformanceId,
                    Stratum = stratum,
                    Label = label,
                    HybridDecision = item.HybridDecision,
                    Reason = "FN: matcher onthield zich bij CorrectCandidate.",
                    PredictedStopId = predictedStop,
                    ExpectedStopId = expected,
                };
            }
            else if (VisitLabelMatching.MatchesVisit(
                         item.ExpectedStopId,
                         item.ExpectedVisitStopIds,
                         predictedStop,
                         predictedSources))
            {
                correctAccepted = true;
            }
            else
            {
                falsePositive = true;
                wrongStop = true;
                var expectedText = string.Join(
                    ',',
                    VisitLabelMatching.ResolveExpectedStopIds(
                        item.ExpectedStopId,
                        item.ExpectedVisitStopIds));
                error = new RecoveryAuditCaseError
                {
                    PerformanceId = item.PerformanceId,
                    Stratum = stratum,
                    Label = label,
                    HybridDecision = item.HybridDecision,
                    Reason =
                        $"FP: verkeerde VisitCandidate (verwacht {expectedText}, kreeg {predictedStop ?? "null"}).",
                    PredictedStopId = predictedStop,
                    ExpectedStopId = expected,
                };
            }
        }
        else if (label is "NoValidCandidate" or "Ambiguous")
        {
            if (accepted)
            {
                falsePositive = true;
                error = new RecoveryAuditCaseError
                {
                    PerformanceId = item.PerformanceId,
                    Stratum = stratum,
                    Label = label,
                    HybridDecision = item.HybridDecision,
                    Reason = $"FP: matcher accepteerde stop bij {label} ({predictedStop ?? "null"}).",
                    PredictedStopId = predictedStop,
                    ExpectedStopId = expected,
                };
            }
        }

        return new ScoredAuditCase(
            item,
            accepted,
            correctAccepted,
            falsePositive,
            falseNegative,
            wrongStop,
            error);
    }

    private static RecoveryAuditMetricSlice ToSlice(IReadOnlyList<ScoredAuditCase> scored)
    {
        var accepted = scored.Count(item => item.Accepted);
        var correctAccepted = scored.Count(item => item.CorrectAccepted);
        return new RecoveryAuditMetricSlice
        {
            CaseCount = scored.Count,
            AcceptedMatches = accepted,
            CorrectAcceptedMatches = correctAccepted,
            Precision = Math.Round(accepted == 0 ? 0 : correctAccepted / (double)accepted, 4),
            FalsePositives = scored.Count(item => item.FalsePositive),
            FalseNegatives = scored.Count(item => item.FalseNegative),
            WrongStopIdChoices = scored.Count(item => item.WrongStopId),
        };
    }

    private static string EscapePipe(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string FormatDistance(double? meters) =>
        meters is null
            ? "â€”"
            : meters.Value.ToString("0.0", CultureInfo.InvariantCulture);

    private sealed record ScoredAuditCase(
        RecoveryAuditCase Case,
        bool Accepted,
        bool CorrectAccepted,
        bool FalsePositive,
        bool FalseNegative,
        bool WrongStopId,
        RecoveryAuditCaseError? Error);

    private sealed record LiveCaseContext(
        NormalizedPilotPerformance Performance,
        string TechnicianName,
        PilotLocationResolution Resolution,
        IReadOnlyList<NormalizedPilotPerformance> SameDayPerformances,
        IReadOnlyList<PilotStop> DayStops);
}
