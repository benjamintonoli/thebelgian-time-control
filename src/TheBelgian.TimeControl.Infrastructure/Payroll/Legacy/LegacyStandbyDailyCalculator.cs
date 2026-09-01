using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyStandbyDailyCalculator
{
    private const int StandbyHfdTaakId = 23;

    public static decimal CalculateDailyTotal(IReadOnlyList<LegacyDailyPerformanceInput> rows)
    {
        var watchRows = rows.Where(row => row.HfdTaakId == StandbyHfdTaakId).ToList();
        if (watchRows.Count == 0)
        {
            return 0m;
        }

        var atlTotal = watchRows.Sum(row => row.AtlHoursRaw);
        var travelInputs = watchRows
            .Select(row => new LegacyTravelPerformanceInput(
                row.PerformanceId,
                row.HfdTaakId,
                row.Start?.TimeOfDay,
                row.AtlHoursRaw))
            .ToList();
        var travel = LegacyTravelDerivation.CalculateDay(string.Empty, default, travelInputs);
        return atlTotal + travel.TravelStartDeductionHours + travel.TravelEndDeductionHours;
    }
}
