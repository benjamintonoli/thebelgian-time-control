using System.Text.Json;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Core.Payroll.Finalization;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Core.Payroll.Review;

namespace TheBelgian.TimeControl.Tests.Payroll;

public sealed class PayrollFinalizationV2Tests
{
    [Fact]
    public void OpenReviewCase_Blocks()
    {
        var blockers = Evaluate(
            employees: [Included("1")],
            cases: [Case("c1", "1", PayrollFindingStatus.Open)]);
        Assert.False(blockers.CanFinalize);
        Assert.Contains(blockers.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.OpenReviewCases);
        Assert.Equal(1, blockers.OpenReviewCases);
    }

    [Fact]
    public void FollowUpReviewCase_Blocks()
    {
        var blockers = Evaluate(
            employees: [Included("1")],
            cases: [Case("c1", "1", PayrollFindingStatus.NeedsFollowUp)]);
        Assert.False(blockers.CanFinalize);
        Assert.Contains(blockers.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.FollowUpReviewCases);
    }

    [Fact]
    public void TerminalReviewCases_DoNotBlock()
    {
        var blockers = Evaluate(
            employees: [Included("1", review: PayrollEmployeeReviewStatus.Pending)],
            cases:
            [
                Case("a", "1", PayrollFindingStatus.Reviewed),
                Case("b", "1", PayrollFindingStatus.Dismissed),
                Case("c", "1", PayrollFindingStatus.Resolved),
            ]);
        Assert.True(blockers.CanFinalize);
        Assert.Equal(0, blockers.OpenReviewCases);
        Assert.Equal(0, blockers.FollowUpReviewCases);
        Assert.Equal(3, blockers.ReviewCasesTotal);
    }

    [Fact]
    public void FortyFourPendingEmployees_Alone_DoNotBlock()
    {
        var employees = Enumerable.Range(1, 44)
            .Select(i => Included(i.ToString(System.Globalization.CultureInfo.InvariantCulture), review: PayrollEmployeeReviewStatus.Pending))
            .ToArray();
        var blockers = Evaluate(employees, cases: []);
        Assert.Equal(44, blockers.PendingIncluded);
        Assert.True(blockers.CanFinalize);
        Assert.DoesNotContain(blockers.SummaryLines, line => line.Contains("Pending", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmployeeNeedsFollowUp_Blocks()
    {
        var blockers = Evaluate(
            employees: [Included("1", review: PayrollEmployeeReviewStatus.NeedsFollowUp)],
            cases: []);
        Assert.False(blockers.CanFinalize);
        Assert.Contains(blockers.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.EmployeeNeedsFollowUp);
    }

    [Fact]
    public void NeedsDecision_Blocks()
    {
        var blockers = Evaluate(
            employees:
            [
                Included("1"),
                new PayrollShadowEmployeeResult
                {
                    ResourceId = "2",
                    DisplayNameSnapshot = "B",
                    EligibilityStatus = PayrollEligibilityStatus.NeedsDecision,
                    AcertaIdentityStatus = AcertaIdentityStatus.Present,
                    OrdinaryStatus = PayrollMonthCalculationStatus.Calculated,
                    StandbyStatus = PayrollMonthCalculationStatus.Calculated,
                    CityStatus = PayrollMonthCalculationStatus.Calculated,
                    KmStatus = PayrollMonthCalculationStatus.Calculated,
                    Code414Status = PayrollMonthCalculationStatus.Calculated,
                    ReviewStatus = PayrollEmployeeReviewStatus.Pending,
                },
            ],
            cases: []);
        Assert.False(blockers.CanFinalize);
        Assert.Contains(blockers.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.EligibilityNeedsDecision);
    }

    [Fact]
    public void MissingAcerta_Blocks()
    {
        var emp = Included("1");
        emp.AcertaIdentityStatus = AcertaIdentityStatus.Missing;
        var blockers = Evaluate([emp], []);
        Assert.False(blockers.CanFinalize);
        Assert.Contains(blockers.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.MissingAcertaId);
    }

    [Fact]
    public void ZeroIncluded_Blocks()
    {
        var blockers = Evaluate(employees: [], cases: []);
        Assert.False(blockers.CanFinalize);
        Assert.Contains(blockers.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.NoIncludedEmployees);
    }

    [Fact]
    public void CalculationIncomplete_Blocks()
    {
        var emp = Included("1");
        emp.OrdinaryStatus = PayrollMonthCalculationStatus.NotCalculated;
        var blockers = Evaluate([emp], []);
        Assert.False(blockers.CanFinalize);
        Assert.Contains(blockers.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.CalculationIncomplete);
    }

    [Fact]
    public void AuditSnapshot_ContainsCounts_NoSensitiveIdentity()
    {
        var month = Month();
        var blockers = Evaluate([Included("1")], []);
        var json = PayrollFinalizationEvaluator.BuildAuditSnapshotJson(month, blockers, "ok");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("includedCount").GetInt32());
        Assert.Equal("ok", doc.RootElement.GetProperty("comment").GetString());
        Assert.Equal("payroll-month-finalization-v2", doc.RootElement.GetProperty("schema").GetString());
        Assert.False(json.Contains("rijksregister", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("national", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("ssn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OpenCases_FriendlyMessage_IncludesCategoryBreakdown()
    {
        var blockers = Evaluate(
            [Included("1"), Included("2")],
            [
                Case("a", "1", PayrollFindingStatus.Open, PayrollReviewCategory.Project300),
                Case("b", "2", PayrollFindingStatus.Open, PayrollReviewCategory.Project300),
                Case("c", "1", PayrollFindingStatus.Open, PayrollReviewCategory.Standby),
            ]);
        var open = Assert.Single(blockers.Blockers, item => item.Code == PayrollFinalizationBlockerCodes.OpenReviewCases);
        Assert.Contains("2 300 zonder planning", open.FriendlyMessage, StringComparison.Ordinal);
        Assert.Contains("1 Wachtdienst", open.FriendlyMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Pending", open.FriendlyMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static PayrollMonthFinalizationBlockers Evaluate(
        IReadOnlyList<PayrollShadowEmployeeResult> employees,
        IReadOnlyList<PayrollReviewCase> cases) =>
        PayrollFinalizationEvaluator.Evaluate(Month(), employees, cases);

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

    private static PayrollShadowEmployeeResult Included(
        string id,
        PayrollEmployeeReviewStatus review = PayrollEmployeeReviewStatus.Pending) =>
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
            ReviewStatus = review,
            Code135At150Units = 1m,
            StandbyRoundedHours = 0.5m,
            CityAllowanceAmount = 2m,
            KmAmount = 3m,
            Code414Amount = 4m,
        };

    private static PayrollReviewCase Case(
        string key,
        string resourceId,
        PayrollFindingStatus status,
        PayrollReviewCategory category = PayrollReviewCategory.Project300) =>
        new(
            key,
            category,
            resourceId,
            "Emp " + resourceId,
            new DateOnly(2026, 8, 1),
            PayrollFindingSeverity.High,
            status,
            "problem",
            null,
            null,
            null,
            null,
            null,
            PayrollReviewCaseActionability.NeedsControl,
            "Controle nodig",
            [],
            [key],
            [],
            null,
            null,
            null,
            null,
            null,
            null);
}
