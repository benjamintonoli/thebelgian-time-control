using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyMonthlyOrdinaryCalculatorTests
{
    private static readonly PayrollPeriodSnapshot July = PayrollPeriodSnapshot.ForMonth(
        2026,
        7,
        new DateOnly(2026, 7, 31));

    [Fact]
    public void Calculate_ActualBelowTheoretical_ProducesNegativeDifference()
    {
        var daily = Daily("1", new DateOnly(2026, 7, 1), 8m);
        var result = LegacyMonthlyOrdinaryCalculator.Calculate(July, "1", [daily], 179m);
        Assert.Equal(-171m, result.DifferenceHours);
    }

    [Fact]
    public void Calculate_NegativeDifference_IsNotClamped()
    {
        var daily = Daily("1", new DateOnly(2026, 7, 1), 150m);
        var result = LegacyMonthlyOrdinaryCalculator.Calculate(July, "1", [daily], 179m);
        Assert.Equal(-29m, result.DifferenceHours);
    }

    [Fact]
    public void Calculate_ZeroDifference()
    {
        var daily = Daily("1", new DateOnly(2026, 7, 1), 179m);
        var result = LegacyMonthlyOrdinaryCalculator.Calculate(July, "1", [daily], 179m);
        Assert.Equal(0m, result.DifferenceHours);
    }

    [Fact]
    public void Calculate_SumsEachDateOnce()
    {
        var days = new[]
        {
            Daily("1", new DateOnly(2026, 7, 1), 8m),
            Daily("1", new DateOnly(2026, 7, 2), 7m),
        };
        var result = LegacyMonthlyOrdinaryCalculator.Calculate(July, "1", days, 179m);
        Assert.Equal(15m, result.ActualOrdinaryHours);
    }

    private static LegacyDailyPayrollResult Daily(string resourceId, DateOnly date, decimal total) =>
        new(
            resourceId,
            date,
            8m,
            total,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            0m,
            total,
            0m,
            0m,
            false,
            total);
}
