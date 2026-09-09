using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Findings;

/// <summary>
/// Builds weak/medium job location evidence from performance postcodes (BON/LEVADR enrichment deferred to geocode path).
/// </summary>
public static class MissingTechnicianJobLocationBuilder
{
    public static IReadOnlyDictionary<string, JobLocationEvidence> FromPerformances(
        IReadOnlyList<NormalizedPerformanceEntry> performances)
    {
        var map = new Dictionary<string, JobLocationEvidence>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in performances.Where(item => !item.IsCalendarSynthetic))
        {
            var evidence = MissingTechnicianSiteConflictAnalyzer.FromPerformancePostcode(row, "perf");
            if (evidence.Confidence == JobLocationConfidence.None)
            {
                continue;
            }

            map[$"perf:{row.SourceEntryId.ToString(CultureInfo.InvariantCulture)}"] = evidence;
            if (!string.IsNullOrWhiteSpace(row.BonNr))
            {
                map.TryAdd("bon:" + row.BonNr.Trim(), evidence with
                {
                    Key = "bon:" + row.BonNr.Trim(),
                    Source = JobLocationSource.BonInterventionAddress,
                    Confidence = JobLocationConfidence.Weak,
                });
            }

            if (!string.IsNullOrWhiteSpace(row.ProjectId))
            {
                map.TryAdd("project:" + row.ProjectId.Trim(), evidence with
                {
                    Key = "project:" + row.ProjectId.Trim(),
                    Source = JobLocationSource.ProjectSiteAddress,
                });
            }

            if (row.ProjectNumber is not null)
            {
                map.TryAdd(
                    "projnr:" + row.ProjectNumber.Value.ToString(CultureInfo.InvariantCulture),
                    evidence with
                    {
                        Key = "projnr:" + row.ProjectNumber.Value.ToString(CultureInfo.InvariantCulture),
                        Source = JobLocationSource.ProjectSiteAddress,
                    });
            }
        }

        return map;
    }
}
