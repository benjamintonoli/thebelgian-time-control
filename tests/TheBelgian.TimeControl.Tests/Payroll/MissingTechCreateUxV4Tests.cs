using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class MissingTechCreateUxV4Tests
{
    [Fact]
    public void GuidedChoices_StillContainCreateConfirmed_ButUiHidesDuplicate()
    {
        var choices = PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.MissingPerformance);
        Assert.Contains(choices, item => item.DecisionCode == PayrollGuidedDecisionCodes.MissingTechConfirmed);
        Assert.Contains(choices, item => !string.Equals(item.DecisionCode, PayrollGuidedDecisionCodes.MissingTechConfirmed, StringComparison.Ordinal));
    }

    [Fact]
    public void CreateSemantics_SurvivesWorkflowOnlyMetadata()
    {
        var a = new PayrollActionCreateProposal(
            "661",
            new DateOnly(2026, 8, 14),
            new DateTimeOffset(2026, 8, 14, 7, 55, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 8, 14, 14, 46, 0, TimeSpan.FromHours(2)),
            6.85m,
            "40167",
            "26501760",
            9,
            PayrollIntervalSemantics.PayableWork);
        var b = a with
        {
            Start = new DateTimeOffset(2026, 8, 14, 7, 55, 11, TimeSpan.FromHours(2)),
            End = new DateTimeOffset(2026, 8, 14, 14, 46, 4, TimeSpan.FromHours(2)),
        };
        Assert.True(PayrollCreateProposalSemantics.AreMateriallyEquivalent(a, b));
    }

    [Fact]
    public void EvidencePeerRegex_SupportsFirstPaintWithoutPlenionPeerRows()
    {
        var finding = new PayrollFindingRecord
        {
            FindingKey = "missing-tech:154651:20260814:661",
            ResourceId = "661",
            Date = new DateOnly(2026, 8, 14),
            FindingType = PayrollFindingType.MissingPlannedTechnicianPerformance,
            Severity = PayrollFindingSeverity.High,
            Title = "t",
            Description = "d",
            Evidence = "class=PlanningPlusPeerPlusGps; intervalSource=gpsSite; travelMode=SeparateVehicleProven; peers=[401#281607:08:10-14:45]; proposedInterval=07:55-14:46; suggestedHfdTaakId=9",
            SuggestedAction = "a",
            GpsClassification = nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps),
            SuggestedPayableStart = new DateTimeOffset(2026, 8, 14, 7, 55, 0, TimeSpan.FromHours(2)),
            SuggestedPayableEnd = new DateTimeOffset(2026, 8, 14, 14, 46, 0, TimeSpan.FromHours(2)),
            SuggestedPayableHours = 6.85m,
            SuggestedProjectId = "40167",
            SuggestedBonNr = "26501760",
        };
        var admin = new PayrollAdminCase(
            "missing:missing-tech:154651:20260814:661",
            PayrollReviewCategory.MissingPerformance,
            "661",
            "Lorenzo Bashi",
            new DateOnly(2026, 8, 14),
            PayrollFindingSeverity.High,
            PayrollFindingStatus.Open,
            "q",
            "issue",
            null,
            "26501760",
            "40167",
            null,
            null,
            null,
            null,
            null,
            "Sterk bewijs",
            null,
            "hint",
            1,
            0,
            null,
            [],
            [1],
            [finding.FindingKey],
            null,
            null,
            null,
            null,
            null,
            false,
            PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.MissingPerformance),
            []);

        var detail = PayrollIntelligenceWorkbenchBuilder.BuildMissingDetail(
            admin,
            finding,
            peerRows: [],
            gps: null,
            gpsPending: true,
            peerDisplayName: "Daniel Cano Zapata");

        Assert.NotNull(detail.Missing);
        Assert.Equal(new TimeOnly(8, 10), detail.Missing!.PeerStart);
        Assert.Equal(new TimeOnly(14, 45), detail.Missing.PeerEnd);
        Assert.Equal("401", detail.Missing.PeerResourceId);
        Assert.Equal("Daniel Cano Zapata", detail.Missing.PeerDisplayName);
        Assert.Contains("Track", detail.Missing.Explanation!.TimingSourcePrimary, StringComparison.OrdinalIgnoreCase);
    }
}
