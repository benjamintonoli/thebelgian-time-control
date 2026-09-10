using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Finalization;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollFinalizationAdminGroupingTests
{
    [Fact]
    public void MultipleReviewFindings_GroupIntoOneAdminCase_AndTerminalDecisionClearsBlockers()
    {
        var date = new DateOnly(2026, 8, 10);
        var openReviews = new[]
        {
            Review("r1", "100", date, PayrollFindingStatus.Open, "40167", findingKeys: ["f1"]),
            Review("r2", "100", date, PayrollFindingStatus.Open, "40167", findingKeys: ["f2"]),
            Review("r3", "100", date, PayrollFindingStatus.Open, "40167", findingKeys: ["f3"]),
        };

        var admin = PayrollAdminCaseBuilder.Build(openReviews);
        var adminCase = Assert.Single(admin);
        Assert.Equal(3, adminCase.UnderlyingReviewCaseCount);
        Assert.Equal(3, adminCase.FindingKeys.Count);

        var before = PayrollFinalizationEvaluator.Evaluate(Month(), [Included("100")], openReviews);
        Assert.False(before.CanFinalize);
        Assert.Equal(1, before.OpenAdminCases);
        Assert.Equal(3, before.OpenReviewCases);
        Assert.Equal(1, before.UnresolvedAdminCases);
        Assert.Equal(3, before.UnderlyingUnresolvedReviewCases);

        // Terminal AdminCase decision updates every represented review case (same as ApplyAdminDecisionCore).
        var closed = openReviews
            .Select(item => item with
            {
                WorkflowStatus = PayrollFindingStatus.Reviewed,
                DecisionCode = "P300_OK",
                DecisionLabel = "Ok",
            })
            .ToArray();

        var afterAdmin = PayrollAdminCaseBuilder.Build(closed);
        Assert.Single(afterAdmin);
        Assert.Equal(PayrollFindingStatus.Reviewed, afterAdmin[0].WorkflowStatus);

        var after = PayrollFinalizationEvaluator.Evaluate(Month(), [Included("100")], closed);
        Assert.True(after.CanFinalize);
        Assert.Equal(0, after.OpenAdminCases);
        Assert.Equal(0, after.OpenReviewCases);
        Assert.Equal(0, after.UnresolvedAdminCases);
        Assert.Equal(0, after.UnderlyingUnresolvedReviewCases);
    }

    [Fact]
    public void EightyNineAdmin_Vs_OneHundredSevenUnderlying_CoexistWithoutLostWork()
    {
        var reviews = new List<PayrollReviewCase>();
        // 28 admin P300 from 38 reviews: 10 extra duplicates sharing resource/date/project.
        for (var i = 0; i < 28; i++)
        {
            var resource = $"p300-{i}";
            var date = new DateOnly(2026, 8, 1).AddDays(i % 28);
            reviews.Add(Review($"p300-a-{i}", resource, date, PayrollFindingStatus.Open, $"proj-{i}"));
            if (i < 10)
            {
                reviews.Add(Review($"p300-b-{i}", resource, date, PayrollFindingStatus.Open, $"proj-{i}"));
            }
        }

        // 23 admin P200 from 31 reviews: 8 extras.
        for (var i = 0; i < 23; i++)
        {
            var resource = $"p200-{i}";
            var date = new DateOnly(2026, 8, 1).AddDays(i % 23);
            reviews.Add(Review(
                $"p200-a-{i}",
                resource,
                date,
                PayrollFindingStatus.Open,
                $"proj200-{i}",
                category: PayrollReviewCategory.Project200));
            if (i < 8)
            {
                reviews.Add(Review(
                    $"p200-b-{i}",
                    resource,
                    date,
                    PayrollFindingStatus.Open,
                    $"proj200-{i}",
                    category: PayrollReviewCategory.Project200));
            }
        }

        reviews.Add(Review("p100-1", "p100-1", new DateOnly(2026, 8, 5), PayrollFindingStatus.Open, "p100", category: PayrollReviewCategory.Project100));
        reviews.Add(Review("p100-2", "p100-2", new DateOnly(2026, 8, 6), PayrollFindingStatus.Open, "p100", category: PayrollReviewCategory.Project100));

        for (var i = 0; i < 16; i++)
        {
            reviews.Add(Review(
                $"sb-{i}",
                $"sb-{i}",
                new DateOnly(2026, 8, 1).AddDays(i),
                PayrollFindingStatus.Open,
                null,
                category: PayrollReviewCategory.Standby));
        }

        for (var i = 0; i < 18; i++)
        {
            reviews.Add(Review(
                $"mt-{i}",
                $"mt-{i}",
                new DateOnly(2026, 8, 1).AddDays(i),
                PayrollFindingStatus.Open,
                $"mt-proj-{i}",
                category: PayrollReviewCategory.MissingPerformance));
        }

        reviews.Add(Review(
            "mt-fu-1",
            "mt-fu-1",
            new DateOnly(2026, 8, 20),
            PayrollFindingStatus.NeedsFollowUp,
            "mt-fu",
            category: PayrollReviewCategory.MissingPerformance));
        reviews.Add(Review(
            "mt-fu-2",
            "mt-fu-2",
            new DateOnly(2026, 8, 21),
            PayrollFindingStatus.NeedsFollowUp,
            "mt-fu",
            category: PayrollReviewCategory.MissingPerformance));

        Assert.Equal(107, reviews.Count);

        var employees = reviews
            .Select(item => item.ResourceId)
            .Distinct(StringComparer.Ordinal)
            .Select(Included)
            .ToArray();

        var blockers = PayrollFinalizationEvaluator.Evaluate(Month(), employees, reviews);
        Assert.False(blockers.CanFinalize);
        Assert.Equal(107, blockers.UnderlyingUnresolvedReviewCases);
        Assert.Equal(89, blockers.UnresolvedAdminCases);
        Assert.Equal(87, blockers.OpenAdminCases);
        Assert.Equal(2, blockers.FollowUpAdminCases);

        var p300 = Assert.Single(blockers.CategoryWorkloads!, item => item.Category == PayrollReviewCategory.Project300);
        Assert.Equal(28, p300.AdminCases);
        Assert.Equal(38, p300.UnderlyingReviewCases);

        var p200 = Assert.Single(blockers.CategoryWorkloads!, item => item.Category == PayrollReviewCategory.Project200);
        Assert.Equal(23, p200.AdminCases);
        Assert.Equal(31, p200.UnderlyingReviewCases);
    }

    private static PayrollShadowMonth Month() => new()
    {
        Year = 2026,
        Month = 8,
        Status = PayrollShadowMonthStatus.InReview,
        CalculationVersion = "test-v1",
        ConfigurationSnapshotJson = """{"ok":true}""",
        PeriodStart = new DateOnly(2026, 8, 1),
        PeriodEnd = new DateOnly(2026, 8, 31),
        EvaluationDate = new DateOnly(2026, 9, 1),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        CreatedBy = "test",
    };

    private static PayrollShadowEmployeeResult Included(string id) =>
        new()
        {
            ResourceId = id,
            DisplayNameSnapshot = "Emp " + id,
            EligibilityStatus = PayrollEligibilityStatus.Included,
            AcertaIdentityStatus = AcertaIdentityStatus.Present,
            OrdinaryStatus = PayrollMonthCalculationStatus.Calculated,
            StandbyStatus = PayrollMonthCalculationStatus.Calculated,
            CityStatus = PayrollMonthCalculationStatus.Calculated,
            KmStatus = PayrollMonthCalculationStatus.Calculated,
            Code414Status = PayrollMonthCalculationStatus.Calculated,
            ReviewStatus = PayrollEmployeeReviewStatus.Pending,
        };

    private static PayrollReviewCase Review(
        string key,
        string resourceId,
        DateOnly date,
        PayrollFindingStatus status,
        string? projectId,
        PayrollReviewCategory category = PayrollReviewCategory.Project300,
        IReadOnlyList<string>? findingKeys = null) =>
        new(
            key,
            category,
            resourceId,
            "Emp " + resourceId,
            date,
            PayrollFindingSeverity.High,
            status,
            "problem",
            BonNr: null,
            ProjectId: projectId,
            BookedSummary: null,
            EvidenceSummary: null,
            ProposalSummary: null,
            PayrollReviewCaseActionability.NeedsControl,
            "Controle nodig",
            FindingIds: [1],
            FindingKeys: findingKeys ?? [key],
            FindingTypes: [PayrollFindingType.Project300WithoutPlanning],
            PrimaryPerformanceId: null,
            RelatedActionId: null,
            HybridScenarioNote: null,
            ReviewedAtUtc: null,
            ReviewedBy: null,
            ReviewComment: null);
}
