using System.Globalization;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Temporary GPS excursion during a planned job (leave site → return).
/// Site presence ≠ payable work continuity.
/// </summary>
public sealed record MissingTechnicianExcursion(
    DateTimeOffset DepartSite,
    DateTimeOffset ArriveAway,
    DateTimeOffset? DepartAway,
    DateTimeOffset ReturnSite,
    string? AwayAddress,
    decimal? AwayLatitude,
    decimal? AwayLongitude,
    MissingTechnicianExcursionClass Classification,
    int DurationMinutes);

public enum MissingTechnicianExcursionClass
{
    WorkMaterialPickup = 0,
    WorkHqVisit = 1,
    WorkSupplierVisit = 2,
    WorkTravel = 3,
    MealBreak = 4,
    OtherJob = 5,
    PersonalOrNonwork = 6,
    Unknown = 7,
}

/// <summary>
/// Payable work continuity across temporary GPS departures from the customer site.
/// </summary>
public enum MissingTechnicianWorkContinuity
{
    ContinuousSupported = 0,
    ContinuousPlausible = 1,
    SplitRequired = 2,
    Unknown = 3,
}

public enum MissingTechnicianOperationalSiteRelation
{
    SameOperationalSiteProven = 0,
    SameOperationalSiteLikely = 1,
    Ambiguous = 2,
    DifferentSite = 3,
}

public sealed record MissingTechnicianContinuityAssessment(
    MissingTechnicianWorkContinuity Continuity,
    MissingTechnicianOperationalSiteRelation OperationalSite,
    IReadOnlyList<MissingTechnicianExcursion> Excursions,
    bool AllocationReview,
    string ContinuityNl,
    string ExcursionSummaryNl,
    string PauseNoteNl);

/// <summary>
/// Pure classifiers: temporary excursions vs payable work continuity (V3).
/// Does not change payroll pause formulas — lunch GPS is context only.
/// </summary>
public static class MissingTechnicianWorkdayContinuity
{
    /// <summary>Fallback HQ geofence when KnownLocations is not injected (The Belgian — Meise).</summary>
    public const double HqLatitude = 50.984487;
    public const double HqLongitude = 4.300723;
    public const double HqRadiusMeters = 150d;

    public const int MealBreakMaxMinutes = 90;
    public const int ShortAwayStopMaxMinutes = 45;

    public static MissingTechnicianContinuityAssessment Assess(
        DateTimeOffset? siteArrival,
        DateTimeOffset? siteDeparture,
        StandbyGpsDayEvidence? gps,
        StandbyGpsDayEvidence? peerGps,
        JobLocationEvidence? plannedLocation,
        NormalizedPerformanceEntry? peer,
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        PlannedWorkGroup group,
        KnownLocationCatalog? knownLocations = null,
        IDistanceCalculator? distanceCalculator = null)
    {
        distanceCalculator ??= new HaversineDistanceCalculator();
        knownLocations ??= KnownLocationCatalog.Empty;

        var sitePostcode = MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(plannedLocation?.Postcode)
            ?? ExtractPostcodeFromArrival(gps, siteArrival);
        var siteStreetHint = ExtractStreetHint(PreferArrivalAddress(gps, siteArrival));

        var excursions = siteArrival is null || siteDeparture is null || gps is null
            ? Array.Empty<MissingTechnicianExcursion>()
            : DetectExcursions(
                siteArrival.Value,
                siteDeparture.Value,
                gps,
                sitePostcode,
                dayRows,
                group,
                knownLocations,
                distanceCalculator);

        var operational = ClassifyOperationalSite(
            sitePostcode,
            siteStreetHint,
            plannedLocation,
            PreferArrivalAddress(gps, siteArrival),
            peerGps,
            peer,
            knownLocations,
            distanceCalculator);

        var continuity = ClassifyContinuity(excursions, operational, siteArrival, siteDeparture);
        var allocationReview = RequiresAllocationReview(excursions, dayRows);
        var pauseNote = BuildPauseNote(excursions);

        return new MissingTechnicianContinuityAssessment(
            continuity,
            operational,
            excursions,
            allocationReview,
            FormatContinuityNl(continuity, excursions),
            FormatExcursionSummaryNl(excursions),
            pauseNote);
    }

