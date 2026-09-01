namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// One expanded legacy calendar absence row after Power BI M synthesis.
/// </summary>
public sealed record CalendarSyntheticEntry(
    long CalendarSourceId,
    string StableSourceKey,
    string ResourceId,
    DateOnly Date,
    int TypTaakId,
    int HfdTaakId,
    decimal SyntheticHoursRaw,
    bool IsFullDay,
    bool IsHalfDay,
    string SourceResourceScope,
    string? Subject);
