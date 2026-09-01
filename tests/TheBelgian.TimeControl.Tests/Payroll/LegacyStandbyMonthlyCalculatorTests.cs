using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class LegacyStandbyMonthlyCalculatorTests
{
    private static readonly PayrollPeriodSnapshot July = PayrollPeriodSnapshot.ForMonth(
        2026,
        7,
        new DateOnly(2026, 7, 31));

    [Fact]
    public void Calculate_NoStandby_ReturnsZero()
    {
        var result = LegacyStandbyMonthlyCalculator.Calculate(July, "1", new Dictionary<DateOnly, decimal>());
        Assert.Equal(0m, result.ExactHours);
        Assert.Equal(0m, result.RoundedHours);
    }

    [Theory]
    [InlineData(0.25, 1)]
    [InlineData(3.08, 4)]
    [InlineData(4.00, 4)]
    [InlineData(4.10, 5)]
    [InlineData(8.17, 9)]
    public void Calculate_CeilingExamples(decimal exact, decimal rounded)
    {
        var result = LegacyStandbyMonthlyCalculator.Calculate(
            July,
            "1",
            new Dictionary<DateOnly, decimal> { [new DateOnly(2026, 7, 1)] = exact });
        Assert.Equal(exact, result.ExactHours);
        Assert.Equal(rounded, result.RoundedHours);
    }

    [Fact]
    public void Calculate_MultipleDays_SumsBeforeCeiling()
    {
        var result = LegacyStandbyMonthlyCalculator.Calculate(
            July,
            "1",
            new Dictionary<DateOnly, decimal>
            {
                [new DateOnly(2026, 7, 1)] = 2m,
                [new DateOnly(2026, 7, 2)] = 2.17m,
            });
        Assert.Equal(4.17m, result.ExactHours);
        Assert.Equal(5m, result.RoundedHours);
    }
}
