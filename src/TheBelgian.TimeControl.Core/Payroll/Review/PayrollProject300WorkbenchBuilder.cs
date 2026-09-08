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
    public const string UnsupportedActivityMessage =
        "Automatische tijdscorrectie niet beschikbaar voor dit type prestatie.";
    public const string ZeroDeleteUnavailableMessage =
        "Volledig verwijderen/nul zetten is nog niet veilig ondersteund.";

    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static PayrollProject300CaseDetail BuildDetail(
        PayrollAdminCase adminCase,
        IReadOnlyList<NormalizedPerformanceEntry> dayPerformances,
        IReadOnlyList<PayrollPlanningReservation> dayPlanning,
        StandbyGpsDayEvidence? gps)
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
                IsSelected: true))
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
        var gpsContext = BuildGpsContext(selected, gps);
        var corrections = BuildCorrectionTargets(selected);
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
            ProjectLabel: entry.ProjectId
                ?? entry.ProjectNumber?.ToString(CultureInfo.InvariantCulture),
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
        var windowStart = selected.Min(item => item.Start!.Value).AddHours(-2);
        var windowEnd = selected.Max(item => item.End!.Value).AddHours(2);
        var selectedStart = selected.Min(item => item.Start!.Value);
        var selectedEnd = selected.Max(item => item.End!.Value);

        var overlapping = evidence.Trips
            .Where(trip => trip.Start < windowEnd && trip.End > windowStart)
            .OrderBy(trip => trip.Start)
            .ToArray();

        if (overlapping.Length == 0)
        {
            return new PayrollProject300GpsContext(
                Available: false,
                Summary: MissingGpsSummary,
                Events: [],
                MappingKind: evidence.MappingKind,
                ObjectIdCollapsed: evidence.ObjectId,
                TripsCollapsed: evidence.Trips);
        }

        var events = overlapping
            .Select(trip =>
            {
                var phase = ClassifyTripPhase(trip, selectedStart, selectedEnd);
                return new PayrollProject300GpsEvent(
                    At: trip.Start,
                    End: trip.End,
                    Label: phase,
                    Detail: BuildTripDetail(trip));
            })
            .ToArray();

        var summary =
            $"{overlapping.Length} rit(ten) rond geboekt venster "
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

    private static string ClassifyTripPhase(
        StandbyGpsTripEvidence trip,
        DateTimeOffset selectedStart,
        DateTimeOffset selectedEnd)
    {
        if (trip.End <= selectedStart)
        {
            return "Voor";
        }

        if (trip.Start >= selectedEnd)
        {
            return "Na";
        }

        return "Tijdens";
    }

    private static string? BuildTripDetail(StandbyGpsTripEvidence trip)
    {
        var parts = new List<string>
        {
            $"{FormatClock(trip.Start)}–{FormatClock(trip.End)}",
        };

        if (!string.IsNullOrWhiteSpace(trip.StartAddress) || !string.IsNullOrWhiteSpace(trip.EndAddress))
        {
            var from = string.IsNullOrWhiteSpace(trip.StartAddress) ? "—" : trip.StartAddress.Trim();
            var to = string.IsNullOrWhiteSpace(trip.EndAddress) ? "—" : trip.EndAddress.Trim();
            parts.Add($"{from} → {to}");
        }

        if (!string.IsNullOrWhiteSpace(trip.VehiclePlate))
        {
            parts.Add(trip.VehiclePlate.Trim());
        }

        parts.Add($"{trip.DistanceKilometres.ToString("0.0", Belgian)} km");
        return string.Join(" · ", parts);
    }

    private static List<PayrollProject300CorrectionTarget> BuildCorrectionTargets(
        NormalizedPerformanceEntry[] selected)
    {
        var targets = new List<PayrollProject300CorrectionTarget>();
        foreach (var item in selected.OrderBy(item => item.Start).ThenBy(item => item.SortKey))
        {
            if (item.HfdTaakId == PayrollStandbyActivityTypes.WaitingMainTaskExternalId)
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
                    : null,
                CorrectionCapability: PayrollProject300CorrectionCapability.ZeroDeleteUnavailable,
                CapabilityMessage: ZeroDeleteUnavailableMessage));
        }

        return targets;
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
