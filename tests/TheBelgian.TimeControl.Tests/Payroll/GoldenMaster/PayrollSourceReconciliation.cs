using System.Globalization;

namespace TheBelgian.TimeControl.Tests.Payroll.GoldenMaster;

public enum PerformanceSourceMatchClassification
{
    Exact,
    RepresentationOnly,
    SourceChanged,
    MissingInPlenion,
    MissingInGoldenMaster,
    Unexplained,
}

public sealed record PerformanceSourceComparison(
    long PerformanceId,
    PerformanceSourceMatchClassification Classification,
    IReadOnlyList<string> FieldDifferences);

public sealed record ResourceSourceReconciliation(
    string ResourceName,
    string ResourceId,
    int GoldenMasterRowCount,
    int PlenionRowCount,
    int MatchedIds,
    int ExactMatches,
    int RepresentationOnlyMatches,
    int SourceChangedMatches,
    int MissingInPlenion,
    int MissingInGoldenMaster,
    int Unexplained,
    decimal GoldenMasterAtlHours,
    decimal PlenionAtlHours,
    decimal AtlDifferenceHours,
    IReadOnlyList<PerformanceSourceComparison> Comparisons);

public sealed record TravelSourceDiagnostics(
    string ResourceName,
    int TotalRows,
    int TravelRows,
    int WorkDays,
    int DaysWithTravel,
    int DaysWorkNoTravel);

public sealed record StandbySourceDiagnostics(
    string ResourceName,
    int StandbyRows,
    decimal StandbyAtlHours,
    int DaysWithStandby,
    int StandbyDaysWithTravel);

public static class PayrollSourceReconciliation
{
    public static ResourceSourceReconciliation ReconcileResource(
        string resourceName,
        string resourceId,
        IReadOnlyList<PowerBiDetailRow> goldenMasterRows,
        IReadOnlyList<PlenionSourceRow> plenionRows)
    {
        var pbi = goldenMasterRows
            .Where(row => string.Equals(row.ResourceId, resourceId, StringComparison.Ordinal))
            .ToList();
        var plenion = plenionRows
            .Where(row => string.Equals(row.ResourceId, resourceId, StringComparison.Ordinal))
            .ToList();

        var pbiById = pbi
            .Where(row => TryParseId(row.PerformanceId, out _))
            .ToDictionary(row => ParseId(row.PerformanceId!), row => row);
        var plenionById = plenion.ToDictionary(row => row.PerformanceId, row => row);

        var comparisons = new List<PerformanceSourceComparison>();
        var exact = 0;
        var representationOnly = 0;
        var sourceChanged = 0;
        var unexplained = 0;

        foreach (var id in pbiById.Keys.Union(plenionById.Keys).OrderBy(id => id))
        {
            var hasPbi = pbiById.TryGetValue(id, out var pbiRow);
            var hasPlenion = plenionById.TryGetValue(id, out var plenionRow);
            if (!hasPbi)
            {
                comparisons.Add(new PerformanceSourceComparison(
                    id,
                    PerformanceSourceMatchClassification.MissingInGoldenMaster,
                    []));
                continue;
            }

            if (!hasPlenion)
            {
                comparisons.Add(new PerformanceSourceComparison(
                    id,
                    PerformanceSourceMatchClassification.MissingInPlenion,
                    []));
                continue;
            }

            var (classification, differences) = CompareRows(pbiRow!, plenionRow!);
            comparisons.Add(new PerformanceSourceComparison(id, classification, differences));
            switch (classification)
            {
                case PerformanceSourceMatchClassification.Exact:
                    exact++;
                    break;
                case PerformanceSourceMatchClassification.RepresentationOnly:
                    representationOnly++;
                    break;
                case PerformanceSourceMatchClassification.SourceChanged:
                    sourceChanged++;
                    break;
                default:
                    unexplained++;
                    break;
            }
        }

        return new ResourceSourceReconciliation(
            resourceName,
            resourceId,
            pbi.Count,
            plenion.Count,
            comparisons.Count(row =>
                row.Classification is not PerformanceSourceMatchClassification.MissingInGoldenMaster
                    and not PerformanceSourceMatchClassification.MissingInPlenion),
            exact,
            representationOnly,
            sourceChanged,
            comparisons.Count(row => row.Classification == PerformanceSourceMatchClassification.MissingInPlenion),
            comparisons.Count(row => row.Classification == PerformanceSourceMatchClassification.MissingInGoldenMaster),
            unexplained,
            pbi.Sum(row => row.AtlHours ?? 0m),
            plenion.Sum(row => row.AtlHoursRaw),
            plenion.Sum(row => row.AtlHoursRaw) - pbi.Sum(row => row.AtlHours ?? 0m),
            comparisons);
    }

