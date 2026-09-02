using TheBelgian.TimeControl.Core.Payroll.Legacy;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Shadow;

public sealed class PayrollShadowCalculationService
{
    public PayrollMonthShadowResult Calculate(
        PayrollPeriodSnapshot period,
        string resourceId,
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<CalendarSyntheticEntry> syntheticAbsences) =>
        Calculate(period, resourceId, function: null, performances, syntheticAbsences);

    public PayrollMonthShadowResult Calculate(
        PayrollPeriodSnapshot period,
        string resourceId,
        string? function,
        IReadOnlyList<NormalizedPerformanceEntry> performances,
        IReadOnlyList<CalendarSyntheticEntry> syntheticAbsences)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        ArgumentNullException.ThrowIfNull(performances);
        ArgumentNullException.ThrowIfNull(syntheticAbsences);

        var cityConfiguration = PayrollShadowConfigurationSnapshot.CreateCityConfiguration(period);
        var kmConfiguration = PayrollShadowConfigurationSnapshot.ResolveKmConfiguration(period);

        var resourcePerformances = performances
            .Where(row => row.ResourceId == resourceId)
            .Where(row => LegacyPayrollPerformanceEligibility.IsIncluded(function, row.HfdTaakId))
            .ToList();
        var resourceSynthetic = syntheticAbsences
            .Where(row => row.ResourceId == resourceId)
            .ToList();

        var ledger = CurrentPayrollLedgerBuilder.Build(resourcePerformances, resourceSynthetic);
        var dailyInputs = CurrentPayrollLegacyAdapter.ToDailyInputs(ledger);

        var dailyResults = new List<LegacyDailyPayrollResult>();
        var standbyTotals = new Dictionary<DateOnly, decimal>();
        foreach (var dayGroup in dailyInputs.GroupBy(row => row.Date))
        {
            var day = LegacyDailyPayrollPipeline.CalculateDay(
                resourceId,
                dayGroup.Key,
                dayGroup.ToList());
            dailyResults.Add(day.DailyResult);
            standbyTotals[dayGroup.Key] = LegacyStandbyDailyCalculator.CalculateDailyTotal(dayGroup.ToList());
        }

        var cityUnits = LegacyCityAllowancePerformanceCalculator.CalculateMonthlyUnits(
            resourcePerformances,
            cityConfiguration);
        var kmDailyInputs = resourcePerformances
            .Select(CurrentPayrollLegacyAdapter.ToDailyInputFromPerformance)
            .ToList();
        var kmResult = LegacyKmAllowanceCalculator.Calculate(kmDailyInputs, period, kmConfiguration);

        return LegacyMonthlyHoursPipeline.Calculate(
            period,
            resourceId,
            dailyResults,
            standbyTotals,
            cityUnits,
            cityConfiguration,
            kmResult);
    }
}
