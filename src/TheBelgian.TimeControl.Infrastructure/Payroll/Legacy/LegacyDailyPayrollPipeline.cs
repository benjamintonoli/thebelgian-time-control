using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyDailyPayrollPipeline
{
    public static LegacyDailyPayrollDayContext CalculateDay(
        string resourceId,
        DateOnly date,
        IReadOnlyList<LegacyDailyPerformanceInput> rows,
        IReadOnlyDictionary<long, decimal>? extra75KmOverride = null,
        LegacyDailyComponentOverrides? componentOverrides = null)
    {
        var overlapInputs = rows
            .Select(row => new LegacyOverlapPerformanceInput(
                row.PerformanceId,
                row.SortKey,
                row.HfdTaakId,
                row.Start,
                row.End,
                row.AtlHoursRaw))
            .ToList();
        var overlapResults = LegacyOverlapCalculator.Calculate(overlapInputs);
        var overlapById = overlapResults.ToDictionary(result => result.PerformanceId);

        var travelInputs = rows
            .Select(row => new LegacyTravelPerformanceInput(
                row.PerformanceId,
                row.HfdTaakId,
                row.Start?.TimeOfDay,
                row.AtlHoursRaw))
            .ToList();
        var travelResults = LegacyTravelDerivation.CalculateRows(travelInputs);
        var travelById = travelResults.ToDictionary(result => result.PerformanceId);

        var pauseResult = LegacyPauseCorrection.Calculate(resourceId, date, rows);
        var extra75Results = LegacyExtra75Calculator.CalculateRows(rows, travelById);
        if (extra75KmOverride is not null)
        {
            extra75Results = extra75Results
                .Select(result => extra75KmOverride.TryGetValue(result.PerformanceId, out var overrideKm)
                    ? result with { Extra75Km = overrideKm }
                    : result)
                .ToList();
        }

        var extra75ById = extra75Results.ToDictionary(result => result.PerformanceId);

        var dailyResult = LegacyDailyPayrollCalculator.Calculate(
            resourceId,
            date,
            rows,
            overlapById,
            travelById,
            extra75ById,
            pauseResult,
            componentOverrides);

        return new LegacyDailyPayrollDayContext(
            overlapResults,
            travelResults,
            pauseResult,
            extra75Results,
            dailyResult);
    }
}

public sealed record LegacyDailyPayrollDayContext(
    IReadOnlyList<LegacyOverlapResult> OverlapResults,
    IReadOnlyList<LegacyTravelRowResult> TravelResults,
    LegacyDailyPauseResult PauseResult,
    IReadOnlyList<LegacyExtra75RowResult> Extra75Results,
    LegacyDailyPayrollResult DailyResult);
