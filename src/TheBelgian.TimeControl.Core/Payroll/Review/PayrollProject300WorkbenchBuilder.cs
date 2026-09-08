using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

/// <summary>
/// Pure Project 300 workbench detail assembly. Evidence-only; never auto-validates.
/// </summary>
public static class PayrollProject300WorkbenchBuilder
{
    public const string MatchingReservationGeen = "Geen";
    public const string MissingGpsSummary = "Geen betrouwbare GPS-context beschikbaar";
    public const string GpsLoadingSummary = "GPS-context laden...";
    public const string UnsupportedActivityMessage =
        "Deze prestatie kan nog niet veilig vanuit TimeControl aangepast worden.";
    public const string ZeroDeleteUnavailableMessage =
        "Volledig verwijderen/nul zetten is nog niet veilig ondersteund.";

    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static PayrollProject300CaseDetail BuildDetail(
        PayrollAdminCase adminCase,
        IReadOnlyList<NormalizedPerformanceEntry> dayPerformances,
        IReadOnlyList<PayrollPlanningReservation> dayPlanning,
        StandbyGpsDayEvidence? gps,
        bool gpsPending = false,
        IReadOnlyDictionary<long, PayrollProject300ResolvedActivity>? activityByPerformanceId = null)
    {
        ArgumentNullException.ThrowIfNull(adminCase);
        dayPerformances ??= [];
        dayPlanning ??= [];

        var selected = ResolveSelectedPerformances(adminCase, dayPerformances);
        var bookedRows = selected
            .OrderBy(item => item.Start)
            .ThenBy(item => item.SortKey)
            .Select(item => new PayrollProject300BookedRow(
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

        var planningRows = dayPlanning
            .Where(item => item.Classification != PayrollPlanningClassification.Absence)
            .OrderBy(item => item.TimeFrom)
            .ThenBy(item => item.IdCalendar)
            .Select(item => new PayrollProject300PlanningRow(
                TimeFrom: item.TimeFrom,
                TimeTo: item.TimeTo,
                Label: BuildPlanningLabel(item),
                Classification: item.Classification,
                IsMatchingSupport: false))
            .ToArray();

        var timeline = BuildNeighborTimeline(selected, dayPerformances);
        var gpsContext = gpsPending && gps is null
            ? new PayrollProject300GpsContext(
                Available: false,
                Summary: GpsLoadingSummary,
                Events: [],
                MappingKind: "Pending",
                ObjectIdCollapsed: null,
                TripsCollapsed: [],
                IsLoading: true)
            : BuildGpsContext(selected, gps);
        var corrections = BuildCorrectionTargets(selected, activityByPerformanceId);
        var technical = BuildTechnicalNotes(adminCase, selected, gps);

        return new PayrollProject300CaseDetail(
            AdminCase: adminCase,
            BookedRows: bookedRows,
            MatchingReservationLabel: MatchingReservationGeen,
            DayPlanningRows: planningRows,
            NeighborTimelineRows: timeline,
            GpsContext: gpsContext,
            CorrectionTargets: corrections,
            TechnicalCollapsedNotes: technical);
    }

    /// <summary>
    /// Human-facing project label: prefer BON, then Project 100/200/300, then project number.
    /// Avoids raw internal database ids as the primary label.
    /// </summary>
    public static string BuildProjectDisplayLabel(
        int? projectNumber,
        string? projectId = null,
        string? bonNr = null)
    {
        if (!string.IsNullOrWhiteSpace(bonNr))
        {
            return "BON " + bonNr.Trim();
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
                && item.ProjectNumber == 300
                && !item.IsCalendarSynthetic
                && !item.IsAbsence)
            .OrderBy(item => item.Start)
            .ThenBy(item => item.SortKey)
            .ToArray();
    }

    private static PayrollProject300TimelineRow[] BuildNeighborTimeline(
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
            return [.. real.Select(item => ToTimelineRow(item, PayrollProject300TimelineKind.Other, selectedIds))];
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

        var rows = new List<PayrollProject300TimelineRow>();
        if (previous is not null)
        {
            rows.Add(ToTimelineRow(previous, PayrollProject300TimelineKind.Previous, selectedIds));
        }

        foreach (var item in selected.OrderBy(item => item.Start).ThenBy(item => item.SortKey))
        {
            rows.Add(ToTimelineRow(item, PayrollProject300TimelineKind.Selected, selectedIds));
        }

        if (next is not null)
        {
            rows.Add(ToTimelineRow(next, PayrollProject300TimelineKind.Next, selectedIds));
        }

        return [.. rows];
    }

    private static PayrollProject300TimelineRow ToTimelineRow(
        NormalizedPerformanceEntry entry,
        PayrollProject300TimelineKind kind,
        HashSet<long> selectedIds) =>
        new(
            Kind: kind,
            PerformanceId: entry.SourceEntryId,
            Start: entry.Start,
            End: entry.End,
            ProjectLabel: BuildProjectDisplayLabel(entry.ProjectNumber, entry.ProjectId, entry.BonNr),
            Description: entry.Description,
            IsSelected300: selectedIds.Contains(entry.SourceEntryId));

    private static PayrollProject300GpsContext BuildGpsContext(
        NormalizedPerformanceEntry[] selected,
        StandbyGpsDayEvidence? gps)
    {
        if (gps is null
            || !gps.HasVehicleMapping
            || gps.Trips.Count == 0
            || selected.Length == 0
            || selected.All(item => item.Start is null || item.End is null))
        {
            return new PayrollProject300GpsContext(
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
        var windowStart = selectedStart.AddHours(-2);
        var windowEnd = selectedEnd.AddHours(2);

        var candidates = evidence.Trips
            .Where(trip => trip.Start < windowEnd && trip.End > windowStart)
            .OrderBy(trip => trip.Start)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new PayrollProject300GpsContext(
                Available: false,
                Summary: MissingGpsSummary,
                Events: [],
                MappingKind: evidence.MappingKind,
                ObjectIdCollapsed: evidence.ObjectId,
                TripsCollapsed: evidence.Trips);
        }

        var events = new List<PayrollProject300GpsEvent>();

        var before = candidates
            .Where(trip => trip.End <= selectedStart)
            .OrderByDescending(trip => trip.End)
            .FirstOrDefault();
        if (before is not null)
        {
            events.Add(BuildBeforeEvent(before));
        }

        foreach (var during in candidates.Where(trip =>
                     trip.Start < selectedEnd && trip.End > selectedStart))
        {
            events.Add(BuildDuringEvent(during));
        }

        var after = candidates
            .Where(trip => trip.Start >= selectedEnd)
            .OrderBy(trip => trip.Start)
            .FirstOrDefault();
        if (after is not null)
        {
            events.AddRange(BuildAfterEvents(after));
        }

        if (events.Count == 0)
        {
            return new PayrollProject300GpsContext(
                Available: false,
                Summary: MissingGpsSummary,
                Events: [],
                MappingKind: evidence.MappingKind,
                ObjectIdCollapsed: evidence.ObjectId,
                TripsCollapsed: evidence.Trips);
        }

        var summary =
            $"{events.Count} GPS-punt(en) rond geboekt venster "
            + $"({FormatClock(selectedStart)}–{FormatClock(selectedEnd)}); "
            + PayrollProject300CaseDetail.GpsNeverValidatesNote;

        return new PayrollProject300GpsContext(
            Available: true,
            Summary: summary,
            Events: events,
            MappingKind: evidence.MappingKind,
            ObjectIdCollapsed: evidence.ObjectId,
            TripsCollapsed: evidence.Trips);
    }

    private static PayrollProject300GpsEvent BuildBeforeEvent(StandbyGpsTripEvidence trip)
    {
        var location = FirstAddress(trip.EndAddress, trip.StartAddress);
        var label = string.IsNullOrWhiteSpace(location)
            ? "Voor: stilstand/locatie"
            : "Voor: stilstand: " + location;

        return new PayrollProject300GpsEvent(
            At: trip.Start,
            End: trip.End,
            Label: label,
            Detail: BuildTripDetail(trip, includeAddresses: false),
            Phase: "Before");
    }

    private static PayrollProject300GpsEvent BuildDuringEvent(StandbyGpsTripEvidence trip)
    {
        var location = FirstAddress(trip.StartAddress, trip.EndAddress);
        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(location))
        {
            detailParts.Add("Locatie: " + location);
        }

        var baseDetail = BuildTripDetail(trip, includeAddresses: false);
        if (!string.IsNullOrWhiteSpace(baseDetail))
        {
            detailParts.Add(baseDetail);
        }

        return new PayrollProject300GpsEvent(
            At: trip.Start,
            End: trip.End,
            Label: "Tijdens geboekte 300-tijd",
            Detail: detailParts.Count == 0 ? null : string.Join(" · ", detailParts),
            Phase: "During");
    }

    private static IEnumerable<PayrollProject300GpsEvent> BuildAfterEvents(StandbyGpsTripEvidence trip)
    {
        var startLocation = TrimOrNull(trip.StartAddress);
        var endLocation = TrimOrNull(trip.EndAddress);

        var vertrekDetailParts = new List<string>
        {
            $"{FormatClock(trip.Start)}–{FormatClock(trip.End)}",
        };
        if (!string.IsNullOrWhiteSpace(startLocation))
        {
            vertrekDetailParts.Add(startLocation);
        }

        if (trip.DistanceKilometres > 0)
        {
            vertrekDetailParts.Add($"{trip.DistanceKilometres.ToString("0.0", Belgian)} km");
        }

        yield return new PayrollProject300GpsEvent(
            At: trip.Start,
            End: trip.End,
            Label: "Na: vertrek",
            Detail: string.Join(" · ", vertrekDetailParts),
            Phase: "After");

        if (!string.IsNullOrWhiteSpace(endLocation))
        {
            yield return new PayrollProject300GpsEvent(
                At: trip.End,
                End: null,
                Label: "Na: aankomst: " + endLocation,
                Detail: FormatClock(trip.End),
                Phase: "After");
        }
    }

    private static string? BuildTripDetail(StandbyGpsTripEvidence trip, bool includeAddresses)
    {
        var parts = new List<string>
        {
            $"{FormatClock(trip.Start)}–{FormatClock(trip.End)}",
        };

        if (includeAddresses
            && (!string.IsNullOrWhiteSpace(trip.StartAddress) || !string.IsNullOrWhiteSpace(trip.EndAddress)))
        {
            var from = string.IsNullOrWhiteSpace(trip.StartAddress) ? "—" : trip.StartAddress.Trim();
            var to = string.IsNullOrWhiteSpace(trip.EndAddress) ? "—" : trip.EndAddress.Trim();
            parts.Add($"{from} → {to}");
        }

        if (!string.IsNullOrWhiteSpace(trip.VehiclePlate))
        {
            parts.Add(trip.VehiclePlate.Trim());
        }

        if (trip.DistanceKilometres > 0)
        {
            parts.Add($"{trip.DistanceKilometres.ToString("0.0", Belgian)} km");
        }

        return string.Join(" · ", parts);
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

    private static List<PayrollProject300CorrectionTarget> BuildCorrectionTargets(
        NormalizedPerformanceEntry[] selected,
        IReadOnlyDictionary<long, PayrollProject300ResolvedActivity>? activityByPerformanceId)
    {
        var targets = new List<PayrollProject300CorrectionTarget>();
        foreach (var item in selected.OrderBy(item => item.Start).ThenBy(item => item.SortKey))
        {
            if (activityByPerformanceId is not null
                && activityByPerformanceId.TryGetValue(item.SourceEntryId, out var resolved))
            {
                targets.Add(new PayrollProject300CorrectionTarget(
                    PerformanceId: item.SourceEntryId,
                    CurrentStart: item.Start,
                    CurrentEnd: item.End,
                    AtlHours: item.AtlHoursRaw,
                    HfdTaakId: item.HfdTaakId,
                    ActivityType: resolved.ActivityType,
                    CorrectionCapability: resolved.Supported
                        ? PayrollProject300CorrectionCapability.SupportedVanTot
                        : PayrollProject300CorrectionCapability.UnsupportedActivity,
                    CapabilityMessage: BuildResolvedCapabilityMessage(resolved),
                    FriendlyTaskName: resolved.FriendlyTaskName));
            }
            else if (item.HfdTaakId == PayrollStandbyActivityTypes.WaitingMainTaskExternalId)
            {
                targets.Add(new PayrollProject300CorrectionTarget(
                    PerformanceId: item.SourceEntryId,
                    CurrentStart: item.Start,
                    CurrentEnd: item.End,
                    AtlHours: item.AtlHoursRaw,
                    HfdTaakId: item.HfdTaakId,
                    ActivityType: PayrollStandbyActivityTypes.WaitingTime,
                    CorrectionCapability: PayrollProject300CorrectionCapability.SupportedVanTot,
                    CapabilityMessage: "VAN/TOT-correctie beschikbaar voor wachttijd (HFDTAAK 23)."));
            }
            else
            {
                targets.Add(new PayrollProject300CorrectionTarget(
                    PerformanceId: item.SourceEntryId,
                    CurrentStart: item.Start,
                    CurrentEnd: item.End,
                    AtlHours: item.AtlHoursRaw,
                    HfdTaakId: item.HfdTaakId,
                    ActivityType: null,
                    CorrectionCapability: PayrollProject300CorrectionCapability.UnsupportedActivity,
                    CapabilityMessage: UnsupportedActivityMessage));
            }

            targets.Add(new PayrollProject300CorrectionTarget(
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
                CorrectionCapability: PayrollProject300CorrectionCapability.ZeroDeleteUnavailable,
                CapabilityMessage: ZeroDeleteUnavailableMessage,
                FriendlyTaskName: activityByPerformanceId is not null
                    && activityByPerformanceId.TryGetValue(item.SourceEntryId, out var zeroFriendly)
                    ? zeroFriendly.FriendlyTaskName
                    : null));
        }

        return targets;
    }

    private static string BuildResolvedCapabilityMessage(PayrollProject300ResolvedActivity resolved)
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
        StandbyGpsDayEvidence? gps)
    {
        var notes = new List<string>
        {
            $"AdminCaseKey={adminCase.AdminCaseKey}",
            $"FindingKeys={string.Join(',', adminCase.FindingKeys)}",
            $"SelectedPerformanceIds={string.Join(',', selected.Select(item => item.SourceEntryId))}",
            PayrollProject300CaseDetail.GpsNeverValidatesNote,
            $"MatchingReservation={MatchingReservationGeen}",
        };

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