    public static TravelSourceDiagnostics BuildTravelDiagnostics(
        string resourceName,
        IReadOnlyList<PlenionSourceRow> rows)
    {
        const int travelHfdTaak = 5;
        const int absenceLeave = 10;
        const int absenceNzGe = 18;
        const int standby = 23;

        var byDay = rows
            .GroupBy(row => row.Date)
            .ToList();
        var daysWithTravel = byDay.Count(day =>
            day.Any(row => row.HfdTaakId == travelHfdTaak));
        var daysWorkNoTravel = byDay.Count(day =>
            day.Any(row => IsWorkRow(row.HfdTaakId, travelHfdTaak, absenceLeave, absenceNzGe, standby)) &&
            day.All(row => row.HfdTaakId != travelHfdTaak));
        var workDays = byDay.Count(day =>
            day.Any(row => IsWorkRow(row.HfdTaakId, travelHfdTaak, absenceLeave, absenceNzGe, standby)));

        return new TravelSourceDiagnostics(
            resourceName,
            rows.Count,
            rows.Count(row => row.HfdTaakId == travelHfdTaak),
            workDays,
            daysWithTravel,
            daysWorkNoTravel);
    }

    private static bool IsWorkRow(int? hfdTaakId, int travel, int leave, int nzGe, int standby) =>
        hfdTaakId is not null &&
        hfdTaakId.Value != travel &&
        hfdTaakId.Value != leave &&
        hfdTaakId.Value != nzGe &&
        hfdTaakId.Value != standby;

    public static StandbySourceDiagnostics BuildStandbyDiagnostics(
        string resourceName,
        IReadOnlyList<PlenionSourceRow> rows)
    {
        const int travelHfdTaak = 5;
        const int standbyHfdTaak = 23;
        var standbyRows = rows.Where(row => row.HfdTaakId == standbyHfdTaak).ToList();
        var standbyDays = standbyRows.Select(row => row.Date).Distinct().ToList();
        var standbyDaysWithTravel = standbyDays.Count(day =>
            rows.Any(row => row.Date == day && row.HfdTaakId == travelHfdTaak));

        return new StandbySourceDiagnostics(
            resourceName,
            standbyRows.Count,
            standbyRows.Sum(row => row.AtlHoursRaw),
            standbyDays.Count,
            standbyDaysWithTravel);
    }

