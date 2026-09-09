using System.Globalization;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Services;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Job site evidence for MissingTechnician GPS matching. Prefer BON/LEVADR geocode when available;
/// postcode/locality is weaker corroboration only.
/// </summary>
public sealed record JobLocationEvidence(
    string Key,
    string? ProjectId,
    int? ProjectNumber,
    string? BonNr,
    string? AddressLabel,
    string? Postcode,
    string? Locality,
    double? Latitude,
    double? Longitude,
    double RadiusMeters,
    JobLocationSource Source,
    JobLocationConfidence Confidence)
{
    public bool HasCoordinates => Latitude is not null && Longitude is not null;
    public bool HasPostcode => !string.IsNullOrWhiteSpace(Postcode);
}

public enum JobLocationSource
{
    None = 0,
    PlanningAddress = 1,
    BonInterventionAddress = 2,
    LevadrCustomerAddress = 3,
    ProjectSiteAddress = 4,
    PerformancePostcode = 5,
    GpsTripAddress = 6,
}

public enum JobLocationConfidence
{
    None = 0,
    Weak = 1,
    Medium = 2,
    Strong = 3,
}

public enum MissingTechnicianSiteMatch
{
    PlannedJobSiteMatch = 0,
    ExistingBookedJobSiteMatch = 1,
    BothPossible = 2,
    NeitherMatch = 3,
    LocationUnknown = 4,
}

public enum MissingTechnicianConflictClass
{
    None = 0,
    ExistingPerformanceSupported = 1,
    PlannedJobSupportedExistingBookingWrong = 2,
    PartialBothJobs = 3,
    TimeConflictOnly = 4,
    LocationUnknown = 5,
}

/// <summary>
/// Pure classifiers: site match + interval-aware conflict for MissingTechnician V2.
/// </summary>
public static class MissingTechnicianSiteConflictAnalyzer
{
    public const double DefaultGeofenceRadiusMeters = 250d;
    public const int MaterialOverlapMinutes = 5;

    public static (DateTimeOffset? Start, DateTimeOffset? End) ProposedInterval(
        DateTimeOffset? gpsArrival,
        DateTimeOffset? gpsDeparture,
        PlannedWorkGroup group,
        NormalizedPerformanceEntry? peer)
    {
        if (gpsArrival is not null && gpsDeparture is not null && gpsDeparture > gpsArrival)
        {
            return (gpsArrival, gpsDeparture);
        }

        if (peer?.Start is not null && peer.End is not null && peer.End > peer.Start)
        {
            return (peer.Start, peer.End);
        }

        if (group.TimeFrom is not null && group.TimeTo is not null && group.TimeTo > group.TimeFrom)
        {
            var start = new DateTimeOffset(group.Date.ToDateTime(group.TimeFrom.Value), TimeSpan.Zero);
            var end = new DateTimeOffset(group.Date.ToDateTime(group.TimeTo.Value), TimeSpan.Zero);
            return (start, end);
        }

        return (null, null);
    }

    /// <summary>
    /// Material overlap with the ACTUAL proposed missing interval (not full planning window alone).
    /// </summary>
    public static bool MateriallyOverlapsProposedInterval(
        NormalizedPerformanceEntry existing,
        DateTimeOffset proposedStart,
        DateTimeOffset proposedEnd)
    {
        if (existing.Start is null || existing.End is null || existing.End <= existing.Start)
        {
            return false;
        }

        var overlapStart = existing.Start.Value > proposedStart ? existing.Start.Value : proposedStart;
        var overlapEnd = existing.End.Value < proposedEnd ? existing.End.Value : proposedEnd;
        if (overlapEnd <= overlapStart)
        {
            return false;
        }

        return (overlapEnd - overlapStart).TotalMinutes >= MaterialOverlapMinutes;
    }

    public static NormalizedPerformanceEntry? FindMaterialConflict(
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        DateTimeOffset proposedStart,
        DateTimeOffset proposedEnd,
        PlannedWorkGroup group,
        Func<NormalizedPerformanceEntry, bool> isCredibleJob)
    {
        return dayRows
            .Where(isCredibleJob)
            .Where(item => !ProjectCompatible(group, item))
            .Where(item => MateriallyOverlapsProposedInterval(item, proposedStart, proposedEnd))
            .OrderBy(item => item.SourceEntryId)
            .FirstOrDefault();
    }

