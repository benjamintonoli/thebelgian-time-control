using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

/// <summary>
/// Pure Project 200 workbench detail assembly. Evidence-only; never auto-validates.
/// </summary>
public static class PayrollProject200WorkbenchBuilder
{
    public const string MatchingReservationGeen = "Geen";
    public const string MissingGpsSummary = "Geen betrouwbare GPS-context beschikbaar";
    public const string GpsLoadingSummary = "GPS-context laden...";
    public const string UnsupportedActivityMessage =
        "Deze prestatie kan nog niet veilig vanuit TimeControl aangepast worden.";
    public const string SupportedDeleteMessage =
        "Volledige prestatie verwijderen beschikbaar (menselijke bevestiging verplicht; PlenionWriteService blokkeert bij afhankelijkheden).";
    public const string ZeroDeleteUnavailableMessage = SupportedDeleteMessage;

    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static PayrollProject200CaseDetail BuildDetail(
        PayrollAdminCase adminCase,
        IReadOnlyList<NormalizedPerformanceEntry> dayPerformances,
        IReadOnlyList<PayrollPlanningReservation> dayPlanning,
        StandbyGpsDayEvidence? gps,
        bool gpsPending = false,
        IReadOnlyDictionary<long, PayrollProject200ResolvedActivity>? activityByPerformanceId = null,
        string? bonTechnicianRemark = null,
        KnownLocationCatalog? knownLocations = null,
        bool includeTechnicalDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(adminCase);
        dayPerformances ??= [];
        dayPlanning ??= [];
        knownLocations ??= KnownLocationCatalog.Empty;

        var selected = ResolveSelectedPerformances(adminCase, dayPerformances);
        var bookedRows = selected
            .OrderBy(item => item.Start)
            .ThenBy(item => item.SortKey)
            .Select(item => new PayrollProject200BookedRow(
                PerformanceId: item.SourceEntryId,
                Start: item.Start,
                End: item.End,
                AtlHours: item.AtlHoursRaw,
                Description: item.Description,
                Memo: item.Memo,
                ProjectId: item.ProjectId ?? item.ProjectNumber?.ToString(CultureInfo.InvariantCulture),
                BonNr: item.BonNr,
                HfdTaakId: item.HfdTaakId,
                IsSelected: true,
                ProjectDisplayLabel: BuildProjectDisplayLabel(item.ProjectNumber, item.ProjectId, item.BonNr),
                ProjectNumber: item.ProjectNumber))
            .ToArray();

        var supportingIds = new HashSet<long>();
        var supporting = new List<PayrollPlanningReservation>();
        foreach (var perf in selected)
        {
            var matches = SpecialProjectTimeControl.FindMatchingReservations(perf, dayPlanning)
                .Where(PayrollPlanningClassifier.CanSupportSpecialProjectWork)
                .ToList();
            foreach (var match in matches)
            {
                if (supportingIds.Add(match.IdCalendar))
                {
                    supporting.Add(match);
                }
            }
        }

        var planningComparison = BuildPlanningComparison(bookedRows, supporting);
        var planningRows = dayPlanning
            .Where(item => item.Classification != PayrollPlanningClassification.Absence)
            .OrderBy(item => item.TimeFrom)
            .ThenBy(item => item.IdCalendar)
            .Select(item => new PayrollProject200PlanningRow(
                TimeFrom: item.TimeFrom,
                TimeTo: item.TimeTo,
                Label: BuildPlanningLabel(item),
                Classification: item.Classification,
                IsMatchingSupport: supportingIds.Contains(item.IdCalendar)))
            .ToArray();

        var matchingLabel = supporting.Count == 0
            ? "Geen passende planning gevonden"
            : supporting.Count == 1
                ? BuildPlanningLabel(supporting[0])
                : $"{supporting.Count} passende reservaties";

        var timeline = BuildNeighborTimeline(selected, dayPerformances);
        var gpsContext = gpsPending && gps is null
            ? new PayrollProject200GpsContext(
                Available: false,
                Summary: GpsLoadingSummary,
                Events: [],
                MappingKind: "Pending",
                ObjectIdCollapsed: null,
                TripsCollapsed: [],
                IsLoading: true)
            : BuildGpsContext(selected, gps, knownLocations);
        var corrections = BuildCorrectionTargets(
            selected,
            activityByPerformanceId,
            planningComparison.SuggestedAdjustStart,
            planningComparison.SuggestedAdjustEnd);
        var technician = BuildTechnicianContext(selected, bonTechnicianRemark, adminCase.BonNr);
        var dayTimeline = BuildUnifiedDayTimeline(selected, dayPerformances, dayPlanning, gps, gpsPending, knownLocations);
        var focused = BuildFocusedReviewContext(dayTimeline, selected) with
        {
            ContextSummary = planningComparison.TopSummary,
        };
        var technical = includeTechnicalDiagnostics
            ? (IReadOnlyList<string>)BuildTechnicalNotes(adminCase, selected, gps, technician)
            : Array.Empty<string>();
        var hasSupported = corrections.Any(item =>
            item.CorrectionCapability == PayrollProject200CorrectionCapability.SupportedVanTot);

        return new PayrollProject200CaseDetail(
            AdminCase: adminCase,
            BookedRows: bookedRows,
            MatchingReservationLabel: matchingLabel,
            DayPlanningRows: planningRows,
            NeighborTimelineRows: timeline,
            GpsContext: gpsContext,
            CorrectionTargets: corrections,
            TechnicalCollapsedNotes: technical,
            TechnicianContext: technician,
            DayTimeline: dayTimeline,
            HasSupportedTimeCorrection: hasSupported,
            FocusedContext: focused,
            PlanningComparison: planningComparison);
    }

