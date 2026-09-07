using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollGuidedReviewTests
{
    [Fact]
    public void GroupsSameEmployeeDateCategoryContext_ForProject300()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.Project300WithoutPlanning, "a", "10", new DateOnly(2026, 8, 28), 1, booked: 1m),
            Finding(PayrollFindingType.Project300WithoutPlanning, "b", "10", new DateOnly(2026, 8, 28), 2, booked: 0.66m),
            Finding(PayrollFindingType.Project300WithoutPlanning, "c", "10", new DateOnly(2026, 8, 28), 3, booked: 1m),
        };
        var employees = Emp("10", "Jarno");
        var review = PayrollReviewCaseBuilder.Build(findings, employees, []);
        Assert.Equal(3, review.Count);
        var admin = PayrollAdminCaseBuilder.Build(review);
        var jarno = Assert.Single(admin);
        Assert.Equal(3, jarno.UnderlyingPerformanceCount);
        Assert.Equal(PayrollGuidedDecisionCodes.P300WorkValid, jarno.Choices[0].DecisionCode);
        Assert.Contains("zonder reservatie", jarno.IssueSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DoesNotGroupDifferentProjectContexts()
    {
        var a = Finding(PayrollFindingType.Project200WithoutPlanning, "a", "10", new DateOnly(2026, 8, 28), 1, booked: 1m, project: "200A");
        var b = Finding(PayrollFindingType.Project200WithoutPlanning, "b", "10", new DateOnly(2026, 8, 28), 2, booked: 1m, project: "200B");
        var review = PayrollReviewCaseBuilder.Build([a, b], Emp("10", "Jarno"), []);
        var admin = PayrollAdminCaseBuilder.Build(review);
        Assert.Equal(2, admin.Count);
    }

    [Fact]
    public void StandbyRemainsOneAdminCase_NotSplitStartDuration()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.StandbyStartMismatch, "StandbyStartMismatch:130:20260830:282625", "130", new DateOnly(2026, 8, 30), 282625),
            Finding(PayrollFindingType.StandbyDurationMismatch, "StandbyDurationMismatch:130:20260830:282625", "130", new DateOnly(2026, 8, 30), 282625),
        };
        var review = PayrollReviewCaseBuilder.Build(findings, Emp("130", "Rajco Cools"), []);
        var admin = Assert.Single(PayrollAdminCaseBuilder.Build(review));
        Assert.Equal("Hoe verliep deze wachtdienst?", admin.BusinessQuestion);
        Assert.False(admin.AllowsBulkDisposition);
    }

    [Fact]
    public void HybridStandby_NoExecutableWrite_BusinessLanguage()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.StandbyStartMismatch, "StandbyStartMismatch:130:20260830:282625", "130", new DateOnly(2026, 8, 30), 282625),
        };
        var action = new PayrollProposedActionRecord
        {
            Id = 1,
            ActionId = Guid.NewGuid(),
            FindingKey = "standby-adjust:130:20260830:282625",
            ResourceId = "130",
            ActionType = PayrollProposedActionType.AdjustExistingPerformanceTime,
            Status = PayrollProposedActionStatus.Blocked,
            BlockReason = "Mogelijke telefonische wachtdienst vóór fysiek vertrek.",
            ProposalSnapshotJson = JsonSerializer.Serialize(new PayrollActionAdjustProposal(
                282625,
                new DateTimeOffset(2026, 8, 30, 15, 25, 0, TimeSpan.FromHours(2)),
                new DateTimeOffset(2026, 8, 30, 18, 33, 0, TimeSpan.FromHours(2)),
                new DateTimeOffset(2026, 8, 30, 16, 18, 0, TimeSpan.FromHours(2)),
                new DateTimeOffset(2026, 8, 30, 18, 33, 0, TimeSpan.FromHours(2)),
                "WaitingTime",
                23)),
            EvidenceSnapshotJson = "{}",
        };
        var review = Assert.Single(PayrollReviewCaseBuilder.Build(findings, Emp("130", "Rajco"), [action]));
        Assert.Equal(PayrollReviewCaseActionability.Blocked, review.Actionability);
        Assert.DoesNotContain("Geblokkeerd", review.ActionabilityLabel, StringComparison.Ordinal);
        var admin = Assert.Single(PayrollAdminCaseBuilder.Build([review]));
        Assert.Contains("verdere controle", admin.ActionabilityHint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Hoe verliep deze wachtdienst?", admin.BusinessQuestion);
    }

    [Fact]
    public void Project300_DecisionMapsToTerminalOrFollowUp()
    {
        var valid = PayrollGuidedDecisions.Resolve(PayrollGuidedDecisionCodes.P300WorkValid, PayrollReviewCategory.Project300);
        Assert.Equal(PayrollFindingStatus.Reviewed, valid.ResultStatus);
        var planning = PayrollGuidedDecisions.Resolve(PayrollGuidedDecisionCodes.P300PlanningMissing, PayrollReviewCategory.Project300);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, planning.ResultStatus);
        var hours = PayrollGuidedDecisions.Resolve(PayrollGuidedDecisionCodes.P300HoursWrong, PayrollReviewCategory.Project300);
        Assert.True(hours.RequiresComment);
    }

    [Fact]
    public void Project200_AndToolbox_ChoicesExist()
    {
        Assert.Contains(PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project200), item => item.DecisionCode == PayrollGuidedDecisionCodes.P200Valid);
        Assert.Contains(PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project100), item => item.DecisionCode == PayrollGuidedDecisionCodes.P100DurationWrong);
    }

    [Fact]
    public void MissingTech_ConfirmedIsFollowUp_PlanningWrongIsTerminal()
    {
        var yes = PayrollGuidedDecisions.Resolve(PayrollGuidedDecisionCodes.MissingTechConfirmed, PayrollReviewCategory.MissingPerformance);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, yes.ResultStatus);
        var no = PayrollGuidedDecisions.Resolve(PayrollGuidedDecisionCodes.MissingTechPlanningWrong, PayrollReviewCategory.MissingPerformance);
        Assert.Equal(PayrollFindingStatus.Reviewed, no.ResultStatus);
    }

    [Fact]
    public void HighMissing_BulkDisabled()
    {
        var finding = Finding(PayrollFindingType.MissingPlannedTechnicianPerformance, "miss", "50", new DateOnly(2026, 8, 31), 0);
        finding.RelatedPerformanceIdsJson = "[]";
        finding.GpsClassification = nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps);
        var review = PayrollReviewCaseBuilder.Build([finding], Emp("50", "Ayrton"), []);
        var admin = Assert.Single(PayrollAdminCaseBuilder.Build(review));
        Assert.Equal("Sterk bewijs", admin.FriendlyState);
        Assert.False(admin.AllowsBulkDisposition);
        Assert.Equal("Heeft deze technieker hier effectief gewerkt?", admin.BusinessQuestion);
    }

    [Fact]
    public void Search_FindsGroupedAdminCase_ByNameAndBon()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.StandbyStartMismatch, "s1", "130", new DateOnly(2026, 8, 30), 1, bon: "26402179"),
            Finding(PayrollFindingType.Project300WithoutPlanning, "p", "388", new DateOnly(2026, 8, 26), 2),
        };
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["130"] = new() { ResourceId = "130", DisplayNameSnapshot = "Rajco Cools" },
            ["388"] = new() { ResourceId = "388", DisplayNameSnapshot = "Ayrton" },
        };
        var admin = PayrollAdminCaseBuilder.Build(PayrollReviewCaseBuilder.Build(findings, employees, []));
        Assert.Single(PayrollAdminCaseBuilder.ApplyFilter(admin, new PayrollReviewQueueFilter(Search: "Rajco", Scope: PayrollReviewQueueScope.All)));
        Assert.Single(PayrollAdminCaseBuilder.ApplyFilter(admin, new PayrollReviewQueueFilter(Search: "26402179", Scope: PayrollReviewQueueScope.All)));
    }

    [Fact]
    public void TerminalDecision_UnblocksAdminCase_FollowUpKeepsBlocking()
    {
        var open = Finding(PayrollFindingType.Project300WithoutPlanning, "o", "1", new DateOnly(2026, 8, 1), 1);
        var reviewed = Finding(PayrollFindingType.Project300WithoutPlanning, "r", "2", new DateOnly(2026, 8, 2), 2);
        reviewed.Status = PayrollFindingStatus.Reviewed;
        reviewed.DecisionCode = PayrollGuidedDecisionCodes.P300WorkValid;
        var follow = Finding(PayrollFindingType.Project300WithoutPlanning, "f", "3", new DateOnly(2026, 8, 3), 3);
        follow.Status = PayrollFindingStatus.NeedsFollowUp;
        follow.DecisionCode = PayrollGuidedDecisionCodes.P300PlanningMissing;
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["1"] = new() { ResourceId = "1", DisplayNameSnapshot = "A" },
            ["2"] = new() { ResourceId = "2", DisplayNameSnapshot = "B" },
            ["3"] = new() { ResourceId = "3", DisplayNameSnapshot = "C" },
        };
        var review = PayrollReviewCaseBuilder.Build([open, reviewed, follow], employees, []);
        var admin = PayrollAdminCaseBuilder.Build(review);
        var summary = PayrollAdminCaseBuilder.Summarize(review, admin);
        Assert.Equal(1, summary.Open);
        Assert.Equal(1, summary.NeedsFollowUp);
        Assert.Equal(1, summary.Completed);
        Assert.True(PayrollReviewCategories.IsUnresolved(admin.Single(item => item.ResourceId == "3").WorkflowStatus));
        Assert.True(PayrollReviewCategories.IsClosed(admin.Single(item => item.ResourceId == "2").WorkflowStatus));
    }

    [Fact]
    public void AdminStatusLabels_AreBusinessLanguage()
    {
        Assert.Equal("Te beoordelen", PayrollGuidedDecisions.AdminStatusLabel(PayrollFindingStatus.Open));
        Assert.Equal("Afgehandeld", PayrollGuidedDecisions.AdminStatusLabel(PayrollFindingStatus.Reviewed));
        Assert.Equal("Niet van toepassing", PayrollGuidedDecisions.AdminStatusLabel(PayrollFindingStatus.Dismissed));
    }

    private static Dictionary<string, PayrollShadowEmployeeResult> Emp(string id, string name) =>
        new(StringComparer.Ordinal)
        {
            [id] = new() { ResourceId = id, DisplayNameSnapshot = name },
        };

    private static PayrollFindingRecord Finding(
        PayrollFindingType type,
        string key,
        string resourceId,
        DateOnly date,
        long perfId,
        string? bon = null,
        string? project = null,
        decimal? booked = null) =>
        new()
        {
            Id = Math.Abs(key.GetHashCode()) % 100000 + 1,
            FindingKey = key,
            ResourceId = resourceId,
            Date = date,
            FindingType = type,
            Severity = PayrollFindingSeverity.High,
            Status = PayrollFindingStatus.Open,
            Title = type.ToString(),
            Description = "d",
            Evidence = "e",
            SuggestedAction = "a",
            RelatedPerformanceIdsJson = JsonSerializer.Serialize(new[] { perfId }),
            SuggestedBonNr = bon,
            SuggestedProjectId = project,
            BookedHours = booked,
            GpsClassification = nameof(StandbyGpsClassification.PhysicalIntervention),
        };
}
