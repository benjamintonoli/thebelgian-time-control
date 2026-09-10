using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using Xunit;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollDailyPauseRulesTests
{
    private static TimeSpan T(string value) =>
        TimeSpan.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void ExactlyFourHours_NoMandatoryPause()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [],
            TimeSpan.FromHours(4));
        Assert.Equal(TimeSpan.Zero, plan.RequiredMinimumPause);
        Assert.Equal(TimeSpan.Zero, plan.PauseToAssignOnTargetRow);
    }

    [Fact]
    public void FourHoursOneMinute_RequiresThirty()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [],
            TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(30), plan.RequiredMinimumPause);
        Assert.Equal(TimeSpan.FromMinutes(30), plan.PauseToAssignOnTargetRow);
    }

    [Fact]
    public void EightHours_RequiresThirtyNotPerRow()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [],
            TimeSpan.FromHours(8));
        Assert.Equal(TimeSpan.FromMinutes(30), plan.PauseToAssignOnTargetRow);
    }

    [Fact]
    public void ExistingThirtyOnOtherRow_AddsZero()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [new PayrollDailyPauseRules.DayPauseInput(TimeSpan.FromHours(2), TimeSpan.FromMinutes(30))],
            TimeSpan.FromHours(4));
        Assert.Equal(TimeSpan.Zero, plan.PauseDeficit);
        Assert.Equal(TimeSpan.Zero, plan.PauseToAssignOnTargetRow);
    }

    [Fact]
    public void ExistingFifteen_AddsMissingFifteen()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [new PayrollDailyPauseRules.DayPauseInput(TimeSpan.FromHours(2), TimeSpan.FromMinutes(15))],
            TimeSpan.FromHours(4));
        Assert.Equal(TimeSpan.FromMinutes(15), plan.PauseToAssignOnTargetRow);
    }

    [Fact]
    public void ExistingFortyFive_PreservedOnTarget()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [],
            TimeSpan.FromHours(8),
            targetRowCurrentPause: TimeSpan.FromMinutes(45));
        Assert.Equal(TimeSpan.FromMinutes(45), plan.PauseToAssignOnTargetRow);
        Assert.Equal(TimeSpan.Zero, plan.PauseDeficit);
    }

    [Fact]
    public void MultiplePerformances_OneDailyMinimum()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [
                new PayrollDailyPauseRules.DayPauseInput(TimeSpan.FromHours(2), TimeSpan.Zero),
                new PayrollDailyPauseRules.DayPauseInput(TimeSpan.FromHours(1), TimeSpan.Zero)
            ],
            TimeSpan.FromHours(2));
        Assert.True(plan.TotalGrossWork > TimeSpan.FromHours(4));
        Assert.Equal(TimeSpan.FromMinutes(30), plan.PauseToAssignOnTargetRow);
    }

    [Fact]
    public void CreateCrossingFourHours_AppliesPause()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [new PayrollDailyPauseRules.DayPauseInput(TimeSpan.FromHours(3), TimeSpan.Zero)],
            TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromMinutes(30), plan.PauseToAssignOnTargetRow);
    }

    [Fact]
    public void DeriveAtl_BashiBoundaryWithPause()
    {
        var atl = PayrollDailyPauseRules.DeriveNetAtl(
            T("07:55:00"),
            T("14:45:00"),
            TimeSpan.FromMinutes(30));
        Assert.Equal(6.3333m, atl);
    }

    [Fact]
    public void GpsLunchAbsence_DoesNotDoublePauseAssignment()
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [],
            TimeSpan.FromHours(7));
        Assert.Equal(TimeSpan.FromMinutes(30), plan.PauseToAssignOnTargetRow);
        Assert.DoesNotContain("GPS", plan.ExplanationNl, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PayrollWriteOverlapSafetyTests
{
    private static TimeSpan T(string value) =>
        TimeSpan.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void TouchingBoundary_Allowed()
    {
        var decision = PayrollWriteOverlapSafety.EvaluateCreateOrAdjust(
            T("07:55:00"),
            T("14:45:00"),
            [new PayrollWriteOverlapSafety.Interval(T("14:45:00"), T("15:15:00"))]);
        Assert.Equal(0, decision.OverlapMinutes);
        Assert.False(decision.BlocksWrite);
    }

    [Fact]
    public void OneMinuteOverlap_RequiresBoundary()
    {
        var decision = PayrollWriteOverlapSafety.EvaluateCreateOrAdjust(
            T("07:55:00"),
            T("14:46:00"),
            [new PayrollWriteOverlapSafety.Interval(T("14:45:00"), T("15:15:00"))]);
        Assert.Equal(1, decision.OverlapMinutes);
        Assert.True(decision.RequiresBoundaryAlignment);
        Assert.True(decision.BlocksWrite);
        Assert.Equal(T("14:45:00"), decision.SuggestedEnd);
    }

    [Fact]
    public void LargeOverlap_Blocks()
    {
        var decision = PayrollWriteOverlapSafety.EvaluateCreateOrAdjust(
            T("08:00:00"),
            T("12:00:00"),
            [new PayrollWriteOverlapSafety.Interval(T("10:00:00"), T("14:00:00"))]);
        Assert.True(decision.OverlapMinutes > 5);
        Assert.True(decision.BlocksWrite);
        Assert.False(decision.RequiresBoundaryAlignment);
    }
}

public sealed class PayrollPrimaryTimingSourceTests
{
    [Fact]
    public void PeerCopiedInterval_IsColleague()
    {
        var source = PayrollPrimaryTimingSourceLabels.Resolve(
            "peer",
            intervalCopiedFromPeer: true,
            intervalFromOwnGps: false,
            intervalFromPlanning: true);
        Assert.Equal(PayrollPrimaryTimingSource.PeerHours, source);
        Assert.Equal("COLLEGA", PayrollPrimaryTimingSourceLabels.Nl(source));
    }

    [Fact]
    public void GpsSite_IsOwnGps()
    {
        var source = PayrollPrimaryTimingSourceLabels.Resolve(
            "gpsSite",
            intervalCopiedFromPeer: false,
            intervalFromOwnGps: true,
            intervalFromPlanning: true);
        Assert.Equal(PayrollPrimaryTimingSource.OwnGps, source);
    }
}
