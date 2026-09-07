using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Web.Pages.Admin.Payroll;

public static class PayrollReviewLabels
{
    public static string ReviewStatus(PayrollEmployeeReviewStatus status) =>
        status switch
        {
            PayrollEmployeeReviewStatus.Pending => "Te controleren",
            PayrollEmployeeReviewStatus.Accepted => "Goedgekeurd",
            PayrollEmployeeReviewStatus.NeedsFollowUp => "Opvolging nodig",
            PayrollEmployeeReviewStatus.ExcludedFromPayroll => "Uitgesloten",
            _ => status.ToString(),
        };

    public static string Eligibility(PayrollEligibilityStatus status) =>
        status switch
        {
            PayrollEligibilityStatus.NeedsDecision => "Nog te beslissen",
            PayrollEligibilityStatus.Included => "Included",
            PayrollEligibilityStatus.Excluded => "Excluded",
            _ => status.ToString(),
        };

    public static string MonthStatus(PayrollShadowMonthStatus status) =>
        status switch
        {
            PayrollShadowMonthStatus.WaitingForData => "Wacht op data",
            PayrollShadowMonthStatus.ReadyForReview => "Klaar voor review",
            PayrollShadowMonthStatus.InReview => "Review bezig",
            PayrollShadowMonthStatus.Finalized => "Afgesloten",
            _ => status.ToString(),
        };

    public static string FindingSeverity(PayrollFindingSeverity severity) =>
        severity switch
        {
            PayrollFindingSeverity.Info => "Info",
            PayrollFindingSeverity.Review => "Review",
            PayrollFindingSeverity.High => "High",
            _ => severity.ToString(),
        };

    public static string FindingType(PayrollFindingType type) =>
        type switch
        {
            PayrollFindingType.Project300WithoutPlanning => "Project 300 zonder planning",
            PayrollFindingType.Project200WithoutPlanning => "Project 200 zonder planning",
            PayrollFindingType.Project200ExceedsPlanning => "Project 200 langer dan planning",
            PayrollFindingType.Project100TrainingHours => "Toolbox/opleiding uren",
            PayrollFindingType.Project100TrainingInOvertime => "Toolbox veroorzaakt overuren",
            PayrollFindingType.Project100ExceedsPlannedDuration => "Toolbox langer dan gepland",
            PayrollFindingType.OverlappingPerformances => "Dubbele uren",
            _ => type.ToString(),
        };

    public static string SeverityBadgeClass(PayrollFindingSeverity severity) =>
        severity switch
        {
            PayrollFindingSeverity.High => "text-bg-danger",
            PayrollFindingSeverity.Review => "text-bg-warning",
            _ => "text-bg-secondary",
        };
}
