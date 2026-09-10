using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using Xunit;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollDailyPauseThresholdV61Tests
{
    [Theory]
    [InlineData(3, 59, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(4, 1, 30)]
    [InlineData(8, 0, 30)]
    public void Threshold_ExactMinutes(int hours, int minutes, int expectedPauseMinutes)
    {
        var plan = PayrollDailyPauseRules.PlanForCreateOrAdjust(
            [],
            TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes));
        Assert.Equal(TimeSpan.FromMinutes(expectedPauseMinutes), plan.RequiredMinimumPause);
        Assert.Equal(TimeSpan.FromMinutes(expectedPauseMinutes), plan.PauseToAssignOnTargetRow);
    }

    [Fact]
    public void ExactlyFourHours_UsesStrictGreaterThan_NotGreaterOrEqual()
    {
        Assert.True(TimeSpan.FromHours(4) > PayrollDailyPauseRules.FourHourThreshold == false);
        Assert.True((TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1)) > PayrollDailyPauseRules.FourHourThreshold);
        var atFour = PayrollDailyPauseRules.PlanForCreateOrAdjust([], TimeSpan.FromHours(4));
        Assert.Equal(TimeSpan.Zero, atFour.RequiredMinimumPause);
        Assert.DoesNotContain("langer dan 4 uur", atFour.ExplanationNl, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class PayrollDailyWriteRevalidationTests
{
    private static TimeSpan T(string value) =>
        TimeSpan.Parse(value, CultureInfo.InvariantCulture);

    private static PayrollDailyWriteRevalidation.SameDayRow Row(
        long id, string start, string end, int pauseMinutes = 0) =>
        new(id, T(start), T(end), TimeSpan.FromMinutes(pauseMinutes));

    [Fact]
    public void A_OtherRowGainsPause_StalesProposalThatStillAddsThirty()
    {
        var result = PayrollDailyWriteRevalidation.ValidateCreate(
            [Row(1, "08:00:00", "10:00:00", pauseMinutes: 30)],
            new PayrollDailyWriteRevalidation.CreateCheck(
                T("10:00:00"),
                T("15:00:00"),
                TimeSpan.FromMinutes(30)));
        Assert.False(result.IsSafe);
        Assert.Contains("Dagpauze gewijzigd", result.StaleReasonNl, StringComparison.Ordinal);
    }

    [Fact]
    public void B_NewOverlappingRow_StalesBoundaryProposal()
    {
        var result = PayrollDailyWriteRevalidation.ValidateAdjust(
            [
                Row(283414, "07:55:00", "14:46:00"),
                Row(999, "14:40:00", "15:00:00")
            ],
            new PayrollDailyWriteRevalidation.AdjustCheck(
                283414,
                T("07:55:00"),
                T("14:45:00"),
                TimeSpan.FromMinutes(30),
                T("07:55:00"),
                T("14:46:00"),
                TimeSpan.Zero));
        Assert.False(result.IsSafe);
        Assert.Contains("Overlap", result.StaleReasonNl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void C_DayShrinksToFourHoursOrLess_StalesThirtyMinutePauseProposal()
    {
        // Only the create itself would be 3:59 — stored pause 30 is no longer required.
        var result = PayrollDailyWriteRevalidation.ValidateCreate(
            [],
            new PayrollDailyWriteRevalidation.CreateCheck(
                T("08:00:00"),
                T("11:59:00"),
                TimeSpan.FromMinutes(30)));
        Assert.False(result.IsSafe);
        Assert.Contains("Dagpauze gewijzigd", result.StaleReasonNl, StringComparison.Ordinal);
    }

    [Fact]
    public void D_DayGrowsAboveFourHours_StalesZeroPauseProposal()
    {
        var result = PayrollDailyWriteRevalidation.ValidateCreate(
            [Row(1, "08:00:00", "11:00:00")],
            new PayrollDailyWriteRevalidation.CreateCheck(
                T("11:00:00"),
                T("14:00:00"),
                TimeSpan.Zero));
        Assert.False(result.IsSafe);
        Assert.Contains("Dagpauze gewijzigd", result.StaleReasonNl, StringComparison.Ordinal);
    }

    [Fact]
    public void E_UnchangedBashiProposal_IsSafe()
    {
        var result = PayrollDailyWriteRevalidation.ValidateAdjust(
            [
                Row(283414, "07:55:00", "14:46:00"),
                Row(281603, "14:45:00", "15:15:00"),
                Row(281602, "15:15:00", "15:50:00")
            ],
            new PayrollDailyWriteRevalidation.AdjustCheck(
                283414,
                T("07:55:00"),
                T("14:45:00"),
                TimeSpan.FromMinutes(30),
                T("07:55:00"),
                T("14:46:00"),
                TimeSpan.Zero));
        Assert.True(result.IsSafe);
        Assert.Null(result.StaleReasonNl);
    }

    [Fact]
    public void Create_TouchingBoundary_IsSafeWithRequiredPause()
    {
        var result = PayrollDailyWriteRevalidation.ValidateCreate(
            [Row(281603, "14:45:00", "15:15:00")],
            new PayrollDailyWriteRevalidation.CreateCheck(
                T("07:55:00"),
                T("14:45:00"),
                TimeSpan.FromMinutes(30)));
        Assert.True(result.IsSafe);
    }
}