    public static PayrollProject200PlanningComparison BuildPlanningComparison(
        IReadOnlyList<PayrollProject200BookedRow> bookedRows,
        IReadOnlyList<PayrollPlanningReservation> supporting)
    {
        var bookedHours = bookedRows.Sum(item => item.AtlHours);
        var bookedStart = bookedRows.Select(item => item.Start).Where(item => item is not null).Min();
        var bookedEnd = bookedRows.Select(item => item.End).Where(item => item is not null).Max();
        var bookedSummary = bookedStart is null || bookedEnd is null
            ? $"{FormatHoursShort(bookedHours)}"
            : $"{bookedStart:HH:mm}–{bookedEnd:HH:mm} · {FormatHoursShort(bookedHours)}";

        if (supporting.Count == 0)
        {
            return new PayrollProject200PlanningComparison(
                HasMatchingPlanning: false,
                MatchingCount: 0,
                BookedSummary: bookedSummary,
                PlannedSummary: null,
                DifferenceSummary: "Geen passende planning gevonden",
                TopSummary: $"Project 200 geboekt {bookedSummary}. Geen passende planning gevonden.",
                BookedHours: bookedHours,
                PlannedHours: null,
                DifferenceMinutes: null,
                SuggestedAdjustStart: null,
                SuggestedAdjustEnd: null);
        }

        var plannedHours = supporting
            .Select(item => item.PlannedHours)
            .Where(hours => hours is not null)
            .Select(hours => hours!.Value)
            .DefaultIfEmpty(0m)
            .Max();
        var primary = supporting
            .OrderByDescending(item => item.PlannedHours ?? 0m)
            .ThenBy(item => item.TimeFrom)
            .First();
        var plannedSummary = primary.TimeFrom is null || primary.TimeTo is null
            ? FormatHoursShort(plannedHours)
            : $"{primary.TimeFrom:HH:mm}–{primary.TimeTo:HH:mm} · {FormatHoursShort(plannedHours)}";
        var diffMinutes = (int)Math.Round((bookedHours - plannedHours) * 60m, MidpointRounding.AwayFromZero);
        var diffSummary = diffMinutes == 0
            ? "0 min"
            : diffMinutes > 0 ? $"+{diffMinutes} min" : $"{diffMinutes} min";
        var top = supporting.Count == 1
            ? $"Project 200 geboekt {bookedSummary}. Planning {plannedSummary}. Verschil {diffSummary}."
            : $"Project 200 geboekt {bookedSummary}. {supporting.Count} passende reservaties (max {FormatHoursShort(plannedHours)}). Verschil {diffSummary}.";

        TimeOnly? suggestStart = null;
        TimeOnly? suggestEnd = null;
        if (supporting.Count == 1
            && primary.TimeFrom is not null
            && primary.TimeTo is not null
            && bookedRows.Count == 1)
        {
            suggestStart = primary.TimeFrom;
            suggestEnd = primary.TimeTo;
        }

        return new PayrollProject200PlanningComparison(
            HasMatchingPlanning: true,
            MatchingCount: supporting.Count,
            BookedSummary: bookedSummary,
            PlannedSummary: plannedSummary,
            DifferenceSummary: diffSummary,
            TopSummary: top,
            BookedHours: bookedHours,
            PlannedHours: plannedHours,
            DifferenceMinutes: diffMinutes,
            SuggestedAdjustStart: suggestStart,
            SuggestedAdjustEnd: suggestEnd);
    }

    private static string FormatHoursShort(decimal hours)
    {
        var totalMinutes = (int)Math.Round(hours * 60m, MidpointRounding.AwayFromZero);
        var h = totalMinutes / 60;
        var m = Math.Abs(totalMinutes % 60);
        return m == 0 ? $"{h}u00" : $"{h}u{m:00}";
    }

    public static IReadOnlyList<PayrollProject200DayTimelineEntry> BuildUnifiedDayTimeline(
        IReadOnlyList<NormalizedPerformanceEntry> selectedPerformances,
        IReadOnlyList<NormalizedPerformanceEntry> dayPerformances,
        IReadOnlyList<PayrollPlanningReservation> dayPlanning,
        StandbyGpsDayEvidence? gps,
        bool gpsPending = false,
        KnownLocationCatalog? knownLocations = null)
    {
        selectedPerformances ??= [];
        dayPerformances ??= [];
        dayPlanning ??= [];
        knownLocations ??= KnownLocationCatalog.Empty;
        var entries = new List<PayrollProject200DayTimelineEntry>();
        var selectedIds = selectedPerformances.Select(item => item.SourceEntryId).ToHashSet();

        var starts = selectedPerformances
            .Where(item => item.Start is not null)
            .Select(item => item.Start!.Value)
            .ToArray();
        var ends = selectedPerformances
            .Where(item => item.End is not null)
            .Select(item => item.End!.Value)
            .ToArray();
        DateTimeOffset? windowStart = starts.Length == 0 ? null : starts.Min();
        DateTimeOffset? windowEnd = ends.Length == 0 ? null : ends.Max();

        foreach (var plan in dayPlanning.Where(item => item.Classification != PayrollPlanningClassification.Absence))
        {
            DateOnly date = default;
            if (selectedPerformances.Count > 0)
            {
                date = selectedPerformances[0].Date;
            }
            else if (dayPerformances.Count > 0)
            {
                date = dayPerformances[0].Date;
            }

            if (date == default || plan.TimeFrom is null)
            {
                continue;
            }

            var start = new DateTimeOffset(date.ToDateTime(plan.TimeFrom.Value), TimeSpan.Zero);
            var end = plan.TimeTo is null
                ? (DateTimeOffset?)null
                : new DateTimeOffset(date.ToDateTime(plan.TimeTo.Value), TimeSpan.Zero);
            entries.Add(new PayrollProject200DayTimelineEntry(
                SortAt: start,
                Start: start,
                End: end,
                Kind: PayrollProject200DayTimelineKind.Planning,
                Badge: "PLANNING",
                Title: BuildPlanningLabel(plan),
                Subtitle: null,
                IsSelected200: false));
        }

        foreach (var perf in dayPerformances.Where(item => !item.IsCalendarSynthetic && !item.IsAbsence && item.Start is not null))
        {
            var is300 = selectedIds.Contains(perf.SourceEntryId);
            var title = is300
                ? FormatDuration(perf.AtlHoursRaw)
                : BuildPerformanceTimelineLabel(perf);
            var subtitle = is300
                ? NormalizeTechnicianText(perf.Description)
                : null;
            entries.Add(new PayrollProject200DayTimelineEntry(
                SortAt: perf.Start!.Value,
                Start: perf.Start,
                End: perf.End,
                Kind: is300 ? PayrollProject200DayTimelineKind.Project200 : PayrollProject200DayTimelineKind.Performance,
                Badge: is300 ? "200" : "PRESTATIE",
                Title: title,
                Subtitle: subtitle,
                IsSelected200: is300));
        }

        if (!gpsPending && gps is { HasVehicleMapping: true } && gps.Trips.Count > 0 && windowStart is not null && windowEnd is not null)
        {
            entries.AddRange(BuildGpsTripChainEntries(
                gps.Trips,
                windowStart.Value,
                windowEnd.Value,
                knownLocations));
        }

        return entries
            .OrderBy(item => item.SortAt)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Badge, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Concise default review window around Project 200. Full day remains in FullDay.
    /// </summary>
    public static PayrollProject200FocusedContext BuildFocusedReviewContext(
        IReadOnlyList<PayrollProject200DayTimelineEntry> fullDay,
        IReadOnlyList<NormalizedPerformanceEntry> selectedPerformances)
    {
        fullDay ??= [];
        selectedPerformances ??= [];
        var ordered = fullDay
            .OrderBy(item => item.SortAt)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.Badge, StringComparer.Ordinal)
            .ToArray();

        var starts = selectedPerformances
            .Where(item => item.Start is not null)
            .Select(item => item.Start!.Value)
            .ToArray();
        var ends = selectedPerformances
            .Where(item => item.End is not null)
            .Select(item => item.End!.Value)
            .ToArray();
        if (starts.Length == 0 || ends.Length == 0)
        {
            var bookingOnly = ordered.Where(item => item.IsSelected200).ToArray();
            return new PayrollProject200FocusedContext(
                ContextSummary: null,
                Before: [],
                Booking: bookingOnly,
                After: [],
                FullDay: ordered,
                HasMoreThanFocused: ordered.Length > bookingOnly.Length);
        }

        var windowStart = starts.Min();
        var windowEnd = ends.Max();
        var booking = ordered.Where(item => item.IsSelected200).ToArray();
        var before = BuildFocusedBefore(ordered, windowStart);
        var after = BuildFocusedAfter(ordered, windowStart, windowEnd, before);
        var duringGps = ordered
            .Where(item =>
                item.Kind == PayrollProject200DayTimelineKind.Gps
                && !before.Contains(item)
                && !after.Contains(item)
                && EventOverlapsWindow(item, windowStart, windowEnd))
            .ToArray();
        var bookingSection = booking
            .Concat(duringGps)
            .OrderBy(item => item.SortAt)
            .ThenBy(item => item.Kind)
            .ToArray();

        var focusedKeys = before.Concat(bookingSection).Concat(after).ToHashSet();
        var summary = BuildDeterministicContextSummary(before, bookingSection, after, windowStart, windowEnd);

        return new PayrollProject200FocusedContext(
            ContextSummary: summary,
            Before: before,
            Booking: bookingSection,
            After: after,
            FullDay: ordered,
            HasMoreThanFocused: ordered.Any(item => !focusedKeys.Contains(item)));
    }

