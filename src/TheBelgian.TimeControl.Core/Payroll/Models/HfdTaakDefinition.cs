namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Read-only HFDTAAK master row for diagnostics. Business rules must not hardcode
/// the full catalog; lookup by id when needed.
/// </summary>
public sealed record HfdTaakDefinition(
    int Id,
    string? Code,
    string? Description);
