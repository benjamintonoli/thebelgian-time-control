namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

/// <summary>
/// Raw PROJ_Prest row as returned by ODBC before payroll normalization.
/// </summary>
public sealed record PlenionPayrollPerformanceRow(
    long IdProjPrest,
    DateOnly Datum,
    object? Van,
    object? Tot,
    object? Pauze,
    decimal AtlHoursRaw,
    decimal? Km,
    string ResourceId,
    string? IdProj,
    int? IdHfdTaak,
    string? BonNr,
    string? Omschr,
    string? Memo,
    DateTime? DatCre,
    int? ProjNr);