    public static string? BuildDeterministicContextSummary(
        IReadOnlyList<PayrollProject200DayTimelineEntry> before,
        IReadOnlyList<PayrollProject200DayTimelineEntry> booking,
        IReadOnlyList<PayrollProject200DayTimelineEntry> after,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var parts = new List<string>();
        var arrival = before
            .Concat(booking)
            .Where(IsGpsArrival)
            .Where(item => item.SortAt >= windowStart.AddMinutes(-15))
            .Where(item => item.SortAt <= windowEnd)
            .OrderBy(item => item.SortAt >= windowStart ? 0 : 1)
            .ThenBy(item => Math.Abs((item.SortAt - windowStart).TotalMinutes))
            .FirstOrDefault();
        if (arrival is null)
        {
            // Fallback: last arrival before booking when no near-window arrival exists.
            arrival = before
                .Concat(booking)
                .Where(IsGpsArrival)
                .Where(item => item.SortAt <= windowStart)
                .OrderByDescending(item => item.SortAt)
                .FirstOrDefault();
        }
        if (arrival is not null)
        {
            var loc = arrival.Locality ?? ExtractLocalityFromTitle(arrival.Title, "Aankomst");
            parts.Add(
                loc is null
                    ? "Aangekomen om " + FormatClock(arrival.SortAt)
                    : "Aangekomen bij " + loc + " om " + FormatClock(arrival.SortAt));
        }

        parts.Add("Project 200 geboekt " + FormatClock(windowStart) + "–" + FormatClock(windowEnd));

        var departure = after
            .Concat(booking)
            .Where(IsGpsDeparture)
            .OrderBy(item => item.SortAt)
            .FirstOrDefault(item => item.SortAt >= windowStart);
        if (departure is not null)
        {
            parts.Add("vertrokken om " + FormatClock(departure.SortAt));
        }

        var nextArrival = after
            .Where(IsGpsArrival)
            .OrderBy(item => item.SortAt)
            .FirstOrDefault();
        if (nextArrival is not null)
        {
            var loc = nextArrival.Locality ?? ExtractLocalityFromTitle(nextArrival.Title, "Aankomst");
            parts.Add(
                loc is null
                    ? "volgende bestemming om " + FormatClock(nextArrival.SortAt)
                    : "volgende bestemming " + loc + " om " + FormatClock(nextArrival.SortAt));
        }

        var sentence = string.Join(" · ", parts) + ".";
        var nextWork = after.FirstOrDefault(item =>
            item.Kind is PayrollProject200DayTimelineKind.Planning or PayrollProject200DayTimelineKind.Performance);
        if (nextWork is not null)
        {
            var kind = nextWork.Kind == PayrollProject200DayTimelineKind.Planning
                ? "Volgende planning"
                : "Volgende prestatie";
            var when = nextWork.Start is null
                ? null
                : FormatClock(nextWork.Start.Value)
                  + (nextWork.End is null ? "" : "–" + FormatClock(nextWork.End.Value));
            sentence += " " + kind + ": " + nextWork.Title
                + (when is null ? "." : " · " + when + ".");
        }

        return sentence;
    }

    private static PayrollProject200DayTimelineEntry[] BuildFocusedBefore(
        IReadOnlyList<PayrollProject200DayTimelineEntry> ordered,
        DateTimeOffset windowStart)
    {
        var gps = ordered.Where(item => item.Kind == PayrollProject200DayTimelineKind.Gps).ToArray();
        if (gps.Length == 0)
        {
            return [];
        }

        // Prefer arrival nearest to the booking start (incoming to the 200 context).
        var arrival = gps
            .Where(IsGpsArrival)
            .Where(item => item.SortAt <= windowStart.AddMinutes(45))
            .OrderBy(item => Math.Abs((item.SortAt - windowStart).TotalMinutes))
            .ThenByDescending(item => item.SortAt)
            .FirstOrDefault();
        if (arrival is null)
        {
            return gps
                .Where(item => item.SortAt < windowStart)
                .OrderByDescending(item => item.SortAt)
                .Take(4)
                .OrderBy(item => item.SortAt)
                .ToArray();
        }

        var arrivalIndex = Array.IndexOf(gps, arrival);
        var startIndex = arrivalIndex;
        for (var i = arrivalIndex - 1; i >= 0; i--)
        {
            var gap = (gps[i + 1].SortAt - (gps[i].End ?? gps[i].SortAt)).TotalMinutes;
            if (gap > 45)
            {
                break;
            }

            startIndex = i;
            if (arrivalIndex - startIndex >= 7)
            {
                break;
            }
        }

        // Keep arrival in VOOR when it is at/near booking start; later during-booking arrivals stay for booking section.
        var take = arrival.SortAt <= windowStart.AddMinutes(5)
            ? arrivalIndex - startIndex + 1
            : Math.Max(0, arrivalIndex - startIndex);
        return take == 0 ? [] : gps.Skip(startIndex).Take(take).ToArray();
    }

