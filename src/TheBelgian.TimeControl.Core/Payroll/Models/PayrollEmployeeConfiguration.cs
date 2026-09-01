namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record PayrollEmployeeConfiguration(
    string ResourceId,
    bool Included,
    EmploymentType EmploymentType,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    string WorkScheduleId,
    string? NationalRegisterNormalized = null);
