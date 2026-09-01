namespace TheBelgian.TimeControl.Core.Payroll.Models;

public sealed record PauseNormalizationResult(
    PauseParseStatus Status,
    decimal? ExactMinutes,
    PauseSourceKind SourceKind,
    string? RawRepresentation);
