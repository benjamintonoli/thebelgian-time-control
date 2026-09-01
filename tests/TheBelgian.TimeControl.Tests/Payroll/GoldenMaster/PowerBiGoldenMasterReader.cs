using System.Globalization;
using System.Text;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public sealed record PowerBiOverviewRow(
    string ResourceId,
    string Resource,
    decimal? TheoreticalHours,
    decimal? TotalHours,
    decimal? OvertimeHours,
    decimal? KmAmount,
    decimal? CityTripUnits,
    decimal? StandbyHours,
    decimal? AtlHours,
    decimal? Prest23Hours,
    decimal? Extra15Hours,
    decimal? Extra75Hours,
    decimal? PauseCorrectionHours,
    decimal? DuplicateHours,
    decimal? ExcessAbsenceHours);

public sealed record PowerBiDetailRow(
    string? PerformanceId,
    string Resource,
    string ResourceId,
    int? HfdTaakId,
    string? MainTask,
    DateOnly? Date,
    string? VanRaw,
    string? TotRaw,
    decimal? AtlHours,
    string? PauseRaw,
    decimal? TotalHours,
    decimal? TravelStartHours,
    decimal? TravelEndHours,
    decimal? Extra15Hours,
    decimal? Extra75Km,
    string? Postcode,
    decimal? CityTripUnits,
    decimal? DuplicateHours);

/// <summary>
/// Test/reconciliation helper for Power BI CSV exports. Not a production dependency.
/// Handles European decimals and duplicate column headers deterministically.
/// </summary>
public static class PowerBiGoldenMasterReader
{
    private static readonly CultureInfo Belgian = CultureInfo.GetCultureInfo("nl-BE");

    public static IReadOnlyList<PowerBiOverviewRow> ReadOverview(string path)
    {
        var (headers, rows) = ReadCsv(path);
        var index = new ColumnIndex(headers);
        return rows.Select(row => new PowerBiOverviewRow(
            Required(row, index, "IDRESOURCE"),
            Required(row, index, "Resource"),
            OptionalDecimal(row, index, "Theoretische uren CJ"),
            OptionalDecimal(row, index, "Totaal CJ"),
            OptionalDecimal(row, index, "Overuren"),
            OptionalDecimal(row, index, "KM-bedrag CJ"),
            OptionalDecimal(row, index, "Extra €5/rit naar grootsteden", "Extra ?5/rit naar grootsteden"),
            OptionalDecimal(row, index, "Wachtdienst CJ"),
            OptionalDecimal(row, index, "ATL"),
            OptionalDecimal(row, index, "Prest 23 zonder km en 15 min"),
            OptionalDecimal(row, index, "15 min CJ YTD"),
            OptionalDecimal(row, index, "extra 75km CJ YTD"),
            OptionalDecimal(row, index, "Pauzecorrectie CJ"),
            OptionalDecimal(row, index, "Dubbele uren"),
            OptionalDecimal(row, index, "teveel ziekte of verlof per dag"))).ToList();
    }

    public static IReadOnlyList<PowerBiDetailRow> ReadDetail(string path)
    {
        var (headers, rows) = ReadCsv(path);
        var index = new ColumnIndex(headers);
        return rows.Select(row => new PowerBiDetailRow(
            Optional(row, index, "IDPROJ_PREST"),
            Required(row, index, "Resource"),
            Required(row, index, "IDRESOURCE"),
            OptionalInt(row, index, "IDHFDTAAK"),
            Optional(row, index, "Hoofdtaak"),
            OptionalDate(row, index, "DATUM"),
            Optional(row, index, "VAN"),
            Optional(row, index, "TOT"),
            OptionalDecimal(row, index, "ATL"),
            Optional(row, index, "PAUZE"),
            OptionalDecimal(row, index, "Totaal uren"),
            OptionalDecimal(row, index, "Verpl begin dag"),
            OptionalDecimal(row, index, "Verpl einde dag"),
            OptionalDecimal(row, index, "15 min extra"),
            OptionalDecimal(row, index, "Extra 75km"),
            Optional(row, index, "postcode"),
            OptionalDecimal(row, index, "Extra €5/rit naar grootsteden", "Extra ?5/rit naar grootsteden"),
            OptionalDecimal(row, index, "Dubbele uren"))).ToList();
    }

    /// <summary>
    /// Power BI consistency check: sum of per-date MAX(Totaal uren) for a resource.
    /// </summary>
    public static decimal SumDailyMaxTotalHours(
        IEnumerable<PowerBiDetailRow> detailRows,
        string resourceName)
    {
        return detailRows
            .Where(row => string.Equals(row.Resource, resourceName, StringComparison.Ordinal))
            .Where(row => row.Date is not null && row.TotalHours is not null)
            .GroupBy(row => row.Date!.Value)
            .Select(group => group.Max(row => row.TotalHours!.Value))
            .DefaultIfEmpty(0m)
            .Sum();
    }

