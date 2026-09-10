using System.Globalization;
using System.Text.RegularExpressions;
using TheBelgian.TimeControl.Core.Payroll.Findings;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

/// <summary>
/// Admin-facing Dutch explainability for MissingTechnician cases.
/// Parses finding evidence tokens; keeps raw enums out of primary UI.
/// </summary>
public static class MissingTechnicianAdminExplainability
{
    private static readonly Dictionary<int, string> KnownTaskLabels = new()
    {
        [9] = "Werkuren Onderhoudstechnicus",
    };

    public static MissingTechnicianAdminExplanation Build(
        string? evidence,
        string? peerResourceId,
        string? peerDisplayName,
        TimeOnly? peerStart,
        TimeOnly? peerEnd,
        TimeOnly? proposalStart,
        TimeOnly? proposalEnd,
        decimal? proposalHours,
        string? projectId,
        string? bonNr,
        int? mainTaskId,
        MissingTechnicianTravelMode travelMode,
        bool canProposeCreate)
    {
        var planning = ParseToken(evidence, "planned");
        var proposed = ParseToken(evidence, "proposedInterval")
            ?? FormatInterval(proposalStart, proposalEnd);
        var siteArrival = ParseToken(evidence, "siteArrival");
        var siteDeparture = ParseToken(evidence, "siteDeparture");
        var excursionClass = ParseToken(evidence, "excursion");
        var excursionInterval = ParseToken(evidence, "excursionInterval");
        var excursionAway = ParseToken(evidence, "excursionAway");
        var workContinuity = ParseToken(evidence, "workContinuity");
        var continuityNl = ParseToken(evidence, "continuityNl");
        var allocationReview = string.Equals(
            ParseToken(evidence, "allocationReview"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var intervalSource = ParseToken(evidence, "intervalSource") ?? "none";
        var planningSubject = ParseToken(evidence, "planningSubject");
        var plannedSite = ParseToken(evidence, "plannedSite");
        var existingBooking = ParseToken(evidence, "planningWindowConflict")
            ?? ParseToken(evidence, "conflict");

        var customerSite = DeriveCustomerSite(planningSubject, plannedSite);
        var timingSourcePrimary = DeriveTimingSourcePrimary(intervalSource, travelMode);
        var timingSourceSupporting = DeriveTimingSourceSupporting(intervalSource, travelMode);
        var whyBullets = BuildWhyBullets(
            planning,
            peerDisplayName ?? peerResourceId,
            peerStart,
            peerEnd,
            travelMode,
            siteArrival,
            siteDeparture,
            excursionClass,
            excursionInterval,
            excursionAway,
            proposed,
            timingSourcePrimary);
        var timeline = BuildTimeline(siteArrival, siteDeparture, excursionInterval, excursionAway);
        var continuityAdmin = DeriveContinuityAdmin(workContinuity, continuityNl, excursionClass);
        var readinessNl = canProposeCreate
            ? "Voorstel klaar voor controle. Na jouw goedkeuring kan deze prestatie in Plenion worden aangemaakt."
            : "Nog niet klaar om aan te maken — zie reden hieronder.";
        var taskLabel = mainTaskId is > 0
            ? KnownTaskLabels.TryGetValue(mainTaskId.Value, out var label)
                ? $"Taak {mainTaskId} — {label}"
                : $"Taak {mainTaskId}"
            : null;

        return new MissingTechnicianAdminExplanation(
            Headline: "ONTBREKENDE PRESTATIE",
            CustomerSiteLabel: customerSite,
            PlanningSubject: CleanPlanningSubject(planningSubject),
            PlannedSiteLabel: CleanPlannedSite(plannedSite),
            PlanningInterval: planning,
            PeerRoleLabel: "Collega op dezelfde job",
            PeerDisplayName: string.IsNullOrWhiteSpace(peerDisplayName) ? peerResourceId : peerDisplayName,
            PeerInterval: FormatInterval(peerStart, peerEnd),
            LeadTechnicianName: null,
            TimingSourcePrimary: timingSourcePrimary,
            TimingSourceSupporting: timingSourceSupporting,
            WhyIntro: "WAAROM WORDT DIT VOORGESTELD?",
            WhyBullets: whyBullets,
            TimelineTitle: travelMode == MissingTechnicianTravelMode.SeparateVehicleProven
                ? "TRACK & TRACE — EIGEN VOERTUIG"
                : "TRACK & TRACE",
            TimelineLines: timeline,
            WorkContinuityTitle: "WERKCONTINUÏTEIT",
            WorkContinuityNl: continuityAdmin,
            AllocationReviewNl: allocationReview
                ? "Projecttoewijzing tijdens de verplaatsing naar The Belgian nog na te kijken. De uren zelf blijven werkuren."
                : null,
            ProposalTitle: "VOORSTEL",
            ProposalInterval: proposed,
            ProposalHoursLabel: proposalHours is null
                ? null
                : proposalHours.Value.ToString("0.##", CultureInfo.GetCultureInfo("nl-BE")) + " uur",
            ProposalProject: projectId,
            ProposalBon: bonNr,
            ProposalTaskLabel: taskLabel,
            ProposalWhyLine: BuildProposalWhyLine(timingSourcePrimary, timingSourceSupporting),
            BookingStatusNl: "Nog niet geregistreerd in Plenion",
            ReadinessNl: readinessNl,
            ExistingOtherBookingNl: FormatExistingBooking(existingBooking),
            HqExcursionNl: FormatHqExcursion(excursionClass, excursionInterval, excursionAway));
    }

    public static string MonthNameNl(int month) => month switch
    {
        1 => "januari",
        2 => "februari",
        3 => "maart",
        4 => "april",
        5 => "mei",
        6 => "juni",
        7 => "juli",
        8 => "augustus",
        9 => "september",
        10 => "oktober",
        11 => "november",
        12 => "december",
        _ => month.ToString(CultureInfo.InvariantCulture),
    };

    public static string MonthTitleNl(int year, int month) =>
        $"LOONCONTROLE — {MonthNameNl(month).ToUpperInvariant()} {year}";

    private static string DeriveTimingSourcePrimary(string intervalSource, MissingTechnicianTravelMode travelMode)
    {
        if (intervalSource.Contains("gps", StringComparison.OrdinalIgnoreCase)
            && travelMode == MissingTechnicianTravelMode.SeparateVehicleProven)
        {
            return "Track & Trace eigen voertuig";
        }

        if (intervalSource.Contains("peer", StringComparison.OrdinalIgnoreCase)
            || travelMode == MissingTechnicianTravelMode.SharedTravelProven)
        {
            return "Collega op dezelfde job";
        }

        if (intervalSource.Contains("planning", StringComparison.OrdinalIgnoreCase))
        {
            return "Planning";
        }

        return "Planning + Track & Trace";
    }

    private static string? DeriveTimingSourceSupporting(string intervalSource, MissingTechnicianTravelMode travelMode)
    {
        if (intervalSource.Contains("gps", StringComparison.OrdinalIgnoreCase)
            && travelMode == MissingTechnicianTravelMode.SeparateVehicleProven)
        {
            return "Planning + collega op dezelfde job";
        }

        if (intervalSource.Contains("peer", StringComparison.OrdinalIgnoreCase))
        {
            return "Planning";
        }

        return null;
    }

    private static List<string> BuildWhyBullets(
        string? planning,
        string? peerName,
        TimeOnly? peerStart,
        TimeOnly? peerEnd,
        MissingTechnicianTravelMode travelMode,
        string? siteArrival,
        string? siteDeparture,
        string? excursionClass,
        string? excursionInterval,
        string? excursionAway,
        string? proposed,
        string timingPrimary)
    {
        var bullets = new List<string>();
        if (!string.IsNullOrWhiteSpace(planning))
        {
            bullets.Add($"Planning: {planning}");
        }

        if (!string.IsNullOrWhiteSpace(peerName))
        {
            var hours = FormatInterval(peerStart, peerEnd);
            bullets.Add(string.IsNullOrWhiteSpace(hours)
                ? $"Collega op dezelfde job: {peerName}"
                : $"Collega op dezelfde job: {peerName} boekte {hours}");
        }

        bullets.Add(travelMode == MissingTechnicianTravelMode.SeparateVehicleProven
            ? "Reiswijze: apart gereden (eigen Track & Trace)"
            : $"Reiswijze: {MissingTechnicianControl.TravelModeDutch(travelMode)}");

        if (!string.IsNullOrWhiteSpace(siteArrival) || !string.IsNullOrWhiteSpace(siteDeparture))
        {
            bullets.Add(
                $"Eigen Track & Trace: aankomst {siteArrival ?? "—"} · vertrek {siteDeparture ?? "—"}");
        }

        if (string.Equals(excursionClass, "WORK_HQ_VISIT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(excursionClass, "WORK_MATERIAL_PICKUP", StringComparison.OrdinalIgnoreCase))
        {
            bullets.Add(
                $"Tijdelijke verplaatsing naar The Belgian"
                + (string.IsNullOrWhiteSpace(excursionInterval) ? "" : $" ({excursionInterval})")
                + (string.IsNullOrWhiteSpace(excursionAway) || excursionAway == "—"
                    ? ""
                    : $": {ShortAddress(excursionAway)}"));
            bullets.Add("De rit naar The Belgian wordt als werkgerelateerde HQ-verplaatsing beschouwd.");
        }

        if (!string.IsNullOrWhiteSpace(proposed)
            && timingPrimary.Contains("eigen", StringComparison.OrdinalIgnoreCase))
        {
            bullets.Add(
                $"Het voorstel {proposed} is gebaseerd op eigen Track & Trace — niet simpelweg overgenomen van de collega.");
        }

        return bullets;
    }

    private static List<string> BuildTimeline(
        string? arrival,
        string? departure,
        string? excursionInterval,
        string? excursionAway)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(arrival))
        {
            lines.Add($"{arrival}  Aankomst werf");
        }

        if (!string.IsNullOrWhiteSpace(excursionInterval)
            && excursionInterval.Contains('-', StringComparison.Ordinal))
        {
            var parts = excursionInterval.Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                lines.Add($"{parts[0]}  Tijdelijk weg van werf");
                if (!string.IsNullOrWhiteSpace(excursionAway) && excursionAway != "—")
                {
                    var mid = InferMidpointLabel(parts[0], parts[1], excursionAway);
                    lines.Add(mid);
                    lines.Add($"{parts[1]}  Terug werf");
                }
                else
                {
                    lines.Add($"{parts[1]}  Terug werf");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(departure))
        {
            lines.Add($"{departure}  Vertrek werf");
        }

        return lines;
    }

    private static string InferMidpointLabel(string start, string end, string away)
    {
        // Prefer HQ wording when Meise/Slozenstraat present.
        if (away.Contains("Meise", StringComparison.OrdinalIgnoreCase)
            || away.Contains("Slozenstraat", StringComparison.OrdinalIgnoreCase))
        {
            return $"—  The Belgian, Meise ({ShortAddress(away)})";
        }

        return $"—  {ShortAddress(away)}";
    }

    private static string DeriveContinuityAdmin(
        string? workContinuity,
        string? continuityNl,
        string? excursionClass)
    {
        if (!string.IsNullOrWhiteSpace(continuityNl)
            && !continuityNl.Contains("CONTINUOUS_", StringComparison.Ordinal)
            && !continuityNl.Contains("WORK_", StringComparison.Ordinal))
        {
            return continuityNl;
        }

        if (string.Equals(excursionClass, "WORK_HQ_VISIT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(workContinuity, "CONTINUOUS_SUPPORTED", StringComparison.OrdinalIgnoreCase))
        {
            return "De technieker verlaat de werf tijdelijk en rijdt naar The Belgian. "
                + "Daarna keert hij terug naar dezelfde job. TimeControl beschouwt dit als een "
                + "werkgerelateerde onderbreking van de werfaanwezigheid, niet als een onderbreking van de werkuren.";
        }

        if (string.Equals(workContinuity, "CONTINUOUS_PLAUSIBLE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(excursionClass, "MEAL_BREAK", StringComparison.OrdinalIgnoreCase))
        {
            return "Er is een tijdelijke afwezigheid zichtbaar in Track & Trace. "
                + "De werktijd hoeft hierdoor niet automatisch onderbroken te worden; normale pauzeregel blijft van toepassing.";
        }

        if (string.Equals(workContinuity, "SPLIT_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            return "Er lijkt een echte onderbreking (andere job of niet-werk). Controleer of het voorstel gesplitst moet worden.";
        }

        return "Werkcontinuïteit: controleer planning, collega en Track & Trace hieronder.";
    }

    private static string? FormatHqExcursion(string? excursionClass, string? interval, string? away)
    {
        if (!string.Equals(excursionClass, "WORK_HQ_VISIT", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(excursionClass, "WORK_MATERIAL_PICKUP", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"HQ-verplaatsing{(string.IsNullOrWhiteSpace(interval) ? "" : $" {interval}")}"
            + (string.IsNullOrWhiteSpace(away) || away == "—" ? "" : $" · {ShortAddress(away)}");
    }

    private static string? FormatExistingBooking(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        // planningWindowConflict=#281602:15:15-15:50 proj=21540 (nonMaterialVsProposed)
        var m = Regex.Match(token, @"#(?<id>\d+):(?<van>\d{2}:\d{2})-(?<tot>\d{2}:\d{2}).*?proj=(?<proj>[^;\s(]+)");
        if (!m.Success)
        {
            return "Andere boeking op dezelfde dag aanwezig (geen overlap met voorstel).";
        }

        return $"Andere boeking op dezelfde dag: {m.Groups["van"].Value}–{m.Groups["tot"].Value}, project {m.Groups["proj"].Value} (#{m.Groups["id"].Value}) — geen overlap met het voorstel.";
    }

    private static string? DeriveCustomerSite(string? planningSubject, string? plannedSite)
    {
        var cleaned = CleanPlanningSubject(planningSubject);
        if (!string.IsNullOrWhiteSpace(cleaned))
        {
            return cleaned;
        }

        return CleanPlannedSite(plannedSite);
    }

    private static string? CleanPlanningSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        // Example: K388 LA §C| Aquafin, 03/450... → prefer Aquafin segment
        var text = subject.Trim();
        var pipe = text.IndexOf('|');
        if (pipe >= 0 && pipe + 1 < text.Length)
        {
            text = text[(pipe + 1)..].Trim();
        }

        text = text.Replace("§C|", "", StringComparison.OrdinalIgnoreCase).Trim();
        var comma = text.IndexOf(',');
        if (comma > 0)
        {
            var head = text[..comma].Trim();
            if (head.Length >= 3)
            {
                return head;
            }
        }

        return text.Length > 80 ? text[..80].Trim() + "…" : text;
    }

    private static string? CleanPlannedSite(string? plannedSite)
    {
        if (string.IsNullOrWhiteSpace(plannedSite))
        {
            return null;
        }

        // plannedSite=2630 src=... conf=Weak  — ParseToken already stops at ;
        // but token value may still include "2630 src=Bon..."
        var first = plannedSite.Split(' ', 2, StringSplitOptions.TrimEntries)[0];
        if (Regex.IsMatch(first, @"^\d{4}$"))
        {
            return $"Postcode {first}";
        }

        return plannedSite.Split(" src=", 2, StringSplitOptions.TrimEntries)[0].Trim();
    }

    private static string BuildProposalWhyLine(string primary, string? supporting) =>
        string.IsNullOrWhiteSpace(supporting)
            ? $"Waarom: {primary}"
            : $"Waarom: {primary} · ondersteund door {supporting}";

    private static string? FormatInterval(TimeOnly? start, TimeOnly? end) =>
        start is null || end is null ? null : $"{start:HH\\:mm}–{end:HH\\:mm}";

    private static string ShortAddress(string address)
    {
        var parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? address : string.Join(", ", parts.Take(2));
    }

    private static string? ParseToken(string? evidence, string name)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return null;
        }

        var match = Regex.Match(evidence, $@"(?:^|;)\s*{Regex.Escape(name)}=([^;]*)");
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1].Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public sealed record MissingTechnicianAdminExplanation(
    string Headline,
    string? CustomerSiteLabel,
    string? PlanningSubject,
    string? PlannedSiteLabel,
    string? PlanningInterval,
    string PeerRoleLabel,
    string? PeerDisplayName,
    string? PeerInterval,
    string? LeadTechnicianName,
    string TimingSourcePrimary,
    string? TimingSourceSupporting,
    string WhyIntro,
    IReadOnlyList<string> WhyBullets,
    string TimelineTitle,
    IReadOnlyList<string> TimelineLines,
    string WorkContinuityTitle,
    string WorkContinuityNl,
    string? AllocationReviewNl,
    string ProposalTitle,
    string? ProposalInterval,
    string? ProposalHoursLabel,
    string? ProposalProject,
    string? ProposalBon,
    string? ProposalTaskLabel,
    string ProposalWhyLine,
    string BookingStatusNl,
    string ReadinessNl,
    string? ExistingOtherBookingNl,
    string? HqExcursionNl);
