using System.Text.Json;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollAdminTriageV2Tests
{
    [Fact]
    public void Project300_ShowsTimeIntervalAndDescriptionFromFindingText()
    {
        var finding = Finding(
            key: "p300",
            perfId: 101,
            booked: 1.5m,
            description: "Geboekt 08:00-09:30 (1,50 u) zonder ondersteunende planning.",
            evidence: "PerformanceId=101; PROJNR=300; desc=Interne klaarzet; memo=magazijn; planning evidence = none.");

        var review = Assert.Single(PayrollReviewCaseBuilder.Build([finding], Emp("10", "Jarno"), []));
        Assert.Equal("08:00–09:30", review.TimeInterval);
        Assert.Equal("Interne klaarzet", review.PerformanceDescription);
        Assert.Equal("magazijn", review.PerformanceMemo);
        Assert.True(review.DescriptionPresent);
        Assert.False(review.PlanningPresent);
        Assert.Contains("08:00–09:30", review.BookedSummary, StringComparison.Ordinal);

        var admin = Assert.Single(PayrollAdminCaseBuilder.Build([review]));
        Assert.Equal("08:00–09:30", admin.TimeIntervalSummary);
        Assert.Equal("Interne klaarzet", admin.PerformanceDescriptionSummary);
        Assert.Contains(admin.EvidenceChips!, chip => chip.Contains("Omschrijving aanwezig", StringComparison.Ordinal));
        Assert.Contains(admin.EvidenceChips!, chip => chip.Contains("Planning ontbreekt", StringComparison.Ordinal));
        Assert.Equal(1.5m, admin.TotalBookedHours);
    }

    [Fact]
    public void MultiPerformance_ExpandRows_NoDuplicateAdminCases()
    {
        var findings = new[]
        {
            Finding("a", 1, 1m, "Geboekt 08:00-09:00 (1,00 u) zonder ondersteunende planning.",
                "PerformanceId=1; PROJNR=300; desc=A; memo=—; planning evidence = none."),
            Finding("b", 2, 0.66m, "Geboekt 10:00-10:40 (0,66 u) zonder ondersteunende planning.",
                "PerformanceId=2; PROJNR=300; desc=B; memo=—; planning evidence = none."),
            Finding("c", 3, 1m, "Geboekt 13:00-14:00 (1,00 u) zonder ondersteunende planning.",
                "PerformanceId=3; PROJNR=300; desc=C; memo=—; planning evidence = none."),
        };

        var review = PayrollReviewCaseBuilder.Build(findings, Emp("10", "Jarno"), []);
        Assert.Equal(3, review.Count);
        var admin = Assert.Single(PayrollAdminCaseBuilder.Build(review));
        Assert.Equal(3, admin.Performances.Count);
        Assert.Equal(3, admin.UnderlyingPerformanceCount);
        Assert.Contains("08:00–09:00", admin.TimeIntervalSummary, StringComparison.Ordinal);
        Assert.Contains("10:00–10:40", admin.TimeIntervalSummary, StringComparison.Ordinal);
        Assert.Equal(2.66m, admin.TotalBookedHours);
        Assert.Equal(3, admin.Performances.Select(item => item.PerformanceId).Distinct().Count());
    }

    [Fact]
    public void OpenHoursTotal_SumsOnlyOpenProject300AdminCases()
    {
        var open = Finding("o", 1, 2m, "Geboekt 08:00-10:00 (2,00 u) zonder ondersteunende planning.",
            "PerformanceId=1; PROJNR=300; desc=werk; memo=—; planning evidence = none.");
        var reviewed = Finding("r", 2, 4m, "Geboekt 08:00-12:00 (4,00 u) zonder ondersteunende planning.",
            "PerformanceId=2; PROJNR=300; desc=klaar; memo=—; planning evidence = none.");
        reviewed.Status = PayrollFindingStatus.Reviewed;
        reviewed.ResourceId = "11";

        var employees = new Dictionary<string, PayrollShadowEmployeeResult>(StringComparer.Ordinal)
        {
            ["10"] = new() { ResourceId = "10", DisplayNameSnapshot = "A" },
            ["11"] = new() { ResourceId = "11", DisplayNameSnapshot = "B" },
        };
        var review = PayrollReviewCaseBuilder.Build([open, reviewed], employees, []);
        var admin = PayrollAdminCaseBuilder.Build(review);
        var summary = PayrollAdminCaseBuilder.Summarize(review, admin);
        Assert.Equal(2m, summary.OpenBookedHours);
        Assert.Equal(2m, summary.OpenBookedHoursByCategory![PayrollReviewCategory.Project300]);
    }

    [Fact]
    public void Project300_InlineChoiceLabels_MatchTriageActions()
    {
        var choices = PayrollGuidedDecisions.ChoicesFor(PayrollReviewCategory.Project300);
        Assert.Equal(
            ["Werk was terecht", "Planning ontbreekt", "Uren zijn fout", "Onzeker"],
            choices.Select(item => item.Label).ToArray());
        Assert.Equal(PayrollFindingStatus.Reviewed, choices[0].ResultStatus);
        Assert.Equal(PayrollFindingStatus.NeedsFollowUp, choices[2].ResultStatus);
        Assert.True(choices[2].RequiresComment);
        Assert.True(choices[3].RequiresComment);
    }

    [Fact]
    public void MissingDescription_IsSurfacedAsAbsent_NotInvented()
    {
        var finding = Finding(
            "p",
            9,
            1m,
            "Geboekt 07:30-08:30 (1,00 u) zonder ondersteunende planning.",
            "PerformanceId=9; PROJNR=300; desc=—; memo=—; planning evidence = none.");
        var review = Assert.Single(PayrollReviewCaseBuilder.Build([finding], Emp("10", "X"), []));
        Assert.False(review.DescriptionPresent);
        Assert.Null(review.PerformanceDescription);
        var admin = Assert.Single(PayrollAdminCaseBuilder.Build([review]));
        Assert.Contains(admin.EvidenceChips!, chip => chip.Contains("Omschrijving ontbreekt", StringComparison.Ordinal));
    }

    [Fact]
    public void GuidedFallback_StillExposesSameAdminCaseChoices()
    {
        var finding = Finding(
            "g",
            5,
            1m,
            "Geboekt 09:00-10:00 (1,00 u) zonder ondersteunende planning.",
            "PerformanceId=5; PROJNR=300; desc=test; memo=—; planning evidence = none.");
        var admin = Assert.Single(PayrollAdminCaseBuilder.Build(
            PayrollReviewCaseBuilder.Build([finding], Emp("10", "Y"), [])));
        Assert.Equal("Waarom werden deze uren op 300 geboekt zonder reservatie?", admin.BusinessQuestion);
        Assert.Contains(admin.Choices, item => item.DecisionCode == PayrollGuidedDecisionCodes.P300WorkValid);
        Assert.Contains(admin.Choices, item => item.DecisionCode == PayrollGuidedDecisionCodes.P300HoursWrong);
    }

    private static Dictionary<string, PayrollShadowEmployeeResult> Emp(string id, string name) =>
        new(StringComparer.Ordinal)
        {
            [id] = new() { ResourceId = id, DisplayNameSnapshot = name },
        };

    private static PayrollFindingRecord Finding(
        string key,
        long perfId,
        decimal booked,
        string description,
        string evidence) =>
        new()
        {
            Id = Math.Abs(key.GetHashCode()) % 100000 + 1,
            FindingKey = key,
            ResourceId = "10",
            Date = new DateOnly(2026, 8, 28),
            FindingType = PayrollFindingType.Project300WithoutPlanning,
            Severity = PayrollFindingSeverity.Review,
            Status = PayrollFindingStatus.Open,
            Title = "Project 300 zonder planning",
            Description = description,
            Evidence = evidence,
            SuggestedAction = "Controleer",
            RelatedPerformanceIdsJson = JsonSerializer.Serialize(new[] { perfId }),
            SuggestedProjectId = "300",
            BookedHours = booked,
        };
}
