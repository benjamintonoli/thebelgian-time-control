namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Canonical raw performance snapshot for shadow payroll.
/// No business calculations; ATL precision is preserved as decimal hours/minutes.
/// </summary>
public sealed record NormalizedPerformanceEntry(
    long SourceEntryId,
    string ResourceId,
    DateOnly Date,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    decimal AtlHoursRaw,
    decimal AtlMinutesExact,
    TimeSpan? GrossClockDuration,
    PauseNormalizationResult Pause,
    decimal? Km,
    int? HfdTaakId,
    string? ProjectId,
    int? ProjectNumber,
    string? BonNr,
    string? Description,
    string? Memo,
    string? Postcode,
    long SortKey,
    bool IsTravel = false,
    bool IsAbsence = false,
    bool IsStandby = false,
    bool IsCalendarSynthetic = false);
