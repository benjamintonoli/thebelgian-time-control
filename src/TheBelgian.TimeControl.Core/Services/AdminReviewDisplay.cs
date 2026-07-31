using System.Globalization;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

/// <summary>Admin-facing labels and formatting. No matching changes.</summary>
public static class AdminReviewDisplay
{
    public static EvidenceStrength Evidence(string matcherStatus) =>
        matcherStatus switch
        {
            "RecoveredProbable" => EvidenceStrength.ProbableVisit,
            "Ambiguous" => EvidenceStrength.MultipleCandidates,
            "Unresolved" => EvidenceStrength.NoReliableMatch,
            "Probable" or "Confirmed" or "ConfirmedLocationMatch" or "ProbableLocationMatch"
                => EvidenceStrength.StrongProposal,
            _ => EvidenceStrength.NoReliableMatch,
        };

    public static string EvidenceLabel(EvidenceStrength strength) =>
        strength switch
        {
            EvidenceStrength.StrongProposal => "Sterk voorstel",
            EvidenceStrength.ProbableVisit => "Waarschijnlijk bezoek",
            EvidenceStrength.MultipleCandidates => "Meerdere mogelijke bezoeken",
            EvidenceStrength.NoReliableMatch => "Geen betrouwbare match",
            _ => strength.ToString(),
        };

    public static string EvidenceLabel(string matcherStatus) =>
        EvidenceLabel(Evidence(matcherStatus));

    /// <summary>Legacy alias used by older call sites; maps to evidence labels.</summary>
    public static string MatcherStatus(string status) => EvidenceLabel(status);

    public static string EvidenceBadgeClass(EvidenceStrength strength) =>
        strength switch
        {
            EvidenceStrength.StrongProposal => "badge-evidence-strong",
            EvidenceStrength.ProbableVisit => "badge-evidence-probable",
            EvidenceStrength.MultipleCandidates => "badge-evidence-ambiguous",
            _ => "badge-evidence-none",
        };

    public static string Impact(SpotcheckPriorityTier? priority) =>
        priority switch
        {
            SpotcheckPriorityTier.HighPriority => "Hoog",
            SpotcheckPriorityTier.IndividualException => "Uitzondering",
            SpotcheckPriorityTier.SmallDeviation => "Kleine afwijking",
            SpotcheckPriorityTier.Informational => "Informatief",
            null => "—",
            _ => priority.ToString() ?? "—",
        };

    public static string ImpactBadgeClass(SpotcheckPriorityTier? priority) =>
        priority switch
        {
            SpotcheckPriorityTier.HighPriority => "badge-impact-high",
            SpotcheckPriorityTier.IndividualException => "badge-impact-exception",
            SpotcheckPriorityTier.SmallDeviation => "badge-impact-small",
            SpotcheckPriorityTier.Informational => "badge-impact-info",
            _ => "badge-impact-none",
        };

    public static string Priority(SpotcheckPriorityTier? priority) => Impact(priority);

    public static string Deviation(int? minutes)
    {
        if (minutes is null)
        {
            return "—";
        }

        if (minutes.Value == 0)
        {
            return "op tijd";
        }

        return minutes.Value > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{minutes.Value} min later")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(minutes.Value)} min vroeger");
    }

    public static string Truncate(string? value, int maxLength = 48)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "—";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, maxLength - 1), "…");
    }

    public static string VisitAddressLine(ReviewVisitCandidate? visit) =>
        visit is null ? "—" : Truncate(visit.Address, 42);

    public static string VisitMetricsLine(ReviewVisitCandidate? visit)
    {
        if (visit is null)
        {
            return "—";
        }

        var window = string.Create(
            CultureInfo.InvariantCulture,
            $"{visit.Arrival:HH:mm}–{visit.Departure:HH:mm}");
        var distance = visit.DistanceMeters is { } meters
            ? string.Create(CultureInfo.InvariantCulture, $"{meters:0} m")
            : "afstand onbekend";
        var overlap = string.Create(
            CultureInfo.InvariantCulture,
            $"{visit.OverlapPercent:0}% overlap");
        var partial = visit.OverlapPercent > 0 && visit.OverlapPercent < 50
            ? " · gedeeltelijke dekking"
            : string.Empty;
        return $"{window} · {distance} · {overlap}{partial}";
    }

    public static string VisitSummary(ReviewVisitCandidate? visit)
    {
        if (visit is null)
        {
            return "—";
        }

        var address = VisitAddressLine(visit);
        return address == "—"
            ? VisitMetricsLine(visit)
            : $"{address} · {VisitMetricsLine(visit)}";
    }

    public static string DecisionActionLabel(AdminReviewStatus status) =>
        status switch
        {
            AdminReviewStatus.Confirmed => "Voorstel bevestigen",
            AdminReviewStatus.Rejected => "Voorstel afwijzen",
            AdminReviewStatus.NoReliableMatch => "Geen betrouwbare match",
            AdminReviewStatus.NeedsMoreInformation => "Meer informatie nodig",
            _ => status.ToString(),
        };
}
