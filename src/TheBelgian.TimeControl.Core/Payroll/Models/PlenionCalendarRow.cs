namespace TheBelgian.TimeControl.Core.Payroll.Models;

/// <summary>
/// Raw KALENDER row as returned by ODBC before legacy calendar synthesis.
/// </summary>
public sealed record PlenionCalendarRow(
    long IdCalendar,
    string? OriginalResourceId,
    string? ResourcesRaw,
    DateOnly DateFrom,
    DateOnly? DateTo,
    TimeOnly? TimeFrom,
    TimeOnly? TimeTo,
    int TaskTypeId,
    string? FullDayRaw,
    string? Subject,
    DateTime? CreatedAt);
