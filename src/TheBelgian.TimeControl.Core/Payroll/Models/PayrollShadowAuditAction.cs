namespace TheBelgian.TimeControl.Core.Payroll.Models;

public enum PayrollShadowAuditAction
{
    EligibilityIncluded = 0,
    EligibilityExcluded = 1,
    EligibilityReset = 2,
    ReviewAccepted = 3,
    ReviewNeedsFollowUp = 4,
    ReviewReset = 5,
    MonthReviewStarted = 6,
    MonthFinalized = 7,
}
