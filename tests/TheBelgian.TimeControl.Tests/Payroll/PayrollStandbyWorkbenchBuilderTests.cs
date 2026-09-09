using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollStandbyWorkbenchBuilderTests
{
    [Fact]
    public void GuidedStandbyChoices_IncludeHoursWrong_AndBookingCorrect()
    {
        var codes = PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Standby)
            .Select(item => item.DecisionCode)
            .ToArray();
        Assert.Contains(PayrollGuidedDecisionCodes.StandbyBookingCorrect, codes);
        Assert.Contains(PayrollGuidedDecisionCodes.StandbyHoursWrong, codes);
        Assert.Contains(PayrollGuidedDecisionCodes.StandbyPhoneOnly, codes);
        Assert.Contains(PayrollGuidedDecisionCodes.StandbyPhysicalOnly, codes);
        Assert.Contains(PayrollGuidedDecisionCodes.StandbyPhoneThenPhysical, codes);
        Assert.Contains(PayrollGuidedDecisionCodes.StandbyWrongDossier, codes);
    }

    [Fact]
    public void CreateGatedMessage_IsExplicit_ForHybridCreate()
    {
        Assert.Contains("tweede prestatie", PayrollStandbyCaseDetail.CreateGatedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("niet volledig", PayrollStandbyCaseDetail.CreateGatedMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HybridProposal_ExcludesUnpaidGap()
    {
        var phone = new PayrollStandbyPhoneProposal(
            new TimeOnly(17, 30),
            new TimeOnly(17, 45),
            0.25m,
            "Telefoon max 15 min");
        var physical = new PayrollStandbyPhysicalProposal(
            new TimeOnly(18, 17),
            new TimeOnly(20, 7),
            1.833m,
            "Vertrek–terug",
            IsCompleteCallout: true);
        var gap = (int)(physical.DepartureStart - phone.ProposedEnd).TotalMinutes;
        var hybrid = new PayrollStandbyHybridProposal(
            phone,
            physical,
            gap,
            $"Niet automatisch betaald: {phone.ProposedEnd:HH:mm}–{physical.DepartureStart:HH:mm}",
            RequiresCreateSplit: true,
            CreateGatedMessage: PayrollStandbyCaseDetail.CreateGatedMessage);

        Assert.Equal(32, hybrid.UnpaidGapMinutes);
        Assert.True(hybrid.RequiresCreateSplit);
        Assert.NotNull(hybrid.CreateGatedMessage);
        Assert.Equal(15, (int)(hybrid.PhoneSegment.ProposedEnd - hybrid.PhoneSegment.ProposedStart).TotalMinutes);
    }

    [Fact]
    public void PhoneProposal_CapsAtFifteenMinutes()
    {
        var start = new TimeOnly(17, 30);
        var end = start.AddMinutes(15);
        var proposal = new PayrollStandbyPhoneProposal(start, end, 0.25m, "max 15");
        Assert.Equal(15, (int)(proposal.ProposedEnd - proposal.ProposedStart).TotalMinutes);
        Assert.True(proposal.PayableHours <= 0.25m);
    }
}
