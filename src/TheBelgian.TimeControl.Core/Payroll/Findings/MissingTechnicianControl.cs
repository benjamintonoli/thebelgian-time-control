using System.Globalization;
using System.Text;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Detects planned multi-technician jobs where a peer booked and another planned
/// technician has no matching job performance. Evidence/proposals only.
/// </summary>
public static class MissingTechnicianControl
{
    public static readonly TimeSpan MatchWindowPadding = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan MinimumSitePresence = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<PayrollFinding> Evaluate(
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<PayrollPlanningReservation> planning,
        IReadOnlyList<StandbyGpsDayEvidence> gpsDays,
        IReadOnlySet<string> includedResourceIds)
    {
        var findings = new List<PayrollFinding>();
        var groups = PlannedWorkGroupBuilder.Build(planning);
        var gpsLookup = gpsDays
            .GroupBy(item => (item.ResourceId, item.Date))
            .ToDictionary(group => group.Key, group => group.First());
        var performancesByResourceDate = performances
            .Where(item => !item.IsCalendarSynthetic)
            .GroupBy(item => (item.ResourceId, item.Date))
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var group in groups)
        {
            var eligibleResources = group.ResourceIds
                .Where(includedResourceIds.Contains)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            if (eligibleResources.Count < 2)
            {
                continue;
            }

            var matches = new Dictionary<string, NormalizedPerformanceEntry?>(StringComparer.Ordinal);
            foreach (var resourceId in eligibleResources)
            {
                performancesByResourceDate.TryGetValue((resourceId, group.Date), out var dayRows);
                dayRows ??= [];
                matches[resourceId] = HasFullDayAbsence(dayRows, planning, resourceId, group.Date)
                    ? null
                    : FindMatchingJobPerformance(group, dayRows);
            }

            var peers = eligibleResources
                .Where(id => matches.TryGetValue(id, out var perf) && perf is not null)
                .Select(id => matches[id]!)
                .OrderBy(item => item.SourceEntryId)
                .ToList();

            // Core rule: peer job performance is required. GPS alone never creates a finding
            // (PlanningPlusGps remains a classification label for diagnostics / tests).
            if (peers.Count == 0)
            {
                continue;
            }

            foreach (var missingId in eligibleResources)
            {
                if (matches.TryGetValue(missingId, out var matched) && matched is not null)
                {
                    continue;
                }

                performancesByResourceDate.TryGetValue((missingId, group.Date), out var dayRows);
                dayRows ??= [];
                if (HasFullDayAbsence(dayRows, planning, missingId, group.Date))
                {
                    continue;
                }

                gpsLookup.TryGetValue((missingId, group.Date), out var gps);
                var peerGps = peers
                    .Select(peer => gpsLookup.TryGetValue((peer.ResourceId, group.Date), out var day) ? day : null)
                    .FirstOrDefault(item => item is not null);
                findings.Add(BuildFinding(group, missingId, peers, dayRows, gps, peerGps));
            }
        }

        return findings
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Classifies GPS-only evidence when no peer booked (no finding is emitted).
    /// </summary>
    public static MissingTechnicianEvidenceClass ClassifyWithoutPeer(StandbyGpsDayEvidence? gps, PlannedWorkGroup group) =>
        ClassifyGps(group, gps) switch
        {
            GpsSupportState.Supports => MissingTechnicianEvidenceClass.PlanningPlusGps,
            GpsSupportState.Contradicted => MissingTechnicianEvidenceClass.ContradictedByGps,
            GpsSupportState.Ambiguous => MissingTechnicianEvidenceClass.Ambiguous,
            _ => MissingTechnicianEvidenceClass.NoGpsData,
        };

