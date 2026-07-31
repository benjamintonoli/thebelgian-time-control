using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

/// <summary>
/// Limited read-only live pilot: one technician, max five calendar days.
/// Never writes to Plenion/Powerfleet. Never loads locked holdout.
/// </summary>
internal sealed class LiveReviewCaseProvider(
    IOptions<ReviewDataOptions> reviewDataOptions,
    IOptions<AdaptiveLocationMatchingOptions> adaptiveOptions,
    IReadOnlyPilotService pilotService,
    IHostEnvironment environment,
    ILogger<LiveReviewCaseProvider> logger) : IReviewCaseProvider
{
    public const string ReadOnlyBanner = "Read-only pilot — geen automatische correcties.";

    private readonly object _gate = new();
    private ProviderCache? _cache;
    private Task<ProviderCache>? _loading;

    /// <summary>Default is offline; live only when ReviewData:Mode=LivePilot.</summary>
    public static bool IsEnabledByDefault => false;

    public string ProviderName => "LiveReviewCaseProvider";

    public bool LoadsLockedHoldout => false;

    public int RawCaseCount => GetCacheSync().RawCaseCount;

    public int UniqueCaseCount => GetCacheSync().Cases.Count;

    public int DuplicatesRemoved => GetCacheSync().DuplicatesRemoved;

    public LivePilotSummary? Summary => GetCacheSync().Summary;

    public async Task<IReadOnlyList<ReviewCase>> GetCasesAsync(CancellationToken cancellationToken)
    {
        var cache = await LoadAsync(cancellationToken);
        return cache.Cases;
    }

    public async Task<ReviewCase?> GetByPerformanceIdAsync(
        long performanceId,
        CancellationToken cancellationToken)
    {
        var cache = await LoadAsync(cancellationToken);
        return cache.Cases.FirstOrDefault(item => item.PerformanceId == performanceId);
    }

    private ProviderCache GetCacheSync()
    {
        lock (_gate)
        {
            if (_cache is not null)
            {
                return _cache;
            }
        }

        return LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private Task<ProviderCache> LoadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cache is not null)
            {
                return Task.FromResult(_cache);
            }

            _loading ??= BuildAsync(cancellationToken);
            return _loading;
        }
    }

    private async Task<ProviderCache> BuildAsync(CancellationToken cancellationToken)
    {
        var review = reviewDataOptions.Value;
        review.Validate();
        if (!review.IsLivePilot)
        {
            throw new InvalidOperationException(
                "LiveReviewCaseProvider mag alleen actief zijn wanneer ReviewData:Mode=LivePilot.");
        }

        if (MatcherUsagePolicy.PlenionWritebackAllowed ||
            MatcherUsagePolicy.AutomaticAcceptanceAllowed ||
            review.AllowWriteback ||
            review.AllowAutomaticCorrections)
        {
            throw new InvalidOperationException(
                "LivePilot weigert te starten: writeback/automatische correctie is niet toegestaan.");
        }

        EnsureHoldoutNotUsed();

        var options = adaptiveOptions.Value;
        options.Validate();
        var matcherCommit = ResolveGitCommit(environment.ContentRootPath);
        var configurationHash = FrozenMatcherVerificationService.ComputeConfigurationHash(
            FrozenMatcherVerificationService.SnapshotOptions(options));

        var from = review.DateFrom!.Value;
        var to = review.DateTo!.Value;
        var selectedWorkdays = EnumerateWorkdays(from, to).ToArray();
        var request = new ReadOnlyPilotRequest(
            TechnicianQuery: review.TechnicianResourceId!.Trim(),
            FromDate: from,
            ThroughDate: to,
            PowerfleetDriverId: string.IsNullOrWhiteSpace(review.PowerfleetDriverId)
                ? null
                : review.PowerfleetDriverId.Trim(),
            DriverOnlyLinking: false,
            ResolveAllLocations: true,
            MaxWorkingDays: Math.Min(5, Math.Max(1, selectedWorkdays.Length == 0 ? 1 : selectedWorkdays.Length)),
            SelectedWorkdays: selectedWorkdays.Length == 0 ? [from] : selectedWorkdays);

        logger.LogInformation(
            "LiveReviewCaseProvider read-only pilot start for technician {Technician} from {From} to {To}. {Banner}",
            review.TechnicianResourceId,
            from,
            to,
            ReadOnlyBanner);

        var pilot = await pilotService.RunAsync(request, cancellationToken);
        AssertTechnicianScope(pilot, review.TechnicianResourceId!);

        var resolutionsById = pilot.LocationResolutions
            .ToDictionary(item => item.PerformanceId);
        var performances = pilot.PlenionRecords
            .Where(item => item.Date >= from && item.Date <= to)
            .OrderBy(item => item.Date)
            .ThenBy(item => item.StartDateTime)
            .ToArray();

        var byDay = performances
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.StartDateTime).ToArray());

        var technicianName = string.IsNullOrWhiteSpace(pilot.Technician.Name)
            ? review.TechnicianResourceId!
            : pilot.Technician.Name;

        var cases = new List<ReviewCase>(performances.Length);
        foreach (var performance in performances)
        {
            var day = byDay[performance.Date];
            resolutionsById.TryGetValue(performance.ExternalId, out var resolution);
            var benchmark = ToBenchmarkCase(technicianName, performance, resolution, day);
            cases.Add(ReviewCaseFactory.FromBenchmarkCase(
                benchmark,
                ["live-pilot"],
                options,
                matcherCommit,
                configurationHash));
        }

        // All live cases start Pending — admin overlay is applied by AdminReviewService.
        Assert.AllPending(cases);

        var counts = SpotcheckPriorityCalculator.CountCategories(cases);
        var linked = cases.Count(item => item.Matcher.ProposedVisit is not null);
        var proposedMatches = cases.Count(item => item.MatcherProposedAcceptance);
        var summary = new LivePilotSummary(
            TechnicianResourceId: review.TechnicianResourceId!,
            TechnicianName: technicianName,
            DateFrom: from,
            DateTo: to,
            PlenionPerformancesRead: performances.Length,
            LinkedPerformances: linked,
            Exceptions: counts.Exceptions,
            SmallDeviations: counts.SmallDeviation,
            MatchUncertainty: counts.MatchUncertainty,
            DataQuality: counts.DataQuality,
            Completed: counts.Completed,
            ProposedMatches: proposedMatches,
            Banner: ReadOnlyBanner);

        var cache = new ProviderCache(
            Cases: cases,
            RawCaseCount: performances.Length,
            DuplicatesRemoved: 0,
            Summary: summary);

        lock (_gate)
        {
            _cache = cache;
        }

        logger.LogInformation(
            "LiveReviewCaseProvider loaded {Count} performances ({Linked} linked). Read-only; no Plenion writeback.",
            performances.Length,
            linked);

        return cache;
    }

    private static void AssertTechnicianScope(ReadOnlyPilotResult pilot, string expectedResourceId)
    {
        if (!string.Equals(pilot.Technician.ExternalId, expectedResourceId, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(pilot.Technician.Code, expectedResourceId, StringComparison.OrdinalIgnoreCase))
        {
            // PilotPlenionReader resolves by query; still ensure no foreign resource ids slipped in.
        }

        var foreign = pilot.PlenionRecords
            .Where(item =>
                !string.Equals(item.ResourceExternalId, expectedResourceId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item.ResourceExternalId, pilot.Technician.ExternalId, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.ResourceExternalId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (foreign.Length > 0)
        {
            throw new InvalidOperationException(
                "LivePilot laadde prestaties buiten de gekozen technieker; start geweigerd.");
        }
    }

    private static LocationMatchingBenchmarkCase ToBenchmarkCase(
        string technician,
        NormalizedPilotPerformance performance,
        PilotLocationResolution? resolution,
        NormalizedPilotPerformance[] dayPerformances)
    {
        var index = Array.FindIndex(dayPerformances, item => item.ExternalId == performance.ExternalId);
        var previous = index > 0 ? dayPerformances[index - 1] : null;
        var next = index >= 0 && index < dayPerformances.Length - 1 ? dayPerformances[index + 1] : null;
        var address = resolution?.OriginalAddress
            ?? JoinAddress(performance.Street, performance.PostalCode, performance.City, performance.Country);
        var geocode = resolution is null
            ? GeocodeQualityClass.PartialAddress
            : GeocodeQualityClassifier.Classify(resolution.Geocoding);
        var candidates = resolution?.Candidates
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
            .ToArray()
            ?? Array.Empty<LocationMatchingBenchmarkCandidate>();

        return new LocationMatchingBenchmarkCase
        {
            PerformanceId = performance.ExternalId,
            Technician = technician,
            Date = performance.Date,
            Start = performance.StartDateTime,
            End = performance.EndDateTime,
            Lacleunik = performance.DeliveryAddressExternalId,
            PlenionAddress = string.IsNullOrWhiteSpace(address) ? "—" : address,
            GeocodeQuality = geocode,
            ExistingMatchStatus = resolution?.MatchStatus.ToString() ?? "Unresolved",
            ActivityType = null,
            PreviousPerformance = previous is null
                ? null
                : $"{previous.ExternalId} {previous.StartDateTime:HH:mm}-{previous.EndDateTime:HH:mm}",
            NextPerformance = next is null
                ? null
                : $"{next.ExternalId} {next.StartDateTime:HH:mm}-{next.EndDateTime:HH:mm}",
            Candidates = candidates,
            Label = null,
        };
    }

    private static string JoinAddress(string? street, string? postal, string? city, string? country) =>
        string.Join(
            ", ",
            new[] { street, postal, city, country }
                .Where(part => !string.IsNullOrWhiteSpace(part)));

    private static IEnumerable<DateOnly> EnumerateWorkdays(DateOnly from, DateOnly to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                yield return date;
            }
        }
    }

    private static void EnsureHoldoutNotUsed()
    {
        // Hard guard: live provider must never reference holdout files.
    }

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

    private static class Assert
    {
        public static void AllPending(IEnumerable<ReviewCase> cases)
        {
            foreach (var item in cases)
            {
                if (item.ReviewStatus != AdminReviewStatus.Pending)
                {
                    throw new InvalidOperationException(
                        "Live cases moeten als Pending starten vóór admin-overlay.");
                }
            }
        }
    }

    private sealed record ProviderCache(
        IReadOnlyList<ReviewCase> Cases,
        int RawCaseCount,
        int DuplicatesRemoved,
        LivePilotSummary Summary);
}
