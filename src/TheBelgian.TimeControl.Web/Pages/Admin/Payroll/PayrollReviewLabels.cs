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
}
