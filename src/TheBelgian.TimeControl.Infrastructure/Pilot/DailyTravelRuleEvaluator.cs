using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal enum DailyTravelRuleStatus
{
    None,
    ValidTravelFromTheBelgian,
    ValidTravelToTheBelgian,
    TravelNeedsReview,
}

internal sealed record DailyTravelRuleEvaluation(
    DailyBoundarySide Side,
    DailyTravelRuleStatus Status,
    int ToleranceMinutes,
    long? TravelPerformanceId,
    string? CompanyStopId,
    DateTimeOffset? CompanyArrival,
    DateTimeOffset? CompanyDeparture,
    DateTimeOffset? CustomerArrival,
    DateTimeOffset? CustomerDeparture,
    double? StartDifferenceMinutes,
    double? EndDifferenceMinutes,
    string Assessment)
{
    public bool IsValid => Status is DailyTravelRuleStatus.ValidTravelFromTheBelgian or
        DailyTravelRuleStatus.ValidTravelToTheBelgian;

    public bool NeedsReview => Status == DailyTravelRuleStatus.TravelNeedsReview;
}

internal static class DailyTravelRuleEvaluator
{
    // Verified Plenion company/site identity: LEVADR 7012, §C | BB | The Belgian NV.
    internal const string CompanyLocationExternalId = "7012";
    internal const string CompanyName = "The Belgian NV";
    internal const int ToleranceMinutes = 5;
    // Candidate population stays stable; this is not an acceptance tolerance.
    internal const int CandidateWindowMinutes = 15;
    internal const double CompanySiteRadiusMeters = 100;
    internal static readonly GeoCoordinate CompanyCoordinate = new(50.9844971, 4.3007144);

    public static DailyTravelRuleEvaluation EvaluateFirstBoundary(
        IReadOnlyList<NormalizedPilotPerformance> performances,
        IReadOnlyList<PilotStop> stops,
        NormalizedPilotPerformance firstCustomerJob,
        DateTimeOffset? exactCustomerArrival,
        IDistanceCalculator distanceCalculator)
    {
        var travel = TravelPerformances(performances)
            .Where(item => item.EndDateTime <= firstCustomerJob.StartDateTime.AddMinutes(CandidateWindowMinutes))
            .OrderByDescending(item => item.EndDateTime)
            .FirstOrDefault();
        if (travel is null)
        {
            return None(DailyBoundarySide.First, "Geen expliciete Plenion-verplaatsingsprestatie vóór de eerste klant.");
        }

        if (exactCustomerArrival is null)
        {
            return Review(DailyBoundarySide.First, travel, "Geen betrouwbare exacte-siteaankomst voor de eerste klant.");
        }

        var companyStop = CompanyStops(stops, distanceCalculator)
            .Where(item => item.Stop.Departure <= exactCustomerArrival)
            .OrderByDescending(item => item.Stop.Departure)
            .FirstOrDefault();
        if (companyStop == default)
        {
            return Review(
                DailyBoundarySide.First,
                travel,
                "Geen betrouwbare passage bij The Belgian vóór de eerste klant; ochtendverplaatsing niet automatisch geldig.");
        }

        var tolerance = TimeSpan.FromMinutes(ToleranceMinutes);
        var temporallyOnRoute = travel.StartDateTime >= companyStop.Stop.Departure - tolerance &&
                                travel.EndDateTime <= exactCustomerArrival.Value + tolerance;
        if (!temporallyOnRoute)
        {
            return Review(
                DailyBoundarySide.First,
                travel,
                "De ochtendverplaatsing sluit niet binnen de tolerantie aan op vertrek The Belgian, klantstart en klantaankomst.",
                companyStop.Stop,
                exactCustomerArrival);
        }

        if (HasInterveningStop(stops, companyStop.Stop.Departure, exactCustomerArrival.Value, companyStop.Stop.StopId))
        {
            return Review(
                DailyBoundarySide.First,
                travel,
                "Er ligt een andere betekenisvolle stop tussen The Belgian en de eerste klant.",
                companyStop.Stop,
                exactCustomerArrival);
        }

        return new(
            DailyBoundarySide.First,
            DailyTravelRuleStatus.ValidTravelFromTheBelgian,
            ToleranceMinutes,
            travel.ExternalId,
            companyStop.Stop.StopId,
            companyStop.Stop.Arrival,
            companyStop.Stop.Departure,
            exactCustomerArrival,
            null,
            DifferenceMinutes(travel.StartDateTime, companyStop.Stop.Departure),
            DifferenceMinutes(travel.EndDateTime, exactCustomerArrival.Value),
            $"HFDTAAK {travel.MainTaskExternalId} ({VerifiedMainTaskSemantics.TravelDescription}) ligt logisch op de directe route " +
            $"van LEVADR {CompanyLocationExternalId} naar de eerste klant; maximale vroege/late overschrijding {ToleranceMinutes} min.");
    }

