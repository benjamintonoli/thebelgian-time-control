using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

/// <summary>
/// Lean Overlap / MissingTechnician workbench projection (facts + advice only).
/// </summary>
public static partial class PayrollIntelligenceWorkbenchBuilder
{
    public const string MissingGpsSummary = "GPS nog niet geladen.";
    public const string NoGpsSummary = "Geen bruikbare GPS voor deze dag.";

    public static PayrollIntelligenceCaseDetail BuildOverlapDetail(
        PayrollAdminCase adminCase,
        OverlapAnalysis analysis,
        IReadOnlyDictionary<long, (string? ActivityType, bool Supported, string Message)> activities,
        string? deleteImpactSummary,
        string? adjustImpactSummary,
        StandbyGpsDayEvidence? gps,
        bool gpsPending)
    {
        var a = BuildSide(analysis.A, activities);
        var b = BuildSide(analysis.B, activities);
        var targetId = analysis.TargetPerformanceId;
        var target = targetId is null
            ? null
            : targetId == a.PerformanceId
                ? a
                : targetId == b.PerformanceId
                    ? b
                    : null;

        var canAdjustAny = target is { AdjustSupported: true };
        var canDelete = target is { DeleteSupported: true };

        TimeOnly? proposedStart = analysis.ProposedStart is { } ps
            ? TimeOnly.FromTimeSpan(ps.TimeOfDay)
            : null;
        TimeOnly? proposedEnd = analysis.ProposedEnd is { } pe
            ? TimeOnly.FromTimeSpan(pe.TimeOfDay)
            : null;

        return new PayrollIntelligenceCaseDetail(
            adminCase,
            new PayrollOverlapWorkbenchDetail(
                a,
                b,
                analysis.OverlapStart,
                analysis.OverlapEnd,
                analysis.OverlapHours,
                analysis.OverlapMinutes,
                analysis.Kind,
                OverlapKindLabelNl(analysis.Kind),
                analysis.Recommendation,
                targetId,
                proposedStart,
                proposedEnd,
                analysis.AdviceNl,
                analysis.Confidence,
                deleteImpactSummary,
                adjustImpactSummary,
                canAdjustAny,
                canDelete,
                canAdjustAny ? null : target?.CapabilityMessage ?? "Geen aanpasbare doelprestatie.",
                canDelete ? null : target?.CapabilityMessage ?? "Geen verwijderbare doelprestatie."),
            Missing: null,
            BuildGpsContext(gps, gpsPending));
    }