    public static IReadOnlyList<MissingTechnicianExcursion> DetectExcursions(
        DateTimeOffset siteArrival,
        DateTimeOffset siteDeparture,
        StandbyGpsDayEvidence gps,
        string? sitePostcode,
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        PlannedWorkGroup group,
        KnownLocationCatalog? knownLocations = null,
        IDistanceCalculator? distanceCalculator = null)
    {
        distanceCalculator ??= new HaversineDistanceCalculator();
        knownLocations ??= KnownLocationCatalog.Empty;

        var trips = gps.Trips
            .Where(t => t.End > siteArrival && t.Start < siteDeparture)
            .OrderBy(t => t.Start)
            .ToList();
        if (trips.Count == 0)
        {
            return [];
        }

        var result = new List<MissingTechnicianExcursion>();
        for (var i = 0; i < trips.Count; i++)
        {
            var trip = trips[i];
            if (IsAtSiteCluster(trip.EndAddress, trip.EndLatitude, trip.EndLongitude, sitePostcode, distanceCalculator))
            {
                continue;
            }

            // Departure from site: trip starts at/near site and ends away.
            if (!IsAtSiteCluster(trip.StartAddress, trip.StartLatitude, trip.StartLongitude, sitePostcode, distanceCalculator)
                && !string.IsNullOrWhiteSpace(sitePostcode)
                && MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(trip.StartAddress) is { } startPc
                && !string.Equals(startPc, sitePostcode, StringComparison.Ordinal))
            {
                continue;
            }

            var awayArrive = trip.End;
            var awayAddr = trip.EndAddress;
            var awayLat = trip.EndLatitude;
            var awayLon = trip.EndLongitude;

            // Find return to site cluster.
            StandbyGpsTripEvidence? returnTrip = null;
            for (var j = i + 1; j < trips.Count; j++)
            {
                if (IsAtSiteCluster(trips[j].EndAddress, trips[j].EndLatitude, trips[j].EndLongitude, sitePostcode, distanceCalculator))
                {
                    returnTrip = trips[j];
                    break;
                }
            }

            if (returnTrip is null)
            {
                continue;
            }

            var departAway = trips
                .Where(t => t.Start >= awayArrive && t.Start <= returnTrip.Start)
                .OrderBy(t => t.Start)
                .Select(t => (DateTimeOffset?)t.Start)
                .FirstOrDefault();

            var classification = ClassifyExcursion(
                trip.Start,
                awayArrive,
                departAway,
                returnTrip.End,
                awayAddr,
                awayLat,
                awayLon,
                dayRows,
                group,
                knownLocations,
                distanceCalculator);

            var duration = (int)Math.Round((returnTrip.End - trip.Start).TotalMinutes, MidpointRounding.AwayFromZero);
            result.Add(new MissingTechnicianExcursion(
                trip.Start,
                awayArrive,
                departAway,
                returnTrip.End,
                awayAddr,
                awayLat,
                awayLon,
                classification,
                duration));

            // Skip past the return trip index.
            i = trips.IndexOf(returnTrip);
        }

        return result;
    }

    public static MissingTechnicianExcursionClass ClassifyExcursion(
        DateTimeOffset departSite,
        DateTimeOffset arriveAway,
        DateTimeOffset? departAway,
        DateTimeOffset returnSite,
        string? awayAddress,
        decimal? awayLat,
        decimal? awayLon,
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        PlannedWorkGroup group,
        KnownLocationCatalog? knownLocations = null,
        IDistanceCalculator? distanceCalculator = null)
    {
        distanceCalculator ??= new HaversineDistanceCalculator();
        knownLocations ??= KnownLocationCatalog.Empty;

        if (HasOtherJobDuring(dayRows, group, departSite, returnSite))
        {
            return MissingTechnicianExcursionClass.OtherJob;
        }

        if (IsTheBelgianHq(awayAddress, awayLat, awayLon, knownLocations, distanceCalculator))
        {
            return MissingTechnicianExcursionClass.WorkHqVisit;
        }

        var totalMinutes = (returnSite - departSite).TotalMinutes;
        var awayStopMinutes = departAway is null
            ? 0
            : Math.Max(0, (departAway.Value - arriveAway).TotalMinutes);
        var midday = arriveAway.Hour is >= 11 and <= 14;

        if (midday
            && totalMinutes <= MealBreakMaxMinutes
            && awayStopMinutes <= ShortAwayStopMaxMinutes
            && !LooksLikeCustomerSite(awayAddress))
        {
            return MissingTechnicianExcursionClass.MealBreak;
        }

        var awayPc = MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(awayAddress);
        if (awayPc is not null && LooksLikeCustomerSite(awayAddress) && totalMinutes > MealBreakMaxMinutes)
        {
            return MissingTechnicianExcursionClass.Unknown;
        }

        if (totalMinutes <= MealBreakMaxMinutes && midday)
        {
            return MissingTechnicianExcursionClass.MealBreak;
        }

        return MissingTechnicianExcursionClass.Unknown;
    }