    public static MissingTechnicianSiteMatch ClassifySiteMatch(
        JobLocationEvidence? plannedLocation,
        JobLocationEvidence? existingBookedLocation,
        decimal? gpsLatitude,
        decimal? gpsLongitude,
        string? gpsAddressOrLabel,
        IDistanceCalculator? distanceCalculator = null)
    {
        distanceCalculator ??= new HaversineDistanceCalculator();
        var planned = MatchesLocation(plannedLocation, gpsLatitude, gpsLongitude, gpsAddressOrLabel, distanceCalculator);
        var existing = MatchesLocation(existingBookedLocation, gpsLatitude, gpsLongitude, gpsAddressOrLabel, distanceCalculator);

        if (!planned.HasEvidence && !existing.HasEvidence)
        {
            return MissingTechnicianSiteMatch.LocationUnknown;
        }

        if (planned.Matched && existing.Matched)
        {
            return MissingTechnicianSiteMatch.BothPossible;
        }

        if (planned.Matched)
        {
            return MissingTechnicianSiteMatch.PlannedJobSiteMatch;
        }

        if (existing.Matched)
        {
            return MissingTechnicianSiteMatch.ExistingBookedJobSiteMatch;
        }

        if (planned.HasEvidence || existing.HasEvidence)
        {
            return MissingTechnicianSiteMatch.NeitherMatch;
        }

        return MissingTechnicianSiteMatch.LocationUnknown;
    }

    public static MissingTechnicianConflictClass ClassifyConflict(
        NormalizedPerformanceEntry? materialConflict,
        MissingTechnicianSiteMatch siteMatch,
        bool hasCompleteGpsSite)
    {
        if (materialConflict is null)
        {
            return MissingTechnicianConflictClass.None;
        }

        return siteMatch switch
        {
            MissingTechnicianSiteMatch.ExistingBookedJobSiteMatch =>
                MissingTechnicianConflictClass.ExistingPerformanceSupported,
            MissingTechnicianSiteMatch.PlannedJobSiteMatch when hasCompleteGpsSite =>
                MissingTechnicianConflictClass.PlannedJobSupportedExistingBookingWrong,
            MissingTechnicianSiteMatch.BothPossible =>
                MissingTechnicianConflictClass.PartialBothJobs,
            MissingTechnicianSiteMatch.LocationUnknown =>
                MissingTechnicianConflictClass.LocationUnknown,
            _ => MissingTechnicianConflictClass.TimeConflictOnly,
        };
    }

    public static (decimal? Lat, decimal? Lon, string? Address) PreferStopPoint(
        IReadOnlyList<StandbyGpsTripEvidence> windowTrips,
        DateTimeOffset? siteArrival)
    {
        if (windowTrips.Count == 0)
        {
            return (null, null, null);
        }

        StandbyGpsTripEvidence? arrivalTrip = null;
        if (siteArrival is not null)
        {
            arrivalTrip = windowTrips
                .OrderBy(trip => Math.Abs((trip.End - siteArrival.Value).TotalSeconds))
                .FirstOrDefault();
        }

        arrivalTrip ??= windowTrips.OrderBy(trip => trip.End).First();
        return (arrivalTrip.EndLatitude, arrivalTrip.EndLongitude, arrivalTrip.EndAddress);
    }

    public static JobLocationEvidence FromPerformancePostcode(
        NormalizedPerformanceEntry performance,
        string keyPrefix)
    {
        var pc = NormalizePostcode(performance.Postcode);
        return new JobLocationEvidence(
            Key: $"{keyPrefix}:{performance.SourceEntryId}",
            ProjectId: performance.ProjectId,
            ProjectNumber: performance.ProjectNumber,
            BonNr: performance.BonNr,
            AddressLabel: pc,
            Postcode: pc,
            Locality: null,
            Latitude: null,
            Longitude: null,
            RadiusMeters: DefaultGeofenceRadiusMeters,
            Source: JobLocationSource.PerformancePostcode,
            Confidence: pc is null ? JobLocationConfidence.None : JobLocationConfidence.Weak);
    }

