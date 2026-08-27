using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Configuration;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Geocoding;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed class HoursAuditService(
    PilotPlenionReader pilotPlenionReader,
    PilotPowerfleetReader powerfleetReader,
    IGeocodingService geocodingService,
    IDistanceCalculator distanceCalculator,
    LocationMatchingOptions locationMatchingOptions,
    IOptions<AdaptiveLocationMatchingOptions> adaptiveOptions,
    ILogger<HoursAuditService> logger)
{
    private readonly AdaptiveLocationMatchingOptions _adaptiveOptions = adaptiveOptions.Value;

    public async Task<HoursAuditResult> RunAsync(
        HoursAuditRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ThroughDate < request.FromDate)
        {
            throw new ArgumentException("Einddatum ligt vóór begindatum.", nameof(request));
        }

        _adaptiveOptions.Validate();
        var technicians = await pilotPlenionReader.ReadTechniciansWithPerformancesAsync(
            request.FromDate,
            request.ThroughDate,
            cancellationToken);
        var plenionScans = new List<(Technician Technician, PlenionPilotReadResult Scan)>();
        var warnings = new List<string>();
        var examined = 0;
        foreach (var technician in technicians)
        {
            var scan = await pilotPlenionReader.ReadAsync(
                new ReadOnlyPilotRequest(
                    technician.ExternalId,
                    request.FromDate,
                    request.ThroughDate,
                    MaximumPerformances: 500),
                cancellationToken);
            plenionScans.Add((technician, scan));
            examined += scan.NormalizedRecords.Count;
            warnings.AddRange(scan.Issues.Select(issue =>
                $"Plenion {technician.Name}: {issue.Category}: {issue.Message}"));
        }

        var workdays = plenionScans
            .SelectMany(item => item.Scan.NormalizedRecords)
            .Select(item => item.Date)
            .Distinct()
            .OrderBy(item => item)
            .ToArray();

        var fleetTrips = new List<NormalizedPilotTrip>();
        foreach (var date in workdays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var daily = await powerfleetReader.ReadAsync(
                new ReadOnlyPilotRequest(
                    "hours-audit",
                    date,
                    date,
                    DriverOnlyLinking: true,
                    MaximumTrips: 1000),
                cancellationToken);
            fleetTrips.AddRange(daily.NormalizedRecords);
            warnings.AddRange(daily.Issues.Select(issue =>
                $"PowerFleet {date:yyyy-MM-dd}: {issue.Category}: {issue.Message}"));
        }

        var distinctFleetTrips = fleetTrips
            .DistinctBy(item => item.ExternalId, StringComparer.Ordinal)
            .ToArray();
        var geocodingByAddress = new Dictionary<string, GeocodingResult>(StringComparer.Ordinal);
        var rows = new List<HoursAuditRow>();
        var reliable = 0;
        var ambiguous = 0;
        var unresolved = 0;
        var nonLocationBound = 0;
        var missingMappings = new List<string>();

        foreach (var (technician, plenion) in plenionScans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var driverId = BroaderValidationPilotService.DiscoverDriverId(
                distinctFleetTrips,
                technician);
            if (string.IsNullOrWhiteSpace(driverId))
            {
                missingMappings.Add(technician.Name);
                foreach (var performance in plenion.NormalizedRecords)
                {
                    var classification = PerformanceActivityClassifier.Classify(
                        performance,
                        technician.Name,
                        null);
                    if (classification.RequiresGeographicMatch)
                    {
                        unresolved++;
                    }
                    else
                    {
                        nonLocationBound++;
                    }
                }

                continue;
            }

            var technicianTrips = distinctFleetTrips
                .Where(item => string.Equals(
                    item.DriverId,
                    driverId,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.StartDateTime)
                .ToArray();
            var stopIssues = new List<PilotIssue>();
            var stops = PilotLocationMatcher.ReconstructStops(technicianTrips, stopIssues);
            warnings.AddRange(stopIssues.Select(issue =>
                $"PowerFleet {technician.Name}: {issue.Category}: {issue.Message}"));
            var performancesByDay = plenion.NormalizedRecords
                .GroupBy(item => item.Date)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var mergedStopsByDay = stops
                .GroupBy(item => item.Date)
                .ToDictionary(
                    group => group.Key,
                    group => MergedStopBuilder.Merge(
                        group.ToArray(),
                        _adaptiveOptions,
                        distanceCalculator));

            foreach (var performance in plenion.NormalizedRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolution = await ResolveInMemoryAsync(
                    performance,
                    stops,
                    geocodingByAddress,
                    cancellationToken);
                var classification = PerformanceActivityClassifier.Classify(
                    performance,
                    technician.Name,
                    resolution);
                if (!classification.RequiresGeographicMatch)
                {
                    nonLocationBound++;
                    continue;
                }

                mergedStopsByDay.TryGetValue(performance.Date, out var dayStops);
                var match = PrecisionPreservingHybridMatcher.Match(
                    performance,
                    technician.Name,
                    resolution,
                    dayStops ?? [],
                    performancesByDay[performance.Date],
                    new Dictionary<string, HistoricalLocationCluster>(StringComparer.Ordinal),
                    _adaptiveOptions,
                    distanceCalculator);
                if (match.Decision == AdaptiveMatchDecision.Ambiguous)
                {
                    ambiguous++;
                    continue;
                }

                if (match.Decision == AdaptiveMatchDecision.Unresolved || match.Selected is null)
                {
                    unresolved++;
                    continue;
                }

                reliable++;
                var selected = match.Selected;
                var startDeviation = PositiveWholeMinutes(
                    selected.Stop.Arrival - performance.StartDateTime);
                var endDeviation = PositiveWholeMinutes(
                    performance.EndDateTime - selected.Stop.Departure);
                if (startDeviation == 0 && endDeviation == 0)
                {
                    continue;
                }

                rows.Add(new HoursAuditRow(
                    performance.Date,
                    technician.Name,
                    performance.ExternalId,
                    JoinNonEmpty(performance.ProjectNumber, performance.WorkOrderNumber),
                    performance.CustomerOrSiteName ?? performance.ProjectName ?? string.Empty,
                    resolution.OriginalAddress,
                    performance.StartDateTime,
                    selected.Stop.Arrival,
                    startDeviation,
                    performance.EndDateTime,
                    selected.Stop.Departure,
                    endDeviation,
                    startDeviation + endDeviation,
                    match.Decision.ToString(),
                    selected.TotalScore,
                    selected.DistanceMeters,
                    selected.OverlapMinutes,
                    match.Assessment));
            }
        }

        var orderedRows = rows
            .OrderByDescending(item => item.TotalDeviationMinutes)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.Technician, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await WriteCsvAsync(request.OutputPath, orderedRows, cancellationToken);
        logger.LogInformation(
            "Hours audit: {Examined} onderzocht, {Reliable} betrouwbaar, {Deviations} afwijkend.",
            examined,
            reliable,
            orderedRows.Length);
        return new HoursAuditResult(
            examined,
            reliable,
            orderedRows.Length,
            ambiguous,
            unresolved,
            nonLocationBound,
            orderedRows.Sum(item => item.TotalDeviationMinutes),
            request.OutputPath,
            orderedRows,
            missingMappings,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private async Task<PilotLocationResolution> ResolveInMemoryAsync(
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
                    stop.Date == performance.Date &&
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

    internal static int PositiveWholeMinutes(TimeSpan difference) =>
        Math.Max(0, (int)Math.Round(
            difference.TotalMinutes,
            MidpointRounding.AwayFromZero));

    private static bool IsHome(PilotStop stop) =>
        (stop.Area?.Contains("Huisadres", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (stop.AreaGroup?.Contains("Huisadres", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string JoinNonEmpty(params string?[] values) =>
        string.Join(" / ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static async Task WriteCsvAsync(
        string path,
        IReadOnlyList<HoursAuditRow> rows,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        builder.AppendLine("Datum,Technieker,PerformanceId,Project/Bon,Klant,Adres,PlenionStart,GPSAankomst,StartAfwijkingMin,PlenionEinde,GPSVertrek,EindAfwijkingMin,TotaleAfwijkingMin,MatcherStatus,Score,AfstandMeters,OverlapMinuten,Reden");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(row.Technician),
                Csv(row.PerformanceId.ToString(CultureInfo.InvariantCulture)),
                Csv(row.ProjectOrWorkOrder),
                Csv(row.Customer),
                Csv(row.Address),
                Csv(row.PlenionStart.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                Csv(row.GpsArrival.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                Csv(row.StartDeviationMinutes.ToString(CultureInfo.InvariantCulture)),
                Csv(row.PlenionEnd.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                Csv(row.GpsDeparture.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)),
                Csv(row.EndDeviationMinutes.ToString(CultureInfo.InvariantCulture)),
                Csv(row.TotalDeviationMinutes.ToString(CultureInfo.InvariantCulture)),
                Csv(row.MatcherStatus),
                Csv(row.Score.ToString("0.0", CultureInfo.InvariantCulture)),
                Csv(row.DistanceMeters?.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(row.OverlapMinutes.ToString(CultureInfo.InvariantCulture)),
                Csv(row.Reason),
            }));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    private static string Csv(string value) =>
        '"' + value.Replace("\"", "\"\"") + '"';
}

internal sealed record HoursAuditRequest(
    DateOnly FromDate,
    DateOnly ThroughDate,
    string OutputPath);

internal sealed record HoursAuditResult(
    int ExaminedPerformances,
    int ReliableMatches,
    int DeviatingPerformances,
    int Ambiguous,
    int Unresolved,
    int NonLocationBound,
    int TotalDeviationMinutes,
    string OutputPath,
    IReadOnlyList<HoursAuditRow> Rows,
    IReadOnlyList<string> MissingMappings,
    IReadOnlyList<string> Warnings);

internal sealed record HoursAuditRow(
    DateOnly Date,
    string Technician,
    long PerformanceId,
    string ProjectOrWorkOrder,
    string Customer,
    string Address,
    DateTimeOffset PlenionStart,
    DateTimeOffset GpsArrival,
    int StartDeviationMinutes,
    DateTimeOffset PlenionEnd,
    DateTimeOffset GpsDeparture,
    int EndDeviationMinutes,
    int TotalDeviationMinutes,
    string MatcherStatus,
    double Score,
    double? DistanceMeters,
    int OverlapMinutes,
    string Reason);
