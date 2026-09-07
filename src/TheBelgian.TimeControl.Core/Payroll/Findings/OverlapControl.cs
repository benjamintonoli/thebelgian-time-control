using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Pairwise ordinary-performance overlap findings (absence/standby excluded).
/// </summary>
public static class OverlapControl
{
    private static readonly HashSet<int> ExcludedHfdTaakIds = [10, 18, 23];

    public static IReadOnlyList<PayrollFinding> Evaluate(
        IReadOnlyList<NormalizedPerformanceEntry> performances)
    {
        var findings = new List<PayrollFinding>();
        var eligible = performances
            .Where(IsEligible)
            .GroupBy(item => (item.ResourceId, item.Date))
            .ToList();

        foreach (var dayGroup in eligible)
        {
            var rows = dayGroup
                .OrderBy(item => item.Start)
                .ThenBy(item => item.SortKey)
                .ToList();

            for (var i = 0; i < rows.Count; i++)
            {
                for (var j = i + 1; j < rows.Count; j++)
                {
                    var left = rows[i];
                    var right = rows[j];
                    var overlapHours = CalculateOverlapHours(left, right);
                    if (overlapHours <= 0m)
                    {
                        continue;
                    }

                    var firstId = Math.Min(left.SourceEntryId, right.SourceEntryId);
                    var secondId = Math.Max(left.SourceEntryId, right.SourceEntryId);
                    var overlapMinutes = Math.Round(overlapHours * 60m, 0, MidpointRounding.AwayFromZero);
                    findings.Add(new PayrollFinding(
                        FindingKey: $"overlap:{left.ResourceId}:{left.Date:yyyyMMdd}:{firstId}:{secondId}",
                        ResourceId: left.ResourceId,
                        Date: left.Date,
                        FindingType: PayrollFindingType.OverlappingPerformances,
                        Severity: PayrollFindingSeverity.High,
                        Title: "Dubbele uren",
                        Description: $"Overlap van {FormatHours(overlapHours)} ({overlapMinutes:0} min) tussen twee prestaties.",
                        Evidence:
                            $"A#{left.SourceEntryId} {FormatInterval(left)} proj={FormatProject(left.ProjectNumber)} {left.Description ?? ""}; "
                            + $"B#{right.SourceEntryId} {FormatInterval(right)} proj={FormatProject(right.ProjectNumber)} {right.Description ?? ""}.",
                        SuggestedAction: $"Controleer dubbele uren; overlap = {FormatHours(overlapHours)}.",
                        RelatedPerformanceIds: [firstId, secondId],
                        OverlapHours: overlapHours,
                        BookedHours: left.AtlHoursRaw + right.AtlHoursRaw));
                }
            }
        }

        return findings
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsEligible(NormalizedPerformanceEntry entry) =>
        !entry.IsCalendarSynthetic
        && entry.Start is not null
        && entry.End is not null
        && entry.End > entry.Start
        && entry.HfdTaakId is not null
        && !ExcludedHfdTaakIds.Contains(entry.HfdTaakId.Value)
        && !entry.IsAbsence
        && !entry.IsStandby;

    private static decimal CalculateOverlapHours(
        NormalizedPerformanceEntry left,
        NormalizedPerformanceEntry right)
    {
        var start = left.Start!.Value > right.Start!.Value ? left.Start.Value : right.Start.Value;
        var end = left.End!.Value < right.End!.Value ? left.End.Value : right.End.Value;
        if (end <= start)
        {
            return 0m;
        }

        return (decimal)(end - start).TotalHours;
    }

    private static string FormatInterval(NormalizedPerformanceEntry entry) =>
        $"{entry.Start:HH:mm}-{entry.End:HH:mm}";

    private static string FormatProject(int? projectNumber) =>
        projectNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";

    private static string FormatHours(decimal hours) =>
        hours.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("nl-BE")) + " u";
}
