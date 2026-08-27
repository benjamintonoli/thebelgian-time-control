using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Geocoding;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed partial class KnownWorkLocationAuditService(
    IPlenionReader plenionReader,
    IGeocodingService geocodingService,
    IDistanceCalculator distanceCalculator,
    ILogger<KnownWorkLocationAuditService> logger)
{
    private static readonly int[] Radii = [100, 250, 500];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<KnownWorkLocationAuditResult> RunAsync(
        KnownWorkLocationAuditRequest request,
        CancellationToken cancellationToken)
    {
        var days = request.DiagnosticsPaths.SelectMany(ReadDiagnostics).ToArray();
        var targets = SelectTargets(days, request.Targets, request.NegativeControls);
        var contextStops = targets.SelectMany(BuildContextStops).ToArray();
        var targetPostals = contextStops.Select(item => ExtractPostal(item.Stop.Address))
            .Where(item => item is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var locations = await plenionReader.GetCustomerLocationsAsync(cancellationToken);
        var workOrders = await plenionReader.GetWorkOrdersAsync(cancellationToken);
        var projects = await plenionReader.GetProjectsAsync(cancellationToken);
        var ordersByLocation = workOrders.Where(item => !string.IsNullOrWhiteSpace(item.DeliveryAddressExternalId))
            .GroupBy(item => item.DeliveryAddressExternalId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(item => item.Key, item => item.ToArray(), StringComparer.OrdinalIgnoreCase);
        var linkedLocations = locations.Where(item =>
                ordersByLocation.ContainsKey(item.ExternalId) &&
                !string.IsNullOrWhiteSpace(item.Address))
            .Select(item => new RawKnownWorkLocation(
                item,
                ordersByLocation[item.ExternalId],
                ExtractPostal(item.Address)))
            .ToArray();
        var localLocations = linkedLocations.Where(item =>
                item.PostalCode is not null && targetPostals.Contains(item.PostalCode))
            .ToArray();
        logger.LogInformation(
            "KnownWorkLocation-index: {All} operationeel gekoppelde LEVADR-locaties; " +
            "{Local} in {PostalCount} relevante postcodes worden in-memory gegeocodeerd.",
            linkedLocations.Length,
            localLocations.Length,
            targetPostals.Count);

        var geocodes = await GeocodeAsync(localLocations, cancellationToken);
        var index = localLocations.Select(item => ToKnownLocation(item, geocodes))
            .Where(item => item.Coordinate is not null)
            .ToArray();
        var projectById = projects.ToDictionary(item => item.ExternalId, StringComparer.OrdinalIgnoreCase);
        var assessments = targets.Select(target => Assess(target, index, workOrders, projects, projectById))
            .ToArray();
        var significant = assessments.Where(item => !item.IsNegativeControl).ToArray();
        var controls = assessments.Where(item => item.IsNegativeControl).ToArray();
        var radiusSummaries = Radii.Select(radius => Summarize(radius, assessments, controls)).ToArray();
        var garritAddress = LocationGeocodingCache.NormalizeAddress("Sint-Pieterskerklaan 58, Brugge");
        var garritEvidence = index.Where(item =>
                LocationGeocodingCache.NormalizeAddress(item.Address).Contains(
                    garritAddress,
                    StringComparison.Ordinal) ||
                LocationGeocodingCache.NormalizeAddress(item.Address).Contains(
                    "sint pieterskerklaan 58",
                    StringComparison.Ordinal))
            .Select(ToEvidenceDetail)
            .ToArray();
        var result = new KnownWorkLocationAuditResult(
            linkedLocations.Length,
            localLocations.Length,
            index.Length,
            significant,
            controls,
            radiusSummaries,
            garritEvidence,
            request.OutputPath,
            request.JsonPath);
        await WriteCsvAsync(request.OutputPath, significant, cancellationToken);
        await File.WriteAllTextAsync(
            request.JsonPath,
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);
        return result;
    }

    private static IReadOnlyList<DailyBoundaryDiagnosticCase> ReadDiagnostics(string path) =>
        JsonSerializer.Deserialize<DailyBoundaryDiagnosticCase[]>(
            File.ReadAllText(path),
            JsonOptions) ?? [];

    private static KnownBoundaryTarget[] SelectTargets(
        IReadOnlyList<DailyBoundaryDiagnosticCase> days,
        IReadOnlyList<KnownWorkTargetSpec> targetSpecs,
        IReadOnlyList<KnownWorkTargetSpec> controlSpecs)
    {
        var result = new List<KnownBoundaryTarget>();
        foreach (var (spec, control) in targetSpecs.Select(item => (item, false))
                     .Concat(controlSpecs.Select(item => (item, true))))
        {
            var day = days.Single(item => item.Date == spec.Date &&
                item.Technician.Equals(spec.Technician, StringComparison.OrdinalIgnoreCase));
            foreach (var side in new[] { DailyBoundarySide.First, DailyBoundarySide.Last })
            {
                var deviation = side == DailyBoundarySide.First
                    ? day.StartDeviationMinutes ?? day.StartPotentialDeviationMinutes ?? 0
                    : day.EndDeviationMinutes ?? day.EndPotentialDeviationMinutes ?? 0;
                if (!control && deviation <= 15)
                {
                    continue;
                }

                result.Add(new KnownBoundaryTarget(day, side, deviation, control));
            }
        }

        return result.ToArray();
    }

    private static KnownContextStop[] BuildContextStops(KnownBoundaryTarget target)
    {
        var boundary = target.Side == DailyBoundarySide.First ? target.Day.First : target.Day.Last;
        if (target.Side == DailyBoundarySide.First && boundary.Arrival is { } arrival)
        {
            return target.Day.Stops.Where(item => item.Departure <= arrival &&
                    arrival - item.Departure <= TimeSpan.FromMinutes(60))
                .OrderByDescending(item => item.Departure)
                .Take(2)
                .Select((item, index) => new KnownContextStop(target, item, index + 1))
                .ToArray();
        }

        if (target.Side == DailyBoundarySide.Last && boundary.Departure is { } departure)
        {
            return target.Day.Stops.Where(item => item.Arrival >= departure &&
                    item.Arrival - departure <= TimeSpan.FromMinutes(60))
                .OrderBy(item => item.Arrival)
                .Take(2)
                .Select((item, index) => new KnownContextStop(target, item, index + 1))
                .ToArray();
        }

        return [];
    }

    private async Task<Dictionary<string, GeocodingResult>> GeocodeAsync(
        IReadOnlyList<RawKnownWorkLocation> locations,
        CancellationToken cancellationToken)
    {
        var unique = locations.Select(item => item.Location.Address!)
            .DistinctBy(LocationGeocodingCache.NormalizeAddress, StringComparer.Ordinal)
            .ToArray();
        var concurrent = new ConcurrentDictionary<string, GeocodingResult>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            unique,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                var normalized = LocationGeocodingCache.NormalizeAddress(address);
                concurrent[normalized] = await geocodingService.GeocodeAsync(address, token);
            });
        return concurrent.ToDictionary(StringComparer.Ordinal);
    }

    private static KnownWorkLocation ToKnownLocation(
        RawKnownWorkLocation raw,
        Dictionary<string, GeocodingResult> geocodes)
    {
        var geocode = geocodes[LocationGeocodingCache.NormalizeAddress(raw.Location.Address!)];
        var coordinate = geocode.Status is GeocodingStatus.Geocoded or GeocodingStatus.LowConfidence
            ? geocode.Primary?.Coordinate
            : null;
        return new KnownWorkLocation(
            raw.Location.ExternalId,
            "LEVADR gekoppeld via BON (BSRTCD 61/62/63)",
            raw.Location.Name,
            raw.Location.Address!,
            raw.PostalCode,
            coordinate,
            geocode.Status.ToString(),
            raw.WorkOrders);
    }

    private BoundaryAssessment Assess(
        KnownBoundaryTarget target,
        IReadOnlyList<KnownWorkLocation> index,
        IReadOnlyList<PlenionWorkOrder> allOrders,
        IReadOnlyList<PlenionProject> allProjects,
        IReadOnlyDictionary<string, PlenionProject> projectById)
    {
        var performance = target.Side == DailyBoundarySide.First
            ? target.Day.Performances.First(item => item.IsFirstBoundary)
            : target.Day.Performances.Last(item => item.IsLastBoundary);
        var context = BoundaryContext(performance, allOrders, allProjects);
        var stops = BuildContextStops(target);
        var matches = new List<ContextLocationMatch>();
        foreach (var contextStop in stops.Where(item =>
                     item.Stop.Latitude is not null && item.Stop.Longitude is not null))
        {
            var stopCoordinate = new GeoCoordinate(
                (double)contextStop.Stop.Latitude!.Value,
                (double)contextStop.Stop.Longitude!.Value);
            foreach (var location in index)
            {
                var distance = distanceCalculator.DistanceMetres(stopCoordinate, location.Coordinate!.Value);
                if (distance > 500)
                {
                    continue;
                }

                var evidenceClass = Classify(location, context);
                var active = location.WorkOrders.Any(item => IsRelevant(item, target.Day.Date));
                matches.Add(new ContextLocationMatch(
                    contextStop,
                    location,
                    Math.Round(distance, 1),
                    evidenceClass,
                    active,
                    Confidence(evidenceClass, active, distance, location.WorkOrders.Count),
                    Relation(location, context, evidenceClass)));
            }
        }

        var byRadius = Radii.Select(radius =>
        {
            var best = matches.Where(item => item.DistanceMeters <= radius)
                .OrderBy(item => EvidenceRank(item.EvidenceClass))
                .ThenBy(item => item.DistanceMeters)
                .ThenByDescending(item => item.ActiveOnDate)
                .FirstOrDefault();
            var matchedContextStops = matches.Where(item => item.DistanceMeters <= radius)
                .Select(item => item.ContextStop.Stop.StopId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            var reduces = best?.EvidenceClass is WorkEvidenceClass.SameJobContext
                or WorkEvidenceClass.SameCustomerContext;
            var unexplained = target.RawDeviationMinutes;
            if (reduces && best is not null)
            {
                var contextTime = target.Side == DailyBoundarySide.First
                    ? best.ContextStop.Stop.Arrival
                    : best.ContextStop.Stop.Departure;
                var boundary = target.Side == DailyBoundarySide.First ? target.Day.First : target.Day.Last;
                unexplained = target.Side == DailyBoundarySide.First
                    ? Math.Max(0, (int)Math.Floor((contextTime - boundary.PlenionStart).TotalMinutes))
                    : Math.Max(0, (int)Math.Floor((boundary.PlenionEnd - contextTime).TotalMinutes));
            }

            return new RadiusBoundaryAssessment(radius, best, unexplained, matchedContextStops);
        }).ToArray();
        return new BoundaryAssessment(
            target.Day.Date,
            target.Day.Technician,
            target.Side.ToString().ToUpperInvariant(),
            target.RawDeviationMinutes,
            target.IsNegativeControl,
            performance.PerformanceId,
            performance.WorkOrderNumber,
            performance.ProjectNumber,
            stops.Select(item => item.Stop.Address ?? "(zonder adres)").ToArray(),
            byRadius);
    }

    private static BoundaryWorkContext BoundaryContext(
        DiagnosticPerformance performance,
        IReadOnlyList<PlenionWorkOrder> orders,
        IReadOnlyList<PlenionProject> projects)
    {
        var matchingOrders = orders.Where(item =>
                !string.IsNullOrWhiteSpace(performance.WorkOrderNumber) &&
                string.Equals(item.Number, performance.WorkOrderNumber, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var matchingProjects = projects.Where(item =>
                (!string.IsNullOrWhiteSpace(performance.ProjectNumber) &&
                 string.Equals(item.Number, performance.ProjectNumber, StringComparison.OrdinalIgnoreCase)) ||
                matchingOrders.Any(order => string.Equals(
                    order.ProjectExternalId,
                    item.ExternalId,
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return new BoundaryWorkContext(
            matchingOrders.Select(item => item.Number).Where(item => item is not null).Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            matchingOrders.Select(item => item.ProjectExternalId).Where(item => item is not null).Cast<string>()
                .Concat(matchingProjects.Select(item => item.ExternalId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            matchingOrders.Select(item => item.DeliveryAddressExternalId).Where(item => item is not null).Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            matchingOrders.Select(item => item.CustomerExternalId).Where(item => item is not null).Cast<string>()
                .Concat(matchingProjects.Select(item => item.CustomerExternalId).Where(item => item is not null).Cast<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static WorkEvidenceClass Classify(
        KnownWorkLocation location,
        BoundaryWorkContext context)
    {
        if (context.SiteIds.Contains(location.LocationExternalId) || location.WorkOrders.Any(item =>
                (!string.IsNullOrWhiteSpace(item.Number) && context.WorkOrderNumbers.Contains(item.Number)) ||
                (!string.IsNullOrWhiteSpace(item.ProjectExternalId) && context.ProjectIds.Contains(item.ProjectExternalId))))
        {
            return WorkEvidenceClass.SameJobContext;
        }

        return location.WorkOrders.Any(item =>
                   !string.IsNullOrWhiteSpace(item.CustomerExternalId) &&
                   context.CustomerIds.Contains(item.CustomerExternalId))
            ? WorkEvidenceClass.SameCustomerContext
            : WorkEvidenceClass.OtherKnownWorkLocation;
    }

    private static string Relation(
        KnownWorkLocation location,
        BoundaryWorkContext context,
        WorkEvidenceClass evidenceClass)
    {
        var orders = location.WorkOrders.Select(item => item.Number)
            .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().Take(5);
        var projects = location.WorkOrders.Select(item => item.ProjectExternalId)
            .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().Take(5);
        var customers = location.WorkOrders.Select(item => item.CustomerExternalId)
            .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().Take(5);
        return $"{evidenceClass}; LEVADR={location.LocationExternalId}; " +
               $"BON={string.Join('/', orders)}; IDPROJ={string.Join('/', projects)}; " +
               $"KLCLEUNIK={string.Join('/', customers)}";
    }

    private static bool IsRelevant(PlenionWorkOrder order, DateOnly date) =>
        (order.CreatedDate is null || order.CreatedDate <= date) &&
        (order.CompletionDate is null || order.CompletionDate >= date);

    private static int EvidenceRank(WorkEvidenceClass evidenceClass) => evidenceClass switch
    {
        WorkEvidenceClass.SameJobContext => 0,
        WorkEvidenceClass.SameCustomerContext => 1,
        WorkEvidenceClass.OtherKnownWorkLocation => 2,
        _ => 3,
    };

    private static string Confidence(
        WorkEvidenceClass evidenceClass,
        bool active,
        double distanceMeters,
        int operationalLinks) => evidenceClass switch
        {
            WorkEvidenceClass.SameJobContext when distanceMeters <= 100 && active => "High",
            WorkEvidenceClass.SameJobContext => "Medium",
            WorkEvidenceClass.SameCustomerContext when distanceMeters <= 100 && active => "High",
            WorkEvidenceClass.SameCustomerContext when distanceMeters <= 100 && operationalLinks >= 2 => "Medium",
            WorkEvidenceClass.SameCustomerContext => "Low",
            _ => "ContextOnly",
        };

    private static RadiusSummary Summarize(
        int radius,
        IReadOnlyList<BoundaryAssessment> assessments,
        IReadOnlyList<BoundaryAssessment> controls)
    {
        var results = assessments.Select(item => item.ByRadius.Single(value => value.RadiusMeters == radius)).ToArray();
        var controlResults = controls.Select(item => item.ByRadius.Single(value => value.RadiusMeters == radius)).ToArray();
        return new RadiusSummary(
            radius,
            results.Sum(item => item.MatchedContextStops),
            results.Count(item => item.Match is not null),
            results.Count(item => item.Match?.EvidenceClass == WorkEvidenceClass.SameJobContext),
            results.Count(item => item.Match?.EvidenceClass == WorkEvidenceClass.SameCustomerContext),
            results.Count(item => item.Match?.EvidenceClass == WorkEvidenceClass.OtherKnownWorkLocation),
            results.Count(item => item.Match is null),
            controlResults.Count(item => item.Match is not null),
            controlResults.Count(item => item.Match?.EvidenceClass is WorkEvidenceClass.SameJobContext or WorkEvidenceClass.SameCustomerContext));
    }

    private static KnownWorkEvidenceDetail ToEvidenceDetail(KnownWorkLocation location) =>
        new(
            location.LocationExternalId,
            location.Name,
            location.Address,
            location.GeocodingStatus,
            location.WorkOrders.Select(item => item.CustomerExternalId).Where(item => item is not null).Distinct().Cast<string>().ToArray(),
            location.WorkOrders.Select(item => item.ProjectExternalId).Where(item => item is not null).Distinct().Cast<string>().ToArray(),
            location.WorkOrders.Select(item => item.Number).Where(item => item is not null).Distinct().Cast<string>().ToArray());

    private static string? ExtractPostal(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var match = BelgianPostalCode().Match(address);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\b[1-9][0-9]{3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex BelgianPostalCode();

    private static async Task WriteCsvAsync(
        string path,
        IReadOnlyList<BoundaryAssessment> assessments,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new StringBuilder();
        builder.AppendLine("Datum,Technieker,Boundary,RuweAfwijking,Contextstop,AfstandKnownWorkLocation,PlenionRelatie,Bewijsklasse,Confidence,Contexttijd,OnverklaardeMinuten,Radius");
        foreach (var assessment in assessments)
        {
            var selected = assessment.ByRadius.Single(item => item.RadiusMeters == 500);
            var match = selected.Match;
            DateTimeOffset? contextTime = match is null ? null : assessment.Boundary == "FIRST"
                ? match.ContextStop.Stop.Arrival
                : match.ContextStop.Stop.Departure;
            builder.AppendLine(string.Join(',', new[]
            {
                Csv(assessment.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                Csv(assessment.Technician), Csv(assessment.Boundary),
                Csv(assessment.RawDeviationMinutes.ToString(CultureInfo.InvariantCulture)),
                Csv(match?.ContextStop.Stop.Address ?? string.Join(" | ", assessment.ContextStopAddresses)),
                Csv(match?.DistanceMeters.ToString("0.0", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(match?.Relation ?? "Geen betrouwbare Plenion-werklocatie binnen 500 m"),
                Csv(match?.EvidenceClass.ToString() ?? WorkEvidenceClass.NoWorkEvidence.ToString()),
                Csv(match?.Confidence ?? string.Empty),
                Csv(contextTime?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? string.Empty),
                Csv(selected.UnexplainedMinutes.ToString(CultureInfo.InvariantCulture)),
                Csv("500"),
            }));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';
}

internal enum WorkEvidenceClass { SameJobContext, SameCustomerContext, OtherKnownWorkLocation, NoWorkEvidence }

internal sealed record KnownWorkLocationAuditRequest(
    IReadOnlyList<string> DiagnosticsPaths,
    IReadOnlyList<KnownWorkTargetSpec> Targets,
    IReadOnlyList<KnownWorkTargetSpec> NegativeControls,
    string OutputPath,
    string JsonPath);

internal sealed record KnownWorkTargetSpec(DateOnly Date, string Technician);
internal sealed record KnownBoundaryTarget(DailyBoundaryDiagnosticCase Day, DailyBoundarySide Side, int RawDeviationMinutes, bool IsNegativeControl);
internal sealed record KnownContextStop(KnownBoundaryTarget Target, DiagnosticStop Stop, int Sequence);
internal sealed record RawKnownWorkLocation(CustomerLocation Location, IReadOnlyList<PlenionWorkOrder> WorkOrders, string? PostalCode);
internal sealed record KnownWorkLocation(string LocationExternalId, string Source, string Name, string Address, string? PostalCode, GeoCoordinate? Coordinate, string GeocodingStatus, IReadOnlyList<PlenionWorkOrder> WorkOrders);
internal sealed record BoundaryWorkContext(IReadOnlySet<string> WorkOrderNumbers, IReadOnlySet<string> ProjectIds, IReadOnlySet<string> SiteIds, IReadOnlySet<string> CustomerIds);
internal sealed record ContextLocationMatch(KnownContextStop ContextStop, KnownWorkLocation Location, double DistanceMeters, WorkEvidenceClass EvidenceClass, bool ActiveOnDate, string Confidence, string Relation);
internal sealed record RadiusBoundaryAssessment(int RadiusMeters, ContextLocationMatch? Match, int UnexplainedMinutes, int MatchedContextStops);
internal sealed record BoundaryAssessment(DateOnly Date, string Technician, string Boundary, int RawDeviationMinutes, bool IsNegativeControl, long PerformanceId, string? WorkOrderNumber, string? ProjectNumber, IReadOnlyList<string> ContextStopAddresses, IReadOnlyList<RadiusBoundaryAssessment> ByRadius);
internal sealed record RadiusSummary(int RadiusMeters, int KnownContextStops, int BoundariesWithKnownLocation, int SameJobContext, int SameCustomerContext, int OtherKnownWorkLocation, int NoWorkEvidence, int NegativeControlMatches, int NegativeControlRelatedMatches);
internal sealed record KnownWorkEvidenceDetail(string LocationExternalId, string Name, string Address, string GeocodingStatus, IReadOnlyList<string> CustomerIds, IReadOnlyList<string> ProjectIds, IReadOnlyList<string> WorkOrderNumbers);
internal sealed record KnownWorkLocationAuditResult(int LinkedPlenionLocations, int LocallyGeocodedCandidates, int UsableIndexedLocations, IReadOnlyList<BoundaryAssessment> SignificantBoundaries, IReadOnlyList<BoundaryAssessment> NegativeControls, IReadOnlyList<RadiusSummary> RadiusSummaries, IReadOnlyList<KnownWorkEvidenceDetail> GarritSintPieterskerklaanEvidence, string OutputPath, string JsonPath);