    public static (IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows) ReadCsv(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var records = ReadCsvRecords(reader);
        if (records.Count == 0)
        {
            throw new InvalidDataException($"CSV is leeg: {path}");
        }

        var headers = DeduplicateHeaders(records[0]);
        var rows = new List<string[]>(records.Count - 1);
        for (var i = 1; i < records.Count; i++)
        {
            var fields = records[i];
            if (fields.Length == 0 || fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (fields.Length < headers.Count)
            {
                Array.Resize(ref fields, headers.Count);
            }

            rows.Add(fields);
        }

        return (headers, rows);
    }

    /// <summary>
    /// RFC4180-style CSV record reader that supports multiline quoted fields.
    /// </summary>
    internal static List<string[]> ReadCsvRecords(TextReader reader)
    {
        var records = new List<string[]>();
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                if (fieldStarted || current.Length > 0 || fields.Count > 0)
                {
                    fields.Add(current.ToString());
                    records.Add(fields.ToArray());
                }

                break;
            }

            var ch = (char)next;
            fieldStarted = true;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    var peek = reader.Peek();
                    if (peek == '"')
                    {
                        reader.Read();
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(current.ToString());
                    current.Clear();
                    break;
                case '\r':
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    fields.Add(current.ToString());
                    records.Add(fields.ToArray());
                    fields = new List<string>();
                    current.Clear();
                    fieldStarted = false;
                    break;
                case '\n':
                    fields.Add(current.ToString());
                    records.Add(fields.ToArray());
                    fields = new List<string>();
                    current.Clear();
                    fieldStarted = false;
                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        return records;
    }

    internal static IReadOnlyList<string> DeduplicateHeaders(IReadOnlyList<string> headers)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<string>(headers.Count);
        foreach (var header in headers)
        {
            if (!seen.TryGetValue(header, out var count))
            {
                seen[header] = 1;
                result.Add(header);
            }
            else
            {
                seen[header] = count + 1;
                result.Add($"{header}__{count}");
            }
        }

        return result;
    }

    internal static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                inQuotes = true;
            }
            else if (ch == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    internal static decimal? ParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim().Trim('"');
        var normalized = NormalizeDecimalText(trimmed);
        if (decimal.TryParse(
                normalized,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Normalizes Belgian/European and invariant decimal text to invariant form.
    /// InvariantCulture treats ',' as a thousands separator, so "8,75" must not
    /// be parsed with Invariant first.
    /// </summary>
    internal static string NormalizeDecimalText(string trimmed)
    {
        var hasComma = trimmed.Contains(',', StringComparison.Ordinal);
        var hasDot = trimmed.Contains('.', StringComparison.Ordinal);
        if (hasComma && hasDot)
        {
            // Last separator is the decimal separator.
            var lastComma = trimmed.LastIndexOf(',');
            var lastDot = trimmed.LastIndexOf('.');
            if (lastComma > lastDot)
            {
                // 1.234,56
                return trimmed.Replace(".", string.Empty, StringComparison.Ordinal)
                    .Replace(',', '.');
            }

            // 1,234.56
            return trimmed.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        if (hasComma)
        {
            // 8,75 or 188,92
            return trimmed.Replace(',', '.');
        }

        return trimmed;
    }

    private static string Required(string[] row, ColumnIndex index, string name) =>
        Optional(row, index, name)
        ?? throw new InvalidDataException($"Verplicht kolom ontbreekt: {name}");

    private static string? Optional(string[] row, ColumnIndex index, params string[] names)
    {
        foreach (var name in names)
        {
            if (index.TryGet(name, out var i) && i < row.Length)
            {
                var value = row[i];
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        return null;
    }

    private static decimal? OptionalDecimal(string[] row, ColumnIndex index, params string[] names) =>
        ParseDecimal(Optional(row, index, names));

    private static int? OptionalInt(string[] row, ColumnIndex index, params string[] names)
    {
        var raw = Optional(row, index, names);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var decimalValue = ParseDecimal(raw);
        if (decimalValue is null)
        {
            return null;
        }

        return (int)decimal.Truncate(decimalValue.Value);
    }

    private static DateOnly? OptionalDate(string[] row, ColumnIndex index, params string[] names)
    {
        var raw = Optional(row, index, names);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var dateTime) ||
            DateTime.TryParse(
                raw,
                Belgian,
                DateTimeStyles.AllowWhiteSpaces,
                out dateTime))
        {
            return DateOnly.FromDateTime(dateTime);
        }

        return null;
    }

    private sealed class ColumnIndex
    {
        private readonly Dictionary<string, int> _map;

        public ColumnIndex(IReadOnlyList<string> headers)
        {
            _map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < headers.Count; i++)
            {
                // First occurrence wins for logical name lookups; duplicates use __N suffix.
                if (!_map.ContainsKey(headers[i]))
                {
                    _map[headers[i]] = i;
                }
            }
        }

        public bool TryGet(string name, out int index) => _map.TryGetValue(name, out index);
    }
}