    public static PayrollIntelligenceCaseDetail BuildMissingDetail(
        PayrollAdminCase adminCase,
        PayrollFindingRecord finding,
        IReadOnlyList<NormalizedPerformanceEntry> peerRows,
        StandbyGpsDayEvidence? gps,
        bool gpsPending)
    {
        var travelMode = ParseTravelMode(finding.Evidence);
        var evidenceClass = finding.GpsClassification ?? nameof(MissingTechnicianEvidenceClass.Ambiguous);
        var peer = ResolvePeer(finding, peerRows);
        var (siteStart, siteEnd, siteNote) = ParseSitePresence(finding.Evidence);
        if (gps is not null && siteStart is null)
        {
            var peerPerf = peerRows.FirstOrDefault(item => item.SourceEntryId == peer.PerformanceId);
            var derived = MissingTechnicianControl.TryDeriveSitePresence(
                BuildPseudoGroup(finding),
                gps,
                peerPerf);
            if (derived.Arrival is not null)
            {
                siteStart = TimeOnly.FromTimeSpan(derived.Arrival.Value.TimeOfDay);
                siteEnd = derived.Departure is null
                    ? null
                    : TimeOnly.FromTimeSpan(derived.Departure.Value.TimeOfDay);
                siteNote = derived.Note;
            }
        }

        var proposalSource = ParseToken(finding.Evidence, "intervalSource") ?? "none";
        TimeOnly? proposalStart = finding.SuggestedPayableStart is { } ss
            ? TimeOnly.FromTimeSpan(ss.TimeOfDay)
            : peer.Start;
        TimeOnly? proposalEnd = finding.SuggestedPayableEnd is { } se
            ? TimeOnly.FromTimeSpan(se.TimeOfDay)
            : peer.End;
        var proposalHours = finding.SuggestedPayableHours
            ?? (proposalStart is not null && proposalEnd is not null && proposalEnd > proposalStart
                ? (decimal)(proposalEnd.Value.ToTimeSpan() - proposalStart.Value.ToTimeSpan()).TotalHours
                : null);

        var mainTaskId = PayrollActionEligibility.ParseSuggestedHfdTaakId(finding.Evidence);
        var (canCreate, createBlock) = EvaluateCreateGate(finding, mainTaskId);
        var siteMatchRaw = ParseToken(finding.Evidence, "siteMatch");
        var conflictRaw = ParseToken(finding.Evidence, "conflictClass");
        Enum.TryParse<MissingTechnicianSiteMatch>(siteMatchRaw, ignoreCase: true, out var siteMatch);
        Enum.TryParse<MissingTechnicianConflictClass>(conflictRaw, ignoreCase: true, out var conflictClass);
        var isWrongDossier = finding.FindingType == PayrollFindingType.WrongProjectBooking
            || conflictClass == MissingTechnicianConflictClass.PlannedJobSupportedExistingBookingWrong;
        var conflictToken = ParseToken(finding.Evidence, "conflict");
        var plannedSite = ParseToken(finding.Evidence, "plannedSite");

        return new PayrollIntelligenceCaseDetail(
            adminCase,
            Overlap: null,
            new PayrollMissingWorkbenchDetail(
                travelMode,
                MissingTechnicianControl.TravelModeDutch(travelMode),
                evidenceClass,
                MissingEvidenceLabel(evidenceClass),
                peer.ResourceId,
                peer.PerformanceId,
                peer.Start,
                peer.End,
                peer.Start is not null && peer.End is not null
                    ? $"{peer.Start:HH\\:mm}–{peer.End:HH\\:mm}"
                    : null,
                siteStart,
                siteEnd,
                siteNote,
                proposalStart,
                proposalEnd,
                proposalHours is null
                    ? null
                    : Math.Round(proposalHours.Value, 2, MidpointRounding.AwayFromZero),
                proposalSource,
                finding.SuggestedProjectId,
                finding.SuggestedBonNr,
                mainTaskId,
                canCreate && !isWrongDossier,
                isWrongDossier
                    ? "Vervangplan: eerst bestaande boeking verwijderen, daarna correcte prestatie aanmaken (Create feature-gated)."
                    : createBlock,
                finding.Evidence,
                finding.SuggestedAction,
                siteMatch,
                MissingTechnicianSiteConflictAnalyzer.FormatSiteMatchNl(siteMatch),
                conflictClass,
                MissingTechnicianSiteConflictAnalyzer.FormatConflictNl(conflictClass),
                plannedSite,
                conflictToken,
                isWrongDossier,
                isWrongDossier
                    ? "1) DeleteExistingPerformance op huidige boeking  2) CreatePerformance op geplande job/BON met GPS-interval"
                    : null),
            BuildGpsContext(gps, gpsPending));
    }

    public static PayrollIntelligenceGpsContext BuildGpsContext(
        StandbyGpsDayEvidence? gps,
        bool gpsPending)
    {
        if (gpsPending)
        {
            return new PayrollIntelligenceGpsContext(false, true, MissingGpsSummary, []);
        }

        if (gps is null || !gps.HasUsableTrips)
        {
            return new PayrollIntelligenceGpsContext(false, false, NoGpsSummary, []);
        }

        var events = gps.Trips
            .OrderBy(item => item.Start)
            .Take(12)
            .Select(trip => new PayrollIntelligenceGpsEvent(
                trip.Start,
                trip.End,
                string.IsNullOrWhiteSpace(trip.VehiclePlate)
                    ? (gps.RegistrationPlate ?? "GPS-rit")
                    : trip.VehiclePlate!,
                FirstNonEmpty(trip.EndAddress, trip.StartAddress)))
            .ToList();

        return new PayrollIntelligenceGpsContext(
            true,
            false,
            $"{events.Count} GPS-rit(ten) · mapping {gps.MappingKind}",
            events);
    }

    public static string OverlapKindLabelNl(OverlapKind kind) => kind switch
    {
        OverlapKind.ExactDuplicate => "Exacte dubbele boeking",
        OverlapKind.PartialSameJob => "Gedeeltelijke overlap (zelfde job)",
        OverlapKind.PartialDifferentJobs => "Gedeeltelijke overlap (andere jobs)",
        OverlapKind.TravelWorkOverlap => "Reis / werk overlap",
        OverlapKind.StandbySpecialOverlap => "Wachtdienst / speciale overlap",
        _ => "Onduidelijke overlap",
    };