    /// <summary>
    /// Site presence = arrival at job (inbound trip end) → departure (outbound trip start).
    /// Never uses home departure → site arrival as payable work.
    /// </summary>
    public static (DateTimeOffset? Arrival, DateTimeOffset? Departure, string? Note) TryDeriveSitePresence(
        PlannedWorkGroup group,
        StandbyGpsDayEvidence? gps,
        NormalizedPerformanceEntry? peer = null)
    {
        if (gps is null || !gps.HasVehicleMapping || gps.MappingAmbiguous || !gps.HasUsableTrips)
        {
            return (null, null, null);
        }

        var windowTrips = SelectWindowTrips(group, gps, peer);
        if (windowTrips.Count == 0)
        {
            return (null, null, null);
        }

        // Inbound arrival = earliest trip end inside/near the job window.
        // Outbound departure = latest trip start inside/near the job window after arrival.
        var arrival = windowTrips
            .OrderBy(trip => trip.End)
            .Select(trip => (DateTimeOffset?)trip.End)
            .FirstOrDefault();
        if (arrival is null)
        {
            return (null, null, null);
        }

        var departure = windowTrips
            .Where(trip => trip.Start >= arrival.Value)
            .OrderByDescending(trip => trip.Start)
            .Select(trip => (DateTimeOffset?)trip.Start)
            .FirstOrDefault();

        if (departure is null || departure <= arrival || departure.Value - arrival.Value < MinimumSitePresence)
        {
            return (arrival, null, "incompleteSiteStop=arrivalOnly");
        }

        return (arrival, departure, "sitePresence=arrivalDeparture");
    }

    public static MissingTechnicianTravelMode ClassifyTravelMode(
        StandbyGpsDayEvidence? missingGps,
        StandbyGpsDayEvidence? peerGps)
    {
        var hasIndependentGps = missingGps is { HasVehicleMapping: true, MappingAmbiguous: false, HasUsableTrips: true };
        if (hasIndependentGps)
        {
            if (IsSameVehicle(missingGps, peerGps))
            {
                return MissingTechnicianTravelMode.SharedTravelProven;
            }

            return MissingTechnicianTravelMode.SeparateVehicleProven;
        }

        if (IsSameVehicle(missingGps, peerGps))
        {
            return MissingTechnicianTravelMode.SharedTravelProven;
        }

        // Absence of personal GPS alone is NOT proof of shared travel.
        return MissingTechnicianTravelMode.SharedTravelPossible;
    }

    public static string TravelModeDutch(MissingTechnicianTravelMode mode) => mode switch
    {
        MissingTechnicianTravelMode.SharedTravelProven => "Samen gereden (sterk bewijs)",
        MissingTechnicianTravelMode.SeparateVehicleProven => "Apart gereden (eigen GPS)",
        MissingTechnicianTravelMode.SharedTravelPossible => "Mogelijk samen gereden",
        _ => "GPS onvoldoende",
    };

