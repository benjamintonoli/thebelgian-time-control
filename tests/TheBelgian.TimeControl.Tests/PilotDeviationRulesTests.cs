using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Tests;

public sealed class PilotDeviationRulesTests
{
    [Fact]
    public void Evaluate_CountsOnlyPositiveDifferencesAboveTolerance()
    {
        var day = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.FromHours(2));

        var result = PilotDeviationRules.Evaluate(
            day.AddHours(7),
            day.AddHours(7).AddMinutes(8),
            day.AddHours(16),
            day.AddHours(15).AddMinutes(53),
            3);

        Assert.Equal(8, result.StartDifferenceMinutes);
        Assert.Equal(7, result.EndDifferenceMinutes);
        Assert.True(result.StartRelevant);
        Assert.True(result.EndRelevant);
        Assert.Equal(15, result.PossibleEmployeeBenefitMinutes);
    }

    [Fact]
    public void Evaluate_LeavesNegativeAndToleranceDifferencesInformational()
    {
        var day = new DateTimeOffset(2026, 7, 24, 0, 0, 0, TimeSpan.FromHours(2));

        var result = PilotDeviationRules.Evaluate(
            day.AddHours(7),
            day.AddHours(6).AddMinutes(58),
            day.AddHours(15),
            day.AddHours(14).AddMinutes(55),
            5);

        Assert.Equal(-2, result.StartDifferenceMinutes);
        Assert.Equal(5, result.EndDifferenceMinutes);
        Assert.False(result.StartRelevant);
        Assert.False(result.EndRelevant);
        Assert.Equal(0, result.PossibleEmployeeBenefitMinutes);
    }
}
