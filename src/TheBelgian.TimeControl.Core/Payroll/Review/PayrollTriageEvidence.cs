using System.Globalization;
using System.Text.RegularExpressions;

namespace TheBelgian.TimeControl.Core.Payroll.Review;

/// <summary>
/// Presentation helpers that surface already-persisted finding text. Does not invent facts.
/// </summary>
public static partial class PayrollTriageEvidence
{
    private static readonly Regex IntervalInDescription = IntervalRegex();

    public static string? ParseTimeInterval(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var match = IntervalInDescription.Match(description);
        if (!match.Success)
        {
            return null;
        }

        return $"{match.Groups[1].Value}–{match.Groups[2].Value}";
    }

    public static string? ExtractEvidenceField(string? evidence, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        var marker = fieldName.TrimEnd('=') + "=";
        var start = evidence.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = evidence.IndexOf(';', start);
        var raw = end < 0 ? evidence[start..] : evidence[start..end];
        raw = raw.Trim();
        if (raw.Length == 0 || raw == "—" || raw == "-" || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return raw;
    }

    public static bool PlanningPresent(string? evidence, string? friendlyState, string? ruleHint)
    {
        if (!string.IsNullOrWhiteSpace(friendlyState)
            && friendlyState.Contains("Zonder", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(ruleHint)
            && ruleHint.Contains("Geen reservatie", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(evidence)
            && evidence.Contains("planning evidence = none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(evidence)
            && (evidence.Contains("planning=[", StringComparison.OrdinalIgnoreCase)
                || evidence.Contains("planningIds=", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public static IReadOnlyList<string> BuildChips(
        bool descriptionPresent,
        bool planningPresent,
        string? gpsClassification,
        string? timeInterval,
        decimal? bookedHours)
    {
        var chips = new List<string>(5);
        chips.Add(descriptionPresent ? "Omschrijving aanwezig" : "Omschrijving ontbreekt");
        chips.Add(planningPresent ? "Planning aanwezig" : "Planning ontbreekt");
        if (!string.IsNullOrWhiteSpace(timeInterval))
        {
            chips.Add(timeInterval);
        }

        if (bookedHours is { } hours)
        {
            chips.Add($"{hours.ToString("0.##", CultureInfo.GetCultureInfo("nl-BE"))} u");
        }

        if (!string.IsNullOrWhiteSpace(gpsClassification)
            && !gpsClassification.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            chips.Add($"GPS: {gpsClassification}");
        }

        return chips;
    }

    public static string FormatHours(decimal? hours) =>
        hours is null
            ? "—"
            : $"{hours.Value.ToString("0.##", CultureInfo.GetCultureInfo("nl-BE"))} u";

    [GeneratedRegex(@"Geboekt\s+(\d{1,2}:\d{2})-(\d{1,2}:\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex IntervalRegex();
}
