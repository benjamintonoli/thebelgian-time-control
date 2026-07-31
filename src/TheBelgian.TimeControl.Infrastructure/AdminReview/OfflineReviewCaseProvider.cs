using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

/// <summary>
/// Offline review cases from development, calibration, and recovery-audit JSON.
/// Never opens locked holdout files. Never writes to Plenion.
/// </summary>
internal sealed class OfflineReviewCaseProvider(
    IHostEnvironment environment,
    IOptions<AdaptiveLocationMatchingOptions> adaptiveOptions) : IReviewCaseProvider
{
    public const bool LoadsLockedHoldoutFlag = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private ProviderCache? _cache;

    public string ProviderName => "OfflineReviewCaseProvider";

    public bool LoadsLockedHoldout => LoadsLockedHoldoutFlag;

    public int RawCaseCount => LoadCached().RawCaseCount;

    public int UniqueCaseCount => LoadCached().Cases.Count;

    public int DuplicatesRemoved => LoadCached().DuplicatesRemoved;

    public Task<IReadOnlyList<ReviewCase>> GetCasesAsync(CancellationToken cancellationToken)
    {
        EnsureHoldoutNotUsed();
        return Task.FromResult(LoadCached().Cases);
    }

    public Task<ReviewCase?> GetByPerformanceIdAsync(
        long performanceId,
        CancellationToken cancellationToken)
    {
        EnsureHoldoutNotUsed();
        var match = LoadCached().Cases.FirstOrDefault(item => item.PerformanceId == performanceId);
        return Task.FromResult(match);
    }

    private ProviderCache LoadCached()
    {
        lock (_gate)
        {
            return _cache ??= BuildCases();
        }
    }

    private ProviderCache BuildCases()
    {
        // Read-only offline mapping; no Plenion writeback, no holdout reuse.
        var docsPath = ResolveDocsPath(environment.ContentRootPath);
        var options = adaptiveOptions.Value;
        options.Validate();

        var matcherCommit = ResolveGitCommit(environment.ContentRootPath);
        var configurationHash = FrozenMatcherVerificationService.ComputeConfigurationHash(
            FrozenMatcherVerificationService.SnapshotOptions(options));

        var byId = new Dictionary<long, StagedCase>();
        var rawCount = 0;

        void Ingest(IEnumerable<LocationMatchingBenchmarkCase> items, string provenance)
        {
            foreach (var item in items)
            {
                rawCount++;
                if (!byId.TryGetValue(item.PerformanceId, out var existing))
                {
                    byId[item.PerformanceId] = new StagedCase(item, [provenance]);
                    continue;
                }

                var mergedProvenance = existing.Provenance
                    .Append(provenance)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var preferNew = CompletenessScore(item) > CompletenessScore(existing.Case);
                var chosen = preferNew ? MergeEvidence(item, existing.Case) : MergeEvidence(existing.Case, item);
                byId[item.PerformanceId] = new StagedCase(chosen, mergedProvenance);
            }
        }

        Ingest(LoadDevelopment(docsPath), "development");
        Ingest(LoadRecoveryAsBenchmark(docsPath), "recovery-audit");
        Ingest(LocationMatchingBenchmarkService.LoadCalibrationCases(docsPath), "calibration");

        var cases = byId.Values
            .Select(item => MapCase(
                item.Case,
                item.Provenance,
                options,
                matcherCommit,
                configurationHash))
            .OrderBy(item => item.PerformanceId)
            .ToArray();

        return new ProviderCache(
            Cases: cases,
            RawCaseCount: rawCount,
            DuplicatesRemoved: Math.Max(0, rawCount - cases.Length));
    }

    private static ReviewCase MapCase(
        LocationMatchingBenchmarkCase item,
        IReadOnlyList<string> provenance,
        AdaptiveLocationMatchingOptions options,
        string matcherCommit,
        string configurationHash)
    {
        var prediction = OfflineHybridPredictor.Predict(item, options, recovery: true);
        var visits = OfflineVisitMerge.Merge(item.Candidates, options);
        var performanceMinutes = Math.Max(
            1,
            (int)Math.Round((item.End - item.Start).TotalMinutes, MidpointRounding.AwayFromZero));
        var addressByStop = item.Candidates
            .GroupBy(candidate => candidate.StopId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(candidate => candidate.Address)
                    .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address)),
                StringComparer.Ordinal);

        var candidates = visits
            .Select(visit =>
            {
                var overlap = OfflineVisitMerge.OverlapMinutes(
                    item.Start,
                    item.End,
                    visit.Arrival,
                    visit.Departure);
                var startDev = (int)Math.Round(
                    (visit.Arrival - item.Start).TotalMinutes,
                    MidpointRounding.AwayFromZero);
                var endDev = (int)Math.Round(
                    (visit.Departure - item.End).TotalMinutes,
                    MidpointRounding.AwayFromZero);
                var address = visit.StopIds
                    .Select(id => addressByStop.GetValueOrDefault(id))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                return new ReviewVisitCandidate(
                    VisitCandidateId: string.Join('/', visit.StopIds),
                    ConstituentStopIds: visit.StopIds,
                    Address: address,
                    Arrival: visit.Arrival,
                    Departure: visit.Departure,
                    DistanceMeters: visit.DistanceMeters,
                    OverlapMinutes: overlap,
                    OverlapPercent: 100d * overlap / performanceMinutes,
                    StartDeviationMinutes: startDev,
                    EndDeviationMinutes: endDev,
                    GeocodeQuality: item.GeocodeQuality.ToString());
            })
            .OrderByDescending(visit => visit.OverlapMinutes)
            .ThenBy(visit => visit.DistanceMeters ?? double.MaxValue)
            .ToArray();

        var status = ResolveStatus(item, prediction, candidates);
        ReviewVisitCandidate? proposed = null;
        if (prediction.Accepted && prediction.SourceStopIds.Count > 0)
        {
            var id = string.Join('/', prediction.SourceStopIds);
            proposed = candidates.FirstOrDefault(visit =>
                string.Equals(visit.VisitCandidateId, id, StringComparison.Ordinal));
            if (proposed is null && candidates.Length > 0)
            {
                proposed = candidates[0];
            }
        }

        // Deviations only when a concrete proposed visit exists — never from ExistingMatchStatus fallback.
        var (startDeviation, endDeviation, maxDeviation) =
            SpotcheckPriorityCalculator.DeviationsForVisit(proposed, proposed is not null);

        var matcher = new MatcherAssessment(
            MatcherStatus: status,
            ProposedAcceptance: prediction.Accepted,
            ProposedVisit: proposed,
            CandidateVisits: candidates,
            MatchReason: BuildMatchReason(status, prediction, proposed, candidates),
            GeocodeQuality: item.GeocodeQuality,
            StartDeviationMinutes: startDeviation,
            EndDeviationMinutes: endDeviation,
            MaxDeviationMinutes: maxDeviation,
            MatcherCommit: matcherCommit,
            ConfigurationHash: configurationHash);

        var source = new SourceEvidence(
            PerformanceId: item.PerformanceId,
            Date: item.Date,
            Technician: item.Technician,
            PlenionStart: item.Start,
            PlenionEnd: item.End,
            PlenionAddress: item.PlenionAddress,
            ProjectContext: item.Lacleunik,
            BonContext: null,
            CustomerContext: null,
            PreviousPerformance: item.PreviousPerformance,
            NextPerformance: item.NextPerformance,
            Lacleunik: item.Lacleunik);

        var draft = new ReviewCase(
            Source: source,
            Matcher: matcher,
            Admin: new AdminDecision(AdminReviewDecisionRules.InitialReviewStatus()),
            Priority: SpotcheckPriorityCalculator.FromDeviationMinutes(maxDeviation),
            Category: ReviewWorkCategory.DataQuality,
            HasRecurringConfirmedPattern: false,
            SourceProvenance: provenance);
        return SpotcheckPriorityCalculator.WithDerivedFields(draft, recurringPattern: false);
    }

    private static int CompletenessScore(LocationMatchingBenchmarkCase item)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(item.PlenionAddress))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(item.PreviousPerformance))
        {
            score += 1;
        }

        if (!string.IsNullOrWhiteSpace(item.NextPerformance))
        {
            score += 1;
        }

        if (!string.IsNullOrWhiteSpace(item.Lacleunik))
        {
            score += 1;
        }

        if (!string.IsNullOrWhiteSpace(item.Label))
        {
            score += 2;
        }

        score += Math.Min(item.Candidates.Count, 5);
        if (item.IsCalibrationCase)
        {
            score += 3;
        }

        return score;
    }

    private static LocationMatchingBenchmarkCase MergeEvidence(
        LocationMatchingBenchmarkCase primary,
        LocationMatchingBenchmarkCase secondary) =>
        primary with
        {
            PreviousPerformance = FirstNonEmpty(primary.PreviousPerformance, secondary.PreviousPerformance),
            NextPerformance = FirstNonEmpty(primary.NextPerformance, secondary.NextPerformance),
            Lacleunik = FirstNonEmpty(primary.Lacleunik, secondary.Lacleunik),
            PlenionAddress = string.IsNullOrWhiteSpace(primary.PlenionAddress)
                ? secondary.PlenionAddress
                : primary.PlenionAddress,
            Candidates = primary.Candidates.Count >= secondary.Candidates.Count
                ? primary.Candidates
                : secondary.Candidates,
            Label = FirstNonEmpty(primary.Label, secondary.Label),
            DatasetRole = FirstNonEmpty(primary.DatasetRole, secondary.DatasetRole),
        };

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first : second;

    private static string ResolveStatus(
        LocationMatchingBenchmarkCase item,
        OfflineHybridPredictor.Prediction prediction,
        ReviewVisitCandidate[] candidates)
    {
        if (prediction.Accepted)
        {
            return prediction.Decision;
        }

        if (item.ExistingMatchStatus.Contains("Ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            return "Ambiguous";
        }

        if (AreTopCandidatesComparable(candidates))
        {
            return "Ambiguous";
        }

        return string.IsNullOrWhiteSpace(prediction.Decision) ? "Unresolved" : prediction.Decision;
    }

    private static bool AreTopCandidatesComparable(ReviewVisitCandidate[] candidates)
    {
        if (candidates.Length < 2)
        {
            return false;
        }

        var first = candidates[0];
        var second = candidates[1];
        var overlapClose = Math.Abs(first.OverlapMinutes - second.OverlapMinutes) <= 5;
        var distanceClose =
            first.DistanceMeters is { } d1 &&
            second.DistanceMeters is { } d2 &&
            Math.Abs(d1 - d2) <= 50;
        return overlapClose && (distanceClose || first.DistanceMeters is null || second.DistanceMeters is null);
    }

    private static string BuildMatchReason(
        string status,
        OfflineHybridPredictor.Prediction prediction,
        ReviewVisitCandidate? proposed,
        ReviewVisitCandidate[] candidates)
    {
        if (string.Equals(status, "Ambiguous", StringComparison.OrdinalIgnoreCase))
        {
            return "Meerdere kandidaatbezoeken zijn vergelijkbaar sterk.";
        }

        if (prediction.Accepted && proposed is not null)
        {
            return prediction.UsedRecovery
                ? $"Waarschijnlijk bezoek op basis van overlap {proposed.OverlapMinutes} min."
                : "Voorgesteld bezoek op basis van afstand/overlap.";
        }

        if (candidates.Length == 0)
        {
            return "Geen kandidaatbezoeken; geen betrouwbare match.";
        }

        return "Geen acceptatie; handmatige review vereist.";
    }

    private static IReadOnlyList<LocationMatchingBenchmarkCase> LoadDevelopment(string docsPath)
    {
        var path = Path.Combine(docsPath, LocationMatchingBenchmarkService.DevelopmentFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        var file = JsonSerializer.Deserialize<LocationMatchingDevelopmentFile>(
            File.ReadAllText(path),
            JsonOptions);
        return file?.Cases ?? [];
    }

    private static LocationMatchingBenchmarkCase[] LoadRecoveryAsBenchmark(string docsPath)
    {
        var path = Path.Combine(docsPath, LocationMatchingRecoveryAuditService.SetFileName);
        if (!File.Exists(path))
        {
            return Array.Empty<LocationMatchingBenchmarkCase>();
        }

        var file = JsonSerializer.Deserialize<RecoveryAuditSetFile>(File.ReadAllText(path), JsonOptions);
        if (file?.Cases is null)
        {
            return Array.Empty<LocationMatchingBenchmarkCase>();
        }

        return file.Cases.Select(item => new LocationMatchingBenchmarkCase
        {
            PerformanceId = item.PerformanceId,
            Technician = item.Technician,
            Date = item.Date,
            Start = item.Start,
            End = item.End,
            Lacleunik = item.Lacleunik,
            PlenionAddress = item.PlenionAddress,
            GeocodeQuality = ParseGeocode(item.GeocodeQuality),
            ExistingMatchStatus = item.AdaptiveDecision,
            PreviousPerformance = item.PreviousPerformance,
            NextPerformance = item.NextPerformance,
            Candidates = item.Candidates,
            DatasetRole = "recovery-audit",
        }).ToArray();
    }

    private static GeocodeQualityClass ParseGeocode(string? value) =>
        Enum.TryParse<GeocodeQualityClass>(value, ignoreCase: true, out var parsed)
            ? parsed
            : GeocodeQualityClass.PartialAddress;

    private static string ResolveDocsPath(string contentRootPath) =>
        Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "docs"));

    private static string ResolveGitCommit(string contentRootPath)
    {
        try
        {
            var repoRoot = Path.GetFullPath(Path.Combine(contentRootPath, "..", ".."));
            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return "unknown";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(output) ? "unknown" : output;
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Explicit policy flag for tests: Admin Review never opens locked holdout artifacts.
    /// </summary>
    public static void EnsureHoldoutNotUsed()
    {
        if (LoadsLockedHoldoutFlag)
        {
            throw new InvalidOperationException(
                "Admin Review mag locked holdoutbestanden niet laden.");
        }
    }

    private sealed record StagedCase(
        LocationMatchingBenchmarkCase Case,
        IReadOnlyList<string> Provenance);

    private sealed record ProviderCache(
        IReadOnlyList<ReviewCase> Cases,
        int RawCaseCount,
        int DuplicatesRemoved);
}
