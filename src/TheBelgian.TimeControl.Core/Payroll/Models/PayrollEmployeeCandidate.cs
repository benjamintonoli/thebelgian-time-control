using TheBelgian.TimeControl.Core.Payroll.Legacy;

namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record PayrollEmployeeCandidate(
    string ResourceId,
    string ResourceCode,
    string DisplayName,
    string? Email,
    string? ResourceType,
    string? MainGroup,
    string? Team,
    string? Function,
    int? Soort,
    DateOnly? EmploymentEndDate,
    AcertaIdentityStatus AcertaIdentityStatus)
{
    /// <summary>
    /// Active for payroll roster/candidacy on period start, including the full calendar
    /// month after DATUMUITDIENST (one-month payroll lag).
    /// </summary>
    public bool IsActiveForPeriod(DateOnly periodStart) =>
        PayrollRosterEmploymentWindow.IsAutoEligibleOn(EmploymentEndDate, periodStart);
}
