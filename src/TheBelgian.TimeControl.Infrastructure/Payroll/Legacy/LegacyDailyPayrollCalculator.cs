using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyDailyPayrollCalculator
{
    private static readonly HashSet<int> WorkExcludedHfdTaakIds = [10, 18, 23];
    private static readonly HashSet<int> AbsenceHfdTaakIds = [10, 18];

    public static LegacyDailyPayrollResult Calculate(
        string resourceId,
        DateOnly date,
        IReadOnlyList<LegacyDailyPerformanceInput> rows,
        IReadOnlyDictionary<long, LegacyOverlapResult> overlapByPerformanceId,
        IReadOnlyDictionary<long, LegacyTravelRowResult> travelByPerformanceId,
        IReadOnlyDictionary<long, LegacyExtra75RowResult> extra75ByPerformanceId,
        LegacyDailyPauseResult pauseResult,
        LegacyDailyComponentOverrides? componentOverrides = null)
    {
        var workRows = rows
            .Where(row => row.HfdTaakId is not null && !WorkExcludedHfdTaakIds.Contains(row.HfdTaakId.Value))
            .ToList();
        var absenceRows = rows
            .Where(row => row.HfdTaakId is not null && AbsenceHfdTaakIds.Contains(row.HfdTaakId.Value))
            .ToList();

        var theoreticalDayHours = GetTheoreticalDayHours(date);
        var registeredWorkHours = workRows.Sum(row => row.AtlHoursRaw);
        var travelStart = componentOverrides?.TravelStartDeductionHours
            ?? travelByPerformanceId.Values.Max(result => result.TravelBeginHours);
        var travelEnd = componentOverrides?.TravelEndDeductionHours
            ?? travelByPerformanceId.Values.Max(result => result.TravelEndHours);
        var pauseCorrection = componentOverrides?.PauseCorrectionHours ?? pauseResult.PauseCorrectionHours;
        var overlapCorrection = workRows.Sum(row =>
            overlapByPerformanceId.TryGetValue(row.PerformanceId, out var overlap)
                ? overlap.OverlapHours
                : 0m);
        var extra15 = workRows.Sum(row =>
            travelByPerformanceId.TryGetValue(row.PerformanceId, out var travel)
                ? travel.Extra15Hours
                : 0m);
        var extra75KmTotal = workRows.Sum(row =>
            extra75ByPerformanceId.TryGetValue(row.PerformanceId, out var extra75)
                ? extra75.Extra75Km
                : 0m);
        var extra75AsHours = extra75KmTotal / 60m;

        var payableWorkHours = Math.Max(
            0m,
            registeredWorkHours
                - travelStart
                - travelEnd
                + pauseCorrection
                - overlapCorrection
                + extra15
                + extra75AsHours);

        var rawAbsenceHours = absenceRows.Sum(row => row.AtlHoursRaw);
        var payableAbsenceHours = theoreticalDayHours > 0m
            ? Math.Min(theoreticalDayHours, Math.Max(0m, rawAbsenceHours))
            : 0m;
        var hasAbsence = payableAbsenceHours > 0m;

        var finalDailyTotal = hasAbsence
            ? Math.Min(theoreticalDayHours, payableWorkHours + payableAbsenceHours)
            : payableWorkHours;

        return new LegacyDailyPayrollResult(
            resourceId,
            date,
            theoreticalDayHours,
            registeredWorkHours,
            travelStart,
            travelEnd,
            pauseCorrection,
            overlapCorrection,
            extra15,
            extra75KmTotal,
            extra75AsHours,
            payableWorkHours,
            rawAbsenceHours,
            payableAbsenceHours,
            hasAbsence,
            finalDailyTotal);
    }

    public static decimal GetTheoreticalDayHours(DateOnly date) =>
        date.DayOfWeek switch
        {
            DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday => 8m,
            DayOfWeek.Friday => 7m,
            _ => 0m,
        };
}
