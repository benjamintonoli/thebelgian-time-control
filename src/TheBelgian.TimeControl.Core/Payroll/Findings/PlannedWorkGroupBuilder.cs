using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll.Findings;

public sealed record PlannedWorkGroup(
    DateOnly Date,
    long IdCalendar,
    string? ProjectId,
    int? ProjectNumber,
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    string? Subject,
    IReadOnlyList<string> ResourceIds,
    IReadOnlyList<PayrollPlanningReservation> Reservations);

public static class PlannedWorkGroupBuilder
{
    public static IReadOnlyList<PlannedWorkGroup> Build(IReadOnlyList<PayrollPlanningReservation> planning)
    {
        return planning
            .Where(item => item.Classification == PayrollPlanningClassification.WorkReservation)
            .GroupBy(item => (item.IdCalendar, item.Date))
            .Select(group =>
            {
                var rows = group
                    .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
                    .ToList();
                var resourceIds = rows
                    .Select(item => item.ResourceId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                var primary = rows[0];
                return new PlannedWorkGroup(
                    primary.Date,
                    primary.IdCalendar,
                    primary.ProjectId,
                    primary.ProjectNumber,
                    primary.TimeFrom,
                    primary.TimeTo,
                    primary.Subject,
                    resourceIds,
                    rows);
            })
            .Where(group => group.ResourceIds.Count >= 2)
            .OrderBy(group => group.Date)
            .ThenBy(group => group.IdCalendar)
            .ToList();
    }
}