    private static PayrollFinding BuildFinding(
        PlannedWorkGroup group,
        string missingResourceId,
        List<NormalizedPerformanceEntry> peers,
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        StandbyGpsDayEvidence? gps,
        StandbyGpsDayEvidence? peerGps)
    {
        var conflicting = FindConflictingPerformance(group, dayRows);
        var gpsState = ClassifyGps(group, gps);
        var travelMode = ClassifyTravelMode(gps, peerGps);
        var site = TryDeriveSitePresence(group, gps, peers.FirstOrDefault());
        MissingTechnicianEvidenceClass evidenceClass;
        PayrollFindingSeverity severity;
        DateTimeOffset? suggestedStart = null;
        DateTimeOffset? suggestedEnd = null;
        decimal? suggestedHours = null;
        string intervalSource = "none";
        int? suggestedHfdTaakId = group.Reservations
            .Where(item => string.Equals(item.ResourceId, missingResourceId, StringComparison.Ordinal))
            .Select(item => item.HfdTaakId)
            .FirstOrDefault(id => id is > 0);
        string? suggestedBon = peers
            .Select(item => item.BonNr)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var suggestedProject = group.ProjectId
            ?? peers.Select(item => item.ProjectId).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        var peerInterval = peers.FirstOrDefault(item =>
            item.Start is not null && item.End is not null && item.End > item.Start);

        if (conflicting is not null)
        {
            evidenceClass = MissingTechnicianEvidenceClass.ContradictedByExistingPerformance;
            severity = PayrollFindingSeverity.Review;
        }
        else if (gpsState == GpsSupportState.Contradicted)
        {
            evidenceClass = MissingTechnicianEvidenceClass.ContradictedByGps;
            severity = PayrollFindingSeverity.Review;
        }
        else if (travelMode == MissingTechnicianTravelMode.SeparateVehicleProven
                 && site.Arrival is not null
                 && site.Departure is not null)
        {
            evidenceClass = MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps;
            severity = PayrollFindingSeverity.High;
            suggestedStart = site.Arrival;
            suggestedEnd = site.Departure;
            suggestedHours = RoundHours((decimal)(site.Departure.Value - site.Arrival.Value).TotalHours);
            intervalSource = "gpsSite";
        }
        else if (travelMode == MissingTechnicianTravelMode.SharedTravelProven
                 && peerInterval is not null)
        {
            evidenceClass = MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps;
            severity = PayrollFindingSeverity.High;
            suggestedStart = peerInterval.Start;
            suggestedEnd = peerInterval.End;
            suggestedHours = RoundHours((decimal)(peerInterval.End!.Value - peerInterval.Start!.Value).TotalHours);
            intervalSource = "peerSharedTravel";
        }
        else if (gpsState == GpsSupportState.Supports
                 && travelMode == MissingTechnicianTravelMode.SeparateVehicleProven
                 && site.Arrival is not null
                 && site.Departure is null)
        {
            // Incomplete site stop → Review, no Ready create interval.
            evidenceClass = MissingTechnicianEvidenceClass.Ambiguous;
            severity = PayrollFindingSeverity.Review;
            intervalSource = "none";
        }
        else if (gpsState == GpsSupportState.Supports)
        {
            // GPS in window but site stop incomplete / ambiguous — keep High only when planning interval exists
            // and travel mode is not claiming a false site presence. Prefer peer/planning for proposal.
            evidenceClass = MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps;
            severity = PayrollFindingSeverity.High;
            (suggestedStart, suggestedEnd, suggestedHours, intervalSource) = ProposeInterval(group, peers, preferPeer: true);
        }
        else if (gpsState == GpsSupportState.NoData)
        {
            // Peer present + no independent GPS (shared vehicle possible) → Review, not absence.
            evidenceClass = gps is null
                ? MissingTechnicianEvidenceClass.PlanningPlusPeer
                : MissingTechnicianEvidenceClass.NoGpsData;
            severity = PayrollFindingSeverity.Review;
            // Workbench may offer peer interval as human choice; do not mark Ready create.
        }
        else
        {
            evidenceClass = MissingTechnicianEvidenceClass.Ambiguous;
            severity = PayrollFindingSeverity.Review;
        }

        if (evidenceClass is not MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps)
        {
            suggestedStart = null;
            suggestedEnd = null;
            suggestedHours = null;
            suggestedProject = null;
            suggestedBon = null;
            suggestedHfdTaakId = null;
            intervalSource = "none";
        }

        var peer = peers[0];
        var planned = FormatPlannedInterval(group);
        var description =
            $"Planning {planned} (kalender {group.IdCalendar}); collega {peer.ResourceId} heeft prestatie "
            + $"{FormatPerfInterval(peer)}; geen matching jobprestatie voor resource {missingResourceId}.";
        var evidence = BuildEvidence(
            group,
            missingResourceId,
            peers,
            conflicting,
            gps,
            peerGps,
            gpsState,
            evidenceClass,
            travelMode,
            intervalSource,
            suggestedHfdTaakId,
            site);
        var action = evidenceClass switch
        {
            MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps when intervalSource == "gpsSite" =>
                "Voorstel volgt eigen GPS werfaanwezigheid (menselijke goedkeuring vereist).",
            MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps when intervalSource == "peerSharedTravel" =>
                "Waarschijnlijk samen gereden. Voorstel volgt de geregistreerde werkuren van de collega.",
            MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps =>
                "Controleer of een prestatie ontbreekt; voorstel is beschikbaar (geen automatische write).",
            MissingTechnicianEvidenceClass.ContradictedByExistingPerformance =>
                "Geen voorstel: conflicterende prestatie in hetzelfde venster.",
            MissingTechnicianEvidenceClass.ContradictedByGps =>
                "Geen voorstel: GPS lijkt elders actief.",
            _ => "Controleer planning vs prestaties; GPS/shared vehicle kan ontbrekende track verklaren.",
        };

        return new PayrollFinding(
            FindingKey: $"missing-tech:{group.IdCalendar}:{group.Date:yyyyMMdd}:{missingResourceId}",
            ResourceId: missingResourceId,
            Date: group.Date,
            FindingType: PayrollFindingType.MissingPlannedTechnicianPerformance,
            Severity: severity,
            Title: "Mogelijk ontbrekende prestatie",
            Description: description,
            Evidence: evidence,
            SuggestedAction: action,
            RelatedPerformanceIds: peers.Select(item => item.SourceEntryId)
                .Concat(conflicting is null ? [] : new[] { conflicting.SourceEntryId })
                .Distinct()
                .OrderBy(id => id)
                .ToArray(),
            PlannedHours: group.TimeFrom is not null && group.TimeTo is not null && group.TimeTo > group.TimeFrom
                ? (decimal)(group.TimeTo.Value.ToTimeSpan() - group.TimeFrom.Value.ToTimeSpan()).TotalHours
                : null,
            SuggestedPayableStart: suggestedStart,
            SuggestedPayableEnd: suggestedEnd,
            SuggestedPayableHours: suggestedHours,
            SuggestedProjectId: suggestedProject,
            SuggestedBonNr: suggestedBon,
            GpsClassification: evidenceClass.ToString());
    }