    public static string? NormalizePostcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Prefer a standalone Belgian postcode token (avoid swallowing house numbers: "Ingberthoeveweg 21, 2630").
        var match = System.Text.RegularExpressions.Regex.Match(value, @"\b([1-9]\d{3})\b");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 4 && trimmed.All(char.IsDigit) && trimmed[0] != '0')
        {
            return trimmed;
        }

        return null;
    }

    public static string? ExtractLocalityOrPostcode(string? addressOrLabel)
    {
        if (string.IsNullOrWhiteSpace(addressOrLabel))
        {
            return null;
        }

        var pc = NormalizePostcode(addressOrLabel);
        if (pc is not null)
        {
            return pc;
        }

        // Reuse Belgian "1785 Merchtem" style locality extraction used by workbenches.
        var text = addressOrLabel.Trim();
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : text;
    }

    private static (bool HasEvidence, bool Matched) MatchesLocation(
        JobLocationEvidence? location,
        decimal? gpsLatitude,
        decimal? gpsLongitude,
        string? gpsAddressOrLabel,
        IDistanceCalculator distanceCalculator)
    {
        if (location is null || location.Confidence == JobLocationConfidence.None)
        {
            return (false, false);
        }

        var hasEvidence = location.HasCoordinates || location.HasPostcode || !string.IsNullOrWhiteSpace(location.Locality);
        if (!hasEvidence)
        {
            return (false, false);
        }

        if (location.HasCoordinates
            && gpsLatitude is not null
            && gpsLongitude is not null)
        {
            var metres = distanceCalculator.DistanceMetres(
                new GeoCoordinate(location.Latitude!.Value, location.Longitude!.Value),
                new GeoCoordinate((double)gpsLatitude.Value, (double)gpsLongitude.Value));
            if (metres <= location.RadiusMeters)
            {
                return (true, true);
            }

            // Strong coordinate evidence that does not match → explicit non-match.
            if (location.Confidence >= JobLocationConfidence.Medium)
            {
                return (true, false);
            }
        }

        var gpsToken = ExtractLocalityOrPostcode(gpsAddressOrLabel);
        var locPc = NormalizePostcode(location.Postcode) ?? NormalizePostcode(location.AddressLabel);
        if (locPc is not null && gpsToken is not null)
        {
            var gpsPc = NormalizePostcode(gpsToken) ?? (gpsToken.Length == 4 && gpsToken.All(char.IsDigit) ? gpsToken : null);
            if (gpsPc is not null && string.Equals(locPc, gpsPc, StringComparison.Ordinal))
            {
                return (true, true);
            }

            if (gpsAddressOrLabel is not null
                && gpsAddressOrLabel.Contains(locPc, StringComparison.OrdinalIgnoreCase))
            {
                return (true, true);
            }
        }

        if (!string.IsNullOrWhiteSpace(location.Locality)
            && !string.IsNullOrWhiteSpace(gpsAddressOrLabel)
            && gpsAddressOrLabel.Contains(location.Locality, StringComparison.OrdinalIgnoreCase))
        {
            return (true, true);
        }

        return (hasEvidence, false);
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

    public static string FormatSiteMatchNl(MissingTechnicianSiteMatch match) => match switch
    {
        MissingTechnicianSiteMatch.PlannedJobSiteMatch => "Track & Trace ondersteunt de geplande job.",
        MissingTechnicianSiteMatch.ExistingBookedJobSiteMatch => "Track & Trace ondersteunt de huidige boeking.",
        MissingTechnicianSiteMatch.BothPossible => "Beide jobs lijken mogelijk.",
        MissingTechnicianSiteMatch.NeitherMatch => "GPS-stop komt niet overeen met gekende joblocaties.",
        _ => "Locatie kan niet betrouwbaar gekoppeld worden.",
    };

    public static string FormatConflictNl(MissingTechnicianConflictClass conflict) => conflict switch
    {
        MissingTechnicianConflictClass.None => "Geen materieel conflicterende prestatie.",
        MissingTechnicianConflictClass.ExistingPerformanceSupported => "Bestaande boeking lijkt ondersteund door GPS.",
        MissingTechnicianConflictClass.PlannedJobSupportedExistingBookingWrong => "Mogelijk verkeerde project/bon geboekt.",
        MissingTechnicianConflictClass.PartialBothJobs => "GPS chronologie ondersteunt mogelijk beide jobs.",
        MissingTechnicianConflictClass.TimeConflictOnly => "Tijdoverlap zonder eenduidige locatie.",
        _ => "Locatie onbekend; conflict blijft review.",
    };
}