    public static DailyTravelRuleEvaluation EvaluateLastBoundary(
        IReadOnlyList<NormalizedPilotPerformance> performances,
        IReadOnlyList<PilotStop> stops,
        NormalizedPilotPerformance lastCustomerJob,
        DateTimeOffset? exactCustomerDeparture,
        IDistanceCalculator distanceCalculator)
    {
        var travel = TravelPerformances(performances)
            .Where(item => item.StartDateTime >= lastCustomerJob.EndDateTime.AddMinutes(-CandidateWindowMinutes))
            .OrderBy(item => item.StartDateTime)
            .FirstOrDefault();
        if (travel is null)
        {
            return None(DailyBoundarySide.Last, "Geen expliciete Plenion-verplaatsingsprestatie na de laatste klant.");
        }

        if (exactCustomerDeparture is null)
        {
            return Review(DailyBoundarySide.Last, travel, "Geen betrouwbaar exacte-sitevertrek voor de laatste klant.");
        }

        var companyStop = CompanyStops(stops, distanceCalculator)
            .Where(item => item.Stop.Arrival >= exactCustomerDeparture.Value)
            .OrderBy(item => item.Stop.Arrival)
            .FirstOrDefault();
        if (companyStop == default)
        {
            return Review(
                DailyBoundarySide.Last,
                travel,
                "Geen betrouwbare passage bij The Belgian na de laatste klant; avondverplaatsing niet automatisch geldig.");
        }

        var tolerance = TimeSpan.FromMinutes(ToleranceMinutes);
        var temporallyOnRoute = travel.StartDateTime >= exactCustomerDeparture.Value - tolerance &&
                                travel.EndDateTime <= companyStop.Stop.Arrival + tolerance;
        if (!temporallyOnRoute)
        {
            return Review(
                DailyBoundarySide.Last,
                travel,
                "De avondverplaatsing sluit niet binnen de tolerantie aan op klantvertrek en aankomst The Belgian.",
                companyStop.Stop,
                customerDeparture: exactCustomerDeparture);
        }

        if (HasInterveningStop(stops, exactCustomerDeparture.Value, companyStop.Stop.Arrival, companyStop.Stop.StopId))
        {
            return Review(
                DailyBoundarySide.Last,
                travel,
                "Er ligt een andere betekenisvolle stop tussen de laatste klant en The Belgian.",
                companyStop.Stop,
                customerDeparture: exactCustomerDeparture);
        }

        return new(
            DailyBoundarySide.Last,
            DailyTravelRuleStatus.ValidTravelToTheBelgian,
            ToleranceMinutes,
            travel.ExternalId,
            companyStop.Stop.StopId,
            companyStop.Stop.Arrival,
            companyStop.Stop.Departure,
            null,
            exactCustomerDeparture,
            DifferenceMinutes(travel.StartDateTime, exactCustomerDeparture.Value),
            DifferenceMinutes(travel.EndDateTime, companyStop.Stop.Arrival),
            $"HFDTAAK {travel.MainTaskExternalId} ({VerifiedMainTaskSemantics.TravelDescription}) ligt logisch op de directe route " +
            $"van de laatste klant naar LEVADR {CompanyLocationExternalId}; maximale vroege/late overschrijding {ToleranceMinutes} min.");
    }

    private static IEnumerable<NormalizedPilotPerformance> TravelPerformances(
        IReadOnlyList<NormalizedPilotPerformance> performances) =>
        performances.Where(item =>
            PerformanceActivityClassifier.Classify(item, string.Empty, null).ActivityType ==
            PerformanceActivityType.Travel);

    private static IEnumerable<(PilotStop Stop, double Distance)> CompanyStops(
        IReadOnlyList<PilotStop> stops,
        IDistanceCalculator distanceCalculator) =>
        stops.Where(item =>
                item.LocationContinuity &&
                item.DurationMinutes >= 3 &&
                item.Latitude is not null &&
                item.Longitude is not null)
            .Select(item => (
                Stop: item,
                Distance: distanceCalculator.DistanceMetres(
                    CompanyCoordinate,
                    new GeoCoordinate((double)item.Latitude!.Value, (double)item.Longitude!.Value))))
            .Where(item => item.Distance <= CompanySiteRadiusMeters);

    private static bool HasInterveningStop(
        IReadOnlyList<PilotStop> stops,
        DateTimeOffset routeStart,
        DateTimeOffset routeEnd,
        string companyStopId) =>
        stops.Any(item =>
            item.StopId != companyStopId &&
            item.Arrival > routeStart &&
            item.Departure < routeEnd &&
            item.DurationMinutes >= 3);

    private static double DifferenceMinutes(DateTimeOffset value, DateTimeOffset reference) =>
        Math.Round((value - reference).TotalMinutes, 2);

    private static DailyTravelRuleEvaluation None(DailyBoundarySide side, string assessment) =>
        new(side, DailyTravelRuleStatus.None, ToleranceMinutes, null, null, null, null, null, null, null, null, assessment);

    private static DailyTravelRuleEvaluation Review(
        DailyBoundarySide side,
        NormalizedPilotPerformance travel,
        string assessment,
        PilotStop? companyStop = null,
        DateTimeOffset? customerArrival = null,
        DateTimeOffset? customerDeparture = null) =>
        new(
            side,
            DailyTravelRuleStatus.TravelNeedsReview,
            ToleranceMinutes,
            travel.ExternalId,
            companyStop?.StopId,
            companyStop?.Arrival,
            companyStop?.Departure,
            customerArrival,
            customerDeparture,
            side == DailyBoundarySide.First && companyStop is not null
                ? DifferenceMinutes(travel.StartDateTime, companyStop.Departure)
                : side == DailyBoundarySide.Last && customerDeparture is not null
                    ? DifferenceMinutes(travel.StartDateTime, customerDeparture.Value)
                    : null,
            side == DailyBoundarySide.First && customerArrival is not null
                ? DifferenceMinutes(travel.EndDateTime, customerArrival.Value)
                : side == DailyBoundarySide.Last && companyStop is not null
                    ? DifferenceMinutes(travel.EndDateTime, companyStop.Arrival)
                    : null,
            assessment);
}