    private static PayrollProject200DayTimelineEntry[] BuildFocusedAfter(
        IReadOnlyList<PayrollProject200DayTimelineEntry> ordered,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        IReadOnlyList<PayrollProject200DayTimelineEntry> before)
    {
        var after = new List<PayrollProject200DayTimelineEntry>();
        var gps = ordered
            .Where(item => item.Kind == PayrollProject200DayTimelineKind.Gps && !before.Contains(item))
            .OrderBy(item => item.SortAt)
            .ToArray();

        var departure = gps
            .Where(IsGpsDeparture)
            .Where(item => item.SortAt >= windowStart)
            .OrderBy(item => item.SortAt)
            .FirstOrDefault();
        DateTimeOffset? nextArrivalAt = null;
        if (departure is not null)
        {
            after.Add(departure);
            var departureIndex = Array.IndexOf(gps, departure);
            for (var i = departureIndex + 1; i < gps.Length; i++)
            {
                var item = gps[i];
                if (item.Title.Contains("stilstand", StringComparison.OrdinalIgnoreCase)
                    && item.SortAt < windowEnd
                    && (item.End ?? item.SortAt) <= windowEnd)
                {
                    continue;
                }

                after.Add(item);
                if (IsGpsArrival(item))
                {
                    nextArrivalAt = item.SortAt;
                    break;
                }

                if (after.Count >= 4)
                {
                    break;
                }
            }
        }

        var cut = after.Count == 0
            ? windowEnd
            : after.Max(item => item.End ?? item.SortAt);
        var anchor = nextArrivalAt ?? cut;

        // Prefer next planning still active / starting around the immediate post-P200 destination.
        // Ignore all-day planning that started long before the booking.
        var nextPlanning = ordered
            .Where(item => item.Kind == PayrollProject200DayTimelineKind.Planning)
            .Where(item => (item.End ?? item.SortAt) > windowStart)
            .Where(item => item.SortAt >= windowStart.AddHours(-1))
            .Where(item => item.SortAt <= anchor.AddHours(1.5))
            .OrderBy(item =>
            {
                if (item.Start is not null && item.End is not null
                    && item.Start <= anchor && item.End >= anchor)
                {
                    return 0d;
                }

                return Math.Abs((item.SortAt - anchor).TotalMinutes);
            })
            .ThenBy(item => item.SortAt)
            .FirstOrDefault();

        var nextPerformance = ordered
            .Where(item => item.Kind == PayrollProject200DayTimelineKind.Performance && !item.IsSelected200)
            .Where(item => item.SortAt >= windowEnd)
            .Where(item => item.SortAt <= anchor.AddHours(1))
            .OrderBy(item => item.SortAt)
            .FirstOrDefault();

        // Prefer planning over an immediate technical performance when both exist.
        PayrollProject200DayTimelineEntry? nextWork = nextPlanning ?? nextPerformance;
        if (nextWork is not null && !after.Contains(nextWork))
        {
            after.Add(nextWork with
            {
                Subtitle = nextWork.Kind == PayrollProject200DayTimelineKind.Planning
                    ? "Volgende planning"
                    : "Volgende prestatie",
            });
        }

        return after
            .OrderBy(item => item.SortAt)
            .ThenBy(item => item.Kind)
            .ToArray();
    }

    private static bool EventOverlapsWindow(
        PayrollProject200DayTimelineEntry entry,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd)
    {
        var start = entry.Start ?? entry.SortAt;
        var end = entry.End ?? entry.SortAt;
        return start < windowEnd && end > windowStart;
    }

    private static bool IsGpsArrival(PayrollProject200DayTimelineEntry entry) =>
        entry.Kind == PayrollProject200DayTimelineKind.Gps
        && entry.Title.StartsWith("Aankomst", StringComparison.Ordinal);

    private static bool IsGpsDeparture(PayrollProject200DayTimelineEntry entry) =>
        entry.Kind == PayrollProject200DayTimelineKind.Gps
        && entry.Title.StartsWith("Vertrek", StringComparison.Ordinal);

