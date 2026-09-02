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
    public bool IsActiveForPeriod(DateOnly periodStart) =>
        EmploymentEndDate is null || EmploymentEndDate >= periodStart;
}