    private static (DateTimeOffset? Start, DateTimeOffset? End, decimal? Hours, string IntervalSource) ProposeInterval(
        PlannedWorkGroup group,
        List<NormalizedPerformanceEntry> peers,
        bool preferPeer)
    {
        if (preferPeer)
        {
            var peer = peers.FirstOrDefault(item => item.Start is not null && item.End is not null && item.End > item.Start);
            if (peer?.Start is not null && peer.End is not null)
            {
                return (peer.Start, peer.End, RoundHours((decimal)(peer.End.Value - peer.Start.Value).TotalHours), "peer");
            }
        }

        if (group.TimeFrom is not null && group.TimeTo is not null && group.TimeTo > group.TimeFrom)
        {
            var offset = TimeSpan.Zero;
            var start = new DateTimeOffset(group.Date.ToDateTime(group.TimeFrom.Value), offset);
            var end = new DateTimeOffset(group.Date.ToDateTime(group.TimeTo.Value), offset);
            return (start, end, RoundHours((decimal)(end - start).TotalHours), "planning");
        }

        var fallbackPeer = peers.FirstOrDefault(item => item.Start is not null && item.End is not null && item.End > item.Start);
        if (fallbackPeer?.Start is not null && fallbackPeer.End is not null)
        {
            return (fallbackPeer.Start, fallbackPeer.End, RoundHours((decimal)(fallbackPeer.End.Value - fallbackPeer.Start.Value).TotalHours), "peer");
        }

        return (null, null, null, "none");
    }

