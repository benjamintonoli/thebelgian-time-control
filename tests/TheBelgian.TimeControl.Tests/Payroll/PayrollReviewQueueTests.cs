using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollReviewQueueTests
{
    [Fact]
    public void CategoryMapping_MapsFindingFamilies()
    {
        Assert.Equal(PayrollReviewCategory.Project300, PayrollReviewCategories.Map(PayrollFindingType.Project300WithoutPlanning));
        Assert.Equal(PayrollReviewCategory.Project200, PayrollReviewCategories.Map(PayrollFindingType.Project200ExceedsPlanning));
        Assert.Equal(PayrollReviewCategory.Project100, PayrollReviewCategories.Map(PayrollFindingType.Project100TrainingHours));
        Assert.Equal(PayrollReviewCategory.Standby, PayrollReviewCategories.Map(PayrollFindingType.StandbyStartMismatch));
        Assert.Equal(PayrollReviewCategory.Overlap, PayrollReviewCategories.Map(PayrollFindingType.OverlappingPerformances));
        Assert.Equal(PayrollReviewCategory.MissingPerformance, PayrollReviewCategories.Map(PayrollFindingType.MissingPlannedTechnicianPerformance));
    }

    [Fact]
    public void Grouping_StandbyStartEndDuration_OneCase()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.StandbyStartMismatch, "StandbyStartMismatch:130:20260830:282625", "130", new DateOnly(2026, 8, 30), 282625),
            Finding(PayrollFindingType.StandbyEndMismatch, "StandbyEndMismatch:130:20260830:282625", "130", new DateOnly(2026, 8, 30), 282625),
            Finding(PayrollFindingType.StandbyDurationMismatch, "StandbyDurationMismatch:130:20260830:282625", "130", new DateOnly(2026, 8, 30), 282625),
        };
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["130"] = new PayrollShadowEmployeeResult { ResourceId = "130", DisplayNameSnapshot = "Rajco Cools" },
        };
        var cases = PayrollReviewCaseBuilder.Build(findings, employees, []);
        var standby = Assert.Single(cases, item => item.Category == PayrollReviewCategory.Standby);
        Assert.Equal(3, standby.FindingKeys.Count);
        Assert.Contains("Start/einde/duur", standby.ProblemLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_ByEmployeeName_AndBon()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.StandbyStartMismatch, "s1", "130", new DateOnly(2026, 8, 30), 1, bon: "26402179"),
            Finding(PayrollFindingType.Project300WithoutPlanning, "p300", "388", new DateOnly(2026, 8, 26), 2, project: "300"),
        };
        findings[1].SuggestedProjectId = "300";
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["130"] = new PayrollShadowEmployeeResult { ResourceId = "130", DisplayNameSnapshot = "Rajco Cools" },
            ["388"] = new PayrollShadowEmployeeResult { ResourceId = "388", DisplayNameSnapshot = "Ayrton Buyle" },
        };
        var all = PayrollReviewCaseBuilder.Build(findings, employees, []);
        var rajco = PayrollReviewCaseBuilder.ApplyFilter(all, new PayrollReviewQueueFilter(Search: "Rajco"));
        Assert.Single(rajco);
        Assert.Equal("130", rajco[0].ResourceId);

        var byBon = PayrollReviewCaseBuilder.ApplyFilter(all, new PayrollReviewQueueFilter(Search: "26402179"));
        Assert.Single(byBon);
        Assert.Equal("130", byBon[0].ResourceId);
    }

    [Fact]
    public void Filter_ByCategory_StandbyAnd300()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.StandbyStartMismatch, "s1", "130", new DateOnly(2026, 8, 30), 1),
            Finding(PayrollFindingType.Project300WithoutPlanning, "p300", "388", new DateOnly(2026, 8, 26), 2),
        };
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["130"] = new() { ResourceId = "130", DisplayNameSnapshot = "Rajco" },
            ["388"] = new() { ResourceId = "388", DisplayNameSnapshot = "Ayrton" },
        };
        var all = PayrollReviewCaseBuilder.Build(findings, employees, []);
        var standby = PayrollReviewCaseBuilder.ApplyFilter(all, new PayrollReviewQueueFilter(Category: PayrollReviewCategory.Standby));
        Assert.All(standby, item => Assert.Equal(PayrollReviewCategory.Standby, item.Category));
        var p300 = PayrollReviewCaseBuilder.ApplyFilter(all, new PayrollReviewQueueFilter(Category: PayrollReviewCategory.Project300));
        Assert.All(p300, item => Assert.Equal(PayrollReviewCategory.Project300, item.Category));
    }

    [Fact]
    public void DefaultOrdering_HighBeforeReview()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.Project300WithoutPlanning, "low", "1", new DateOnly(2026, 8, 10), 1, severity: PayrollFindingSeverity.Review),
            Finding(PayrollFindingType.StandbyStartMismatch, "high", "2", new DateOnly(2026, 8, 1), 2, severity: PayrollFindingSeverity.High),
        };
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["1"] = new() { ResourceId = "1", DisplayNameSnapshot = "Zulu" },
            ["2"] = new() { ResourceId = "2", DisplayNameSnapshot = "Alpha" },
        };
        var cases = PayrollReviewCaseBuilder.Build(findings, employees, []);
        Assert.Equal(PayrollFindingSeverity.High, cases[0].Severity);
    }

    [Fact]
    public void HybridBlockedAction_ShowsHybridLabel()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.StandbyStartMismatch, "StandbyStartMismatch:130:20260830:282625", "130", new DateOnly(2026, 8, 30), 282625),
        };
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["130"] = new() { ResourceId = "130", DisplayNameSnapshot = "Rajco Cools" },
        };
        var action = new PayrollProposedActionRecord
        {
            Id = 1,
            ActionId = Guid.NewGuid(),
            FindingKey = "standby-adjust:130:20260830:282625",
            ResourceId = "130",
            ActionType = PayrollProposedActionType.AdjustExistingPerformanceTime,
            Status = PayrollProposedActionStatus.Blocked,
            BlockReason = "Mogelijke telefonische wachtdienst vóór fysiek vertrek. Eerst bevestigen hoe de interventie opgebouwd is.",
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
        var cases = PayrollReviewCaseBuilder.Build(findings, employees, [action]);
        var item = Assert.Single(cases);
        Assert.Equal(PayrollReviewCaseActionability.Blocked, item.Actionability);
        Assert.Equal("Hybride telefoon + fysieke interventie", item.ProblemLabel);
        Assert.Contains("0:15", item.HybridScenarioNote, StringComparison.Ordinal);
    }

    private static PayrollFindingRecord Finding(
        PayrollFindingType type,
        string key,
        string resourceId,
        DateOnly date,
        long perfId,
        string? bon = null,
        string? project = null,
        PayrollFindingSeverity severity = PayrollFindingSeverity.High) =>
        new()
        {
            Id = Math.Abs(key.GetHashCode()) % 100000 + 1,
            FindingKey = key,
            ResourceId = resourceId,
            Date = date,
            FindingType = type,
            Severity = severity,
            Status = PayrollFindingStatus.Open,
            Title = type.ToString(),
            Description = "d",
            Evidence = "e",
            SuggestedAction = "a",
            RelatedPerformanceIdsJson = JsonSerializer.Serialize(new[] { perfId }),
            SuggestedBonNr = bon,
            SuggestedProjectId = project,
            GpsClassification = nameof(StandbyGpsClassification.PhysicalIntervention),
        };
}
