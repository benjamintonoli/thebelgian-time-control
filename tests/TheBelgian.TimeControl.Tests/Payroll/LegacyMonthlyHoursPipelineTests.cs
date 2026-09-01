using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyMonthlyHoursPipelineTests
{
    [Fact]
    public void Calculate_PreservesNegativeCode135At150()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 7, 31));
        var daily = new LegacyDailyPayrollResult(
            "1",
            new DateOnly(2026, 7, 1),
            8m,
            150m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            150m,
            0m,
            0m,
            false,
            150m);

        var result = LegacyMonthlyHoursPipeline.Calculate(period, "1", [daily], new Dictionary<DateOnly, decimal>());
        Assert.Equal(-29m, result.Code135At150!.CalculatedUnits);
        Assert.Equal(-29m, result.Overtime150Units);
    }

    [Fact]
    public void Calculate_LeavesAllowanceFieldsNotCalculated()
    {
        var period = PayrollPeriodSnapshot.ForMonth(2026, 7, new DateOnly(2026, 7, 31));
        var result = LegacyMonthlyHoursPipeline.Calculate(
            period,
            "1",
            [],
            new Dictionary<DateOnly, decimal> { [new DateOnly(2026, 7, 1)] = 3.08m });

        Assert.Equal(PayrollMonthCalculationStatus.NotCalculated, result.KmStatus);
        Assert.Equal(PayrollMonthCalculationStatus.NotCalculated, result.CityStatus);
        Assert.Equal(PayrollMonthCalculationStatus.NotCalculated, result.Code414Status);
        Assert.Null(result.KmAmount);
        Assert.Null(result.Code414Amount);
        Assert.Equal(4m, result.Code135At200!.CalculatedUnits);
    }
}