    public static MissingTechnicianWorkContinuity ClassifyContinuity(
        IReadOnlyList<MissingTechnicianExcursion> excursions,
        MissingTechnicianOperationalSiteRelation operationalSite,
        DateTimeOffset? siteArrival,
        DateTimeOffset? siteDeparture)
    {
        if (siteArrival is null || siteDeparture is null || siteDeparture <= siteArrival)
        {
            return MissingTechnicianWorkContinuity.Unknown;
        }

        if (excursions.Count == 0)
        {
            return operationalSite is MissingTechnicianOperationalSiteRelation.DifferentSite
                ? MissingTechnicianWorkContinuity.Unknown
                : MissingTechnicianWorkContinuity.ContinuousSupported;
        }

        if (excursions.Any(e =>
                e.Classification is MissingTechnicianExcursionClass.OtherJob
                    or MissingTechnicianExcursionClass.PersonalOrNonwork))
        {
            return MissingTechnicianWorkContinuity.SplitRequired;
        }

        if (excursions.All(e =>
                e.Classification is MissingTechnicianExcursionClass.WorkHqVisit
                    or MissingTechnicianExcursionClass.WorkMaterialPickup
                    or MissingTechnicianExcursionClass.WorkSupplierVisit
                    or MissingTechnicianExcursionClass.WorkTravel))
        {
            return MissingTechnicianWorkContinuity.ContinuousSupported;
        }

        if (excursions.All(e =>
                e.Classification is MissingTechnicianExcursionClass.MealBreak
                    or MissingTechnicianExcursionClass.WorkHqVisit
                    or MissingTechnicianExcursionClass.WorkMaterialPickup
                    or MissingTechnicianExcursionClass.WorkTravel
                    or MissingTechnicianExcursionClass.WorkSupplierVisit))
        {
            return MissingTechnicianWorkContinuity.ContinuousPlausible;
        }

        // Unknown destination but returned to same job cluster → plausible continuity, not auto-split.
        if (excursions.All(e => e.Classification is MissingTechnicianExcursionClass.Unknown
                or MissingTechnicianExcursionClass.MealBreak
                or MissingTechnicianExcursionClass.WorkHqVisit
                or MissingTechnicianExcursionClass.WorkMaterialPickup
                or MissingTechnicianExcursionClass.WorkTravel))
        {
            return MissingTechnicianWorkContinuity.ContinuousPlausible;
        }

        return MissingTechnicianWorkContinuity.Unknown;
    }