    private static string? ExtractLocalityFromTitle(string title, string prefix)
    {
        if (string.IsNullOrWhiteSpace(title) || !title.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = title[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(rest) ? null : rest;
    }

    /// <summary>
    /// Explicit origin → destination trip chain. Does not invent missing arrivals.
    /// </summary>
    public static IReadOnlyList<PayrollProject200DayTimelineEntry> BuildGpsTripChainEntries(
        IReadOnlyList<StandbyGpsTripEvidence> trips,
        DateTimeOffset selectedStart,
        DateTimeOffset selectedEnd,
        KnownLocationCatalog? knownLocations = null)
    {
        knownLocations ??= KnownLocationCatalog.Empty;
        trips ??= [];
        var rangeStart = selectedStart.AddHours(-3);
        var rangeEnd = selectedEnd.AddHours(3);
        var ordered = trips
            .Where(t => t.Start < rangeEnd && t.End > rangeStart)
            .OrderBy(t => t.Start)
            .ThenBy(t => t.End)
            .ToArray();

        if (ordered.Length == 0)
        {
            return [];
        }

        var entries = new List<PayrollProject200DayTimelineEntry>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var trip = ordered[i];
            if (i > 0)
            {
                var previous = ordered[i - 1];
                var gapMinutes = (trip.Start - previous.End).TotalMinutes;
                if (gapMinutes >= 2)
                {
                    var stop = ResolvePoint(
                        knownLocations,
                        previous.EndLatitude,
                        previous.EndLongitude,
                        FirstAddress(previous.EndAddress, previous.StartAddress));
                    entries.Add(new PayrollProject200DayTimelineEntry(
                        SortAt: previous.End,
                        Start: previous.End,
                        End: trip.Start,
                        Kind: PayrollProject200DayTimelineKind.Gps,
                        Badge: "GPS",
                        Title: stop.Primary is null ? "stilstand" : "stilstand " + stop.Primary,
                        Subtitle: FormatOverlapSubtitle(previous.End, trip.Start, selectedStart, selectedEnd),
                        IsSelected200: false,
                        Locality: stop.Primary,
                        SecondaryDetail: stop.Secondary,
                        GpsRelation: ClassifyPhase(previous.End, trip.Start, selectedStart, selectedEnd)));
                }
            }

            if (IsLikelyStationary(trip))
            {
                var stop = ResolvePoint(
                    knownLocations,
                    trip.EndLatitude ?? trip.StartLatitude,
                    trip.EndLongitude ?? trip.StartLongitude,
                    FirstAddress(trip.EndAddress, trip.StartAddress));
                entries.Add(new PayrollProject200DayTimelineEntry(
                    SortAt: trip.Start,
                    Start: trip.Start,
                    End: trip.End,
                    Kind: PayrollProject200DayTimelineKind.Gps,
                    Badge: "GPS",
                    Title: stop.Primary is null ? "stilstand" : "stilstand " + stop.Primary,
                    Subtitle: FormatOverlapSubtitle(trip.Start, trip.End, selectedStart, selectedEnd),
                    IsSelected200: false,
                    Locality: stop.Primary,
                    SecondaryDetail: stop.Secondary,
                    GpsRelation: ClassifyPhase(trip.Start, trip.End, selectedStart, selectedEnd)));
                continue;
            }

            var origin = ResolvePoint(
                knownLocations,
                trip.StartLatitude,
                trip.StartLongitude,
                trip.StartAddress);
            var destinationReliable = HasReliableDestination(trip);
            var destination = destinationReliable
                ? ResolvePoint(knownLocations, trip.EndLatitude, trip.EndLongitude, trip.EndAddress)
                : null;

            string? departureSubtitle;
            if (!destinationReliable)
            {
                departureSubtitle = "Vertrek geregistreerd, bestemming niet betrouwbaar beschikbaar";
            }
            else if (trip.Start >= selectedStart && trip.Start < selectedEnd)
            {
                var minsBeforeEnd = Math.Max(
                    0,
                    (int)Math.Round((selectedEnd - trip.Start).TotalMinutes, MidpointRounding.AwayFromZero));
                departureSubtitle = minsBeforeEnd > 0
                    ? "Vertrek " + minsBeforeEnd + " min vóór einde boeking"
                    : "Vertrek tijdens 200-boeking";
            }
            else if (trip.Start == selectedEnd)
            {
                departureSubtitle = "Vertrek tijdens 200-boeking";
            }
            else
            {
                departureSubtitle = FormatOverlapSubtitle(trip.Start, trip.Start.AddMinutes(1), selectedStart, selectedEnd)
                    ?? (destination?.Primary is null ? null : "→ " + destination.Primary);
            }

            entries.Add(new PayrollProject200DayTimelineEntry(
                SortAt: trip.Start,
                Start: trip.Start,
                End: null,
                Kind: PayrollProject200DayTimelineKind.Gps,
                Badge: "GPS",
                Title: origin.Primary is null ? "Vertrek" : "Vertrek " + origin.Primary,
                Subtitle: departureSubtitle,
                IsSelected200: false,
                Locality: origin.Primary,
                SecondaryDetail: origin.Secondary,
                GpsRelation: ClassifyPhase(trip.Start, trip.Start.AddSeconds(1), selectedStart, selectedEnd)));

            if (destinationReliable && destination is not null)
            {
                entries.Add(new PayrollProject200DayTimelineEntry(
                    SortAt: trip.End,
                    Start: trip.End,
                    End: null,
                    Kind: PayrollProject200DayTimelineKind.Gps,
                    Badge: "GPS",
                    Title: destination.Primary is null ? "Aankomst" : "Aankomst " + destination.Primary,
                    Subtitle: FormatExactArrivalOverlap(trip.End, selectedStart, selectedEnd)
                        ?? FormatOverlapSubtitle(trip.End, trip.End, selectedStart, selectedEnd),
                    IsSelected200: false,
                    Locality: destination.Primary,
                    SecondaryDetail: destination.Secondary,
                    GpsRelation: ClassifyPhase(trip.End.AddSeconds(-1), trip.End, selectedStart, selectedEnd)));
            }
            else if (!destinationReliable)
            {
                entries.Add(new PayrollProject200DayTimelineEntry(
                    SortAt: trip.End,
                    Start: trip.End,
                    End: null,
                    Kind: PayrollProject200DayTimelineKind.Gps,
                    Badge: "GPS",
                    Title: "GPS-keten onvolledig",
                    Subtitle: "Bestemming/aankomst niet betrouwbaar beschikbaar",
                    IsSelected200: false,
                    Locality: null,
                    SecondaryDetail: null,
                    GpsRelation: ClassifyPhase(trip.End, trip.End, selectedStart, selectedEnd)));
            }
        }

        return entries;
    }

    public static string? FormatExactOverlapWording(
        DateTimeOffset eventStart,
        DateTimeOffset eventEnd,
        DateTimeOffset selectedStart,
        DateTimeOffset selectedEnd)
    {
        var overlapStart = eventStart > selectedStart ? eventStart : selectedStart;
        var overlapEnd = eventEnd < selectedEnd ? eventEnd : selectedEnd;
        if (overlapEnd <= overlapStart)
        {
            return null;
        }

        var overlapMinutes = Math.Max(1, (int)Math.Round((overlapEnd - overlapStart).TotalMinutes, MidpointRounding.AwayFromZero));
        var relation = ClassifyGpsOverlap(eventStart, eventEnd, selectedStart, selectedEnd);

        if (relation == "Departure"
            || (eventStart >= selectedStart && eventStart < selectedEnd && eventEnd > selectedEnd))
        {
            return "Vertrek tijdens 200-boeking";
        }

        if (eventEnd >= selectedStart
            && eventEnd <= selectedEnd
            && eventStart < selectedStart)
        {
            var minsBeforeEnd = Math.Max(0, (int)Math.Round((selectedEnd - eventEnd).TotalMinutes, MidpointRounding.AwayFromZero));
            return "Aankomst " + minsBeforeEnd + " min vóór einde 200-boeking";
        }

        if (relation == "During")
        {
            return "Overlapt " + overlapMinutes + " min met 200-boeking";
        }

        return "Overlapt " + overlapMinutes + " min met 200-boeking";
    }

    private static string? FormatOverlapSubtitle(
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset selectedStart,
        DateTimeOffset selectedEnd) =>
        FormatExactOverlapWording(start, end <= start ? start.AddMinutes(1) : end, selectedStart, selectedEnd);

    private static string? FormatExactArrivalOverlap(
        DateTimeOffset arrival,
        DateTimeOffset selectedStart,
        DateTimeOffset selectedEnd)
    {
        if (arrival < selectedStart || arrival > selectedEnd)
        {
            return FormatExactOverlapWording(arrival, arrival.AddMinutes(1), selectedStart, selectedEnd);
        }

        var minsBeforeEnd = Math.Max(0, (int)Math.Round((selectedEnd - arrival).TotalMinutes, MidpointRounding.AwayFromZero));
        return "Aankomst " + minsBeforeEnd + " min vóór einde 200-boeking";
    }

    private static string ClassifyPhase(
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset selectedStart,
        DateTimeOffset selectedEnd)
    {
        if (end <= selectedStart)
        {
            return "Before";
        }

        if (start >= selectedEnd)
        {
            return "After";
        }

        return ClassifyGpsOverlap(start, end, selectedStart, selectedEnd) switch
        {
            "Departure" => "Departure",
            "Overlap" => "Overlap",
            "During" => "During",
            _ => "During",
        };
    }

    private static bool HasReliableDestination(StandbyGpsTripEvidence trip) =>
        !string.IsNullOrWhiteSpace(trip.EndAddress)
        || (trip.EndLatitude is not null && trip.EndLongitude is not null);

    private static bool IsLikelyStationary(StandbyGpsTripEvidence trip)
    {
        if (trip.DistanceKilometres <= 0.25m && trip.DrivingMinutes <= 3)
        {
            return true;
        }

        var startLoc = ExtractLocality(trip.StartAddress);
        var endLoc = ExtractLocality(trip.EndAddress);
        return trip.DistanceKilometres <= 0.5m
            && !string.IsNullOrWhiteSpace(startLoc)
            && string.Equals(startLoc, endLoc, StringComparison.OrdinalIgnoreCase)
            && trip.DrivingMinutes <= 8;
    }

    private static ResolvedKnownLocation ResolvePoint(
        KnownLocationCatalog catalog,
        decimal? latitude,
        decimal? longitude,
        string? address) =>
        catalog.Resolve(latitude, longitude, address);

    private static string FormatDuration(decimal atlHours)
    {
        var minutes = (int)Math.Round(atlHours * 60m, MidpointRounding.AwayFromZero);
        if (minutes < 60)
        {
            return minutes + " min";
        }

        var h = minutes / 60;
        var m = minutes % 60;
        return m == 0 ? h + " u" : h + " u " + m + " min";
    }

    public static PayrollProject200TechnicianContext BuildTechnicianContext(
        IReadOnlyList<NormalizedPerformanceEntry> selectedPerformances,
        string? bonTechnicianRemark,
        string? fallbackBonNr = null)
    {
        selectedPerformances ??= [];
        var bonNr = selectedPerformances
            .Select(item => item.BonNr)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
            ?? fallbackBonNr;
        var bonText = NormalizeTechnicianText(bonTechnicianRemark);

        var remarks = selectedPerformances
            .OrderBy(item => item.Start)
            .ThenBy(item => item.SortKey)
            .Select(item =>
            {
                var omschr = NormalizeTechnicianText(item.Description);
                var memo = NormalizeTechnicianText(item.Memo);
                var showMemo = memo is not null
                    && !string.Equals(omschr, memo, StringComparison.OrdinalIgnoreCase);
                return new PayrollProject200PerformanceRemark(
                    item.SourceEntryId,
                    item.Start,
                    item.End,
                    item.AtlHoursRaw,
                    omschr,
                    memo,
                    showMemo);
            })
            .ToArray();

        var hasAny = bonText is not null
            || remarks.Any(item => item.PrestOmschr is not null || item.PrestMemo is not null);

        return new PayrollProject200TechnicianContext(
            BonTechnicianRemark: bonText,
            BonNr: string.IsNullOrWhiteSpace(bonNr) ? null : bonNr.Trim(),
            BonRemarkSourceField: PayrollProject200TechnicianContext.BonMemoSourceField,
            PerformanceRemarks: remarks,
            HasAnyTechnicianText: hasAny);
    }

    public static string? BuildQueueTechnicianPreview(
        string? prestDescription,
        string? prestMemo,
        string? bonTechnicianRemark,
        int maxLength = 72)
    {
        var text = NormalizeTechnicianText(prestDescription)
            ?? NormalizeTechnicianText(prestMemo)
            ?? NormalizeTechnicianText(bonTechnicianRemark);
        if (text is null)
        {
            return null;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 1)].TrimEnd() + "…";
    }