    public static string MissingEvidenceLabel(string? classification) =>
        classification switch
        {
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps) =>
                "Ondersteunt aanwezigheid (planning + collega + GPS)",
            nameof(MissingTechnicianEvidenceClass.PlanningPlusPeer) => "Planning + collega",
            nameof(MissingTechnicianEvidenceClass.PlanningPlusGps) => "Planning + GPS",
            nameof(MissingTechnicianEvidenceClass.NoGpsData) => "Geen GPS",
            nameof(MissingTechnicianEvidenceClass.ContradictedByGps) => "Tegenstrijdig (GPS)",
            nameof(MissingTechnicianEvidenceClass.ContradictedByExistingPerformance) =>
                "Tegenstrijdig (bestaande prestatie)",
            nameof(MissingTechnicianEvidenceClass.Ambiguous) => "Onduidelijk",
            _ => classification ?? "—",
        };

    public static OverlapAnalysis AnalyzeFromDay(
        IReadOnlyList<NormalizedPerformanceEntry> dayRows,
        IReadOnlyList<long> relatedIds,
        StandbyGpsDayEvidence? gps = null)
    {
        var byId = dayRows.ToDictionary(item => item.SourceEntryId);
        if (relatedIds.Count >= 2
            && byId.TryGetValue(relatedIds[0], out var left)
            && byId.TryGetValue(relatedIds[1], out var right))
        {
            return OverlapIntelligence.Analyze(left, right, gps);
        }

        var pair = dayRows
            .Where(item => relatedIds.Contains(item.SourceEntryId))
            .OrderBy(item => item.SourceEntryId)
            .Take(2)
            .ToList();
        if (pair.Count >= 2)
        {
            return OverlapIntelligence.Analyze(pair[0], pair[1], gps);
        }

        if (dayRows.Count >= 2)
        {
            // Last resort: first two rows (should be rare — finding keys always carry ids).
            return OverlapIntelligence.Analyze(dayRows[0], dayRows[1], gps);
        }

        var stub = dayRows.Count > 0 ? dayRows[0] : StubPerformance();
        return OverlapAnalysis.Empty(stub, stub);
    }

    public static IReadOnlyList<long> ParseRelatedIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<long[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static MissingTechnicianTravelMode ParseTravelMode(string? evidence)
    {
        var raw = ParseToken(evidence, "travelMode");
        if (Enum.TryParse<MissingTechnicianTravelMode>(raw, ignoreCase: true, out var mode))
        {
            return mode;
        }

        return MissingTechnicianTravelMode.GpsInsufficient;
    }

    public static string? ParseToken(string? evidence, string key)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return null;
        }

        var match = Regex.Match(
            evidence,
            $@"(?:^|;\s*){Regex.Escape(key)}=([^;]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static PayrollOverlapSideCard BuildSide(
        NormalizedPerformanceEntry entry,
        IReadOnlyDictionary<long, (string? ActivityType, bool Supported, string Message)> activities)
    {
        activities.TryGetValue(entry.SourceEntryId, out var resolved);
        var supported = resolved.Supported && !string.IsNullOrWhiteSpace(resolved.ActivityType);
        var deleteSupported = entry.HfdTaakId is > 0
            && !string.IsNullOrWhiteSpace(entry.ProjectId ?? entry.ProjectNumber?.ToString(CultureInfo.InvariantCulture));
        return new PayrollOverlapSideCard(
            entry.SourceEntryId,
            entry.Start,
            entry.End,
            entry.AtlHoursRaw,
            entry.ProjectId ?? entry.ProjectNumber?.ToString(CultureInfo.InvariantCulture),
            entry.BonNr,
            entry.Description,
            entry.Memo,
            entry.HfdTaakId,
            resolved.ActivityType,
            supported,
            deleteSupported,
            resolved.Message ?? (supported ? "OK" : "Activiteit niet ondersteund voor correctie."));
    }

    private static (bool Ok, string? Reason) EvaluateCreateGate(
        PayrollFindingRecord finding,
        int? mainTaskId)
    {
        if (finding.Severity != PayrollFindingSeverity.High)
        {
            return (false, "Alleen High-bevindingen kunnen een create-voorstel worden.");
        }

        if (finding.SuggestedPayableStart is null
            || finding.SuggestedPayableEnd is null
            || finding.SuggestedPayableEnd <= finding.SuggestedPayableStart)
        {
            return (false, "Geen geldig voorstel-interval (VAN/TOT).");
        }

        if (string.IsNullOrWhiteSpace(finding.SuggestedProjectId))
        {
            return (false, "Project ontbreekt op de bevinding.");
        }

        if (mainTaskId is null or <= 0)
        {
            return (false, "IDHFDTAAK niet bewezen — create blijft geblokkeerd.");
        }

        if (!PayrollCreateAllowedMainTasks.IsAllowed(mainTaskId.Value))
        {
            return (false, PayrollCreateAllowedMainTasks.RejectReason(mainTaskId.Value));
        }

        if (string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianEvidenceClass.ContradictedByExistingPerformance),
                StringComparison.Ordinal)
            || string.Equals(
                finding.GpsClassification,
                nameof(MissingTechnicianEvidenceClass.ContradictedByGps),
                StringComparison.Ordinal))
        {
            return (false, "Bewijs is tegenstrijdig — geen create.");
        }

        return (true, null);
    }

    private static (string? ResourceId, long? PerformanceId, TimeOnly? Start, TimeOnly? End) ResolvePeer(
        PayrollFindingRecord finding,
        IReadOnlyList<NormalizedPerformanceEntry> peerRows)
    {
        var related = ParseRelatedIds(finding.RelatedPerformanceIdsJson);
        NormalizedPerformanceEntry? match = null;
        foreach (var item in peerRows)
        {
            if (related.Contains(item.SourceEntryId))
            {
                match = item;
                break;
            }
        }

        match ??= peerRows.Count > 0 ? peerRows[0] : null;
        if (match is not null)
        {
            return (
                match.ResourceId,
                match.SourceEntryId,
                match.Start is null ? null : TimeOnly.FromTimeSpan(match.Start.Value.TimeOfDay),
                match.End is null ? null : TimeOnly.FromTimeSpan(match.End.Value.TimeOfDay));
        }

        var parsed = PeerEvidenceRegex().Match(finding.Evidence ?? string.Empty);
        if (parsed.Success)
        {
            var resourceId = parsed.Groups[1].Value;
            _ = long.TryParse(parsed.Groups[2].Value, CultureInfo.InvariantCulture, out var perfId);
            TimeOnly? start = TimeOnly.TryParse(parsed.Groups[3].Value, CultureInfo.InvariantCulture, out var s) ? s : null;
            TimeOnly? end = TimeOnly.TryParse(parsed.Groups[4].Value, CultureInfo.InvariantCulture, out var e) ? e : null;
            return (resourceId, perfId > 0 ? perfId : null, start, end);
        }

        return (null, null, null, null);
    }

    private static (TimeOnly? Start, TimeOnly? End, string? Note) ParseSitePresence(string? evidence)
    {
        var arrival = ParseToken(evidence, "siteArrival");
        var departure = ParseToken(evidence, "siteDeparture");
        TimeOnly? start = TimeOnly.TryParse(arrival, CultureInfo.InvariantCulture, out var a) ? a : null;
        TimeOnly? end = TimeOnly.TryParse(departure, CultureInfo.InvariantCulture, out var d) ? d : null;
        return (start, end, null);
    }

    private static PlannedWorkGroup BuildPseudoGroup(PayrollFindingRecord finding) =>
        new(
            Date: finding.Date,
            IdCalendar: 0,
            ProjectId: finding.SuggestedProjectId,
            ProjectNumber: null,
            TimeFrom: finding.SuggestedPayableStart is { } s
                ? TimeOnly.FromTimeSpan(s.TimeOfDay)
                : null,
            TimeTo: finding.SuggestedPayableEnd is { } e
                ? TimeOnly.FromTimeSpan(e.TimeOfDay)
                : null,
            Subject: null,
            ResourceIds: [finding.ResourceId],
            Reservations: []);

    private static NormalizedPerformanceEntry StubPerformance() =>
        new(
            0,
            "empty",
            string.Empty,
            default,
            null,
            null,
            0m,
            0m,
            null,
            new PauseNormalizationResult(default, null, default, null),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    [GeneratedRegex(@"peers=\[([^\s#]+)#(\d+):(\d{2}:\d{2})-(\d{2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex PeerEvidenceRegex();
}
