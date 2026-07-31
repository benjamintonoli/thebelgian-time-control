using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Services;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.AdminReview;

/// <summary>
/// Live read-only admin review queue. Never loads locked holdout files and never writes to Plenion.
/// </summary>
internal sealed class AdminReviewService(
    IReadOnlyPilotService pilotService,
    IDistanceCalculator distanceCalculator,
    IOptions<AdaptiveLocationMatchingOptions> adaptiveOptions,
    AdminReviewDecisionRepository decisionRepository,
    TimeProvider timeProvider) : IAdminReviewService
{
    public static readonly string[] DefaultTechnicians =
    [
        "Filip Dekuyper",
        "Jonas Deklerck",
        "Jasper De Smet",
        "Jarno Vergauwen",
        "Dimitri Stiers",
    ];

    public async Task<IReadOnlyList<AdminReviewCase>> SearchAsync(
        AdminReviewFilter filter,
        CancellationToken cancellationToken)
    {
        EnsureHoldoutNotUsed();
        var from = filter.FromDate ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-7));
        var through = filter.ThroughDate ?? DateOnly.FromDateTime(DateTime.Today);
        if (through < from)
        {
            throw new InvalidOperationException("ThroughDate moet op of na FromDate liggen.");
        }

        var technicians = string.IsNullOrWhiteSpace(filter.Technician)
            ? DefaultTechnicians
            : [filter.Technician.Trim()];

        var built = new List<AdminReviewCase>();
        foreach (var technician in technicians)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dayCases = await LoadTechnicianAsync(technician, from, through, cancellationToken);
            built.AddRange(dayCases);
        }

        var recurring = SpotcheckPriorityCalculator.DetectRecurringSmallAdvantageTechnicians(
            built
                .Where(item => item.ProposedVisit is not null)
                .Select(item => (
                    item.Technician,
                    item.ProposedVisit!.StartDeviationMinutes,
                    item.ProposedVisit.EndDeviationMinutes)));
        if (recurring.Count > 0)
        {
            built = built
                .Select(item => recurring.Contains(item.Technician)
                    ? item with { RecurringSmallAdvantage = true }
                    : item)
                .ToList();
        }

        var latest = await decisionRepository.LatestByPerformanceAsync(
            built.Select(item => item.PerformanceId).ToArray(),
            cancellationToken);
        built = built
            .Select(item => OverlayDecision(item, latest))
            .ToList();

        return SpotcheckPriorityCalculator.ApplyFilterAndSort(built, filter);
    }

    public async Task<AdminReviewCase?> GetAsync(
        long performanceId,
        string technician,
        DateOnly performanceDate,
        CancellationToken cancellationToken)
    {
        EnsureHoldoutNotUsed();
        var cases = await SearchAsync(
            new AdminReviewFilter(
                Technician: technician,
                FromDate: performanceDate,
                ThroughDate: performanceDate),
            cancellationToken);
        return cases.FirstOrDefault(item => item.PerformanceId == performanceId);
    }

    public async Task<AdminReviewDecisionAudit> RecordDecisionAsync(
        long performanceId,
        string technician,
        DateOnly performanceDate,
        AdminReviewStatus decision,
        string reviewer,
        string? comment,
        string? chosenVisitCandidateId,
        IReadOnlyList<string>? chosenVisitSourceStopIds,
        CancellationToken cancellationToken)
    {
        EnsureHoldoutNotUsed();
        AdminReviewDecisionRules.Validate(decision, reviewer, comment);

        var current = await GetAsync(performanceId, technician, performanceDate, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Prestatie {performanceId} niet gevonden voor review.");

        var row = new AdminReviewDecisionAudit
        {
            PerformanceId = performanceId,
            OriginalMatcherDecision = current.MatcherStatus,
            ProposedVisitCandidateId = current.ProposedVisit?.VisitCandidateId,
            ProposedVisitSourceStopIdsJson = AdminReviewDecisionRepository.SerializeStopIds(
                current.ProposedVisit?.ConstituentStopIds),
            AdminDecision = decision.ToString(),
            ChosenVisitCandidateId = chosenVisitCandidateId,
            ChosenVisitSourceStopIdsJson = chosenVisitSourceStopIds is { Count: > 0 }
                ? AdminReviewDecisionRepository.SerializeStopIds(chosenVisitSourceStopIds)
                : null,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            Reviewer = reviewer.Trim(),
            DecidedAt = timeProvider.GetUtcNow(),
            MatcherCommit = current.MatcherCommit,
            ConfigurationHashSha256 = current.ConfigurationHashSha256,
        };

        return await decisionRepository.AppendAsync(row, cancellationToken);
    }

    public Task<IReadOnlyList<AdminReviewDecisionAudit>> GetAuditTrailAsync(
        long performanceId,
        CancellationToken cancellationToken) =>
        decisionRepository.ListForPerformanceAsync(performanceId, cancellationToken);

    private async Task<List<AdminReviewCase>> LoadTechnicianAsync(
        string technician,
        DateOnly from,
        DateOnly through,
        CancellationToken cancellationToken)
    {
        var pilot = await pilotService.RunAsync(
            new ReadOnlyPilotRequest(
                TechnicianQuery: technician,
                FromDate: from,
                ThroughDate: through,
                PowerfleetDriverId: null,
                DriverOnlyLinking: true,
                ResolveAllLocations: true,
                MaximumPerformances: 500,
                MaximumTrips: 2000),
            cancellationToken);

        var options = adaptiveOptions.Value;
        options.Validate();
        var configurationHash = FrozenMatcherVerificationService.ComputeConfigurationHash(
            FrozenMatcherVerificationService.SnapshotOptions(options));
        var matcherCommit = TryReadGitCommit();
        var emptyClusters = new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal);

        var performances = pilot.PlenionRecords
            .OrderBy(item => item.StartDateTime)
            .ToArray();
        var resolutions = pilot.LocationResolutions
            .ToDictionary(item => item.PerformanceId);
        var stopsByDate = pilot.PowerfleetStops
            .GroupBy(item => item.Date)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray());

        var cases = new List<AdminReviewCase>();
        foreach (var performance in performances)
        {
            if (!resolutions.TryGetValue(performance.ExternalId, out var resolution))
            {
                continue;
            }

            var dayStops = stopsByDate.TryGetValue(performance.Date, out var stops)
                ? stops
                : [];
            var sameDay = performances
                .Where(item => item.Date == performance.Date)
                .ToArray();
            var merged = MergedStopBuilder.Merge(dayStops, options, distanceCalculator);
            var hybrid = PrecisionPreservingHybridMatcher.Match(
                performance,
                technician,
                resolution,
                merged,
                sameDay,
                emptyClusters,
                options,
                distanceCalculator);

            cases.Add(ToCase(
                performance,
                technician,
                sameDay,
                hybrid,
                matcherCommit,
                configurationHash));
        }

        return cases;
    }

    private static AdminReviewCase ToCase(
        NormalizedPilotPerformance performance,
        string technician,
        IReadOnlyList<NormalizedPilotPerformance> sameDay,
        AdaptiveMatchResult hybrid,
        string matcherCommit,
        string configurationHash)
    {
        var candidates = hybrid.Candidates
            .Select(item => ToVisitSummary(item, hybrid.GeocodeQuality))
            .ToArray();
        var proposed = hybrid.Selected is null
            ? null
            : ToVisitSummary(hybrid.Selected, hybrid.GeocodeQuality);
        var maxDeviation = proposed is null
            ? 0
            : SpotcheckPriorityCalculator.MaxDeviationMinutes(
                proposed.StartDeviationMinutes,
                proposed.EndDeviationMinutes);
        var proposedAcceptance = hybrid.Decision is AdaptiveMatchDecision.Confirmed
            or AdaptiveMatchDecision.Probable;
        var status = hybrid.UsedRecovery
            ? "RecoveredProbable"
            : hybrid.Decision.ToString();
        var reason = hybrid.UsedRecovery
            ? hybrid.RecoveryReason ?? hybrid.Assessment
            : hybrid.Assessment;

        return new AdminReviewCase(
            PerformanceId: performance.ExternalId,
            Date: performance.Date,
            Technician: technician,
            PerformanceStart: performance.StartDateTime,
            PerformanceEnd: performance.EndDateTime,
            PlenionAddress: BuildAddress(performance),
            Lacleunik: performance.DeliveryAddressExternalId,
            ProjectOrBonContext: BuildProjectContext(performance),
            PreviousPerformance: FormatNeighbor(
                sameDay.LastOrDefault(item => item.EndDateTime <= performance.StartDateTime)),
            NextPerformance: FormatNeighbor(
                sameDay.FirstOrDefault(item => item.StartDateTime >= performance.EndDateTime)),
            MatcherStatus: status,
            MatchReason: reason,
            MatcherProposedAcceptance: proposedAcceptance,
            ProposedVisit: proposed,
            CandidateVisits: candidates,
            GeocodeQuality: hybrid.GeocodeQuality,
            MaxDeviationMinutes: maxDeviation,
            Priority: SpotcheckPriorityCalculator.FromDeviationMinutes(maxDeviation),
            RecurringSmallAdvantage: false,
            ReviewStatus: AdminReviewDecisionRules.InitialReviewStatus(proposedAcceptance),
            LatestReviewer: null,
            LatestComment: null,
            MatcherCommit: matcherCommit,
            ConfigurationHashSha256: configurationHash);
    }

    private static AdminReviewVisitSummary ToVisitSummary(
        AdaptiveMatchCandidate candidate,
        GeocodeQualityClass geocodeQuality) =>
        new(
            VisitCandidateId: candidate.Stop.MergedStopId,
            ConstituentStopIds: candidate.Stop.SourceStopIds.ToArray(),
            Address: candidate.Stop.Address,
            Arrival: candidate.Stop.Arrival,
            Departure: candidate.Stop.Departure,
            DistanceMeters: candidate.DistanceMeters,
            OverlapMinutes: candidate.OverlapMinutes,
            OverlapPercent: candidate.OverlapPercent,
            StartDeviationMinutes: candidate.ArrivalDifferenceMinutes,
            EndDeviationMinutes: candidate.DepartureDifferenceMinutes,
            GeocodeQuality: geocodeQuality.ToString());

    private static AdminReviewCase OverlayDecision(
        AdminReviewCase item,
        IReadOnlyDictionary<long, AdminReviewDecisionAudit> latest)
    {
        if (!latest.TryGetValue(item.PerformanceId, out var audit))
        {
            return item;
        }

        if (!Enum.TryParse<AdminReviewStatus>(audit.AdminDecision, ignoreCase: true, out var status))
        {
            return item;
        }

        return item with
        {
            ReviewStatus = status,
            LatestReviewer = audit.Reviewer,
            LatestComment = audit.Comment,
        };
    }

    private static string BuildAddress(NormalizedPilotPerformance performance)
    {
        var parts = new[]
        {
            performance.Street,
            performance.PostalCode,
            performance.City,
            performance.Country,
        }.Where(item => !string.IsNullOrWhiteSpace(item));
        var joined = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(joined)
            ? performance.CustomerOrSiteName ?? "(geen adres)"
            : joined;
    }

    private static string? BuildProjectContext(NormalizedPilotPerformance performance)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(performance.ProjectNumber) ||
            !string.IsNullOrWhiteSpace(performance.ProjectName))
        {
            parts.Add(
                $"{performance.ProjectNumber ?? "?"} – {performance.ProjectName ?? "?"}".Trim());
        }

        if (!string.IsNullOrWhiteSpace(performance.WorkOrderNumber))
        {
            parts.Add($"bon {performance.WorkOrderNumber}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? FormatNeighbor(NormalizedPilotPerformance? item)
    {
        if (item is null)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{item.ExternalId} {item.StartDateTime:HH:mm}-{item.EndDateTime:HH:mm}");
    }

    /// <summary>
    /// Explicit policy flag for tests: Admin Review never opens locked holdout artifacts.
    /// </summary>
    public const bool LoadsLockedHoldout = false;

    private static void EnsureHoldoutNotUsed()
    {
        if (LoadsLockedHoldout)
        {
            throw new InvalidOperationException(
                "Admin Review mag locked holdoutbestanden niet laden.");
        }
    }

    private static string TryReadGitCommit()
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse HEAD",
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
}
