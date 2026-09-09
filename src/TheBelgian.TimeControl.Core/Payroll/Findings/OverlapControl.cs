using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

/// <summary>
/// Pairwise ordinary-performance overlap findings (absence/standby excluded).
/// Classification and proposals via <see cref="OverlapIntelligence"/>.
/// </summary>
public static class OverlapControl
{
    private static readonly HashSet<int> ExcludedHfdTaakIds = [10, 18, 23];

    public static IReadOnlyList<PayrollFinding> Evaluate(
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<StandbyGpsDayEvidence>? gpsDays = null)
    {
        var findings = new List<PayrollFinding>();
        var gpsLookup = (gpsDays ?? [])
            .GroupBy(item => (item.ResourceId, item.Date))
            .ToDictionary(group => group.Key, group => group.First());
        var eligible = performances
            .Where(IsEligible)
            .GroupBy(item => (item.ResourceId, item.Date))
            .ToList();

        foreach (var dayGroup in eligible)
        {
            gpsLookup.TryGetValue(dayGroup.Key, out var gps);
            var rows = dayGroup
                .OrderBy(item => item.Start)
                .ThenBy(item => item.SortKey)
                .ToList();

            for (var i = 0; i < rows.Count; i++)
            {
                for (var j = i + 1; j < rows.Count; j++)
                {
                    var analysis = OverlapIntelligence.Analyze(rows[i], rows[j], gps);
                    if (!analysis.HasOverlap)
                    {
                        continue;
                    }

                    var firstId = Math.Min(analysis.A.SourceEntryId, analysis.B.SourceEntryId);
                    var secondId = Math.Max(analysis.A.SourceEntryId, analysis.B.SourceEntryId);
                    var severity = analysis.Confidence == OverlapConfidence.High
                        ? PayrollFindingSeverity.High
                        : PayrollFindingSeverity.Review;

                    findings.Add(new PayrollFinding(
                        FindingKey: $"overlap:{analysis.A.ResourceId}:{analysis.A.Date:yyyyMMdd}:{firstId}:{secondId}",
                        ResourceId: analysis.A.ResourceId,
                        Date: analysis.A.Date,
                        FindingType: PayrollFindingType.OverlappingPerformances,
                        Severity: severity,
                        Title: analysis.Kind == OverlapKind.ExactDuplicate
                            ? "Exacte dubbele prestatie"
                            : "Dubbele uren",
                        Description: BuildDescription(analysis),
                        Evidence: analysis.Evidence,
                        SuggestedAction: BuildSuggestedAction(analysis),
                        RelatedPerformanceIds: [firstId, secondId],
                        OverlapHours: analysis.OverlapHours,
                        BookedHours: analysis.A.AtlHoursRaw + analysis.B.AtlHoursRaw,
                        SuggestedPayableStart: analysis.ProposedStart,
                        SuggestedPayableEnd: analysis.ProposedEnd,
                        SuggestedPayableHours: analysis.ProposedStart is not null && analysis.ProposedEnd is not null
                            ? Math.Round(
                                (decimal)(analysis.ProposedEnd.Value - analysis.ProposedStart.Value).TotalHours,
                                2,
                                MidpointRounding.AwayFromZero)
                            : null,
                        GpsClassification: analysis.Kind.ToString()));
                }
            }
        }

        return findings
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.Date)
            .ThenBy(item => item.FindingKey, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildDescription(OverlapAnalysis analysis)
    {
        var minutes = analysis.OverlapMinutes.ToString("0", CultureInfo.GetCultureInfo("nl-BE"));
        return analysis.Kind switch
        {
            OverlapKind.ExactDuplicate =>
                $"Exacte dubbele boeking ({minutes} min overlap). {analysis.AdviceNl}",
            _ =>
                $"Overlap van {FormatHours(analysis.OverlapHours)} ({minutes} min) tussen twee prestaties. {analysis.AdviceNl}",
        };
    }

    private static string BuildSuggestedAction(OverlapAnalysis analysis) =>
        analysis.Recommendation switch
        {
            OverlapRecommendationAction.DeleteDuplicate =>
                "Voorstel: dubbele prestatie verwijderen (menselijke goedkeuring vereist).",
            OverlapRecommendationAction.AdjustBoundary when analysis.TargetPerformanceId is not null =>
                $"Voorstel: tijd aanpassen op prestatie #{analysis.TargetPerformanceId} (menselijke goedkeuring vereist).",
            _ =>
                $"Controleer dubbele uren; overlap = {FormatHours(analysis.OverlapHours)}.",
        };

    private static bool IsEligible(NormalizedPerformanceEntry entry) =>
        !entry.IsCalendarSynthetic
        && entry.Start is not null
        && entry.End is not null
        && entry.End > entry.Start
        && entry.HfdTaakId is not null
        && !ExcludedHfdTaakIds.Contains(entry.HfdTaakId.Value)
        && !entry.IsAbsence
        && !entry.IsStandby;

    private static string FormatHours(decimal hours) =>
        hours.ToString("0.00", CultureInfo.GetCultureInfo("nl-BE")) + " u";
}
