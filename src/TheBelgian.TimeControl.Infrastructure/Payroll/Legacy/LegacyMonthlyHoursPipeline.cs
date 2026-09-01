using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyMonthlyHoursPipeline
{
    public static PayrollMonthShadowResult Calculate(
        PayrollPeriodSnapshot period,
        string resourceId,
        IReadOnlyList<LegacyDailyPayrollResult> dailyResults,
        IReadOnlyDictionary<DateOnly, decimal> dailyStandbyTotals)
    {
        var theoreticalHours = LegacyGlobalWeekdayTheoreticalHoursProvider.GetMonthlyHours(period);
        var ordinary = LegacyMonthlyOrdinaryCalculator.Calculate(
            period,
            resourceId,
            dailyResults,
            theoreticalHours);
        var standby = LegacyStandbyMonthlyCalculator.Calculate(period, resourceId, dailyStandbyTotals);

        var code135At150 = new PayrollCode135ShadowCandidate(150, ordinary.DifferenceHours);
        var code135At200 = new PayrollCode135ShadowCandidate(200, standby.RoundedHours);

        return new PayrollMonthShadowResult
        {
            ResourceId = resourceId,
            Year = period.Year,
            Month = period.Month,
            EvaluationDate = period.EvaluationDate,
            LegacyTheoreticalHours = theoreticalHours,
            LegacyActualOrdinaryHours = ordinary.ActualOrdinaryHours,
            LegacyDifferenceHours = ordinary.DifferenceHours,
            StandbyExactHours = standby.ExactHours,
            Code135At150 = code135At150,
            Code135At200 = code135At200,
            OrdinaryStatus = PayrollMonthCalculationStatus.Calculated,
            StandbyStatus = PayrollMonthCalculationStatus.Calculated,
            KmStatus = PayrollMonthCalculationStatus.NotCalculated,
            CityStatus = PayrollMonthCalculationStatus.NotCalculated,
            Code414Status = PayrollMonthCalculationStatus.NotCalculated,
            TheoreticalMinutes = theoreticalHours * 60m,
            PayableOrdinaryMinutes = ordinary.ActualOrdinaryHours * 60m,
            DifferenceMinutes = ordinary.DifferenceHours * 60m,
            Overtime150Units = ordinary.DifferenceHours,
            StandbyExactMinutes = standby.ExactHours * 60m,
            StandbyRoundedHours = standby.RoundedHours,
            Standby200Units = standby.RoundedHours,
            EligibleKm = null,
            KmAmount = null,
            CityTripUnits = null,
            CityAllowanceAmount = null,
            Code414Amount = null,
        };
    }
}
