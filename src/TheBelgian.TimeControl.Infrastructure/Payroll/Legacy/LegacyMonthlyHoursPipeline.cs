using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

public static class LegacyMonthlyHoursPipeline
{
    public static PayrollMonthShadowResult Calculate(
        PayrollPeriodSnapshot period,
        string resourceId,
        IReadOnlyList<LegacyDailyPayrollResult> dailyResults,
        IReadOnlyDictionary<DateOnly, decimal> dailyStandbyTotals) =>
        Calculate(period, resourceId, dailyResults, dailyStandbyTotals, null, null, null);

    public static PayrollMonthShadowResult Calculate(
        PayrollPeriodSnapshot period,
        string resourceId,
        IReadOnlyList<LegacyDailyPayrollResult> dailyResults,
        IReadOnlyDictionary<DateOnly, decimal> dailyStandbyTotals,
        int? cityTripUnits,
        CityAllowanceConfiguration? cityConfiguration) =>
        Calculate(period, resourceId, dailyResults, dailyStandbyTotals, cityTripUnits, cityConfiguration, null);

    public static PayrollMonthShadowResult Calculate(
        PayrollPeriodSnapshot period,
        string resourceId,
        IReadOnlyList<LegacyDailyPayrollResult> dailyResults,
        IReadOnlyDictionary<DateOnly, decimal> dailyStandbyTotals,
        int? cityTripUnits,
        CityAllowanceConfiguration? cityConfiguration,
        LegacyKmAllowanceResult? kmResult)
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

        decimal? cityAllowanceAmount = null;
        var cityStatus = PayrollMonthCalculationStatus.NotCalculated;
        if (cityTripUnits is not null && cityConfiguration is not null)
        {
            var city = LegacyCityAllowanceMonthlyCalculator.Calculate(cityTripUnits.Value, cityConfiguration);
            cityTripUnits = city.CityTripUnits;
            cityAllowanceAmount = city.CityAllowanceAmount;
            cityStatus = PayrollMonthCalculationStatus.Calculated;
        }

        decimal? eligibleKm = null;
        decimal? extra75YtdHours = null;
        decimal? kmRate = null;
        decimal? netKmLegacyQuantity = null;
        decimal? kmAmount = null;
        var kmStatus = PayrollMonthCalculationStatus.NotCalculated;
        if (kmResult is not null)
        {
            eligibleKm = kmResult.EligibleKm;
            extra75YtdHours = kmResult.Extra75YtdHours;
            kmRate = kmResult.RatePerKm;
            netKmLegacyQuantity = kmResult.NetKmLegacyQuantity;
            kmAmount = kmResult.KmAmount;
            kmStatus = PayrollMonthCalculationStatus.Calculated;
        }

        var (code414Status, code414Amount) = LegacyCode414ShadowCalculator.Calculate(
            cityStatus,
            cityAllowanceAmount,
            kmStatus,
            kmAmount);

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
            KmStatus = kmStatus,
            CityStatus = cityStatus,
            Code414Status = code414Status,
            TheoreticalMinutes = theoreticalHours * 60m,
            PayableOrdinaryMinutes = ordinary.ActualOrdinaryHours * 60m,
            DifferenceMinutes = ordinary.DifferenceHours * 60m,
            Overtime150Units = ordinary.DifferenceHours,
            StandbyExactMinutes = standby.ExactHours * 60m,
            StandbyRoundedHours = standby.RoundedHours,
            Standby200Units = standby.RoundedHours,
            EligibleKm = eligibleKm,
            Extra75YtdHours = extra75YtdHours,
            KmRate = kmRate,
            NetKmLegacyQuantity = netKmLegacyQuantity,
            KmAmount = kmAmount,
            CityTripUnits = cityTripUnits,
            CityAllowanceAmount = cityAllowanceAmount,
            Code414Amount = code414Amount,
        };
    }
}
