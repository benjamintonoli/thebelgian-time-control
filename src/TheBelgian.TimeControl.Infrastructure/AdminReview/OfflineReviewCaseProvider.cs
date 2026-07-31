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
            .Select(item => ReviewCaseFactory.FromBenchmarkCase(
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