    public static MissingTechnicianOperationalSiteRelation ClassifyOperationalSite(
        string? sitePostcode,
        string? gpsStreetHint,
        JobLocationEvidence? plannedLocation,
        string? gpsArrivalAddress,
        StandbyGpsDayEvidence? peerGps,
        NormalizedPerformanceEntry? peer,
        KnownLocationCatalog? knownLocations = null,
        IDistanceCalculator? distanceCalculator = null)
    {
        _ = knownLocations;
        distanceCalculator ??= new HaversineDistanceCalculator();

        var plannedPc = MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(plannedLocation?.Postcode)
            ?? MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(plannedLocation?.AddressLabel);
        var gpsPc = MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(gpsArrivalAddress) ?? sitePostcode;

        if (plannedPc is null || gpsPc is null)
        {
            return MissingTechnicianOperationalSiteRelation.Ambiguous;
        }

        if (!string.Equals(plannedPc, gpsPc, StringComparison.Ordinal))
        {
            return MissingTechnicianOperationalSiteRelation.DifferentSite;
        }

        var plannedStreet = ExtractStreetHint(plannedLocation?.AddressLabel);
        var gpsStreet = gpsStreetHint ?? ExtractStreetHint(gpsArrivalAddress);
        var peerSupportsCluster = PeerGpsSupportsCluster(peerGps, peer, gpsPc, gpsStreet, distanceCalculator);

        if (peerSupportsCluster)
        {
            // Peer booked same job while GPS at same cluster → operational site proven even if BON street differs.
            return MissingTechnicianOperationalSiteRelation.SameOperationalSiteProven;
        }

        if (!string.IsNullOrWhiteSpace(plannedStreet)
            && !string.IsNullOrWhiteSpace(gpsStreet)
            && !StreetsCompatible(plannedStreet, gpsStreet))
        {
            // Same postcode, different street, no peer GPS corroboration.
            return MissingTechnicianOperationalSiteRelation.SameOperationalSiteLikely;
        }

        return MissingTechnicianOperationalSiteRelation.SameOperationalSiteLikely;
    }

