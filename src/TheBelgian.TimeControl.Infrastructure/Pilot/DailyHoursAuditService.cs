using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Geocoding;
using TheBelgian.TimeControl.Infrastructure.VehicleAssignments;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed class DailyHoursAuditService(
    PilotPlenionReader plenionReader,
    PilotPowerfleetReader powerfleetReader,
    IGeocodingService geocodingService,
    IDistanceCalculator distanceCalculator,
    DailyBoundaryContextIndexProvider contextIndexProvider,
    LocationMatchingOptions locationMatchingOptions,
    IOptions<AdaptiveLocationMatchingOptions> adaptiveOptions,
    TechnicianVehicleAssignmentService vehicleAssignmentService,
    TechnicianTrackingEligibilityService trackingEligibilityService,
    ILogger<DailyHoursAuditService> logger)
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly AdaptiveLocationMatchingOptions _adaptiveOptions = adaptiveOptions.Value;
    private int _contextBoundariesConsidered;
    private int _contextBoundariesSkippedNoTemporalStop;

    public async Task<DailyHoursAuditResult> RunAsync(
        DailyHoursAuditRequest request,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var sourceStopwatch = Stopwatch.StartNew();
        var exactSiteDuration = TimeSpan.Zero;
        var contextSupportedDuration = TimeSpan.Zero;
        var worksiteSessionDuration = TimeSpan.Zero;
        var worksiteSessionBoundariesConsidered = 0;
        var worksiteSessionBoundariesChanged = 0;
        var ambiguousWorksiteSessions = 0;
        var worksiteSessionClusters = 0;
        var worksiteSessionHistoricalLookups = 0;
        var ambiguousVehicleAssignments = 0;
        var insufficientVehicleAssignments = 0;
        var excludedNoTrackAndTrace = 0;
        var daysWithValidVehicleAssignment = 0;
        var vehicleStreamRisks = new List<PowerfleetVehicleStreamRisk>();
        _contextBoundariesConsidered = 0;
        _contextBoundariesSkippedNoTemporalStop = 0;
        _adaptiveOptions.Validate();
        var technicians = await plenionReader.ReadTechniciansWithPerformancesAsync(
            request.FromDate,
            request.ThroughDate,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.TechnicianQuery))
        {
            technicians = technicians.Where(item =>
                    item.Name.Contains(request.TechnicianQuery, StringComparison.OrdinalIgnoreCase) ||
                    item.ExternalId.Contains(request.TechnicianQuery, StringComparison.OrdinalIgnoreCase) ||
                    item.Code.Contains(request.TechnicianQuery, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        var resourceIds = technicians.Select(item => item.ExternalId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var calendar = await plenionReader.ReadCalendarAbsencesAsync(
            resourceIds,
            request.FromDate,
            request.ThroughDate,
            cancellationToken);
        var scans = new List<(Technician Technician, PlenionPilotReadResult Scan)>();
        foreach (var technician in technicians)
        {
            var scan = await plenionReader.ReadAsync(
                new ReadOnlyPilotRequest(
                    technician.ExternalId,
                    request.FromDate,
                    request.ThroughDate,
                    MaximumPerformances: 500),
                cancellationToken);
            scans.Add((technician, scan));
        }

        var dates = scans.SelectMany(item => item.Scan.NormalizedRecords)
            .Select(item => item.Date)
            .Where(date => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            .Where(date => !BelgianPublicHolidayCalendar.IsPublicHoliday(date))
            .Distinct()
            .OrderBy(item => item)
            .ToArray();
        var fleetTrips = new List<NormalizedPilotTrip>();
        var rawRouteByTrip = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var date in dates)
        {
            var daily = await powerfleetReader.ReadAsync(
                new ReadOnlyPilotRequest(
                    "daily-hours-audit",
                    date,
                    date,
                    DriverOnlyLinking: true,
                    MaximumTrips: 1000),
                cancellationToken);
            fleetTrips.AddRange(daily.NormalizedRecords);
            foreach (var raw in daily.RawRecords)
            {
                rawRouteByTrip[raw.SourceId] = raw.Fields.TryGetValue("route", out var route)
                    ? route.Text
                    : null;
            }
        }

        var distinctTrips = fleetTrips
            .DistinctBy(PowerfleetVehicleStreamIdentity.ObservationKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var contextIndex = await contextIndexProvider.BuildAsync(cancellationToken);
        sourceStopwatch.Stop();
        var geocodingByAddress = new Dictionary<string, GeocodingResult>(StringComparer.Ordinal);
        var auditRows = new List<DailyHoursAuditRow>();
        var diagnostics = new List<DailyBoundaryDiagnosticCase>();
        var technicianDays = 0;
        var reliableDays = 0;
        var partialDays = 0;
        var unresolvedDays = 0;
        var excludedWeekend = 0;
        var excludedPublicHoliday = 0;
        var excludedLeave = 0;
        var excludedSickness = 0;
        var exactSiteBoundaries = 0;
        var contextSupportedBoundaries = 0;
        var firstReliableBoundaries = 0;
        var lastReliableBoundaries = 0;
        var confirmedDeviationsOver5 = 0;
        var confirmedDeviationsOver15 = 0;
        var confirmedDeviationsOver30 = 0;
        var confirmedDeviations = 0;
        var reviewPotentialDeviationsOver5 = 0;
        var reviewPotentialDeviationsOver15 = 0;
        var reviewPotentialDeviationsOver30 = 0;
        var reviewPotentialDeviations = 0;
        var totalPositiveRawExactSiteDeviationMinutes = 0;
        var confirmedEffectiveDeviationMinutes = 0;
        var reviewPotentialDeviationMinutes = 0;
        var exclusions = new List<DailyAuditExclusion>();
        var classifiedPerformances = scans.SelectMany(item => item.Scan.NormalizedRecords.Select(performance =>
                (item.Technician, Performance: performance, Classification: PerformanceActivityClassifier.Classify(
                    performance, item.Technician.Name, null))))
            .ToArray();
        var waitingPerformances = classifiedPerformances
            .Where(item => item.Classification.ActivityType == PerformanceActivityType.WaitingTime)
            .ToArray();
        var travelPerformances = classifiedPerformances
            .Where(item => item.Classification.ActivityType == PerformanceActivityType.Travel)
            .ToArray();
        var excludedWaitingPerformances = waitingPerformances.Length;
        var excludedWaitingDays = waitingPerformances.Select(item =>
                (item.Technician.ExternalId, item.Performance.Date))
            .Distinct()
            .Count();
        var excludedTravelPerformances = travelPerformances.Length;
        var boundaryClassificationChanges = new List<BoundaryClassificationChange>();

        foreach (var (technician, scan) in scans)
        {
            foreach (var dayGroup in scan.NormalizedRecords.GroupBy(item => item.Date))
            {
                var date = dayGroup.Key;
                var dayAbsences = CalendarWindows(
                    calendar.Where(item =>
                            string.Equals(
                                item.ResourceExternalId,
                                technician.ExternalId,
                                StringComparison.OrdinalIgnoreCase) &&
                            item.StartDate <= date && item.EndDate >= date)
                        .ToArray(),
                    date);
                var eligibility = DailyAuditDayEligibility.Evaluate(date, dayAbsences);
                if (!eligibility.IsEligible)
                {
                    switch (eligibility.Status)
                    {
                        case DailyAuditDayStatus.ExcludedWeekend: excludedWeekend++; break;
                        case DailyAuditDayStatus.ExcludedPublicHoliday: excludedPublicHoliday++; break;
                        case DailyAuditDayStatus.ExcludedLeave: excludedLeave++; break;
                        case DailyAuditDayStatus.ExcludedSickness: excludedSickness++; break;
                    }

                    exclusions.Add(new DailyAuditExclusion(
                        date,
                        technician.Name,
                        eligibility.Status.ToString(),
                        eligibility.Reason));
                    continue;
                }

                var locationJobs = SelectLocationJobs(dayGroup, technician.Name, dayAbsences);
                if (locationJobs.Length == 0)
                {
                    continue;
                }

                var chronologicalPerformances = dayGroup.OrderBy(item => item.StartDateTime).ToArray();
                var excludedBeforeFirst = chronologicalPerformances.TakeWhile(item =>
                        item.ExternalId != locationJobs[0].ExternalId)
                    .Select(item => PerformanceActivityClassifier.Classify(item, technician.Name, null))
                    .Where(item => !item.RequiresGeographicMatch)
                    .ToArray();
                var excludedAfterLast = chronologicalPerformances.Reverse().TakeWhile(item =>
                        item.ExternalId != locationJobs[^1].ExternalId)
                    .Select(item => PerformanceActivityClassifier.Classify(item, technician.Name, null))
                    .Where(item => !item.RequiresGeographicMatch)
                    .ToArray();
                if (excludedBeforeFirst.Length > 0 || excludedAfterLast.Length > 0)
                {
                    boundaryClassificationChanges.Add(new BoundaryClassificationChange(
                        date,
                        technician.Name,
                        locationJobs[0].ExternalId,
                        locationJobs[^1].ExternalId,
                        excludedBeforeFirst.Select(item => $"{item.PerformanceId}:{item.ActivityType}").ToArray(),
                        excludedAfterLast.Select(item => $"{item.PerformanceId}:{item.ActivityType}").ToArray()));
                }

                technicianDays++;
                var first = locationJobs[0];
                var last = locationJobs[^1];
                var firstBlock = BoundaryBlockFromStart(locationJobs);
                var lastBlock = BoundaryBlockFromEnd(locationJobs);
                var trackingEligibility = await trackingEligibilityService.ResolveAsync(
                    technician.ExternalId, firstBlock.Start, cancellationToken);
                if (trackingEligibility?.TrackingStatus == TechnicianTrackingStatus.NoTrackAndTrace)
                {
                    excludedNoTrackAndTrace++;
                    exclusions.Add(new DailyAuditExclusion(
                        date,
                        technician.Name,
                        "ExcludedNoTrackAndTrace",
                        "Geen Track & Trace — niet controleerbaar via voertuiglocatie"));
                    continue;
                }
                var firstAssignment = await vehicleAssignmentService.ResolveAsync(
                    technician.ExternalId, firstBlock.Start, cancellationToken);
                var lastAssignment = await vehicleAssignmentService.ResolveAsync(
                    technician.ExternalId, lastBlock.End, cancellationToken);
                var assignmentAmbiguous =
                    firstAssignment.Status == VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment ||
                    lastAssignment.Status == VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment;
                var assignmentInsufficient =
                    firstAssignment.Status == VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment ||
                    lastAssignment.Status == VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment;
                if (assignmentAmbiguous) ambiguousVehicleAssignments++;
                if (assignmentInsufficient) insufficientVehicleAssignments++;
                if (firstAssignment.Status == VehicleAssignmentResolutionStatus.Resolved &&
                    lastAssignment.Status == VehicleAssignmentResolutionStatus.Resolved)
                    daysWithValidVehicleAssignment++;
                var vehicleStreamRisk = AssignmentRisk(
                    date, technician.Name, firstAssignment, lastAssignment);
                if (assignmentAmbiguous || assignmentInsufficient)
                {
                    vehicleStreamRisks.Add(vehicleStreamRisk);
                }

                var firstTrips = TripsForAssignment(distinctTrips, firstAssignment);
                var lastTrips = firstAssignment.ObjectId == lastAssignment.ObjectId
                    ? firstTrips
                    : TripsForAssignment(distinctTrips, lastAssignment);
                var firstStops = UsableStops(firstTrips, date, dayAbsences);
                var lastStops = firstAssignment.ObjectId == lastAssignment.ObjectId
                    ? firstStops
                    : UsableStops(lastTrips, date, dayAbsences);
                var firstMergedStops = MergedStopBuilder.Merge(
                    firstStops, _adaptiveOptions, distanceCalculator);
                var lastMergedStops = firstAssignment.ObjectId == lastAssignment.ObjectId
                    ? firstMergedStops
                    : MergedStopBuilder.Merge(lastStops, _adaptiveOptions, distanceCalculator);
                var exactStopwatch = Stopwatch.StartNew();
                var firstMatch = await MatchAsync(
                    first,
                    technician.Name,
                    locationJobs,
                    firstStops,
                    firstMergedStops,
                    geocodingByAddress,
                    cancellationToken);
                var lastMatch = first.ExternalId == last.ExternalId &&
                                firstAssignment.ObjectId == lastAssignment.ObjectId
                    ? firstMatch
                    : await MatchAsync(
                        last,
                        technician.Name,
                        locationJobs,
                        lastStops,
                        lastMergedStops,
                        geocodingByAddress,
                        cancellationToken);
                var firstBoundary = DailyBoundarySelector.Select(
                    DailyBoundarySide.First,
                    firstBlock.Start,
                    firstBlock.End,
                    firstMatch.Resolution,
                    firstStops,
                    firstMatch.Match,
                    _adaptiveOptions,
                    distanceCalculator);
                var lastBoundary = DailyBoundarySelector.Select(
                    DailyBoundarySide.Last,
                    lastBlock.Start,
                    lastBlock.End,
                    lastMatch.Resolution,
                    lastStops,
                    lastMatch.Match,
                    _adaptiveOptions,
                    distanceCalculator);
                WorksiteSessionDetection firstSession;
                WorksiteSessionDetection lastSession;
                if (firstAssignment.Status != VehicleAssignmentResolutionStatus.Resolved)
                {
                    firstBoundary = UnavailableVehicleSelection(firstBoundary, firstAssignment);
                    firstSession = new(firstBoundary, false, false, false, 0, 0, TimeSpan.Zero);
                }
                else
                {
                    firstSession = WorksiteSessionDetector.Apply(
                        technician.Name,
                        DailyBoundarySide.First,
                        firstBlock,
                        locationJobs,
                        firstStops,
                        firstTrips,
                        firstBoundary);
                    firstBoundary = firstSession.Selection;
                }
                if (lastAssignment.Status != VehicleAssignmentResolutionStatus.Resolved)
                {
                    lastBoundary = UnavailableVehicleSelection(lastBoundary, lastAssignment);
                    lastSession = new(lastBoundary, false, false, false, 0, 0, TimeSpan.Zero);
                }
                else
                {
                    lastSession = WorksiteSessionDetector.Apply(
                        technician.Name,
                        DailyBoundarySide.Last,
                        lastBlock,
                        locationJobs,
                        lastStops,
                        lastTrips,
                        lastBoundary);
                    lastBoundary = lastSession.Selection;
                }
                worksiteSessionDuration += firstSession.Duration + lastSession.Duration;
                worksiteSessionBoundariesConsidered +=
                    (firstSession.Considered ? 1 : 0) + (lastSession.Considered ? 1 : 0);
                worksiteSessionBoundariesChanged +=
                    (firstSession.Changed ? 1 : 0) + (lastSession.Changed ? 1 : 0);
                ambiguousWorksiteSessions +=
                    (firstSession.Ambiguous ? 1 : 0) + (lastSession.Ambiguous ? 1 : 0);
                worksiteSessionClusters += firstSession.ClusterCount + lastSession.ClusterCount;
                worksiteSessionHistoricalLookups +=
                    firstSession.HistoricalLookups + lastSession.HistoricalLookups;
                exactStopwatch.Stop();
                exactSiteDuration += exactStopwatch.Elapsed;
                var contextStopwatch = Stopwatch.StartNew();
                var firstBoundaryContextIndex = await ResolveContextIndexAsync(
                    DailyBoundarySide.First,
                    firstBlock,
                    first,
                    firstBoundary,
                    firstStops,
                    contextIndex,
                    cancellationToken);
                var lastBoundaryContextIndex = await ResolveContextIndexAsync(
                    DailyBoundarySide.Last,
                    lastBlock,
                    last,
                    lastBoundary,
                    lastStops,
                    contextIndex,
                    cancellationToken);
                var firstEvidence = DailyBoundaryContextSelector.Select(
                    DailyBoundarySide.First,
                    firstBlock,
                    first,
                    firstBoundary,
                    firstStops,
                    firstBoundaryContextIndex,
                    _adaptiveOptions.MinimumStopDurationMinutes,
                    distanceCalculator);
                var lastEvidence = DailyBoundaryContextSelector.Select(
                    DailyBoundarySide.Last,
                    lastBlock,
                    last,
                    lastBoundary,
                    lastStops,
                    lastBoundaryContextIndex,
                    _adaptiveOptions.MinimumStopDurationMinutes,
                    distanceCalculator);
                contextStopwatch.Stop();
                contextSupportedDuration += contextStopwatch.Elapsed;
                var firstReliable = firstEvidence.IsReliable;
                var lastReliable = lastEvidence.IsReliable;
                if (firstReliable) firstReliableBoundaries++;
                if (lastReliable) lastReliableBoundaries++;
                exactSiteBoundaries += (firstEvidence.EvidenceType == DailyBoundaryEvidenceType.ExactSite ? 1 : 0) +
                                       (lastEvidence.EvidenceType == DailyBoundaryEvidenceType.ExactSite ? 1 : 0);
                contextSupportedBoundaries += (firstEvidence.EvidenceType == DailyBoundaryEvidenceType.ContextSupported ? 1 : 0) +
                                              (lastEvidence.EvidenceType == DailyBoundaryEvidenceType.ContextSupported ? 1 : 0);
                var reviewStatus = firstReliable && lastReliable
                    ? "Reliable"
                    : firstReliable || lastReliable
                        ? "Partial"
                        : "Unresolved";
                if (reviewStatus == "Reliable") reliableDays++;
                else if (reviewStatus == "Partial") partialDays++;
                else unresolvedDays++;

                var startDeviation = firstEvidence.EffectiveDeviationMinutes;
                var endDeviation = lastEvidence.EffectiveDeviationMinutes;
                var startPotentialDeviation = firstEvidence.PotentialDeviationMinutes;
                var endPotentialDeviation = lastEvidence.PotentialDeviationMinutes;
                totalPositiveRawExactSiteDeviationMinutes +=
                    firstEvidence.RawExactSiteDeviationMinutes + lastEvidence.RawExactSiteDeviationMinutes;
                confirmedDeviationsOver5 += (startDeviation > 5 ? 1 : 0) +
                                            (endDeviation > 5 ? 1 : 0);
                confirmedDeviationsOver15 += (startDeviation > 15 ? 1 : 0) +
                                             (endDeviation > 15 ? 1 : 0);
                confirmedDeviationsOver30 += (startDeviation > 30 ? 1 : 0) +
                                             (endDeviation > 30 ? 1 : 0);
                confirmedDeviations += (startDeviation > 0 ? 1 : 0) +
                                       (endDeviation > 0 ? 1 : 0);
                reviewPotentialDeviationsOver5 += (startPotentialDeviation > 5 ? 1 : 0) +
                                                  (endPotentialDeviation > 5 ? 1 : 0);
                reviewPotentialDeviationsOver15 += (startPotentialDeviation > 15 ? 1 : 0) +
                                                   (endPotentialDeviation > 15 ? 1 : 0);
                reviewPotentialDeviationsOver30 += (startPotentialDeviation > 30 ? 1 : 0) +
                                                   (endPotentialDeviation > 30 ? 1 : 0);
                reviewPotentialDeviations += (startPotentialDeviation > 0 ? 1 : 0) +
                                             (endPotentialDeviation > 0 ? 1 : 0);
                confirmedEffectiveDeviationMinutes += (startDeviation ?? 0) + (endDeviation ?? 0);
                reviewPotentialDeviationMinutes +=
                    (startPotentialDeviation ?? 0) + (endPotentialDeviation ?? 0);
                if (startDeviation > 0 || endDeviation > 0 ||
                    startPotentialDeviation > 0 || endPotentialDeviation > 0)
                {
                    auditRows.Add(new DailyHoursAuditRow(
                        date,
                        technician.Name,
                        first.ExternalId,
                        DisplayCustomer(first),
                        firstMatch.Resolution.OriginalAddress,
                        firstBlock.Start,
                        firstEvidence.ExactSiteBoundaryTime,
                        firstEvidence.RawExactSiteDeviationMinutes,
                        firstEvidence.ContextBoundaryTime,
                        firstEvidence.ContextAddress,
                        firstEvidence.ContextDistanceMeters,
                        firstEvidence.ContextCustomerRelation,
                        firstEvidence.EvidenceType.ToString(),
                        firstEvidence.EffectiveBoundaryTime,
                        startDeviation,
                        startPotentialDeviation,
                        firstBoundary.Decision.ToString(),
                        firstBoundary.Selected?.ConfidenceScore,
                        firstBoundary.Selected?.DistanceMeters,
                        last.ExternalId,
                        DisplayCustomer(last),
                        lastMatch.Resolution.OriginalAddress,
                        lastEvidence.ExactSiteBoundaryTime,
                        lastEvidence.RawExactSiteDeviationMinutes,
                        lastEvidence.ContextBoundaryTime,
                        lastEvidence.ContextAddress,
                        lastEvidence.ContextDistanceMeters,
                        lastEvidence.ContextCustomerRelation,
                        lastEvidence.EvidenceType.ToString(),
                        lastEvidence.EffectiveBoundaryTime,
                        lastBlock.End,
                        endDeviation,
                        endPotentialDeviation,
                        lastBoundary.Decision.ToString(),
                        lastBoundary.Selected?.ConfidenceScore,
                        lastBoundary.Selected?.DistanceMeters,
                        (startDeviation ?? 0) + (endDeviation ?? 0),
                        (startPotentialDeviation ?? 0) + (endPotentialDeviation ?? 0),
                        reviewStatus,
                        BuildReason(firstEvidence, lastEvidence, dayAbsences)));
                }

                var diagnosticResolutions = new Dictionary<long, PilotLocationResolution>
                {
                    [first.ExternalId] = firstMatch.Resolution,
                    [last.ExternalId] = lastMatch.Resolution,
                };
                if (request.DetailedDiagnostics)
                {
                    foreach (var locationJob in locationJobs)
                    {
                        if (!diagnosticResolutions.ContainsKey(locationJob.ExternalId))
                        {
                            diagnosticResolutions[locationJob.ExternalId] = await ResolveAsync(
                                locationJob,
                                firstStops.Concat(lastStops)
                                    .DistinctBy(item => item.StopId, StringComparer.Ordinal)
                                    .ToArray(),
                                geocodingByAddress,
                                cancellationToken);
                        }
                    }
                }

                diagnostics.Add(new DailyBoundaryDiagnosticCase(
                    date,
                    technician.Name,
                    startDeviation,
                    endDeviation,
                    (startDeviation ?? 0) + (endDeviation ?? 0),
                    startPotentialDeviation,
                    endPotentialDeviation,
                    (startPotentialDeviation ?? 0) + (endPotentialDeviation ?? 0),
                    reviewStatus,
                    dayGroup.OrderBy(item => item.StartDateTime)
                        .Select(item => ToDiagnosticPerformance(
                            item,
                            technician.Name,
                            first.ExternalId,
                            last.ExternalId,
                            diagnosticResolutions.GetValueOrDefault(item.ExternalId)))
                        .ToArray(),
                    firstTrips.Concat(lastTrips)
                        .DistinctBy(item => PowerfleetVehicleStreamIdentity.ObservationKey(item),
                            StringComparer.OrdinalIgnoreCase)
                        .Where(item =>
                            DateOnly.FromDateTime(item.StartDateTime.DateTime) == date)
                        .Select(item => new DiagnosticTrip(
                            item.ExternalId,
                            item.StartDateTime,
                            item.EndDateTime,
                            item.StartAddress ?? item.StartLocation,
                            item.EndAddress ?? item.EndLocation,
                            item.StartLatitude,
                            item.StartLongitude,
                            item.EndLatitude,
                            item.EndLongitude,
                            rawRouteByTrip.GetValueOrDefault(item.ExternalId),
                            item.ObjectId,
                            item.ObjectName,
                            item.VehiclePlate,
                            item.DriverId,
                            item.DriverName))
                        .ToArray(),
                    firstStops.Concat(lastStops)
                        .DistinctBy(item => item.StopId, StringComparer.Ordinal)
                        .Select(item => new DiagnosticStop(
                            item.StopId,
                            item.Arrival,
                            item.Departure,
                            item.Address,
                            item.Latitude,
                            item.Longitude,
                            item.LocationContinuity,
                            item.ObjectId,
                            item.ObjectName,
                            item.VehiclePlate,
                            item.DriverId,
                            item.DriverName))
                        .ToArray(),
                    ToBoundaryDiagnostic(first, firstBlock, firstMatch, firstBoundary),
                    ToBoundaryDiagnostic(last, lastBlock, lastMatch, lastBoundary),
                    dayAbsences.Select(item => new DiagnosticCalendarWindow(
                            item.Start,
                            item.End,
                            item.Kind.ToString(),
                            item.Subject))
                        .ToArray(),
                    firstEvidence,
                    lastEvidence,
                    vehicleStreamRisk));
            }
        }

        var rows = auditRows.OrderByDescending(item =>
                item.TotalConfirmedDeviation + item.TotalReviewPotentialDeviation)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await WriteCsvAsync(request.OutputPath, rows, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.DiagnosticsPath))
        {
            var diagnosticDirectory = Path.GetDirectoryName(request.DiagnosticsPath);
            if (!string.IsNullOrWhiteSpace(diagnosticDirectory))
            {
                Directory.CreateDirectory(diagnosticDirectory);
            }

            var orderedDiagnostics = diagnostics
                .OrderByDescending(item =>
                    item.TotalConfirmedDeviation + item.TotalReviewPotentialDeviation)
                .ThenBy(item => item.Date)
                .ThenBy(item => item.Technician, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            await File.WriteAllTextAsync(
                request.DiagnosticsPath,
                JsonSerializer.Serialize(
                    orderedDiagnostics,
                    DiagnosticJsonOptions),
                cancellationToken);
        }
        logger.LogInformation(
            "Daily hours audit: {Days} technieker-dagen, {Reliable} reliable, {Deviations} afwijkend.",
            technicianDays,
            reliableDays,
            rows.Length);
        var contextMetrics = contextIndexProvider.Metrics;
        totalStopwatch.Stop();
        return new DailyHoursAuditResult(
            technicianDays,
            reliableDays,
            partialDays,
            unresolvedDays,
            rows.Length,
            excludedWeekend,
            excludedPublicHoliday,
            excludedLeave,
            excludedSickness,
            excludedWaitingPerformances,
            excludedWaitingDays,
            excludedTravelPerformances,
            firstReliableBoundaries,
            lastReliableBoundaries,
            exactSiteBoundaries,
            contextSupportedBoundaries,
            confirmedDeviationsOver5,
            confirmedDeviationsOver15,
            reviewPotentialDeviationsOver5,
            reviewPotentialDeviationsOver15,
            totalPositiveRawExactSiteDeviationMinutes,
            confirmedEffectiveDeviationMinutes,
            reviewPotentialDeviationMinutes,
            sourceStopwatch.Elapsed,
            exactSiteDuration,
            contextSupportedDuration,
            worksiteSessionDuration,
            worksiteSessionBoundariesConsidered,
            worksiteSessionBoundariesChanged,
            ambiguousWorksiteSessions,
            worksiteSessionClusters,
            worksiteSessionHistoricalLookups,
            totalStopwatch.Elapsed,
            _contextBoundariesConsidered,
            _contextBoundariesSkippedNoTemporalStop,
            contextMetrics.AddressMatchesWithoutGeocoding,
            contextMetrics.GeocodeCacheHits,
            contextMetrics.GeocodeCacheMisses,
            contextMetrics.ExternalGeocodeCalls,
            contextMetrics.UniquePlenionLocationsGeocoded,
            contextMetrics.NegativeCacheHits,
            ambiguousVehicleAssignments,
            insufficientVehicleAssignments,
            excludedNoTrackAndTrace,
            daysWithValidVehicleAssignment,
            confirmedDeviations,
            confirmedDeviationsOver30,
            reviewPotentialDeviations,
            reviewPotentialDeviationsOver30,
            vehicleStreamRisks,
            boundaryClassificationChanges,
            exclusions,
            request.OutputPath,
            rows);
    }

    internal static DailyBoundarySelection AmbiguousVehicleSelection(
        DailyBoundarySelection selection,
        PowerfleetVehicleStreamRisk risk) =>
        selection with
        {
            Decision = AdaptiveMatchDecision.Ambiguous,
            Selected = null,
            Assessment = $"{PowerfleetVehicleStreamIdentity.AmbiguousStatus}: {risk.Reason}",
            WorksiteSession = null,
        };

    internal static DailyBoundarySelection UnavailableVehicleSelection(
        DailyBoundarySelection selection,
        VehicleAssignmentResolution resolution) =>
        selection with
        {
            Decision = resolution.Status == VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment
                ? AdaptiveMatchDecision.Ambiguous
                : AdaptiveMatchDecision.Unresolved,
            Selected = null,
            Assessment = $"{resolution.Status}: {resolution.Reason}",
            WorksiteSession = null,
        };

    internal static NormalizedPilotTrip[] TripsForAssignment(
        IReadOnlyList<NormalizedPilotTrip> trips,
        VehicleAssignmentResolution resolution) =>
        resolution.Status != VehicleAssignmentResolutionStatus.Resolved ||
        string.IsNullOrWhiteSpace(resolution.ObjectId)
            ? []
            : trips.Where(item =>
                    string.Equals(item.ObjectId, resolution.ObjectId, StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrWhiteSpace(item.ObjectId) &&
                     !string.IsNullOrWhiteSpace(item.VehiclePlate) &&
                     resolution.Assignments.Count == 1 &&
                     !string.IsNullOrWhiteSpace(resolution.Assignments[0].RegistrationPlateSnapshot) &&
                     NormalizePlate(item.VehiclePlate) ==
                     NormalizePlate(resolution.Assignments[0].RegistrationPlateSnapshot!)))
                .OrderBy(item => item.StartDateTime)
                .ToArray();

    private static string NormalizePlate(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static PilotStop[] UsableStops(
        IReadOnlyList<NormalizedPilotTrip> trips,
        DateOnly date,
        IReadOnlyList<DailyAbsenceWindow> dayAbsences)
    {
        var stops = PilotLocationMatcher.ReconstructStops(trips.ToArray(), []);
        return stops.Where(stop => stop.Date == date &&
                !dayAbsences.Any(absence =>
                    Overlaps(stop.Arrival, stop.Departure, absence.Start, absence.End)))
            .ToArray();
    }

    private static PowerfleetVehicleStreamRisk AssignmentRisk(
        DateOnly date,
        string technician,
        VehicleAssignmentResolution first,
        VehicleAssignmentResolution last)
    {
        var assignments = first.Assignments.Concat(last.Assignments)
            .DistinctBy(item => item.Id)
            .ToArray();
        var statuses = new[] { first.Status, last.Status }.Distinct().ToArray();
        var status = statuses.Contains(VehicleAssignmentResolutionStatus.AmbiguousVehicleAssignment)
            ? PowerfleetVehicleStreamIdentity.AmbiguousStatus
            : statuses.Contains(VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment)
                ? VehicleAssignmentResolutionStatus.InsufficientVehicleAssignment.ToString()
                : "ResolvedVehicleAssignment";
        return new PowerfleetVehicleStreamRisk(
            date,
            technician,
            assignments.Select(item => item.ObjectId)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            assignments.Select(item =>
                    $"object:{item.ObjectId}|plate={item.RegistrationPlateSnapshot ?? "-"}|source={item.Source}")
                .ToArray(),
            0,
            status,
            $"FIRST: {first.Reason} LAST: {last.Reason}");
    }

    private async Task<(PilotLocationResolution Resolution, AdaptiveMatchResult? Match)> MatchAsync(
        NormalizedPilotPerformance performance,
        string technicianName,
        IReadOnlyList<NormalizedPilotPerformance> locationJobs,
        IReadOnlyList<PilotStop> stops,
        IReadOnlyList<MergedPilotStop> mergedStops,
        Dictionary<string, GeocodingResult> geocodingByAddress,
        CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(
            performance,
            stops,
            geocodingByAddress,
            cancellationToken);
        var match = PrecisionPreservingHybridMatcher.Match(
            performance,
            technicianName,
            resolution,
            mergedStops,
            locationJobs,
            new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
            _adaptiveOptions,
            distanceCalculator);
        return (resolution, match);
    }

    private async Task<DailyBoundaryContextIndex> ResolveContextIndexAsync(
        DailyBoundarySide side,
        BoundaryBlock block,
        NormalizedPilotPerformance performance,
        DailyBoundarySelection exact,
        IReadOnlyList<PilotStop> stops,
        DailyBoundaryContextIndex index,
        CancellationToken cancellationToken)
    {
        if (!exact.IsReliable || exact.Selected is null)
        {
            return index with { Locations = [] };
        }

        var plenionTime = side == DailyBoundarySide.First ? block.Start : block.End;
        var exactTime = side == DailyBoundarySide.First
            ? exact.Selected.Stop.Arrival
            : exact.Selected.Stop.Departure;
        var rawDeviation = HoursAuditService.PositiveWholeMinutes(side == DailyBoundarySide.First
            ? exactTime - plenionTime
            : plenionTime - exactTime);
        if (rawDeviation <= DailyBoundaryContextSelector.MaximumBoundaryDifferenceMinutes)
        {
            return index with { Locations = [] };
        }

        _contextBoundariesConsidered++;

        var stop = DailyBoundaryContextSelector.AdjacentMeaningfulStop(
            side,
            exactTime,
            stops,
            _adaptiveOptions.MinimumStopDurationMinutes);
        if (stop is null)
        {
            _contextBoundariesSkippedNoTemporalStop++;
            return index with { Locations = [] };
        }

        var contextTime = side == DailyBoundarySide.First ? stop.Arrival : stop.Departure;
        var withinTemporalWindow = Math.Abs((contextTime - plenionTime).TotalMinutes) <=
                                   DailyBoundaryContextSelector.MaximumBoundaryDifferenceMinutes;
        if (!withinTemporalWindow)
        {
            _contextBoundariesSkippedNoTemporalStop++;
        }

        return await contextIndexProvider.ResolveAsync(
            index,
            performance,
            stop.Address,
            withinTemporalWindow,
            cancellationToken);
    }

    private static DiagnosticBoundary ToBoundaryDiagnostic(
        NormalizedPilotPerformance performance,
        BoundaryBlock block,
        (PilotLocationResolution Resolution, AdaptiveMatchResult? Match) result,
        DailyBoundarySelection boundary)
    {
        var selectedId = boundary.Selected?.Stop.MergedStopId;
        var geocode = result.Resolution.Geocoding.Primary;
        return new DiagnosticBoundary(
            performance.ExternalId,
            block.Start,
            block.End,
            result.Resolution.OriginalAddress,
            boundary.Decision.ToString(),
            boundary.Selected?.ConfidenceScore,
            boundary.Selected?.DistanceMeters,
            boundary.Selected?.OverlapMinutes,
            selectedId,
            boundary.Selected?.Stop.Arrival,
            boundary.Selected?.Stop.Departure,
            boundary.Selected?.Stop.Address,
            geocode?.Coordinate.Latitude,
            geocode?.Coordinate.Longitude,
            geocode?.FormattedAddress,
            result.Resolution.Geocoding.Status.ToString(),
            boundary.Assessment,
            boundary.Candidates.Select(candidate => new DiagnosticCandidate(
                    candidate.Stop.MergedStopId,
                    candidate.Stop.Arrival,
                    candidate.Stop.Departure,
                    candidate.Stop.Address,
                    candidate.ConfidenceScore,
                    candidate.DistanceMeters,
                    candidate.OverlapMinutes,
                    block.DurationMinutes == 0 ? 0 : 100d * candidate.OverlapMinutes / block.DurationMinutes,
                    candidate.Stop.Latitude,
                    candidate.Stop.Longitude,
                    candidate.Stop.MergedStopId == selectedId,
                    false,
                    candidate.Explanation))
                .ToArray(),
            result.Match?.Decision.ToString() ?? "Unresolved",
            result.Match?.Selected?.Stop.MergedStopId,
            result.Match?.Assessment ?? "geen algemene match",
            boundary.WorksiteSession);
    }

    private static DiagnosticPerformance ToDiagnosticPerformance(
        NormalizedPilotPerformance performance,
        string technicianName,
        long firstPerformanceId,
        long lastPerformanceId,
        PilotLocationResolution? resolution)
    {
        var geocode = resolution?.Geocoding.Primary;
        return new DiagnosticPerformance(
            performance.ExternalId,
            PerformanceActivityClassifier.Classify(performance, technicianName, null)
                .ActivityType.ToString(),
            DisplayCustomer(performance),
            JoinNonEmpty(performance.Street, performance.PostalCode, performance.City, performance.Country),
            performance.StartDateTime,
            performance.EndDateTime,
            performance.ExternalId == firstPerformanceId,
            performance.ExternalId == lastPerformanceId,
            performance.ProjectNumber,
            performance.WorkOrderNumber,
            performance.Description,
            performance.MainTaskExternalId,
            geocode?.Coordinate.Latitude,
            geocode?.Coordinate.Longitude,
            geocode?.FormattedAddress,
            resolution?.Geocoding.Status.ToString());
    }

    private async Task<PilotLocationResolution> ResolveAsync(
        NormalizedPilotPerformance performance,
        IReadOnlyList<PilotStop> stops,
        Dictionary<string, GeocodingResult> geocodingByAddress,
        CancellationToken cancellationToken)
    {
        var address = JoinNonEmpty(
            performance.Street,
            performance.PostalCode,
            performance.City,
            performance.Country);
        var normalized = LocationGeocodingCache.NormalizeAddress(address);
        var hash = LocationGeocodingCache.Hash(normalized);
        if (!geocodingByAddress.TryGetValue(hash, out var geocoding))
        {
            geocoding = string.IsNullOrWhiteSpace(address)
                ? new GeocodingResult(
                    GeocodingStatus.InvalidAddress,
                    geocodingService.Provider,
                    null,
                    [])
                : await geocodingService.GeocodeAsync(address, cancellationToken);
            geocodingByAddress[hash] = geocoding;
        }

        var candidates = LocationResolutionPilotService.EvaluateCandidates(
            performance,
            stops.Where(stop =>
                    stop.LocationContinuity &&
                    stop.Latitude is not null &&
                    stop.Longitude is not null &&
                    !IsHome(stop))
                .ToArray(),
            geocoding,
            locationMatchingOptions,
            distanceCalculator);
        var status = LocationResolutionPilotService.ResolveStatus(geocoding, candidates);
        return new PilotLocationResolution(
            performance.ExternalId,
            performance.Date,
            performance.ProjectNumber,
            performance.ProjectName,
            performance.WorkOrderNumber,
            performance.StartDateTime,
            performance.EndDateTime,
            performance.DeliveryAddressExternalId,
            address,
            normalized,
            hash,
            geocoding,
            candidates,
            status,
            status.ToString(),
            status.ToString());
    }

    private static DailyAbsenceWindow[] CalendarWindows(
        IReadOnlyList<PlenionCalendarAbsence> absences,
        DateOnly date) =>
        absences.Select(item => new DailyAbsenceWindow(
                Local(date, item.StartTime),
                Local(date, item.EndTime),
                item.Kind,
                item.Subject))
            .ToArray();

    private static DateTimeOffset Local(DateOnly date, TimeOnly time)
    {
        var value = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        return new DateTimeOffset(value, zone.GetUtcOffset(value));
    }

    private static bool Overlaps(
        DateTimeOffset leftStart,
        DateTimeOffset leftEnd,
        DateTimeOffset rightStart,
        DateTimeOffset rightEnd) =>
        leftStart < rightEnd && leftEnd > rightStart;

    private static bool IsHome(PilotStop stop) =>
        (stop.Area?.Contains("Huisadres", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (stop.AreaGroup?.Contains("Huisadres", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string DisplayCustomer(NormalizedPilotPerformance performance) =>
        performance.CustomerOrSiteName ?? performance.ProjectName ?? string.Empty;

    private static string JoinNonEmpty(params string?[] values) =>
        string.Join(" / ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static BoundaryBlock BoundaryBlockFromStart(
        NormalizedPilotPerformance[] jobs)
    {
        var anchor = jobs[0];
        var members = jobs.TakeWhile(item => SameSite(anchor, item)).ToArray();
        return ToBoundaryBlock(members);
    }

    private static BoundaryBlock BoundaryBlockFromEnd(
        NormalizedPilotPerformance[] jobs)
    {
        var anchor = jobs[^1];
        var members = jobs.Reverse().TakeWhile(item => SameSite(anchor, item)).Reverse().ToArray();
        return ToBoundaryBlock(members);
    }

    private static BoundaryBlock ToBoundaryBlock(
        NormalizedPilotPerformance[] jobs) =>
        new(
            jobs.Min(item => item.StartDateTime),
            jobs.Max(item => item.EndDateTime),
            jobs.Select(item => item.ExternalId).ToArray());

    internal static bool SameSite(
        NormalizedPilotPerformance left,
        NormalizedPilotPerformance right)
    {
        if (left.ExternalId == right.ExternalId)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(left.DeliveryAddressExternalId) &&
            !string.IsNullOrWhiteSpace(right.DeliveryAddressExternalId))
        {
            return string.Equals(
                left.DeliveryAddressExternalId,
                right.DeliveryAddressExternalId,
                StringComparison.OrdinalIgnoreCase);
        }

        var leftAddress = LocationGeocodingCache.NormalizeAddress(JoinNonEmpty(
            left.Street, left.PostalCode, left.City, left.Country));
        var rightAddress = LocationGeocodingCache.NormalizeAddress(JoinNonEmpty(
            right.Street, right.PostalCode, right.City, right.Country));
        return !string.IsNullOrWhiteSpace(leftAddress) &&
               string.Equals(leftAddress, rightAddress, StringComparison.Ordinal);
    }

    internal static NormalizedPilotPerformance[] SelectLocationJobs(
        IEnumerable<NormalizedPilotPerformance> performances,
        string technicianName,
        IReadOnlyList<DailyAbsenceWindow> absences) =>
        performances.Where(performance => PerformanceActivityClassifier.Classify(
                performance,
                technicianName,
                null).RequiresGeographicMatch)
            .Where(performance => !absences.Any(absence =>
                Overlaps(performance.StartDateTime, performance.EndDateTime, absence.Start, absence.End)))
            .OrderBy(item => item.StartDateTime)
            .ToArray();

    private static string BuildReason(
        DailyBoundaryEvidence first,
        DailyBoundaryEvidence last,
        DailyAbsenceWindow[] absences) =>
        $"Eerste [{first.EvidenceType}]: {first.Reason}; " +
        $"Laatste [{last.EvidenceType}]: {last.Reason}; " +
        (absences.Length == 0
            ? "geen kalenderafwezigheid"
            : $"{absences.Length} kalenderafwezigheidsvenster(s) toegepast");

    private static async Task WriteCsvAsync(
        string path,
        IReadOnlyList<DailyHoursAuditRow> rows,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new StringBuilder();
        builder.AppendLine("Datum,Technieker,EerstePerformanceId,EersteKlant,EersteAdres,PlenionEersteStart,ExactSiteAankomstEerste,RawExactSiteStartAfwijkingMin,EersteContexttijd,EersteContextadres,EersteContextafstandMeters,EersteContextklantrelatie,EersteEvidenceType,EffectieveAankomstEerste,StartAfwijkingMin,StartPotentialDeviationMin,EersteMatcherStatus,EersteScore,EersteAfstandMeters,LaatstePerformanceId,LaatsteKlant,LaatsteAdres,ExactSiteVertrekLaatste,RawExactSiteEindAfwijkingMin,LaatsteContexttijd,LaatsteContextadres,LaatsteContextafstandMeters,LaatsteContextklantrelatie,LaatsteEvidenceType,EffectiefVertrekLaatste,PlenionLaatsteEinde,EindAfwijkingMin,EindPotentialDeviationMin,LaatsteMatcherStatus,LaatsteScore,LaatsteAfstandMeters,ConfirmedEffectiveDeviationMin,ReviewPotentialDeviationMin,ReviewStatus,Reason");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(row.Technician),
                Csv(row.FirstPerformanceId.ToString(CultureInfo.InvariantCulture)),
                Csv(row.FirstCustomer), Csv(row.FirstAddress),
                Csv(Format(row.PlenionFirstStart)), Csv(Format(row.PowerfleetFirstArrival)),
                Csv(row.RawExactSiteStartDeviationMinutes.ToString(CultureInfo.InvariantCulture)),
                Csv(Format(row.FirstContextBoundaryTime)), Csv(row.FirstContextAddress ?? string.Empty),
                Csv(Number(row.FirstContextDistanceMeters)), Csv(row.FirstContextCustomerRelation ?? string.Empty),
                Csv(row.FirstEvidenceType), Csv(Format(row.EffectiveFirstBoundaryTime)),
                Csv(Integer(row.StartDeviationMinutes)), Csv(Integer(row.StartPotentialDeviationMinutes)),
                Csv(row.FirstMatcherStatus), Csv(Number(row.FirstScore)), Csv(Number(row.FirstDistanceMeters)),
                Csv(row.LastPerformanceId.ToString(CultureInfo.InvariantCulture)),
                Csv(row.LastCustomer), Csv(row.LastAddress),
                Csv(Format(row.PowerfleetLastDeparture)),
                Csv(row.RawExactSiteEndDeviationMinutes.ToString(CultureInfo.InvariantCulture)),
                Csv(Format(row.LastContextBoundaryTime)), Csv(row.LastContextAddress ?? string.Empty),
                Csv(Number(row.LastContextDistanceMeters)), Csv(row.LastContextCustomerRelation ?? string.Empty),
                Csv(row.LastEvidenceType), Csv(Format(row.EffectiveLastBoundaryTime)),
                Csv(Format(row.PlenionLastEnd)),
                Csv(Integer(row.EndDeviationMinutes)), Csv(Integer(row.EndPotentialDeviationMinutes)),
                Csv(row.LastMatcherStatus), Csv(Number(row.LastScore)), Csv(Number(row.LastDistanceMeters)),
                Csv(row.TotalConfirmedDeviation.ToString(CultureInfo.InvariantCulture)),
                Csv(row.TotalReviewPotentialDeviation.ToString(CultureInfo.InvariantCulture)),
                Csv(row.ReviewStatus), Csv(row.Reason),
            }));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    private static string Format(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(double? value) =>
        value?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Integer(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}

internal enum PlenionCalendarAbsenceKind { Leave, Sickness }

internal sealed record PlenionCalendarAbsence(
    long CalendarId,
    string ResourceExternalId,
    DateOnly StartDate,
    DateOnly EndDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    PlenionCalendarAbsenceKind Kind,
    string Subject);

internal sealed record DailyAbsenceWindow(
    DateTimeOffset Start,
    DateTimeOffset End,
    PlenionCalendarAbsenceKind Kind,
    string Subject);

internal sealed record BoundaryBlock(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<long> PerformanceIds)
{
    public int DurationMinutes => Math.Max(0, (int)Math.Round((End - Start).TotalMinutes));
}

internal sealed record DailyHoursAuditRequest(
    DateOnly FromDate,
    DateOnly ThroughDate,
    string OutputPath,
    string? DiagnosticsPath = null,
    string? TechnicianQuery = null,
    bool DetailedDiagnostics = false);

internal sealed record DailyHoursAuditResult(
    int TechnicianDays,
    int ReliableDays,
    int PartialDays,
    int UnresolvedDays,
    int DeviatingDays,
    int ExcludedWeekend,
    int ExcludedPublicHoliday,
    int ExcludedLeave,
    int ExcludedSickness,
    int ExcludedWaitingPerformances,
    int ExcludedWaitingDays,
    int ExcludedTravelPerformances,
    int FirstReliableBoundaries,
    int LastReliableBoundaries,
    int ExactSiteBoundaries,
    int ContextSupportedBoundaries,
    int ConfirmedDeviationsOver5,
    int ConfirmedDeviationsOver15,
    int ReviewPotentialDeviationsOver5,
    int ReviewPotentialDeviationsOver15,
    int TotalPositiveRawExactSiteDeviationMinutes,
    int ConfirmedEffectiveDeviationMinutes,
    int ReviewPotentialDeviationMinutes,
    TimeSpan SourceExtractDuration,
    TimeSpan ExactSiteDuration,
    TimeSpan ContextSupportedDuration,
    TimeSpan WorksiteSessionDuration,
    int WorksiteSessionBoundariesConsidered,
    int WorksiteSessionBoundariesChanged,
    int AmbiguousWorksiteSessions,
    int WorksiteSessionClusters,
    int WorksiteSessionHistoricalLookups,
    TimeSpan TotalDuration,
    int ContextBoundariesConsidered,
    int ContextBoundariesSkippedNoTemporalStop,
    int AddressMatchesWithoutGeocoding,
    int GeocodeCacheHits,
    int GeocodeCacheMisses,
    int ExternalGeocodeCalls,
    int UniquePlenionLocationsGeocoded,
    int NegativeCacheHits,
    int AmbiguousVehicleAssignments,
    int InsufficientVehicleAssignments,
    int ExcludedNoTrackAndTrace,
    int DaysWithValidVehicleAssignment,
    int ConfirmedDeviations,
    int ConfirmedDeviationsOver30,
    int ReviewPotentialDeviations,
    int ReviewPotentialDeviationsOver30,
    IReadOnlyList<PowerfleetVehicleStreamRisk> VehicleStreamRisks,
    IReadOnlyList<BoundaryClassificationChange> BoundaryClassificationChanges,
    IReadOnlyList<DailyAuditExclusion> Exclusions,
    string OutputPath,
    IReadOnlyList<DailyHoursAuditRow> Rows);

internal sealed record DailyAuditExclusion(
    DateOnly Date,
    string Technician,
    string Status,
    string Reason);

internal sealed record BoundaryClassificationChange(
    DateOnly Date,
    string Technician,
    long FirstPerformanceId,
    long LastPerformanceId,
    IReadOnlyList<string> ExcludedBeforeFirst,
    IReadOnlyList<string> ExcludedAfterLast);

internal sealed record DailyHoursAuditRow(
    DateOnly Date,
    string Technician,
    long FirstPerformanceId,
    string FirstCustomer,
    string FirstAddress,
    DateTimeOffset PlenionFirstStart,
    DateTimeOffset? PowerfleetFirstArrival,
    int RawExactSiteStartDeviationMinutes,
    DateTimeOffset? FirstContextBoundaryTime,
    string? FirstContextAddress,
    double? FirstContextDistanceMeters,
    string? FirstContextCustomerRelation,
    string FirstEvidenceType,
    DateTimeOffset? EffectiveFirstBoundaryTime,
    int? StartDeviationMinutes,
    int? StartPotentialDeviationMinutes,
    string FirstMatcherStatus,
    double? FirstScore,
    double? FirstDistanceMeters,
    long LastPerformanceId,
    string LastCustomer,
    string LastAddress,
    DateTimeOffset? PowerfleetLastDeparture,
    int RawExactSiteEndDeviationMinutes,
    DateTimeOffset? LastContextBoundaryTime,
    string? LastContextAddress,
    double? LastContextDistanceMeters,
    string? LastContextCustomerRelation,
    string LastEvidenceType,
    DateTimeOffset? EffectiveLastBoundaryTime,
    DateTimeOffset PlenionLastEnd,
    int? EndDeviationMinutes,
    int? EndPotentialDeviationMinutes,
    string LastMatcherStatus,
    double? LastScore,
    double? LastDistanceMeters,
    int TotalConfirmedDeviation,
    int TotalReviewPotentialDeviation,
    string ReviewStatus,
    string Reason);

internal sealed record DailyBoundaryDiagnosticCase(
    DateOnly Date,
    string Technician,
    int? StartDeviationMinutes,
    int? EndDeviationMinutes,
    int TotalConfirmedDeviation,
    int? StartPotentialDeviationMinutes,
    int? EndPotentialDeviationMinutes,
    int TotalReviewPotentialDeviation,
    string ReviewStatus,
    IReadOnlyList<DiagnosticPerformance> Performances,
    IReadOnlyList<DiagnosticTrip> Trips,
    IReadOnlyList<DiagnosticStop> Stops,
    DiagnosticBoundary First,
    DiagnosticBoundary Last,
    IReadOnlyList<DiagnosticCalendarWindow> CalendarWindows,
    DailyBoundaryEvidence? FirstEvidence = null,
    DailyBoundaryEvidence? LastEvidence = null,
    PowerfleetVehicleStreamRisk? VehicleStream = null);

internal sealed record DiagnosticPerformance(
    long PerformanceId,
    string ActivityType,
    string Customer,
    string Address,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsFirstBoundary,
    bool IsLastBoundary,
    string? ProjectNumber,
    string? WorkOrderNumber,
    string? Description,
    string? MainTaskExternalId,
    double? Latitude,
    double? Longitude,
    string? GeocodedAddress,
    string? GeocodingStatus);

internal sealed record DiagnosticTrip(
    string TripId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? StartLocation,
    string? EndLocation,
    decimal? StartLatitude,
    decimal? StartLongitude,
    decimal? EndLatitude,
    decimal? EndLongitude,
    string? RawRoute,
    string? ObjectId = null,
    string? ObjectName = null,
    string? VehiclePlate = null,
    string? DriverId = null,
    string? DriverName = null);

internal sealed record DiagnosticStop(
    string StopId,
    DateTimeOffset Arrival,
    DateTimeOffset Departure,
    string? Address,
    decimal? Latitude,
    decimal? Longitude,
    bool LocationContinuity,
    string? ObjectId = null,
    string? ObjectName = null,
    string? VehiclePlate = null,
    string? DriverId = null,
    string? DriverName = null);

internal sealed record DiagnosticBoundary(
    long PerformanceId,
    DateTimeOffset PlenionStart,
    DateTimeOffset PlenionEnd,
    string PlenionAddress,
    string MatcherStatus,
    double? Score,
    double? DistanceMeters,
    int? OverlapMinutes,
    string? SelectedVisitId,
    DateTimeOffset? Arrival,
    DateTimeOffset? Departure,
    string? SelectedAddress,
    double? PlenionLatitude,
    double? PlenionLongitude,
    string? GeocodedAddress,
    string GeocodingStatus,
    string Assessment,
    IReadOnlyList<DiagnosticCandidate> Candidates,
    string GeneralMatcherStatus,
    string? GeneralSelectedVisitId,
    string GeneralAssessment,
    WorksiteSession? WorksiteSession);

internal sealed record DiagnosticCandidate(
    string VisitId,
    DateTimeOffset Arrival,
    DateTimeOffset Departure,
    string? Address,
    double Score,
    double? DistanceMeters,
    int OverlapMinutes,
    double OverlapPercent,
    decimal? Latitude,
    decimal? Longitude,
    bool Selected,
    bool CompetingPerformance,
    string Explanation);

internal sealed record DiagnosticCalendarWindow(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Kind,
    string Subject);
