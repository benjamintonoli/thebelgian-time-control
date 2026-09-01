using TheBelgian.TimeControl.Core.Payroll;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;

/// <summary>
/// Reproduces Power BI KALENDER M synthesis for legacy calendar absence rows.
/// </summary>
public static class LegacyCalendarSynthesis
{
    private static readonly HashSet<int> SupportedTaskTypes = [3, 5, 8];
    private static readonly string[] FullDayTokens = ["1", "TRUE", "WAAR", "YES", "JA"];

    public static IReadOnlyList<CalendarSyntheticEntry> Synthesize(
        IEnumerable<PlenionCalendarRow> sourceRows,
        DateOnly? clipFrom = null,
        DateOnly? clipThrough = null,
        IReadOnlySet<string>? resourceFilter = null)
    {
        var expanded = new List<ExpandedCalendarRow>();
        foreach (var row in sourceRows.Where(row => SupportedTaskTypes.Contains(row.TaskTypeId)))
        {
            foreach (var resourceId in ExpandResources(row))
            {
                if (resourceFilter is not null && !resourceFilter.Contains(resourceId))
                {
                    continue;
                }

                foreach (var date in ExpandDates(row))
                {
                    if (clipFrom is not null && date < clipFrom.Value)
                    {
                        continue;
                    }

                    if (clipThrough is not null && date > clipThrough.Value)
                    {
                        continue;
                    }

                    if (!IsWeekday(date))
                    {
                        continue;
                    }

                    expanded.Add(new ExpandedCalendarRow(row, resourceId, date));
                }
            }
        }

        var deduped = expanded
            .GroupBy(item => (item.Row.IdCalendar, item.ResourceId, item.Date))
            .Select(group => group.First())
            .ToList();

        return deduped
            .Select(item => CreateSyntheticEntry(item.Row, item.ResourceId, item.Date))
            .ToList();
    }

    public static IReadOnlyList<string> ExpandResources(PlenionCalendarRow row)
    {
        if (TryParseResourceId(row.OriginalResourceId, out var direct) && direct != "0")
        {
            return [direct];
        }

        if (string.IsNullOrWhiteSpace(row.ResourcesRaw))
        {
            return [];
        }

        return row.ResourcesRaw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static IEnumerable<DateOnly> ExpandDates(PlenionCalendarRow row)
    {
        var startDate = row.DateFrom;
        var endDate = row.DateTo is null || row.DateTo < row.DateFrom
            ? startDate
            : row.DateTo.Value;

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    public static bool IsWeekday(DateOnly date) => PowerBiDayOfWeek(date) is >= 0 and <= 4;

    public static int PowerBiDayOfWeek(DateOnly date) =>
        ((int)date.DayOfWeek + 6) % 7;

    public static decimal GetFullDayHours(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Friday ? 7m : 8m;

    public static decimal GetHalfDayHours(DateOnly date) =>
        date.DayOfWeek == DayOfWeek.Friday ? 3.5m : 4m;

    public static bool IsMarkedFullDay(string? fullDayRaw)
    {
        if (string.IsNullOrWhiteSpace(fullDayRaw))
        {
            return false;
        }

        var normalized = fullDayRaw.Trim().ToUpperInvariant();
        return FullDayTokens.Contains(normalized);
    }

    public static bool HasUsableTimes(TimeOnly? start, TimeOnly? end) =>
        start is not null && end is not null && start != end;

    public static decimal CalculateDurationHours(TimeOnly start, TimeOnly end)
    {
        var duration = end.ToTimeSpan() - start.ToTimeSpan();
        if (duration < TimeSpan.Zero)
        {
            duration += TimeSpan.FromHours(24);
        }

        return (decimal)duration.TotalHours;
    }

    public static bool LooksLikeHalfDay(TimeOnly start, TimeOnly end)
    {
        var durationHours = CalculateDurationHours(start, end);
        var startHour = start.Hour;
        var endHour = end.Hour;
        return durationHours <= 5m ||
               startHour > 10 ||
               (startHour < 10 && endHour <= 13);
    }

    public static int MapHfdTaakId(int taskTypeId) =>
        taskTypeId switch
        {
            3 => 18,
            5 => 10,
            8 => 10,
            _ => throw new InvalidOperationException($"Unsupported task type {taskTypeId}."),
        };

    public static decimal CalculateSyntheticHours(PlenionCalendarRow row, DateOnly date)
    {
        var isMarkedFullDay = IsMarkedFullDay(row.FullDayRaw);
        if (HasUsableTimes(row.TimeFrom, row.TimeTo))
        {
            if (isMarkedFullDay)
            {
                return GetFullDayHours(date);
            }

            if (LooksLikeHalfDay(row.TimeFrom!.Value, row.TimeTo!.Value))
            {
                return GetHalfDayHours(date);
            }

            return GetFullDayHours(date);
        }

        return GetFullDayHours(date);
    }

    private static CalendarSyntheticEntry CreateSyntheticEntry(
        PlenionCalendarRow row,
        string resourceId,
        DateOnly date)
    {
        var isMarkedFullDay = IsMarkedFullDay(row.FullDayRaw);
        var hasUsableTimes = HasUsableTimes(row.TimeFrom, row.TimeTo);
        var isHalfDay = !isMarkedFullDay &&
                        hasUsableTimes &&
                        LooksLikeHalfDay(row.TimeFrom!.Value, row.TimeTo!.Value);
        var hours = CalculateSyntheticHours(row, date);
        var stableKey = LegacySourceIdentity.ForCalendarSynthetic(row.IdCalendar, date, resourceId);
        var scope = !string.IsNullOrWhiteSpace(row.OriginalResourceId) &&
                    row.OriginalResourceId != "0"
            ? "IDRESOURCE"
            : "RESOURCES";

        return new CalendarSyntheticEntry(
            row.IdCalendar,
            stableKey,
            resourceId,
            date,
            row.TaskTypeId,
            MapHfdTaakId(row.TaskTypeId),
            hours,
            !isHalfDay,
            isHalfDay,
            scope,
            row.Subject);
    }

    private static bool TryParseResourceId(string? raw, out string resourceId)
    {
        resourceId = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        resourceId = raw.Trim();
        return resourceId != "0";
    }

    private sealed record ExpandedCalendarRow(
        PlenionCalendarRow Row,
        string ResourceId,
        DateOnly Date);
}
