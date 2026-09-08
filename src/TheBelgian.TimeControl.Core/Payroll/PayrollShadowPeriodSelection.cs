using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Core.Payroll;

/// <summary>
/// Canonical selection of the payroll shadow period for landing / top-nav entry.
/// Reuses existing month statuses — does not invent a separate "current month" clock rule.
/// </summary>
public static class PayrollShadowPeriodSelection
{
    /// <summary>
    /// Prefer an open review period: InReview, then ReadyForReview, then other non-finalized,
    /// then the latest finalized month. Ties broken by year/month descending (ListMonths order).
    /// </summary>
    public static PayrollShadowMonthSummary? SelectCanonical(
        IReadOnlyList<PayrollShadowMonthSummary> months)
    {
        if (months.Count == 0)
        {
            return null;
        }

        // ListMonthsAsync already returns Year/Month descending.
        return months.FirstOrDefault(item => item.Status == PayrollShadowMonthStatus.InReview)
            ?? months.FirstOrDefault(item => item.Status == PayrollShadowMonthStatus.ReadyForReview)
            ?? months.FirstOrDefault(item => item.Status != PayrollShadowMonthStatus.Finalized)
            ?? months[0];
    }
}