    private static bool IsSameVehicle(StandbyGpsDayEvidence? a, StandbyGpsDayEvidence? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(a.ObjectId)
            && !string.IsNullOrWhiteSpace(b.ObjectId)
            && string.Equals(a.ObjectId.Trim(), b.ObjectId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var plateA = NormalizePlate(a.RegistrationPlate);
        var plateB = NormalizePlate(b.RegistrationPlate);
        return plateA is not null
            && plateB is not null
            && string.Equals(plateA, plateB, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
        {
            return null;
        }

        var chars = plate.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray();
        return chars.Length == 0 ? null : new string(chars);
    }

    private static NormalizedPerformanceEntry? FindMatchingJobPerformance(
        PlannedWorkGroup group,
        IReadOnlyList<NormalizedPerformanceEntry> dayRows)
    {
        return dayRows
            .Where(IsCredibleJobPerformance)
            .Where(item => ProjectCompatible(group, item))
            .Where(item => TimesCompatible(group, item))
            .OrderByDescending(item => ScoreMatch(group, item))
            .ThenBy(item => item.SourceEntryId)
            .FirstOrDefault();
    }

    private static NormalizedPerformanceEntry? FindConflictingPerformance(
        PlannedWorkGroup group,
        IReadOnlyList<NormalizedPerformanceEntry> dayRows)
    {
        return dayRows
            .Where(IsCredibleJobPerformance)
            .Where(item => !ProjectCompatible(group, item) || !TimesCompatible(group, item))
            .Where(item => TimesOverlapPlanning(group, item))
            .OrderBy(item => item.SourceEntryId)
            .FirstOrDefault();
    }

    private static bool IsCredibleJobPerformance(NormalizedPerformanceEntry entry) =>
        !entry.IsCalendarSynthetic
        && !entry.IsAbsence
        && !entry.IsStandby
        && entry.HfdTaakId != 23
        && !entry.IsTravel
        && entry.HfdTaakId != 5;

    private static bool ProjectCompatible(PlannedWorkGroup group, NormalizedPerformanceEntry entry)
    {
        if (string.IsNullOrWhiteSpace(group.ProjectId) || string.IsNullOrWhiteSpace(entry.ProjectId))
        {
            return true;
        }

        return string.Equals(group.ProjectId, entry.ProjectId, StringComparison.OrdinalIgnoreCase)
            || (group.ProjectNumber is not null && entry.ProjectNumber == group.ProjectNumber);
    }

    private static bool TimesCompatible(PlannedWorkGroup group, NormalizedPerformanceEntry entry)
    {
        if (group.TimeFrom is null || group.TimeTo is null || entry.Start is null || entry.End is null)
        {
            return true;
        }

        return TimesOverlapPlanning(group, entry);
    }

    private static bool TimesOverlapPlanning(PlannedWorkGroup group, NormalizedPerformanceEntry entry)
    {
        if (group.TimeFrom is null || group.TimeTo is null || entry.Start is null || entry.End is null)
        {
            return false;
        }

        var planStart = group.Date.ToDateTime(group.TimeFrom.Value);
        var planEnd = group.Date.ToDateTime(group.TimeTo.Value);
        var start = entry.Start.Value.DateTime;
        var end = entry.End.Value.DateTime;
        return start < planEnd && end > planStart;
    }

    private static int ScoreMatch(PlannedWorkGroup group, NormalizedPerformanceEntry entry)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(group.ProjectId)
            && string.Equals(group.ProjectId, entry.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            score += 3;
        }

        if (group.ProjectNumber is not null && entry.ProjectNumber == group.ProjectNumber)
        {
            score += 2;
        }

        if (TimesOverlapPlanning(group, entry))
        {
            score += 2;
        }

        return score;
    }

    private static bool HasFullDayAbsence(
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        IReadOnlyList<PayrollPlanningReservation> planning,
        string resourceId,
        DateOnly date)
    {
        if (dayRows.Any(item => item.IsAbsence && item.AtlHoursRaw >= 7.5m))
        {
            return true;
        }

        return planning.Any(item =>
            string.Equals(item.ResourceId, resourceId, StringComparison.Ordinal)
            && item.Date == date
            && item.Classification == PayrollPlanningClassification.Absence
            && (item.TimeFrom is null || item.TimeTo is null
                || (item.PlannedHours is not null && item.PlannedHours >= 7.5m)));
    }

    private static GpsSupportState ClassifyGps(PlannedWorkGroup group, StandbyGpsDayEvidence? gps)
    {
        if (gps is null || !gps.HasVehicleMapping || gps.MappingAmbiguous || !gps.HasUsableTrips)
        {
            return GpsSupportState.NoData;
        }

        var inWindow = SelectWindowTrips(group, gps);
        if (inWindow.Count > 0)
        {
            return GpsSupportState.Supports;
        }

        // Trips exist but none in planned window — ambiguous (shared vehicle / elsewhere).
        // Only contradict when all trip activity is clearly disjoint by a large margin.
        if (group.TimeFrom is null || group.TimeTo is null)
        {
            return GpsSupportState.Ambiguous;
        }

        var planStart = new DateTimeOffset(group.Date.ToDateTime(group.TimeFrom.Value), gps.Trips[0].Start.Offset);
        var planEnd = new DateTimeOffset(group.Date.ToDateTime(group.TimeTo.Value), gps.Trips[0].Start.Offset);
        var anyNear = gps.Trips.Any(trip =>
            trip.End >= planStart - TimeSpan.FromHours(2)
            && trip.Start <= planEnd + TimeSpan.FromHours(2));
        return anyNear ? GpsSupportState.Ambiguous : GpsSupportState.Contradicted;
    }

    private static List<StandbyGpsTripEvidence> SelectWindowTrips(
        PlannedWorkGroup group,
        StandbyGpsDayEvidence gps,
        NormalizedPerformanceEntry? peer = null)
    {
        DateTimeOffset start;
        DateTimeOffset end;
        var offset = gps.Trips.Count > 0 ? gps.Trips[0].Start.Offset : TimeSpan.Zero;
        if (peer?.Start is not null && peer.End is not null)
        {
            start = peer.Start.Value - MatchWindowPadding;
            end = peer.End.Value + MatchWindowPadding;
        }
        else if (group.TimeFrom is not null && group.TimeTo is not null)
        {
            start = new DateTimeOffset(group.Date.ToDateTime(group.TimeFrom.Value), offset) - MatchWindowPadding;
            end = new DateTimeOffset(group.Date.ToDateTime(group.TimeTo.Value), offset) + MatchWindowPadding;
        }
        else if (gps.Trips.Count > 0)
        {
            start = new DateTimeOffset(group.Date.ToDateTime(TimeOnly.MinValue), offset);
            end = new DateTimeOffset(group.Date.ToDateTime(new TimeOnly(23, 59, 59)), offset);
        }
        else
        {
            return [];
        }

        return gps.Trips
            .Where(trip => trip.Start < end && trip.End > start)
            .OrderBy(trip => trip.Start)
            .ToList();
    }

    private static string BuildEvidence(
        PlannedWorkGroup group,
        string missingResourceId,
        List<NormalizedPerformanceEntry> peers,
        NormalizedPerformanceEntry? conflicting,
        StandbyGpsDayEvidence? gps,
        StandbyGpsDayEvidence? peerGps,
        GpsSupportState gpsState,
        MissingTechnicianEvidenceClass evidenceClass,
        MissingTechnicianTravelMode travelMode,
        string intervalSource,
        int? suggestedHfdTaakId,
        (DateTimeOffset? Arrival, DateTimeOffset? Departure, string? Note) site)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"class={evidenceClass}; idKalender={group.IdCalendar}; project={group.ProjectId ?? "—"}; ");
        sb.Append(CultureInfo.InvariantCulture,
            $"planned={FormatPlannedInterval(group)}; missing={missingResourceId}; ");
        sb.Append(CultureInfo.InvariantCulture,
            $"intervalSource={intervalSource}; travelMode={travelMode}; travelNl={TravelModeDutch(travelMode)}; ");
        if (suggestedHfdTaakId is > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"suggestedHfdTaakId={suggestedHfdTaakId.Value}; ");
        }

        if (site.Arrival is not null)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"siteArrival={site.Arrival:HH:mm}; siteDeparture={(site.Departure is null ? "—" : site.Departure.Value.ToString("HH:mm", CultureInfo.InvariantCulture))}; ");
            if (!string.IsNullOrWhiteSpace(site.Note))
            {
                sb.Append(CultureInfo.InvariantCulture, $"{site.Note}; ");
            }
        }

        sb.Append(CultureInfo.InvariantCulture,
            $"peers=[{string.Join(',', peers.Select(p => $"{p.ResourceId}#{p.SourceEntryId}:{FormatPerfInterval(p)}"))}]; ");
        if (conflicting is not null)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"conflict=#{conflicting.SourceEntryId}:{FormatPerfInterval(conflicting)} proj={conflicting.ProjectId ?? "—"}; ");
        }

        if (gps is null)
        {
            sb.Append("gps=none; sharedVehicleNote=geen aparte track bewijst geen afwezigheid");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"gpsState={gpsState}; mapping={gps.MappingKind}; object={gps.ObjectId ?? "—"}; trips={gps.Trips.Count}; "
                + $"sharedVehicleNote=geen aparte track bewijst geen afwezigheid");
        }

        if (peerGps is not null)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"; peerGpsObject={peerGps.ObjectId ?? "—"}; peerGpsPlate={peerGps.RegistrationPlate ?? "—"}");
        }

        return sb.ToString();
    }

    private static string FormatPlannedInterval(PlannedWorkGroup group) =>
        group.TimeFrom is null || group.TimeTo is null
            ? "—"
            : $"{group.TimeFrom:HH:mm}-{group.TimeTo:HH:mm}";

    private static string FormatPerfInterval(NormalizedPerformanceEntry entry) =>
        entry.Start is null || entry.End is null
            ? "—"
            : $"{entry.Start:HH:mm}-{entry.End:HH:mm}";

    private static decimal RoundHours(decimal hours) =>
        Math.Round(hours, 2, MidpointRounding.AwayFromZero);

    private enum GpsSupportState
    {
        Supports,
        NoData,
        Ambiguous,
        Contradicted,
    }
}

public enum MissingTechnicianTravelMode
{
    SharedTravelProven = 0,
    SeparateVehicleProven = 1,
    SharedTravelPossible = 2,
    GpsInsufficient = 3,
}
