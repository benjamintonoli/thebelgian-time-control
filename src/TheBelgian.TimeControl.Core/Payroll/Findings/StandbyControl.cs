using System.Globalization;
using System.Text;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// HFDTAAK 23 standby findings. Evidence/proposals only; never mutates payroll totals.
/// </summary>
public static class StandbyControl
{
    public const decimal PhoneMaxHours = 0.25m;
    public const int MismatchThresholdMinutes = 15;
    public const decimal MeaningfulMovementKm = 1.0m;
    public const int MeaningfulDrivingMinutes = 5;
    public static readonly TimeSpan MatchWindowPadding = TimeSpan.FromMinutes(30);

    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static IReadOnlyList<PayrollFinding> Evaluate(
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<PayrollPlanningReservation> planning,
        IReadOnlyList<StandbyGpsDayEvidence> gpsDays)
    {
        var findings = new List<PayrollFinding>();
        var gpsLookup = gpsDays
            .GroupBy(item => (item.ResourceId, item.Date))
            .ToDictionary(group => group.Key, group => group.First());

        var standbyRows = performances
            .Where(IsStandbyRow)
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.Start)
            .ThenBy(item => item.SortKey)
            .ToList();

        foreach (var intervention in GroupInterventions(standbyRows))
        {
            gpsLookup.TryGetValue((intervention.ResourceId, intervention.Date), out var gps);
            findings.AddRange(EvaluateIntervention(intervention, gps, planning, performances));
        }