    public static bool IsTheBelgianHq(
        string? address,
        decimal? latitude,
        decimal? longitude,
        KnownLocationCatalog? knownLocations = null,
        IDistanceCalculator? distanceCalculator = null)
    {
        knownLocations ??= KnownLocationCatalog.Empty;
        distanceCalculator ??= new HaversineDistanceCalculator();

        if (knownLocations.HasLocations)
        {
            var resolved = knownLocations.Resolve(latitude, longitude, address);
            if (resolved.MatchedByGeofence
                && resolved.AliasName is not null
                && resolved.AliasName.Contains("Belgian", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (latitude is not null && longitude is not null)
        {
            var metres = distanceCalculator.DistanceMetres(
                new GeoCoordinate(HqLatitude, HqLongitude),
                new GeoCoordinate((double)latitude.Value, (double)longitude.Value));
            if (metres <= HqRadiusMeters)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var a = address;
        var hasSlozen = a.Contains("Slozenstraat", StringComparison.OrdinalIgnoreCase);
        var hasMeise = a.Contains("Meise", StringComparison.OrdinalIgnoreCase)
            || a.Contains("1861", StringComparison.Ordinal);
        return hasSlozen && hasMeise;
    }

    public static bool AllowsContinuousProposal(MissingTechnicianWorkContinuity continuity) =>
        continuity is MissingTechnicianWorkContinuity.ContinuousSupported
            or MissingTechnicianWorkContinuity.ContinuousPlausible;

    public static string FormatContinuityNl(
        MissingTechnicianWorkContinuity continuity,
        IReadOnlyList<MissingTechnicianExcursion> excursions)
    {
        var primary = excursions.Count > 0 ? excursions[0].Classification : (MissingTechnicianExcursionClass?)null;
        return continuity switch
        {
            MissingTechnicianWorkContinuity.ContinuousSupported when primary is MissingTechnicianExcursionClass.WorkHqVisit
                or MissingTechnicianExcursionClass.WorkMaterialPickup =>
                "Verplaatsing naar The Belgian wordt als werkgerelateerd beschouwd. De geregistreerde werktijd hoeft hierdoor niet noodzakelijk onderbroken te worden.",
            MissingTechnicianWorkContinuity.ContinuousSupported =>
                "Werkcontinuïteit ondersteund ondanks tijdelijke GPS-verplaatsing.",
            MissingTechnicianWorkContinuity.ContinuousPlausible when primary == MissingTechnicianExcursionClass.MealBreak =>
                "Korte afwezigheid rond middag. Normale pauzeregel blijft van toepassing; GPS-gat is geen aparte onbetaalde aftrek.",
            MissingTechnicianWorkContinuity.ContinuousPlausible =>
                "Mogelijk werkgerelateerde of tijdelijke verplaatsing. De geregistreerde werktijd hoeft hierdoor niet noodzakelijk onderbroken te worden.",
            MissingTechnicianWorkContinuity.SplitRequired =>
                "Onderbreking vereist: andere job of niet-werkperiode bewezen tijdens de GPS-afwezigheid.",
            _ => "Werkcontinuïteit onzeker; manuele controle vereist.",
        };
    }

    public static string FormatExcursionClass(MissingTechnicianExcursionClass c) => c switch
    {
        MissingTechnicianExcursionClass.WorkMaterialPickup => "WORK_MATERIAL_PICKUP",
        MissingTechnicianExcursionClass.WorkHqVisit => "WORK_HQ_VISIT",
        MissingTechnicianExcursionClass.WorkSupplierVisit => "WORK_SUPPLIER_VISIT",
        MissingTechnicianExcursionClass.WorkTravel => "WORK_TRAVEL",
        MissingTechnicianExcursionClass.MealBreak => "MEAL_BREAK",
        MissingTechnicianExcursionClass.OtherJob => "OTHER_JOB",
        MissingTechnicianExcursionClass.PersonalOrNonwork => "PERSONAL_OR_NONWORK",
        _ => "UNKNOWN",
    };

    public static string FormatContinuity(MissingTechnicianWorkContinuity c) => c switch
    {
        MissingTechnicianWorkContinuity.ContinuousSupported => "CONTINUOUS_SUPPORTED",
        MissingTechnicianWorkContinuity.ContinuousPlausible => "CONTINUOUS_PLAUSIBLE",
        MissingTechnicianWorkContinuity.SplitRequired => "SPLIT_REQUIRED",
        _ => "UNKNOWN",
    };

    public static string FormatOperationalSite(MissingTechnicianOperationalSiteRelation r) => r switch
    {
        MissingTechnicianOperationalSiteRelation.SameOperationalSiteProven => "SAME_OPERATIONAL_SITE_PROVEN",
        MissingTechnicianOperationalSiteRelation.SameOperationalSiteLikely => "SAME_OPERATIONAL_SITE_LIKELY",
        MissingTechnicianOperationalSiteRelation.DifferentSite => "DIFFERENT_SITE",
        _ => "AMBIGUOUS",
    };

    private static string FormatExcursionSummaryNl(IReadOnlyList<MissingTechnicianExcursion> excursions)
    {
        if (excursions.Count == 0)
        {
            return "Geen tijdelijke verplaatsing gedetecteerd tussen aankomst en vertrek.";
        }

        return string.Join(" | ", excursions.Select(e =>
            $"{e.DepartSite:HH:mm}→{e.ReturnSite:HH:mm} {FormatExcursionClass(e.Classification)}"
            + (string.IsNullOrWhiteSpace(e.AwayAddress) ? "" : $" ({ShortAddress(e.AwayAddress)})")));
    }

    private static string BuildPauseNote(IReadOnlyList<MissingTechnicianExcursion> excursions)
    {
        if (excursions.Any(e => e.Classification == MissingTechnicianExcursionClass.MealBreak))
        {
            return "Canonical weekday pause (0,5 u) blijft van toepassing op dagniveau; GPS-lunchbeweging niet nogmaals aftrekken van voorsteluren.";
        }

        return "Voorsteluren zijn bruto VAN–TOT; canonical pause werkt op dagpayroll, niet als dubbele GPS-aftrek.";
    }

    private static bool RequiresAllocationReview(
        IReadOnlyList<MissingTechnicianExcursion> excursions,
        IReadOnlyList<NormalizedPerformanceEntry> dayRows)
    {
        if (!excursions.Any(e =>
                e.Classification is MissingTechnicianExcursionClass.WorkHqVisit
                    or MissingTechnicianExcursionClass.WorkMaterialPickup
                    or MissingTechnicianExcursionClass.WorkSupplierVisit))
        {
            return false;
        }

        // Absence of Project 300 booking around the excursion is review for costing, not unpaid time.
        var hasP300 = dayRows.Any(r => r.ProjectNumber == 300);
        return !hasP300;
    }

    private static bool HasOtherJobDuring(
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        PlannedWorkGroup group,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        foreach (var row in dayRows)
        {
            if (row.Start is null || row.End is null || row.End <= row.Start)
            {
                continue;
            }

            if (ProjectCompatible(group, row))
            {
                continue;
            }

            if (MissingTechnicianSiteConflictAnalyzer.MateriallyOverlapsProposedInterval(row, from, to))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ProjectCompatible(PlannedWorkGroup group, NormalizedPerformanceEntry entry)
    {
        if (string.IsNullOrWhiteSpace(group.ProjectId) || string.IsNullOrWhiteSpace(entry.ProjectId))
        {
            return true;
        }

        return string.Equals(group.ProjectId, entry.ProjectId, StringComparison.OrdinalIgnoreCase)
            || (group.ProjectNumber is not null && entry.ProjectNumber == group.ProjectNumber);
    }

    private static bool IsAtSiteCluster(
        string? address,
        decimal? lat,
        decimal? lon,
        string? sitePostcode,
        IDistanceCalculator distanceCalculator)
    {
        _ = lat;
        _ = lon;
        _ = distanceCalculator;
        if (string.IsNullOrWhiteSpace(sitePostcode))
        {
            return false;
        }

        var pc = MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(address);
        return pc is not null && string.Equals(pc, sitePostcode, StringComparison.Ordinal);
    }

    private static bool PeerGpsSupportsCluster(
        StandbyGpsDayEvidence? peerGps,
        NormalizedPerformanceEntry? peer,
        string sitePostcode,
        string? siteStreet,
        IDistanceCalculator distanceCalculator)
    {
        _ = distanceCalculator;
        if (peerGps is null || !peerGps.HasUsableTrips || peer?.Start is null || peer.End is null)
        {
            return false;
        }

        var windowTrips = peerGps.Trips
            .Where(t => t.End >= peer.Start.Value - TimeSpan.FromMinutes(30)
                        && t.Start <= peer.End.Value + TimeSpan.FromMinutes(30))
            .ToList();
        if (windowTrips.Count == 0)
        {
            return false;
        }

        var samePc = windowTrips.Count(t =>
            string.Equals(
                MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(t.EndAddress),
                sitePostcode,
                StringComparison.Ordinal));
        if (samePc == 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(siteStreet))
        {
            var sameStreet = windowTrips.Any(t =>
                StreetsCompatible(siteStreet, ExtractStreetHint(t.EndAddress)));
            if (sameStreet)
            {
                return true;
            }
        }

        // Majority of peer window trip ends in same postcode while peer booked same job.
        return samePc >= Math.Max(1, windowTrips.Count / 2);
    }

    private static bool LooksLikeCustomerSite(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        // Heuristic: Belgian street address with postcode, not retail food keywords.
        var lower = address.ToLowerInvariant();
        if (lower.Contains("brood") || lower.Contains("bakker") || lower.Contains("pizza")
            || lower.Contains("mcdonald") || lower.Contains("delhaize") || lower.Contains("colruyt")
            || lower.Contains("carrefour") || lower.Contains("spar "))
        {
            return false;
        }

        return MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(address) is not null;
    }

    private static bool StreetsCompatible(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        static string Norm(string s)
        {
            var t = s.Trim().ToLowerInvariant();
            foreach (var suffix in new[] { "straat", "laan", "weg", "steenweg", "dreef", "lei" })
            {
                if (t.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return t;
                }
            }

            return t;
        }

        var na = Norm(a);
        var nb = Norm(b);
        return na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal);
    }

    private static string? ExtractStreetHint(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var first = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
        {
            return null;
        }

        // Drop trailing house number.
        var parts = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[^1].Any(char.IsDigit))
        {
            return string.Join(' ', parts.Take(parts.Length - 1));
        }

        return first;
    }

    private static string? PreferArrivalAddress(StandbyGpsDayEvidence? gps, DateTimeOffset? arrival)
    {
        if (gps is null || arrival is null || gps.Trips.Count == 0)
        {
            return null;
        }

        return gps.Trips
            .OrderBy(t => Math.Abs((t.End - arrival.Value).TotalSeconds))
            .Select(t => t.EndAddress)
            .FirstOrDefault();
    }

    private static string? ExtractPostcodeFromArrival(StandbyGpsDayEvidence? gps, DateTimeOffset? arrival) =>
        MissingTechnicianSiteConflictAnalyzer.NormalizePostcode(PreferArrivalAddress(gps, arrival));

    private static string ShortAddress(string address)
    {
        var parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? address : string.Join(", ", parts.Take(2));
    }
}
