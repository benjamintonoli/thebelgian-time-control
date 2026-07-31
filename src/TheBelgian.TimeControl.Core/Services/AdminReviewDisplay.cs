using System.Globalization;
using TheBelgian.TimeControl.Core.Models;

namespace TheBelgian.TimeControl.Core.Services;

/// <summary>Admin-facing labels and formatting. No matching changes.</summary>
public static class AdminReviewDisplay
{
    public static string MatcherStatus(string status) =>
        status switch
        {
            "RecoveredProbable" => "Waarschijnlijk bezoek",
            "Probable" or "Confirmed" or "ConfirmedLocationMatch" or "ProbableLocationMatch"
                => "Voorgesteld bezoek",
            "Ambiguous" => "Meerdere mogelijke bezoeken",
            "Unresolved" => "Geen betrouwbare match",
            _ => status,
        };

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

    public static string Priority(SpotcheckPriorityTier? priority) =>
        priority switch
        {
            SpotcheckPriorityTier.HighPriority => "Hoog",
            SpotcheckPriorityTier.IndividualException => "Uitzondering",
            SpotcheckPriorityTier.SmallDeviation => "Kleine afwijking",
            SpotcheckPriorityTier.Informational => "Informatief",
            null => "—",
            _ => priority.ToString() ?? "—",
        };

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

    public static string VisitSummary(ReviewVisitCandidate? visit)
    {
        if (visit is null)
        {
            return "—";
        }

        var address = Truncate(visit.Address, 40);
        var window = string.Create(
            CultureInfo.InvariantCulture,
            $"{visit.Arrival:HH:mm}–{visit.Departure:HH:mm}");
        return address == "—"
            ? window
            : $"{address} · {window}";
    }
}