        return findings
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToList();
    }

    public static StandbyGpsClassification ClassifyDay(StandbyGpsDayEvidence? gps)
    {
        if (gps is null)
        {
            return StandbyGpsClassification.NoGpsData;
        }

        if (gps.MappingAmbiguous)
        {
            return StandbyGpsClassification.Ambiguous;
        }

        if (!gps.HasVehicleMapping || !gps.HasUsableTrips)
        {
            return StandbyGpsClassification.NoGpsData;
        }

        if (HasAmbiguousStreams(gps.Trips))
        {
            return StandbyGpsClassification.Ambiguous;
        }

        return StandbyGpsClassification.PhoneOnly;
    }

    public static StandbyGpsClassification ClassifyIntervention(
        StandbyGpsDayEvidence? gps,
        DateTimeOffset? bookedStart,
        DateTimeOffset? bookedEnd)
    {
        var day = ClassifyDay(gps);
        if (day is StandbyGpsClassification.NoGpsData or StandbyGpsClassification.Ambiguous)
        {
            return day;
        }

        var synthetic = new StandbyIntervention(
            ResourceId: gps!.ResourceId,
            Date: gps.Date,
            BookedStart: bookedStart,
            BookedEnd: bookedEnd,
            BookedHours: 0m,
            ProjectId: null,
            ProjectNumber: null,
            BonNr: null,
            Description: null,
            Memo: null,
            PerformanceIds: [0]);
        return SelectMovementTrips(synthetic, gps).Count > 0
            ? StandbyGpsClassification.PhysicalIntervention
            : StandbyGpsClassification.PhoneOnly;
    }

    private static List<PayrollFinding> EvaluateIntervention(
        StandbyIntervention intervention,
        StandbyGpsDayEvidence? gps,
        IReadOnlyList<PayrollPlanningReservation> planning,
        IReadOnlyList<NormalizedPerformanceEntry> performances)
    {
        var findings = new List<PayrollFinding>();
        var dayClass = ClassifyDay(gps);
        var primaryId = intervention.PerformanceIds[0];
        var related = intervention.PerformanceIds;
        var bookedHours = intervention.BookedHours;

        if (dayClass == StandbyGpsClassification.NoGpsData)
        {
            findings.Add(new PayrollFinding(
                FindingKey: $"standby-nogps:{intervention.ResourceId}:{intervention.Date:yyyyMMdd}:{primaryId}",
                ResourceId: intervention.ResourceId,
                Date: intervention.Date,
                FindingType: PayrollFindingType.StandbyNoGpsData,
                Severity: PayrollFindingSeverity.Info,
                Title: "Wachtdienst zonder GPS-bewijs",
                Description: $"HFDTAAK 23 geboekt ({FormatHours(bookedHours)}) zonder bruikbare PowerFleet-data.",
                Evidence: BuildNoGpsEvidence(intervention, gps),
                SuggestedAction: "Controleer voertuigtoewijzing/GPS; niet automatisch als telefonisch behandelen.",
                RelatedPerformanceIds: related,
                BookedHours: bookedHours,
                GpsClassification: StandbyGpsClassification.NoGpsData.ToString()));
            return findings;
        }

        if (dayClass == StandbyGpsClassification.Ambiguous)
        {
            findings.Add(new PayrollFinding(
                FindingKey: $"standby-ambig:{intervention.ResourceId}:{intervention.Date:yyyyMMdd}:{primaryId}",
                ResourceId: intervention.ResourceId,
                Date: intervention.Date,
                FindingType: PayrollFindingType.StandbyAmbiguousEvidence,
                Severity: PayrollFindingSeverity.Review,
                Title: "Wachtdienst GPS-bewijs onduidelijk",
                Description: "PowerFleet-bewijs conflicteert of is onvoldoende betrouwbaar voor een doelinterval.",
                Evidence: BuildAmbiguousEvidence(intervention, gps!),
                SuggestedAction: "Manueel beoordelen; geen automatische doeluren.",
                RelatedPerformanceIds: related,
                BookedHours: bookedHours,
                GpsClassification: StandbyGpsClassification.Ambiguous.ToString()));
            return findings;
        }

        var movement = SelectMovementTrips(intervention, gps!);
        var classification = movement.Count > 0
            ? StandbyGpsClassification.PhysicalIntervention
            : StandbyGpsClassification.PhoneOnly;

        if (classification == StandbyGpsClassification.PhoneOnly)
        {
            if (bookedHours > PhoneMaxHours)
            {
                findings.Add(new PayrollFinding(
                    FindingKey: $"standby-phone15:{intervention.ResourceId}:{intervention.Date:yyyyMMdd}:{primaryId}",
                    ResourceId: intervention.ResourceId,
                    Date: intervention.Date,
                    FindingType: PayrollFindingType.StandbyPhoneExceeds15Min,
                    Severity: PayrollFindingSeverity.Review,
                    Title: "Wachtdienst telefonisch langer dan 15 min",
                    Description:
                        $"Geboekt {FormatHours(bookedHours)}; telefonisch maximum {FormatHours(PhoneMaxHours)}.",
                    Evidence: BuildPhoneEvidence(intervention, gps!),
                    SuggestedAction: $"Beperk betaalbare uren tot {FormatHours(PhoneMaxHours)} indien telefonisch.",
                    RelatedPerformanceIds: related,
                    BookedHours: bookedHours,
                    SuggestedPayableHours: PhoneMaxHours,
                    GpsClassification: classification.ToString()));
            }

            var phoneDossier = FindCompetingDossier(intervention, planning, performances, []);
            if (phoneDossier is not null)
            {
                findings.Add(phoneDossier);
            }

            return findings;
        }

        var gpsStart = movement.Min(item => item.Start);
        var gpsEnd = movement.Max(item => item.End);
        var gpsHours = RoundHours(Math.Max(0m, (decimal)(gpsEnd - gpsStart).TotalHours));
        var totalKm = movement.Sum(item => item.DistanceKilometres);
        var canCompare = intervention.BookedStart is not null
            && intervention.BookedEnd is not null
            && totalKm >= MeaningfulMovementKm;

        if (canCompare)
        {
            var bookedStart = intervention.BookedStart!.Value;
            var bookedEnd = intervention.BookedEnd!.Value;
            var startDiff = Math.Abs((bookedStart - gpsStart).TotalMinutes);
            var endDiff = Math.Abs((bookedEnd - gpsEnd).TotalMinutes);
            var durationDiffMinutes = Math.Abs((double)(bookedHours - gpsHours) * 60d);
            var evidence = BuildPhysicalEvidence(intervention, gps!, movement);

            if (startDiff > MismatchThresholdMinutes)
            {
                findings.Add(Mismatch(
                    intervention,
                    PayrollFindingType.StandbyStartMismatch,
                    "Wachtdienst start wijkt af van GPS",
                    $"Start geboekt {bookedStart:HH:mm} vs GPS {gpsStart:HH:mm} (Δ {startDiff:0} min).",
                    "Controleer of start gelijk moet lopen met fysiek vertrek.",
                    evidence,
                    bookedHours,
                    gpsStart,
                    gpsEnd,
                    gpsHours));
            }

            if (endDiff > MismatchThresholdMinutes)
            {
                findings.Add(Mismatch(
                    intervention,
                    PayrollFindingType.StandbyEndMismatch,
                    "Wachtdienst einde wijkt af van GPS",
                    $"Einde geboekt {bookedEnd:HH:mm} vs GPS {gpsEnd:HH:mm} (Δ {endDiff:0} min).",
                    "Controleer of einde gelijk moet lopen met fysieke terugkeer.",
                    evidence,
                    bookedHours,
                    gpsStart,
                    gpsEnd,
                    gpsHours));
            }

            if (durationDiffMinutes > MismatchThresholdMinutes)
            {
                findings.Add(Mismatch(
                    intervention,
                    PayrollFindingType.StandbyDurationMismatch,
                    "Wachtdienst duur wijkt af van GPS",
                    $"Geboekt {FormatHours(bookedHours)} vs GPS {FormatHours(gpsHours)} (Δ {durationDiffMinutes:0} min).",
                    "Controleer betaalbare duur t.o.v. fysieke interventie.",
                    evidence,
                    bookedHours,
                    gpsStart,
                    gpsEnd,
                    gpsHours,
                    plannedHours: gpsHours));
            }
        }

        var dossier = FindCompetingDossier(intervention, planning, performances, movement);
        if (dossier is not null)
        {
            findings.Add(dossier);
        }

        return findings;
    }

    private static PayrollFinding Mismatch(
        StandbyIntervention intervention,
        PayrollFindingType type,
        string title,
        string description,
        string action,
        string evidence,
        decimal bookedHours,
        DateTimeOffset gpsStart,
        DateTimeOffset gpsEnd,
        decimal gpsHours,
        decimal? plannedHours = null) =>
        new(
            FindingKey: $"{type}:{intervention.ResourceId}:{intervention.Date:yyyyMMdd}:{intervention.PerformanceIds[0]}",
            ResourceId: intervention.ResourceId,
            Date: intervention.Date,
            FindingType: type,
            Severity: PayrollFindingSeverity.High,
            Title: title,
            Description: description,
            Evidence: evidence,
            SuggestedAction: action,
            RelatedPerformanceIds: intervention.PerformanceIds,
            PlannedHours: plannedHours,
            BookedHours: bookedHours,
            SuggestedPayableStart: gpsStart,
            SuggestedPayableEnd: gpsEnd,
            SuggestedPayableHours: gpsHours,
            GpsClassification: StandbyGpsClassification.PhysicalIntervention.ToString());

    private static PayrollFinding? FindCompetingDossier(
        StandbyIntervention intervention,
        IReadOnlyList<PayrollPlanningReservation> planning,
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        List<StandbyGpsTripEvidence> movement)
    {
        var bookedProject = intervention.ProjectId;
        var competingPlanning = planning
            .Where(item =>
                string.Equals(item.ResourceId, intervention.ResourceId, StringComparison.Ordinal)
                && item.Date == intervention.Date
                && item.Classification is PayrollPlanningClassification.WorkReservation
                    or PayrollPlanningClassification.Ambiguous
                && !string.IsNullOrWhiteSpace(item.ProjectId)
                && !string.Equals(item.ProjectId, bookedProject, StringComparison.OrdinalIgnoreCase)
                && TimesOverlapPlanning(intervention, item))
            .OrderBy(item => item.IdCalendar)
            .ToList();

        if (competingPlanning.Count == 0 && movement.Count > 0)
        {
            competingPlanning = planning
                .Where(item =>
                    string.Equals(item.ResourceId, intervention.ResourceId, StringComparison.Ordinal)
                    && item.Date == intervention.Date
                    && item.Classification is PayrollPlanningClassification.WorkReservation
                        or PayrollPlanningClassification.Ambiguous
                    && !string.IsNullOrWhiteSpace(item.ProjectId)
                    && !string.Equals(item.ProjectId, bookedProject, StringComparison.OrdinalIgnoreCase)
                    && GpsOverlapsPlanning(movement, item))
                .OrderBy(item => item.IdCalendar)
                .ToList();
        }

        if (competingPlanning.Count == 0)
        {
            var competingPerf = performances
                .Where(item =>
                    string.Equals(item.ResourceId, intervention.ResourceId, StringComparison.Ordinal)
                    && item.Date == intervention.Date
                    && !IsStandbyRow(item)
                    && !item.IsCalendarSynthetic
                    && !string.IsNullOrWhiteSpace(item.ProjectId)
                    && !string.Equals(item.ProjectId, bookedProject, StringComparison.OrdinalIgnoreCase)
                    && TimesOverlapPerformance(intervention, item))
                .OrderBy(item => item.SortKey)
                .FirstOrDefault();
            if (competingPerf is null)
            {
                return null;
            }

            return new PayrollFinding(
                FindingKey: $"standby-dossier:{intervention.ResourceId}:{intervention.Date:yyyyMMdd}:{intervention.PerformanceIds[0]}",
                ResourceId: intervention.ResourceId,
                Date: intervention.Date,
                FindingType: PayrollFindingType.StandbyPossibleWrongDossier,
                Severity: PayrollFindingSeverity.High,
                Title: "Wachtdienst mogelijk op verkeerd dossier",
                Description:
                    $"Geboekt project {bookedProject ?? "—"} / BON {intervention.BonNr ?? "—"}; "
                    + $"concurrerend prestatie-project {competingPerf.ProjectId}.",
                Evidence:
                    $"source=same-day-performance; competingPerformanceId={competingPerf.SourceEntryId}; "
                    + $"competingBon={competingPerf.BonNr ?? "—"}; confidence=Review.",
                SuggestedAction: "Controleer dossier/klant vóór goedkeuring; geen automatische write.",
                RelatedPerformanceIds: intervention.PerformanceIds
                    .Concat([competingPerf.SourceEntryId])
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray(),
                BookedHours: intervention.BookedHours,
                SuggestedProjectId: competingPerf.ProjectId,
                SuggestedBonNr: competingPerf.BonNr,
                GpsClassification: StandbyGpsClassification.PhysicalIntervention.ToString());
        }

        var best = competingPlanning[0];
        return new PayrollFinding(
            FindingKey: $"standby-dossier:{intervention.ResourceId}:{intervention.Date:yyyyMMdd}:{intervention.PerformanceIds[0]}",
            ResourceId: intervention.ResourceId,
            Date: intervention.Date,
            FindingType: PayrollFindingType.StandbyPossibleWrongDossier,
            Severity: PayrollFindingSeverity.High,
            Title: "Wachtdienst mogelijk op verkeerd dossier",
            Description:
                $"Geboekt project {bookedProject ?? "—"} / BON {intervention.BonNr ?? "—"}; "
                + $"suggestie planning-project {best.ProjectId} (PROJNR {best.ProjectNumber?.ToString(CultureInfo.InvariantCulture) ?? "—"}).",
            Evidence:
                $"source=planning; idKalender={best.IdCalendar}; subject={best.Subject ?? "—"}; "
                + $"confidence=High; movementTrips={movement.Count}.",
            SuggestedAction: "Controleer dossier/klant vóór goedkeuring; geen automatische write.",
            RelatedPerformanceIds: intervention.PerformanceIds,
            BookedHours: intervention.BookedHours,
            SuggestedProjectId: best.ProjectId,
            SuggestedBonNr: null,
            GpsClassification: movement.Count > 0
                ? StandbyGpsClassification.PhysicalIntervention.ToString()
                : StandbyGpsClassification.PhoneOnly.ToString());
    }

    private static List<StandbyGpsTripEvidence> SelectMovementTrips(
        StandbyIntervention intervention,
        StandbyGpsDayEvidence gps)
    {
        DateTimeOffset start;
        DateTimeOffset end;
        if (intervention.BookedStart is not null && intervention.BookedEnd is not null)
        {
            start = intervention.BookedStart.Value - MatchWindowPadding;
            end = intervention.BookedEnd.Value + MatchWindowPadding;
        }
        else if (gps.Trips.Count > 0)
        {
            var offset = gps.Trips[0].Start.Offset;
            start = new DateTimeOffset(intervention.Date.ToDateTime(TimeOnly.MinValue), offset);
            end = new DateTimeOffset(intervention.Date.ToDateTime(new TimeOnly(23, 59, 59)), offset);
        }
        else
        {
            return [];
        }

        return gps.Trips
            .Where(trip => trip.Start < end && trip.End > start)
            .Where(trip =>
                trip.DistanceKilometres >= MeaningfulMovementKm
                || trip.DrivingMinutes >= MeaningfulDrivingMinutes)
            .OrderBy(trip => trip.Start)
            .ToList();
    }

    private static List<StandbyIntervention> GroupInterventions(
        IReadOnlyList<NormalizedPerformanceEntry> rows)
    {
        var groups = new List<StandbyIntervention>();
        foreach (var dayGroup in rows.GroupBy(item => (item.ResourceId, item.Date)))
        {
            var ordered = dayGroup
                .OrderBy(item => item.Start)
                .ThenBy(item => item.SortKey)
                .ToList();
            var current = new List<NormalizedPerformanceEntry>();
            foreach (var row in ordered)
            {
                if (current.Count == 0)
                {
                    current.Add(row);
                    continue;
                }

                var last = current[^1];
                if (IntervalsOverlapOrTouch(last, row))
                {
                    current.Add(row);
                }
                else
                {
                    groups.Add(ToIntervention(current));
                    current = [row];
                }
            }

            if (current.Count > 0)
            {
                groups.Add(ToIntervention(current));
            }
        }

        return groups;
    }

    private static StandbyIntervention ToIntervention(List<NormalizedPerformanceEntry> rows)
    {
        var primary = rows[0];
        var start = rows.Select(item => item.Start).Where(item => item is not null).Min();
        var end = rows.Select(item => item.End).Where(item => item is not null).Max();
        var hours = rows.Sum(BookedHours);
        return new StandbyIntervention(
            primary.ResourceId,
            primary.Date,
            start,
            end,
            hours,
            primary.ProjectId,
            primary.ProjectNumber,
            primary.BonNr,
            primary.Description,
            primary.Memo,
            rows.Select(item => item.SourceEntryId).Distinct().OrderBy(id => id).ToArray());
    }

    private static bool IntervalsOverlapOrTouch(
        NormalizedPerformanceEntry left,
        NormalizedPerformanceEntry right)
    {
        if (left.Start is null || left.End is null || right.Start is null || right.End is null)
        {
            return false;
        }

        var gap = right.Start.Value - left.End.Value;
        return right.Start < left.End || gap <= TimeSpan.FromMinutes(5);
    }

    private static bool IsStandbyRow(NormalizedPerformanceEntry entry) =>
        !entry.IsCalendarSynthetic
        && (entry.IsStandby || entry.HfdTaakId == 23);

    private static decimal BookedHours(NormalizedPerformanceEntry entry)
    {
        if (entry.AtlHoursRaw > 0m)
        {
            return entry.AtlHoursRaw;
        }

        if (entry.Start is not null && entry.End is not null && entry.End > entry.Start)
        {
            return (decimal)(entry.End.Value - entry.Start.Value).TotalHours;
        }

        return 0m;
    }

    private static bool HasAmbiguousStreams(IReadOnlyList<StandbyGpsTripEvidence> trips)
    {
        var keys = trips
            .Select(trip =>
                !string.IsNullOrWhiteSpace(trip.ObjectId)
                    ? "object:" + trip.ObjectId.Trim()
                    : !string.IsNullOrWhiteSpace(trip.VehiclePlate)
                        ? "plate:" + NormalizePlate(trip.VehiclePlate)
                        : null)
            .Where(key => key is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return keys.Count > 1;
    }

    private static bool TimesOverlapPlanning(
        StandbyIntervention intervention,
        PayrollPlanningReservation reservation)
    {
        if (intervention.BookedStart is null || intervention.BookedEnd is null
            || reservation.TimeFrom is null || reservation.TimeTo is null)
        {
            return true;
        }

        var from = reservation.TimeFrom.Value;
        var to = reservation.TimeTo.Value;
        var start = TimeOnly.FromTimeSpan(intervention.BookedStart.Value.TimeOfDay);
        var end = TimeOnly.FromTimeSpan(intervention.BookedEnd.Value.TimeOfDay);
        return from < end && to > start;
    }

    private static bool GpsOverlapsPlanning(
        List<StandbyGpsTripEvidence> movement,
        PayrollPlanningReservation reservation)
    {
        if (reservation.TimeFrom is null || reservation.TimeTo is null || movement.Count == 0)
        {
            return false;
        }

        var day = movement[0].Start;
        var planStart = new DateTimeOffset(DateOnly.FromDateTime(day.DateTime).ToDateTime(reservation.TimeFrom.Value), day.Offset);
        var planEnd = new DateTimeOffset(DateOnly.FromDateTime(day.DateTime).ToDateTime(reservation.TimeTo.Value), day.Offset);
        return movement.Any(trip => trip.Start < planEnd && trip.End > planStart);
    }

    private static bool TimesOverlapPerformance(
        StandbyIntervention intervention,
        NormalizedPerformanceEntry other)
    {
        if (intervention.BookedStart is null || intervention.BookedEnd is null
            || other.Start is null || other.End is null)
        {
            return false;
        }

        return other.Start < intervention.BookedEnd && other.End > intervention.BookedStart;
    }

    private static string BuildNoGpsEvidence(StandbyIntervention intervention, StandbyGpsDayEvidence? gps)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"perfIds={string.Join(',', intervention.PerformanceIds)}; ");
        sb.Append(CultureInfo.InvariantCulture, $"booked={FormatInterval(intervention)}; hours={FormatHours(intervention.BookedHours)}; ");
        sb.Append(CultureInfo.InvariantCulture, $"proj={intervention.ProjectId ?? "—"}; bon={intervention.BonNr ?? "—"}; ");
        if (gps is null)
        {
            sb.Append("gps=none");
        }
        else
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"mapping={gps.HasVehicleMapping}; ambiguous={gps.MappingAmbiguous}; object={gps.ObjectId ?? "—"}; trips={gps.Trips.Count}; reason={gps.MappingReason}");
        }

        return sb.ToString();
    }

    private static string BuildAmbiguousEvidence(StandbyIntervention intervention, StandbyGpsDayEvidence gps) =>
        BuildNoGpsEvidence(intervention, gps) + "; classification=Ambiguous";

    private static string BuildPhoneEvidence(StandbyIntervention intervention, StandbyGpsDayEvidence gps) =>
        BuildNoGpsEvidence(intervention, gps)
        + $"; classification=PhoneOnly; tripsInDay={gps.Trips.Count}; meaningfulMovement=false";

    private static string BuildPhysicalEvidence(
        StandbyIntervention intervention,
        StandbyGpsDayEvidence gps,
        List<StandbyGpsTripEvidence> movement)
    {
        var sb = new StringBuilder(BuildNoGpsEvidence(intervention, gps));
        sb.Append("; classification=PhysicalIntervention; homeReference=unavailable; ");
        sb.Append(CultureInfo.InvariantCulture,
            $"movement=[{string.Join("; ", movement.Select(item => $"{item.TripId}:{item.Start:HH:mm}-{item.End:HH:mm}/{item.DistanceKilometres.ToString("0.0", CultureInfo.InvariantCulture)}km"))}]");
        return sb.ToString();
    }

    private static string FormatInterval(StandbyIntervention intervention) =>
        intervention.BookedStart is null || intervention.BookedEnd is null
            ? "—"
            : $"{intervention.BookedStart:HH:mm}-{intervention.BookedEnd:HH:mm}";

    private static string FormatHours(decimal hours) =>
        hours.ToString("0.00", Belgian) + " u";

    private static decimal RoundHours(decimal hours) =>
        Math.Round(hours, 2, MidpointRounding.AwayFromZero);

    private static string NormalizePlate(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record StandbyIntervention(
        string ResourceId,
        DateOnly Date,
        DateTimeOffset? BookedStart,
        DateTimeOffset? BookedEnd,
        decimal BookedHours,
        string? ProjectId,
        int? ProjectNumber,
        string? BonNr,
        string? Description,
        string? Memo,
        IReadOnlyList<long> PerformanceIds);
}
