using System.Globalization;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Normalization;

/// <summary>
/// Normalizes Plenion/Power BI PAUZE values. Numeric interpretation requires an
/// explicit <see cref="PauseSourceKind"/> — ambiguous bare numbers are Invalid.
/// </summary>
public static class PauseNormalizer
{
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static PauseNormalizationResult Normalize(
        object? raw,
        PauseSourceKind sourceKind = PauseSourceKind.Unspecified)
    {
        if (raw is null || raw is DBNull)
        {
            return Missing(sourceKind, null);
        }

        return raw switch
        {
            TimeSpan timeSpan => FromTimeSpan(timeSpan, sourceKind, timeSpan.ToString()),
            DateTime dateTime => FromTimeSpan(
                dateTime.TimeOfDay,
                PreferTimeOfDay(sourceKind),
                dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            decimal numeric => FromNumeric(numeric, sourceKind, FormatDecimal(numeric)),
            double numeric => FromNumeric(
                Convert.ToDecimal(numeric, CultureInfo.InvariantCulture),
                sourceKind,
                Convert.ToString(numeric, CultureInfo.InvariantCulture)),
            float numeric => FromNumeric(
                Convert.ToDecimal(numeric, CultureInfo.InvariantCulture),
                sourceKind,
                Convert.ToString(numeric, CultureInfo.InvariantCulture)),
            int numeric => FromNumeric(numeric, sourceKind, numeric.ToString(CultureInfo.InvariantCulture)),
            long numeric => FromNumeric(numeric, sourceKind, numeric.ToString(CultureInfo.InvariantCulture)),
            string text => FromText(text, sourceKind),
            _ => Invalid(
                sourceKind,
                Convert.ToString(raw, CultureInfo.InvariantCulture)),
        };
    }

    public static PauseNormalizationResult NormalizeText(
        string? raw,
        PauseSourceKind sourceKind = PauseSourceKind.Unspecified) =>
        Normalize((object?)raw, sourceKind);

    private static PauseNormalizationResult FromText(string text, PauseSourceKind sourceKind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Missing(sourceKind, text);
        }

        var trimmed = text.Trim();

        if (TryParseTimeOfDay(trimmed, out var timeSpan))
        {
            return FromTimeSpan(timeSpan, PreferTimeOfDay(sourceKind), trimmed);
        }

        if (TryParseDecimal(trimmed, out var numeric))
        {
            return FromNumeric(numeric, sourceKind, trimmed);
        }

        return Invalid(sourceKind, trimmed);
    }

    private static PauseNormalizationResult FromTimeSpan(
        TimeSpan timeSpan,
        PauseSourceKind sourceKind,
        string? rawRepresentation)
    {
        if (timeSpan < TimeSpan.Zero || timeSpan > TimeSpan.FromHours(24))
        {
            return Invalid(sourceKind, rawRepresentation);
        }

        // Preserve fractional minutes when present (e.g. 17 minutes exact).
        var exactMinutes = (decimal)timeSpan.TotalMinutes;
        return new PauseNormalizationResult(
            PauseParseStatus.Valid,
            exactMinutes,
            PreferTimeOfDay(sourceKind),
            rawRepresentation);
    }

    private static PauseNormalizationResult FromNumeric(
        decimal numeric,
        PauseSourceKind sourceKind,
        string? rawRepresentation)
    {
        if (numeric < 0)
        {
            return Invalid(sourceKind, rawRepresentation);
        }

        return sourceKind switch
        {
            PauseSourceKind.Hours =>
                Valid(numeric * 60m, PauseSourceKind.Hours, rawRepresentation),
            PauseSourceKind.ExcelDayFraction =>
                // Historical Power Query: Number(PAUZE) * 24 = pause hours.
                Valid(numeric * 24m * 60m, PauseSourceKind.ExcelDayFraction, rawRepresentation),
            PauseSourceKind.TimeOfDay =>
                // Numeric under TimeOfDay is not a proven convention → Invalid.
                Invalid(sourceKind, rawRepresentation),
            _ =>
                Invalid(PauseSourceKind.Unspecified, rawRepresentation),
        };
    }

    private static bool TryParseTimeOfDay(string value, out TimeSpan timeSpan)
    {
        // Plain clock: 00:30:00
        if (value.Contains(':', StringComparison.Ordinal) &&
            !value.Contains(' ', StringComparison.Ordinal) &&
            TimeSpan.TryParseExact(
                value,
                ["hh\\:mm\\:ss", "h\\:mm\\:ss", "hh\\:mm", "h\\:mm"],
                CultureInfo.InvariantCulture,
                out timeSpan))
        {
            return true;
        }

        // Excel export: 1899-12-30 00:30:00 — take TimeOfDay only.
        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateTime) ||
            DateTime.TryParse(
                value,
                Belgian,
                DateTimeStyles.AllowWhiteSpaces,
                out dateTime))
        {
            timeSpan = dateTime.TimeOfDay;
            return true;
        }

        timeSpan = default;
        return false;
    }

    private static bool TryParseDecimal(string value, out decimal numeric)
    {
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out numeric) ||
               decimal.TryParse(value, NumberStyles.Number, Belgian, out numeric);
    }

    private static PauseSourceKind PreferTimeOfDay(PauseSourceKind sourceKind) =>
        sourceKind is PauseSourceKind.Unspecified
            ? PauseSourceKind.TimeOfDay
            : sourceKind;

    private static PauseNormalizationResult Missing(
        PauseSourceKind sourceKind,
        string? raw) =>
        new(PauseParseStatus.Missing, null, sourceKind, raw);

    private static PauseNormalizationResult Invalid(
        PauseSourceKind sourceKind,
        string? raw) =>
        new(PauseParseStatus.Invalid, null, sourceKind, raw);

    private static PauseNormalizationResult Valid(
        decimal exactMinutes,
        PauseSourceKind sourceKind,
        string? raw) =>
        new(PauseParseStatus.Valid, exactMinutes, sourceKind, raw);

    private static string FormatDecimal(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