    public static string? NormalizeTechnicianText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed is "—" or "-" or "–")
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Human-facing project label: prefer meaningful BON, then Project 100/200/300, then project number.
    /// Avoids raw internal database ids and meaningless BON 0 as the primary label.
    /// </summary>
    public static string BuildProjectDisplayLabel(
        int? projectNumber,
        string? projectId = null,
        string? bonNr = null)
    {
        if (IsMeaningfulBonNr(bonNr))
        {
            return "BON " + bonNr!.Trim();
        }

        if (projectNumber is 100 or 200 or 300)
        {
            return "Project " + projectNumber.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (projectNumber is > 0)
        {
            return projectNumber.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            var trimmed = projectId.Trim();
            if (!LooksLikeRawInternalId(trimmed))
            {
                return trimmed;
            }
        }

        return "—";
    }

    public static bool IsMeaningfulBonNr(string? bonNr)
    {
        if (string.IsNullOrWhiteSpace(bonNr))
        {
            return false;
        }

        var trimmed = bonNr.Trim();
        if (trimmed.All(ch => ch == '0'))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Timeline label for non-selected performances. Never primary-labels as "BON 0".
    /// </summary>
    public static string BuildPerformanceTimelineLabel(NormalizedPerformanceEntry performance)
    {
        ArgumentNullException.ThrowIfNull(performance);
        if (IsMeaningfulBonNr(performance.BonNr))
        {
            return "BON " + performance.BonNr!.Trim();
        }

        var description = NormalizeTechnicianText(performance.Description);
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description.Length <= 72 ? description : description[..72].TrimEnd() + "…";
        }

        var memo = NormalizeTechnicianText(performance.Memo);
        if (!string.IsNullOrWhiteSpace(memo))
        {
            return memo.Length <= 72 ? memo : memo[..72].TrimEnd() + "…";
        }

        if (performance.ProjectNumber is > 0 and not 200)
        {
            return "Project " + performance.ProjectNumber.Value.ToString(CultureInfo.InvariantCulture);
        }

        return "Andere prestatie";
    }

    private static bool LooksLikeRawInternalId(string value) =>
        value.Length >= 4
        && value.All(char.IsDigit);

    private static NormalizedPerformanceEntry[] ResolveSelectedPerformances(
        PayrollAdminCase adminCase,
        IReadOnlyList<NormalizedPerformanceEntry> dayPerformances)
    {
        var ids = adminCase.UnderlyingCases
            .Select(item => item.PrimaryPerformanceId)
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToHashSet();

        if (ids.Count > 0)
        {
            var matched = dayPerformances
                .Where(item => ids.Contains(item.SourceEntryId))
                .OrderBy(item => item.Start)
                .ThenBy(item => item.SortKey)
                .ToArray();
            if (matched.Length > 0)
            {
                return matched;
            }
        }

        return dayPerformances
            .Where(item =>
                item.Date == adminCase.Date
                && item.ProjectNumber == 200
                && !item.IsCalendarSynthetic
                && !item.IsAbsence)
            .OrderBy(item => item.Start)
            .ThenBy(item => item.SortKey)
            .ToArray();
    }

    private static PayrollProject200TimelineRow[] BuildNeighborTimeline(
        NormalizedPerformanceEntry[] selected,
        IReadOnlyList<NormalizedPerformanceEntry> dayPerformances)
    {
        var selectedIds = selected.Select(item => item.SourceEntryId).ToHashSet();
        var real = dayPerformances
            .Where(item => !item.IsCalendarSynthetic && !item.IsAbsence)
            .OrderBy(item => item.Start ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.SortKey)
            .ToArray();

        if (selected.Length == 0)
        {
            return [.. real.Select(item => ToTimelineRow(item, PayrollProject200TimelineKind.Other, selectedIds))];
        }

        var windowStart = selected.Min(item => item.Start ?? DateTimeOffset.MaxValue);
        var windowEnd = selected.Max(item => item.End ?? DateTimeOffset.MinValue);

        NormalizedPerformanceEntry? previous = null;
        NormalizedPerformanceEntry? next = null;
        foreach (var row in real)
        {
            if (selectedIds.Contains(row.SourceEntryId))
            {
                continue;
            }

            if (row.End is { } end && end <= windowStart)
            {
                previous = row;
            }
            else if (next is null && row.Start is { } start && start >= windowEnd)
            {
                next = row;
            }
        }

        var rows = new List<PayrollProject200TimelineRow>();
        if (previous is not null)
        {
            rows.Add(ToTimelineRow(previous, PayrollProject200TimelineKind.Previous, selectedIds));
        }

        foreach (var item in selected.OrderBy(item => item.Start).ThenBy(item => item.SortKey))
        {
            rows.Add(ToTimelineRow(item, PayrollProject200TimelineKind.Selected, selectedIds));
        }

        if (next is not null)
        {
            rows.Add(ToTimelineRow(next, PayrollProject200TimelineKind.Next, selectedIds));
        }

        return [.. rows];
    }

    private static PayrollProject200TimelineRow ToTimelineRow(
        NormalizedPerformanceEntry entry,
        PayrollProject200TimelineKind kind,
        HashSet<long> selectedIds) =>
        new(
            Kind: kind,
            PerformanceId: entry.SourceEntryId,
            Start: entry.Start,
            End: entry.End,
            ProjectLabel: BuildProjectDisplayLabel(entry.ProjectNumber, entry.ProjectId, entry.BonNr),
            Description: entry.Description,
            IsSelected200: selectedIds.Contains(entry.SourceEntryId));

    private static PayrollProject200GpsContext BuildGpsContext(
        NormalizedPerformanceEntry[] selected,
        StandbyGpsDayEvidence? gps,
        KnownLocationCatalog? knownLocations = null)
    {
        knownLocations ??= KnownLocationCatalog.Empty;
        if (gps is null
            || !gps.HasVehicleMapping
            || gps.Trips.Count == 0
            || selected.Length == 0
            || selected.All(item => item.Start is null || item.End is null))
        {
            return new PayrollProject200GpsContext(
                Available: false,
                Summary: MissingGpsSummary,
                Events: [],
                MappingKind: gps?.MappingKind ?? "None",
                ObjectIdCollapsed: gps?.ObjectId,
                TripsCollapsed: gps?.Trips ?? []);
        }

        var evidence = gps;
        var selectedStart = selected.Min(item => item.Start!.Value);
        var selectedEnd = selected.Max(item => item.End!.Value);
        var chain = BuildGpsTripChainEntries(evidence.Trips, selectedStart, selectedEnd, knownLocations);
        if (chain.Count == 0)
        {
            return new PayrollProject200GpsContext(
                Available: false,
                Summary: MissingGpsSummary,
                Events: [],
                MappingKind: evidence.MappingKind,
                ObjectIdCollapsed: evidence.ObjectId,
                TripsCollapsed: evidence.Trips);
        }

        var events = chain
            .Select(item => new PayrollProject200GpsEvent(
                At: item.Start ?? item.SortAt,
                End: item.End,
                Label: item.Title,
                Detail: string.IsNullOrWhiteSpace(item.Subtitle)
                    ? item.SecondaryDetail
                    : string.IsNullOrWhiteSpace(item.SecondaryDetail)
                        ? item.Subtitle
                        : item.Subtitle + " · " + item.SecondaryDetail,
                Phase: item.GpsRelation ?? ""))
            .ToList();

        var summary =
            $"{events.Count} GPS-punt(en) rond geboekt venster "
            + $"({FormatClock(selectedStart)}–{FormatClock(selectedEnd)}); "
            + PayrollProject200CaseDetail.GpsNeverValidatesNote;

        return new PayrollProject200GpsContext(
            Available: true,
            Summary: summary,
            Events: events,
            MappingKind: evidence.MappingKind,
            ObjectIdCollapsed: evidence.ObjectId,
            TripsCollapsed: evidence.Trips);
    }

    /// <summary>
    /// Full / partial overlap vs booked window. Partial → Overlap/Departure wording.
    /// </summary>
    public static string ClassifyGpsOverlap(
        DateTimeOffset tripStart,
        DateTimeOffset tripEnd,
        DateTimeOffset selectedStart,
        DateTimeOffset selectedEnd)
    {
        var overlapStart = tripStart > selectedStart ? tripStart : selectedStart;
        var overlapEnd = tripEnd < selectedEnd ? tripEnd : selectedEnd;
        if (overlapEnd <= overlapStart)
        {
            return "None";
        }

        var overlapMinutes = (overlapEnd - overlapStart).TotalMinutes;
        var tripMinutes = Math.Max(1, (tripEnd - tripStart).TotalMinutes);
        var windowMinutes = Math.Max(1, (selectedEnd - selectedStart).TotalMinutes);
        var coversMostOfTrip = overlapMinutes >= tripMinutes * 0.8;
        var coversMostOfWindow = overlapMinutes >= windowMinutes * 0.8;
        if (coversMostOfTrip || coversMostOfWindow)
        {
            return "During";
        }

        if (tripStart >= selectedStart && tripStart < selectedEnd && tripEnd > selectedEnd)
        {
            return "Departure";
        }

        return "Overlap";
    }

    public static string? ExtractLocality(string? addressOrLabel)
    {
        if (string.IsNullOrWhiteSpace(addressOrLabel))
        {
            return null;
        }

        var text = addressOrLabel.Trim();
        // Prefer Belgian postcode locality: "1785 Merchtem"
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"\b\d{4}\s+([A-Za-zÀ-ÿ'’\-]+(?:\s+[A-Za-zÀ-ÿ'’\-]+){0,3})\b");
        if (match.Success)
        {
            return match.Groups[1].Value.Trim().TrimEnd(',', '.');
        }

        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
        {
            var candidate = parts[^2];
            candidate = System.Text.RegularExpressions.Regex.Replace(candidate, @"^\d{4}\s+", "").Trim();
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 40)
            {
                return candidate;
            }
        }

        return text.Length <= 32 ? text : null;
    }

    private static string? FirstAddress(params string?[] values)
    {
        foreach (var value in values)
        {
            var trimmed = TrimOrNull(value);
            if (trimmed is not null)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<PayrollProject200CorrectionTarget> BuildCorrectionTargets(
        NormalizedPerformanceEntry[] selected,
        IReadOnlyDictionary<long, PayrollProject200ResolvedActivity>? activityByPerformanceId,
        TimeOnly? suggestedAdjustStart = null,
        TimeOnly? suggestedAdjustEnd = null)
    {
        var targets = new List<PayrollProject200CorrectionTarget>();
        foreach (var item in selected.OrderBy(item => item.Start).ThenBy(item => item.SortKey))
        {
            if (activityByPerformanceId is not null
                && activityByPerformanceId.TryGetValue(item.SourceEntryId, out var resolved))
            {
                targets.Add(new PayrollProject200CorrectionTarget(
                    PerformanceId: item.SourceEntryId,
                    CurrentStart: item.Start,
                    CurrentEnd: item.End,
                    AtlHours: item.AtlHoursRaw,
                    HfdTaakId: item.HfdTaakId,
                    ActivityType: resolved.ActivityType,
                    CorrectionCapability: resolved.Supported
                        ? PayrollProject200CorrectionCapability.SupportedVanTot
                        : PayrollProject200CorrectionCapability.UnsupportedActivity,
                    CapabilityMessage: BuildResolvedCapabilityMessage(resolved),
                    FriendlyTaskName: resolved.FriendlyTaskName,
                    SuggestedAdjustStart: suggestedAdjustStart,
                    SuggestedAdjustEnd: suggestedAdjustEnd));
            }
            else if (item.HfdTaakId == PayrollStandbyActivityTypes.WaitingMainTaskExternalId)
            {
                targets.Add(new PayrollProject200CorrectionTarget(
                    PerformanceId: item.SourceEntryId,
                    CurrentStart: item.Start,
                    CurrentEnd: item.End,
                    AtlHours: item.AtlHoursRaw,
                    HfdTaakId: item.HfdTaakId,
                    ActivityType: PayrollStandbyActivityTypes.WaitingTime,
                    CorrectionCapability: PayrollProject200CorrectionCapability.SupportedVanTot,
                    CapabilityMessage: "VAN/TOT-correctie beschikbaar voor wachttijd (HFDTAAK 23).",
                    SuggestedAdjustStart: suggestedAdjustStart,
                    SuggestedAdjustEnd: suggestedAdjustEnd));
            }
            else
            {
                targets.Add(new PayrollProject200CorrectionTarget(
                    PerformanceId: item.SourceEntryId,
                    CurrentStart: item.Start,
                    CurrentEnd: item.End,
                    AtlHours: item.AtlHoursRaw,
                    HfdTaakId: item.HfdTaakId,
                    ActivityType: null,
                    CorrectionCapability: PayrollProject200CorrectionCapability.UnsupportedActivity,
                    CapabilityMessage: UnsupportedActivityMessage,
                    SuggestedAdjustStart: suggestedAdjustStart,
                    SuggestedAdjustEnd: suggestedAdjustEnd));
            }

            targets.Add(new PayrollProject200CorrectionTarget(
                PerformanceId: item.SourceEntryId,
                CurrentStart: item.Start,
                CurrentEnd: item.End,
                AtlHours: item.AtlHoursRaw,
                HfdTaakId: item.HfdTaakId,
                ActivityType: item.HfdTaakId == PayrollStandbyActivityTypes.WaitingMainTaskExternalId
                    ? PayrollStandbyActivityTypes.WaitingTime
                    : activityByPerformanceId is not null
                        && activityByPerformanceId.TryGetValue(item.SourceEntryId, out var zeroResolved)
                        ? zeroResolved.ActivityType
                        : null,
                CorrectionCapability: PayrollProject200CorrectionCapability.SupportedDelete,
                CapabilityMessage: SupportedDeleteMessage,
                FriendlyTaskName: activityByPerformanceId is not null
                    && activityByPerformanceId.TryGetValue(item.SourceEntryId, out var zeroFriendly)
                    ? zeroFriendly.FriendlyTaskName
                    : null));
        }

        return targets;
    }

    private static string BuildResolvedCapabilityMessage(PayrollProject200ResolvedActivity resolved)
    {
        if (resolved.Supported)
        {
            return string.IsNullOrWhiteSpace(resolved.Message)
                ? "VAN/TOT-correctie beschikbaar."
                : resolved.Message;
        }

        var message = string.IsNullOrWhiteSpace(resolved.Message)
            ? UnsupportedActivityMessage
            : resolved.Message;

        if (!string.IsNullOrWhiteSpace(resolved.FriendlyTaskName)
            && !message.Contains(resolved.FriendlyTaskName, StringComparison.Ordinal))
        {
            return message + " (" + resolved.FriendlyTaskName.Trim() + ")";
        }

        return message;
    }

    private static List<string> BuildTechnicalNotes(
        PayrollAdminCase adminCase,
        NormalizedPerformanceEntry[] selected,
        StandbyGpsDayEvidence? gps,
        PayrollProject200TechnicianContext? technician = null)
    {
        var notes = new List<string>
        {
            $"AdminCaseKey={adminCase.AdminCaseKey}",
            $"FindingKeys={string.Join(',', adminCase.FindingKeys)}",
            $"SelectedPerformanceIds={string.Join(',', selected.Select(item => item.SourceEntryId))}",
            PayrollProject200CaseDetail.GpsNeverValidatesNote,
            $"MatchingReservation={MatchingReservationGeen}",
            "TechnicianPrestText=PROJ_Prest.OMSCHR + PROJ_Prest.MEMO (not merged with BON)",
        };

        if (technician is not null)
        {
            notes.Add(
                $"BonTechnicianRemarkSource={technician.BonRemarkSourceField}; BONNR={technician.BonNr ?? "—"}; present={(technician.BonTechnicianRemark is null ? "no" : "yes")}");
        }

        if (gps is not null)
        {
            notes.Add($"GpsMappingKind={gps.MappingKind}; ObjectId={gps.ObjectId ?? "—"}; Trips={gps.Trips.Count}");
            if (!string.IsNullOrWhiteSpace(gps.MappingReason))
            {
                notes.Add($"GpsMappingReason={gps.MappingReason}");
            }
        }
        else
        {
            notes.Add("Gps=none");
        }

        return notes;
    }

    private static string BuildPlanningLabel(PayrollPlanningReservation reservation)
    {
        var subject = string.IsNullOrWhiteSpace(reservation.Subject)
            ? reservation.TaskTypeName
            : reservation.Subject;
        var project = reservation.ProjectId
            ?? reservation.ProjectNumber?.ToString(CultureInfo.InvariantCulture)
            ?? "—";
        var time = reservation.TimeFrom is null || reservation.TimeTo is null
            ? "—"
            : $"{reservation.TimeFrom:HH:mm}–{reservation.TimeTo:HH:mm}";
        return $"{time} · {project} · {subject ?? "—"}";
    }

    private static string FormatClock(DateTimeOffset value) =>
        value.ToString("HH:mm", Belgian);
}
