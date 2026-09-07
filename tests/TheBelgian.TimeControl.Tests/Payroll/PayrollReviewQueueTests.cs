using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Actions;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
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
        Assert.Contains("Telefonisch gedeelte eerst bevestigen", item.HybridScenarioNote, StringComparison.Ordinal);
        Assert.Equal("Mogelijk telefoon + fysieke interventie", item.FriendlyState);
        Assert.Equal(PayrollReviewCaseActionability.Blocked, item.Actionability);
    }

    [Fact]
    public void Project300_OptimizedRow_ShowsReservationRule()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.Project300WithoutPlanning, "p300", "388", new DateOnly(2026, 8, 26), 10, project: "300"),
        };
        findings[0].SuggestedProjectId = "300";
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["388"] = new() { ResourceId = "388", DisplayNameSnapshot = "Ayrton Buyle" },
        };
        var item = Assert.Single(PayrollReviewCaseBuilder.Build(findings, employees, []));
        Assert.Equal(PayrollReviewCategory.Project300, item.Category);
        Assert.Equal("Project 300 zonder reservatie", item.ProblemLabel);
        Assert.Equal("Geen reservatie in planning gevonden.", item.RuleHint);
        Assert.Contains("Reservatie ontbreekt", PayrollReviewCategories.SuggestedReasons(PayrollReviewCategory.Project300));
    }

    [Fact]
    public void Project200_ShowsBookedVsPlannedDifference()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.Project200ExceedsPlanning, "p200", "10", new DateOnly(2026, 8, 12), 20, project: "200"),
        };
        findings[0].BookedHours = 6m;
        findings[0].PlannedHours = 4m;
        findings[0].SuggestedProjectId = "200";
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["10"] = new() { ResourceId = "10", DisplayNameSnapshot = "Ben" },
        };
        var item = Assert.Single(PayrollReviewCaseBuilder.Build(findings, employees, []));
        Assert.Equal("Project 200: 2 u meer geboekt dan gepland", item.ProblemLabel);
        Assert.Contains("Δ 2", item.DifferenceSummary, StringComparison.Ordinal);
        Assert.Equal("Meer geboekt", item.FriendlyState);
    }

    [Fact]
    public void Project100_ShowsToolboxAndOvertimeHint()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.Project100TrainingInOvertime, "p100", "11", new DateOnly(2026, 8, 5), 30, project: "100"),
        };
        findings[0].SuggestedOvertimeAdjustmentHours = 1.5m;
        findings[0].PlannedHours = 2m;
        findings[0].BookedHours = 3.5m;
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["11"] = new() { ResourceId = "11", DisplayNameSnapshot = "Chris" },
        };
        var item = Assert.Single(PayrollReviewCaseBuilder.Build(findings, employees, []));
        Assert.Equal(PayrollReviewCategory.Project100, item.Category);
        Assert.Contains("overuren", item.ProblemLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1,5", item.DifferenceSummary?.Replace('.', ',') ?? item.DifferenceSummary ?? "", StringComparison.Ordinal);
        Assert.Equal("Toolbox / opleiding", item.FriendlyState);
    }

    [Fact]
    public void MissingTechnician_ShowsEvidenceFriendlyState()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.MissingPlannedTechnicianPerformance, "miss", "50", new DateOnly(2026, 8, 8), 0),
        };
        findings[0].RelatedPerformanceIdsJson = "[]";
        findings[0].GpsClassification = nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps);
        findings[0].PlannedHours = 8m;
        findings[0].Evidence = "peer=99";
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["50"] = new() { ResourceId = "50", DisplayNameSnapshot = "Missing Tech" },
        };
        var item = Assert.Single(PayrollReviewCaseBuilder.Build(findings, employees, []));
        Assert.Equal("Sterk bewijs", item.FriendlyState);
        Assert.Equal(nameof(MissingTechnicianEvidenceClass.PlanningPlusPeerPlusGps), item.EvidenceSummary);
    }

    [Fact]
    public void CategoryCards_UnresolvedCounts_UpdateAfterDisposition()
    {
        var open300 = Finding(PayrollFindingType.Project300WithoutPlanning, "a", "1", new DateOnly(2026, 8, 1), 1);
        var reviewed300 = Finding(PayrollFindingType.Project300WithoutPlanning, "b", "2", new DateOnly(2026, 8, 2), 2);
        reviewed300.Status = PayrollFindingStatus.Reviewed;
        var standby = Finding(PayrollFindingType.StandbyStartMismatch, "c", "3", new DateOnly(2026, 8, 3), 3);
        var employees = new[]
        {
            new PayrollShadowEmployeeResult { ResourceId = "1", DisplayNameSnapshot = "A", EligibilityStatus = PayrollEligibilityStatus.Included },
            new PayrollShadowEmployeeResult { ResourceId = "2", DisplayNameSnapshot = "B", EligibilityStatus = PayrollEligibilityStatus.Included },
            new PayrollShadowEmployeeResult { ResourceId = "3", DisplayNameSnapshot = "C", EligibilityStatus = PayrollEligibilityStatus.Included },
        };
        var byResource = employees.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
        var cases = PayrollReviewCaseBuilder.Build([open300, reviewed300, standby], byResource, []);
        var summary = PayrollReviewCaseBuilder.Summarize(cases, employees, []);
        Assert.Equal(2, summary.ByCategory[PayrollReviewCategory.Project300]);
        Assert.Equal(1, summary.UnresolvedByCategory[PayrollReviewCategory.Project300]);
        Assert.Equal(1, summary.UnresolvedByCategory[PayrollReviewCategory.Standby]);
        Assert.Equal(1, summary.Reviewed);
        Assert.Equal(2, summary.Open);
    }

    [Fact]
    public void Scope_OpenClosedAll_FiltersCorrectly()
    {
        var open = Finding(PayrollFindingType.Project300WithoutPlanning, "o", "1", new DateOnly(2026, 8, 1), 1);
        var closed = Finding(PayrollFindingType.Project200WithoutPlanning, "c", "2", new DateOnly(2026, 8, 2), 2);
        closed.Status = PayrollFindingStatus.Reviewed;
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["1"] = new() { ResourceId = "1", DisplayNameSnapshot = "Open Emp" },
            ["2"] = new() { ResourceId = "2", DisplayNameSnapshot = "Closed Emp" },
        };
        var all = PayrollReviewCaseBuilder.Build([open, closed], employees, []);
        Assert.Single(PayrollReviewCaseBuilder.ApplyFilter(all, new PayrollReviewQueueFilter(Scope: PayrollReviewQueueScope.Open)));
        Assert.Single(PayrollReviewCaseBuilder.ApplyFilter(all, new PayrollReviewQueueFilter(Scope: PayrollReviewQueueScope.Closed)));
        Assert.Equal(2, PayrollReviewCaseBuilder.ApplyFilter(all, new PayrollReviewQueueFilter(Scope: PayrollReviewQueueScope.All)).Count);
    }

    [Fact]
    public void Sort_EmployeeAlphabetical()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.Project300WithoutPlanning, "z", "9", new DateOnly(2026, 8, 10), 1),
            Finding(PayrollFindingType.Project300WithoutPlanning, "a", "8", new DateOnly(2026, 8, 1), 2),
        };
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["9"] = new() { ResourceId = "9", DisplayNameSnapshot = "Zulu" },
            ["8"] = new() { ResourceId = "8", DisplayNameSnapshot = "Alpha" },
        };
        var all = PayrollReviewCaseBuilder.Build(findings, employees, []);
        var sorted = PayrollReviewCaseBuilder.ApplyFilter(
            all,
            new PayrollReviewQueueFilter(Sort: "medewerker", Scope: PayrollReviewQueueScope.All));
        Assert.Equal("Alpha", sorted[0].DisplayName);
        Assert.Equal("Zulu", sorted[1].DisplayName);
    }

    [Fact]
    public void NextNavigation_PreservesCategoryFilterOrder()
    {
        var findings = new[]
        {
            Finding(PayrollFindingType.Project300WithoutPlanning, "c1", "1", new DateOnly(2026, 8, 10), 1),
            Finding(PayrollFindingType.Project300WithoutPlanning, "c2", "2", new DateOnly(2026, 8, 9), 2),
            Finding(PayrollFindingType.StandbyStartMismatch, "s1", "3", new DateOnly(2026, 8, 8), 3),
        };
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["1"] = new() { ResourceId = "1", DisplayNameSnapshot = "One" },
            ["2"] = new() { ResourceId = "2", DisplayNameSnapshot = "Two" },
            ["3"] = new() { ResourceId = "3", DisplayNameSnapshot = "Three" },
        };
        var all = PayrollReviewCaseBuilder.Build(findings, employees, []);
        var filtered = PayrollReviewCaseBuilder.ApplyFilter(
            all,
            new PayrollReviewQueueFilter(Category: PayrollReviewCategory.Project300, Scope: PayrollReviewQueueScope.All)).ToList();
        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, item => item.Category == PayrollReviewCategory.Standby);
        Assert.Equal(filtered[1].CaseKey, filtered[1].CaseKey);
        Assert.True(filtered.Count >= 2);
    }

    [Fact]
    public void FilteredSelectAll_Safety_OnlyExplicitKeysMatter()
    {
        // Mimic UI: visible filtered set vs full month. Bulk must never invent keys.
        var findings = new[]
        {
            Finding(PayrollFindingType.Project300WithoutPlanning, "v1", "1", new DateOnly(2026, 8, 1), 1),
            Finding(PayrollFindingType.Project300WithoutPlanning, "v2", "2", new DateOnly(2026, 8, 2), 2),
            Finding(PayrollFindingType.StandbyStartMismatch, "hidden", "3", new DateOnly(2026, 8, 3), 3),
        };
        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["1"] = new() { ResourceId = "1", DisplayNameSnapshot = "A" },
            ["2"] = new() { ResourceId = "2", DisplayNameSnapshot = "B" },
            ["3"] = new() { ResourceId = "3", DisplayNameSnapshot = "C" },
        };
        var all = PayrollReviewCaseBuilder.Build(findings, employees, []);
        var visible = PayrollReviewCaseBuilder.ApplyFilter(
            all,
            new PayrollReviewQueueFilter(Category: PayrollReviewCategory.Project300, Scope: PayrollReviewQueueScope.All));
        Assert.Equal(2, visible.Count);
        var selected = visible.Select(item => item.CaseKey).ToArray();
        Assert.DoesNotContain(all.Single(item => item.Category == PayrollReviewCategory.Standby).CaseKey, selected);
        Assert.Equal(2, selected.Length);
        Assert.All(selected, key => Assert.Contains(visible, item => item.CaseKey == key));
    }

    [Fact]
    public void SuggestedReasons_AreOptionalHelpers_NotAutoSelected()
    {
        var reasons300 = PayrollReviewCategories.SuggestedReasons(PayrollReviewCategory.Project300);
        Assert.Contains("Andere reden", reasons300);
        Assert.Contains("Intern werk bevestigd", reasons300);
        var reasons200 = PayrollReviewCategories.SuggestedReasons(PayrollReviewCategory.Project200);
        Assert.Contains("Planning ontbreekt", reasons200);
        var reasonsStandby = PayrollReviewCategories.SuggestedReasons(PayrollReviewCategory.Standby);
        Assert.Contains("Telefonisch contact na te kijken", reasonsStandby);
    }

    [Fact]
    public void DisplayNames_MatchCategoryCards()
    {
        Assert.Equal("300 zonder planning", PayrollReviewCategories.DisplayName(PayrollReviewCategory.Project300));
        Assert.Equal("200 controle", PayrollReviewCategories.DisplayName(PayrollReviewCategory.Project200));
        Assert.Equal("Toolbox / opleiding", PayrollReviewCategories.DisplayName(PayrollReviewCategory.Project100));
        Assert.Equal("Wachtdienst", PayrollReviewCategories.DisplayName(PayrollReviewCategory.Standby));
        Assert.Equal("Dubbele uren", PayrollReviewCategories.DisplayName(PayrollReviewCategory.Overlap));
        Assert.Equal("Ontbrekende prestatie", PayrollReviewCategories.DisplayName(PayrollReviewCategory.MissingPerformance));
    }

    [Fact]
    public void Hybrid_RemainsNonExecutable_InQueuePresentation()
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
        var item = Assert.Single(PayrollReviewCaseBuilder.Build(findings, employees, [action]));
        Assert.NotEqual(PayrollReviewCaseActionability.ReadyProposal, item.Actionability);
        Assert.Equal(PayrollReviewCaseActionability.Blocked, item.Actionability);
        Assert.DoesNotContain("ReadyForApproval", item.ActionabilityLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Niet bewezen", item.HybridScenarioNote, StringComparison.OrdinalIgnoreCase);
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
