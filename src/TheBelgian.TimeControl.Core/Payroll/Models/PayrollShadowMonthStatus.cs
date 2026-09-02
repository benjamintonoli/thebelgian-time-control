namespace TheBelgian.TimeControl.Core.Payroll.Models;

public enum PayrollShadowMonthStatus
{
    WaitingForData = 0,
    ReadyForReview = 1,
    InReview = 2,
    Finalized = 3,
}