    private static (PerformanceSourceMatchClassification, IReadOnlyList<string>) CompareRows(
        PowerBiDetailRow pbi,
        PlenionSourceRow plenion)
    {
        var differences = new List<string>();

        CompareField(differences, "DATUM", pbi.Date, plenion.Date);
        CompareField(differences, "IDRESOURCE", pbi.ResourceId, plenion.ResourceId);
        CompareField(differences, "IDHFDTAAK", pbi.HfdTaakId, plenion.HfdTaakId);
        CompareField(differences, "ATL", pbi.AtlHours, plenion.AtlHoursRaw);

        var pbiVan = NormalizeClock(pbi.VanRaw);
        var pbiTot = NormalizeClock(pbi.TotRaw);
        if (!ClockEquivalent(pbiVan, plenion.VanClock))
        {
            differences.Add($"VAN: pbi={pbiVan} plenion={plenion.VanClock}");
        }

        if (!ClockEquivalent(pbiTot, plenion.TotClock))
        {
            differences.Add($"TOT: pbi={pbiTot} plenion={plenion.TotClock}");
        }

        if (PauseComparable(pbi.PauseRaw, plenion.PauseMinutes))
        {
            // comparable
        }
        else if (pbi.PauseRaw is not null || plenion.PauseMinutes is not null)
        {
            differences.Add($"PAUZE: pbi={pbi.PauseRaw} plenionMinutes={plenion.PauseMinutes}");
        }

        if (differences.Count == 0)
        {
            return (PerformanceSourceMatchClassification.Exact, differences);
        }

        if (differences.All(IsRepresentationOnlyDifference))
        {
            return (PerformanceSourceMatchClassification.RepresentationOnly, differences);
        }

        if (differences.All(diff =>
                diff.StartsWith("VAN:", StringComparison.Ordinal) ||
                diff.StartsWith("TOT:", StringComparison.Ordinal) ||
                diff.StartsWith("ATL:", StringComparison.Ordinal) ||
                diff.StartsWith("PAUZE:", StringComparison.Ordinal)))
        {
            return (PerformanceSourceMatchClassification.SourceChanged, differences);
        }

        return (PerformanceSourceMatchClassification.Unexplained, differences);
    }

    private static bool IsRepresentationOnlyDifference(string difference) =>
        difference.StartsWith("VAN:", StringComparison.Ordinal) ||
        difference.StartsWith("TOT:", StringComparison.Ordinal) ||
        difference.StartsWith("PAUZE:", StringComparison.Ordinal);

    private static void CompareField<T>(
        List<string> differences,
        string field,
        T? expected,
        T? actual)
        where T : struct, IEquatable<T>
    {
        if (!Nullable.Equals(expected, actual))
        {
            differences.Add($"{field}: pbi={expected} plenion={actual}");
        }
    }

    private static void CompareField(
        List<string> differences,
        string field,
        decimal? expected,
        decimal? actual)
    {
        if (expected is null && actual is null)
        {
            return;
        }

        if (expected is null || actual is null || expected.Value != actual.Value)
        {
            differences.Add($"{field}: pbi={expected} plenion={actual}");
        }
    }

    private static void CompareField(
        List<string> differences,
        string field,
        string? expected,
        string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            differences.Add($"{field}: pbi={expected} plenion={actual}");
        }
    }

    private static bool PauseComparable(string? pbiPause, decimal? plenionMinutes)
    {
        if (string.IsNullOrWhiteSpace(pbiPause) && plenionMinutes is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(pbiPause))
        {
            return plenionMinutes == 0m;
        }

        if (!TryParsePauseMinutes(pbiPause, out var pbiMinutes))
        {
            return false;
        }

        return pbiMinutes == plenionMinutes;
    }

    private static bool TryParsePauseMinutes(string raw, out decimal minutes)
    {
        minutes = 0m;
        if (DateTime.TryParse(raw, out var dateTime))
        {
            minutes = (decimal)dateTime.TimeOfDay.TotalMinutes;
            return true;
        }

        return decimal.TryParse(raw, out minutes);
    }

    private static string? NormalizeClock(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTime.TryParse(raw, out var dateTime))
        {
            return dateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return raw.Trim();
    }

    private static bool ClockEquivalent(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryParseId(string? value, out long id)
    {
        id = 0;
        return !string.IsNullOrWhiteSpace(value) &&
               long.TryParse(value.Trim().Trim('"'), out id);
    }

    private static long ParseId(string value) =>
        long.Parse(value.Trim().Trim('"'), CultureInfo.InvariantCulture);
}

public sealed record PlenionSourceRow(
    long PerformanceId,
    string ResourceId,
    DateOnly Date,
    string? VanClock,
    string? TotClock,
    decimal AtlHoursRaw,
    decimal? Km,
    int? HfdTaakId,
    string? BonNr,
    decimal? PauseMinutes);
