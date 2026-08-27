using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Pilot;

internal sealed class PilotPlenionReader(
    IOptions<PlenionOptions> options,
    ILogger<PilotPlenionReader> logger)
{
    private const int DefaultMaximumPerformances = 100;
    private const int AbsoluteMaximumPerformances = 500;
    private readonly string _connectionString = options.Value.PlenionOdbc;

    public async Task<PlenionPilotReadResult> ReadAsync(
        ReadOnlyPilotRequest request,
        CancellationToken cancellationToken)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();
        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var technician = await FindTechnicianAsync(
            connection,
            request.TechnicianQuery,
            cancellationToken);
        return await ReadPerformancesAsync(
            connection,
            technician,
            request.FromDate,
            request.ThroughDate,
            ResolveMaximumPerformances(request.MaximumPerformances),
            cancellationToken);
    }

    public async Task<IReadOnlyList<Technician>> ReadTechniciansWithPerformancesAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        CancellationToken cancellationToken)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();
        const string sql = """
            SELECT DISTINCT R.IDRESOURCE, R.RESCODE, R.OMSCHR
            FROM Resource R
            INNER JOIN PROJ_Prest P ON P.IDRESOURCE = R.IDRESOURCE
            WHERE R.SOORT = 1 AND P.DATUM >= ? AND P.DATUM <= ?
            ORDER BY R.OMSCHR, R.IDRESOURCE
            """;
        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("fromDate", OdbcType.Date).Value =
            fromDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("throughDate", OdbcType.Date).Value =
            throughDate.ToDateTime(TimeOnly.MinValue);
        var technicians = new List<Technician>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            technicians.Add(new Technician
            {
                ExternalId = RequiredText(reader, "IDRESOURCE"),
                Code = OptionalText(reader, "RESCODE") ?? string.Empty,
                Name = OptionalText(reader, "OMSCHR") ?? string.Empty,
                Kind = 1,
            });
        }

        return technicians;
    }

    public async Task<IReadOnlyList<PlenionCalendarAbsence>> ReadCalendarAbsencesAsync(
        IReadOnlySet<string> resourceIds,
        DateOnly fromDate,
        DateOnly throughDate,
        CancellationToken cancellationToken)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();
        const string sql = """
            SELECT IDKALENDER, IDResource, DATUM, DATUMTOT, UURVAN, UURTOT,
                   ONDERWERP, status, geschrapt
            FROM KALENDER
            WHERE DATUM <= ? AND (DATUMTOT IS NULL OR DATUMTOT >= ?)
              AND status = 255 AND geschrapt = 0
            ORDER BY IDResource, DATUM, IDKALENDER
            """;
        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("throughDate", OdbcType.Date).Value =
            throughDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("fromDate", OdbcType.Date).Value =
            fromDate.ToDateTime(TimeOnly.MinValue);
        var absences = new List<PlenionCalendarAbsence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var resourceId = RequiredText(reader, "IDResource");
            if (!resourceIds.Contains(resourceId))
            {
                continue;
            }

            var subject = OptionalText(reader, "ONDERWERP") ?? string.Empty;
            var kind = ClassifyCalendarAbsence(subject);
            if (kind is null)
            {
                continue;
            }

            var startDate = DateOnly.FromDateTime(
                Convert.ToDateTime(reader["DATUM"], CultureInfo.InvariantCulture));
            var endDate = reader["DATUMTOT"] is DBNull
                ? startDate
                : DateOnly.FromDateTime(
                    Convert.ToDateTime(reader["DATUMTOT"], CultureInfo.InvariantCulture));
            absences.Add(new PlenionCalendarAbsence(
                Convert.ToInt64(reader["IDKALENDER"], CultureInfo.InvariantCulture),
                resourceId,
                startDate,
                endDate,
                ReadCalendarTime(reader["UURVAN"], TimeOnly.MinValue),
                ReadCalendarTime(reader["UURTOT"], TimeOnly.MaxValue),
                kind.Value,
                subject));
        }

        return absences;
    }

    private static PlenionCalendarAbsenceKind? ClassifyCalendarAbsence(string subject)
    {
        if (subject.Contains("ziek", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("afwezig", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("arbeidsongeschikt", StringComparison.OrdinalIgnoreCase))
        {
            return PlenionCalendarAbsenceKind.Sickness;
        }

        return subject.Contains("verlof", StringComparison.OrdinalIgnoreCase)
            ? PlenionCalendarAbsenceKind.Leave
            : null;
    }

    private static TimeOnly ReadCalendarTime(object value, TimeOnly fallback) =>
        value switch
        {
            DBNull => fallback,
            TimeSpan time => TimeOnly.FromTimeSpan(time),
            DateTime dateTime => TimeOnly.FromDateTime(dateTime),
            _ when TimeOnly.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed) => parsed,
            _ => fallback,
        };

    private static async Task<Technician> FindTechnicianAsync(
        OdbcConnection connection,
        string query,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT IDRESOURCE, RESCODE, OMSCHR
            FROM Resource
            WHERE SOORT = 1
              AND (OMSCHR LIKE ? OR RESCODE = ? OR CAST(IDRESOURCE AS VARCHAR(32)) = ?)
            """;
        await using var command = new OdbcCommand(sql, connection);
        var trimmed = query.Trim();
        command.Parameters.Add("name", OdbcType.VarChar).Value = $"%{trimmed}%";
        command.Parameters.Add("code", OdbcType.VarChar).Value = trimmed;
        command.Parameters.Add("id", OdbcType.VarChar).Value = trimmed;
        var matches = new List<Technician>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add(new Technician
            {
                ExternalId = RequiredText(reader, "IDRESOURCE"),
                Code = RequiredText(reader, "RESCODE"),
                Name = RequiredText(reader, "OMSCHR"),
                Kind = 1,
            });
        }

        if (matches.Count > 1)
        {
            var exact = matches.Where(item =>
                    string.Equals(item.ExternalId, trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Code, trimmed, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => item.ExternalId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (exact.Length == 1)
            {
                return exact[0];
            }
        }

        return matches.Count switch
        {
            0 => throw new InvalidOperationException(
                "Geen medewerker gevonden voor de opgegeven naam of RESCODE."),
            1 => matches[0],
            _ => throw new InvalidOperationException(
                "De zoekterm vindt meerdere medewerkers; gebruik een unieke RESCODE."),
        };
    }

    private async Task<PlenionPilotReadResult> ReadPerformancesAsync(
        OdbcConnection connection,
        Technician technician,
        DateOnly fromDate,
        DateOnly throughDate,
        int maximumPerformances,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT IDPROJ_PREST, DATUM, VAN, TOT, PAUZE, KM, IDRESOURCE,
                   IDPROJ, IDHFDTAAK, BONNR, OMSCHR, OPMERKING
            FROM PROJ_Prest
            WHERE DATUM >= ? AND DATUM <= ? AND IDRESOURCE = ?
            ORDER BY DATUM, VAN, IDPROJ_PREST
            """;
        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("fromDate", OdbcType.Date).Value =
            fromDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("throughDate", OdbcType.Date).Value =
            throughDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("resourceId", OdbcType.VarChar).Value = technician.ExternalId;

        var rawRecords = new List<PilotRawRecord>();
        var normalized = new List<NormalizedPilotPerformance>();
        var issues = new List<PilotIssue>();
        var observations = new HashSet<string>(StringComparer.Ordinal);
        var readCount = 0;
        var rejectedCount = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            readCount++;
            if (readCount > maximumPerformances)
            {
                issues.Add(new PilotIssue(
                    "Plenion",
                    null,
                    "Onvoldoende gegevens",
                    $"De pilotlimiet van {maximumPerformances} prestaties is bereikt."));
                break;
            }

            var raw = CreateRawRecord(reader);
            rawRecords.Add(raw);
            try
            {
                var performance = Normalize(reader, observations);
                normalized.Add(performance);
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidCastException or InvalidDataException
                    or OverflowException or ArgumentOutOfRangeException)
            {
                rejectedCount++;
                issues.Add(new PilotIssue(
                    "Plenion",
                    raw.SourceId,
                    "Parseprobleem",
                    exception.Message));
            }
        }
        await reader.DisposeAsync();

        for (var index = 0; index < normalized.Count; index++)
        {
            normalized[index] = await EnrichPerformanceAsync(
                connection,
                normalized[index],
                issues,
                observations,
                cancellationToken);
        }

        logger.LogInformation(
            "Read-only Plenion-pilot las {ReadCount} prestaties; {RejectedCount} records afgewezen.",
            Math.Min(readCount, maximumPerformances),
            rejectedCount);
        return new PlenionPilotReadResult(
            technician,
            rawRecords,
            normalized,
            issues,
            observations.ToArray(),
            Math.Min(readCount, maximumPerformances),
            rejectedCount);
    }

    private static int ResolveMaximumPerformances(int? requested) =>
        Math.Clamp(requested ?? DefaultMaximumPerformances, 1, AbsoluteMaximumPerformances);

    private static async Task<NormalizedPilotPerformance> EnrichPerformanceAsync(
        OdbcConnection connection,
        NormalizedPilotPerformance performance,
        List<PilotIssue> issues,
        HashSet<string> observations,
        CancellationToken cancellationToken)
    {
        var projectCandidates = await ReadProjectCandidatesAsync(
            connection,
            performance.ProjectExternalId,
            cancellationToken);
        var workOrderCandidates = await ReadWorkOrderCandidatesAsync(
            connection,
            performance.ProjectExternalId,
            performance.WorkOrderNumber,
            cancellationToken);
        var joinNotes = new List<string>();
        ProjectCandidate? project = null;
        if (projectCandidates.Count == 1)
        {
            project = projectCandidates[0];
            joinNotes.Add("PROJ uniek gekoppeld via IDPROJ.");
        }
        else if (projectCandidates.Count > 1)
        {
            joinNotes.Add("PROJ-join ambigu.");
            issues.Add(new PilotIssue(
                "Plenion-join",
                performance.ExternalId.ToString(CultureInfo.InvariantCulture),
                "Ambiguous",
                $"{projectCandidates.Count} PROJ-records matchen hetzelfde IDPROJ."));
        }
        else
        {
            joinNotes.Add("Geen PROJ-record gevonden.");
        }

        WorkOrderCandidate? workOrder = null;
        if (workOrderCandidates.Count > 0)
        {
            var bestScore = workOrderCandidates.Max(candidate =>
                WorkOrderScore(candidate, performance));
            var bestCandidates = workOrderCandidates
                .Where(candidate =>
                    WorkOrderScore(candidate, performance) == bestScore)
                .ToArray();
            if (bestCandidates.Length == 1)
            {
                workOrder = bestCandidates[0];
                joinNotes.Add(
                    $"BON uniek gekoppeld ({WorkOrderScoreReason(workOrder, performance)}).");
            }
            else
            {
                joinNotes.Add("BON-join ambigu; geen record gekozen.");
                issues.Add(new PilotIssue(
                    "Plenion-join",
                    performance.ExternalId.ToString(CultureInfo.InvariantCulture),
                    "Ambiguous",
                    $"{bestCandidates.Length} BON-records delen de hoogste matchscore."));
            }
        }
        else
        {
            joinNotes.Add("Geen BON-record gevonden.");
        }

        var addressCandidates = await ReadAddressCandidatesAsync(
            connection,
            workOrder?.DeliveryAddressExternalId,
            cancellationToken);
        AddressCandidate? address = null;
        if (addressCandidates.Count == 1)
        {
            address = addressCandidates[0];
            joinNotes.Add("LEVADR uniek gekoppeld via LACLEUNIK.");
        }
        else if (addressCandidates.Count > 1)
        {
            joinNotes.Add("LEVADR-join ambigu; geen adres gekozen.");
            issues.Add(new PilotIssue(
                "Plenion-join",
                performance.ExternalId.ToString(CultureInfo.InvariantCulture),
                "Ambiguous",
                $"{addressCandidates.Count} LEVADR-records matchen hetzelfde LACLEUNIK."));
        }
        else if (workOrder is not null)
        {
            joinNotes.Add("Geen LEVADR-record gevonden voor BON.LACLEUNIK.");
        }

        observations.Add(
            "Plenion-verrijking gebruikt afzonderlijke begrensde lookups; ambigue topmatches worden niet gekozen.");
        return performance with
        {
            ProjectNumber = project?.Number,
            ProjectName = project?.Name,
            DeliveryAddressExternalId = workOrder?.DeliveryAddressExternalId,
            CustomerOrSiteName = address?.Name,
            Street = address?.Street,
            PostalCode = address?.PostalCode,
            City = address?.City,
            Country = address?.Country,
            ProjectCandidateCount = projectCandidates.Count,
            WorkOrderCandidateCount = workOrderCandidates.Count,
            AddressCandidateCount = addressCandidates.Count,
            JoinAssessment = string.Join(" ", joinNotes),
        };
    }

    private static async Task<List<ProjectCandidate>> ReadProjectCandidatesAsync(
        OdbcConnection connection,
        string? projectId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return [];
        }

        const string sql = """
            SELECT IDPROJ, PROJNR, NAAM
            FROM PROJ
            WHERE IDPROJ = ?
            """;
        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("projectId", OdbcType.VarChar).Value = projectId;
        var result = new List<ProjectCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ProjectCandidate(
                RequiredText(reader, "IDPROJ"),
                OptionalText(reader, "PROJNR"),
                OptionalText(reader, "NAAM")));
        }

        return result;
    }

    private static async Task<List<WorkOrderCandidate>> ReadWorkOrderCandidatesAsync(
        OdbcConnection connection,
        string? projectId,
        string? workOrderNumber,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId) &&
            string.IsNullOrWhiteSpace(workOrderNumber))
        {
            return [];
        }

        string sql;
        await using var command = new OdbcCommand();
        command.Connection = connection;
        if (!string.IsNullOrWhiteSpace(projectId) &&
            !string.IsNullOrWhiteSpace(workOrderNumber))
        {
            sql = """
                SELECT BOCLEUNIK, BONNR, IDPROJ, LACLEUNIK
                FROM BON
                WHERE BONNR = ? OR IDPROJ = ?
                """;
            command.Parameters.Add("workOrderNumber", OdbcType.VarChar).Value =
                workOrderNumber;
            command.Parameters.Add("projectId", OdbcType.VarChar).Value = projectId;
        }
        else if (!string.IsNullOrWhiteSpace(workOrderNumber))
        {
            sql = """
                SELECT BOCLEUNIK, BONNR, IDPROJ, LACLEUNIK
                FROM BON
                WHERE BONNR = ?
                """;
            command.Parameters.Add("workOrderNumber", OdbcType.VarChar).Value =
                workOrderNumber;
        }
        else
        {
            sql = """
                SELECT BOCLEUNIK, BONNR, IDPROJ, LACLEUNIK
                FROM BON
                WHERE IDPROJ = ?
                """;
            command.Parameters.Add("projectId", OdbcType.VarChar).Value = projectId!;
        }

        command.CommandText = sql;
        var result = new List<WorkOrderCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorkOrderCandidate(
                RequiredText(reader, "BOCLEUNIK"),
                OptionalText(reader, "BONNR"),
                OptionalText(reader, "IDPROJ"),
                OptionalText(reader, "LACLEUNIK")));
        }

        return result;
    }

    private static async Task<List<AddressCandidate>> ReadAddressCandidatesAsync(
        OdbcConnection connection,
        string? addressId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(addressId))
        {
            return [];
        }

        const string sql = """
            SELECT LACLEUNIK, LANAAM, LAADRES, LAPOST, LAWNPL, LALAND
            FROM LEVADR
            WHERE LACLEUNIK = ?
            """;
        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("addressId", OdbcType.VarChar).Value = addressId;
        var result = new List<AddressCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AddressCandidate(
                RequiredText(reader, "LACLEUNIK"),
                OptionalText(reader, "LANAAM"),
                OptionalText(reader, "LAADRES"),
                OptionalText(reader, "LAPOST"),
                OptionalText(reader, "LAWNPL"),
                OptionalText(reader, "LALAND")));
        }

        return result;
    }

    private static int WorkOrderScore(
        WorkOrderCandidate candidate,
        NormalizedPilotPerformance performance)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(performance.WorkOrderNumber) &&
            performance.WorkOrderNumber.Equals(
                candidate.Number,
                StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (!string.IsNullOrWhiteSpace(performance.ProjectExternalId) &&
            performance.ProjectExternalId.Equals(
                candidate.ProjectId,
                StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        return score;
    }

    private static string WorkOrderScoreReason(
        WorkOrderCandidate candidate,
        NormalizedPilotPerformance performance)
    {
        var numberMatches =
            performance.WorkOrderNumber?.Equals(
                candidate.Number,
                StringComparison.OrdinalIgnoreCase) == true;
        var projectMatches =
            performance.ProjectExternalId?.Equals(
                candidate.ProjectId,
                StringComparison.OrdinalIgnoreCase) == true;
        return (numberMatches, projectMatches) switch
        {
            (true, true) => "BONNR en IDPROJ",
            (true, false) => "BONNR",
            (false, true) => "IDPROJ",
            _ => "geen sleutelovereenkomst",
        };
    }

    private static NormalizedPilotPerformance Normalize(
        DbDataReader reader,
        ISet<string> observations)
    {
        var id = Convert.ToInt64(reader["IDPROJ_PREST"], CultureInfo.InvariantCulture);
        var date = ParseDate(reader["DATUM"]);
        var start = ParseTime(date, reader["VAN"], "VAN", observations);
        var end = ParseTime(date, reader["TOT"], "TOT", observations);
        var normalization = new List<string>();
        if (end < start)
        {
            end = end.AddDays(1);
            normalization.Add("TOT lag vóór VAN en is als volgende kalenderdag geïnterpreteerd.");
        }

        var grossMinutes = CheckedMinutes(end - start, "VAN/TOT");
        var pauseMinutes = ParseDuration(
            reader["PAUZE"],
            "PAUZE",
            grossMinutes,
            observations,
            normalization);
        if (pauseMinutes > grossMinutes)
        {
            throw new InvalidDataException(
                $"PAUZE ({pauseMinutes} min) is groter dan bruto tijd ({grossMinutes} min).");
        }

        var kilometres = ParseDecimal(reader["KM"], "KM", observations);
        normalization.Add("KM is op basis van de Plenion-veldnaam als kilometer geïnterpreteerd.");
        return new NormalizedPilotPerformance(
            id,
            RequiredText(reader, "IDRESOURCE"),
            date,
            start,
            end,
            pauseMinutes,
            grossMinutes,
            grossMinutes - pauseMinutes,
            kilometres,
            OptionalText(reader, "IDPROJ"),
            OptionalText(reader, "IDHFDTAAK"),
            OptionalText(reader, "BONNR"),
            OptionalText(reader, "OMSCHR"),
            OptionalText(reader, "OPMERKING"),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            "Nog niet verrijkt.",
            string.Join(" ", normalization));
    }

    private static PilotRawRecord CreateRawRecord(DbDataReader reader)
    {
        var fields = new Dictionary<string, PilotRawValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in FieldNames)
        {
            var ordinal = reader.GetOrdinal(name);
            var value = reader.GetValue(ordinal);
            var sourceType = reader.GetFieldType(ordinal).FullName ?? reader.GetDataTypeName(ordinal);
            fields[name] = new PilotRawValue(
                value is DBNull
                    ? null
                    : Convert.ToString(value, CultureInfo.InvariantCulture),
                $"{reader.GetDataTypeName(ordinal)} / {sourceType}");
        }

        return new PilotRawRecord(
            fields["IDPROJ_PREST"].Text ?? "(zonder ID)",
            fields);
    }

    private static DateOnly ParseDate(object value) => value switch
    {
        DateTime dateTime => DateOnly.FromDateTime(dateTime),
        _ when DateOnly.TryParse(
            Convert.ToString(value, CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed) => parsed,
        _ => throw new InvalidDataException("DATUM heeft een onbekend formaat."),
    };

    private static DateTimeOffset ParseTime(
        DateOnly date,
        object value,
        string field,
        ISet<string> observations)
    {
        TimeOnly time;
        string strategy;
        switch (value)
        {
            case TimeSpan timeSpan:
                time = TimeOnly.FromTimeSpan(timeSpan);
                strategy = "TimeSpan";
                break;
            case DateTime dateTime:
                time = TimeOnly.FromDateTime(dateTime);
                strategy = "DateTime.TimeOfDay";
                break;
            default:
                var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!TimeOnly.TryParse(
                        text,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out time))
                {
                    throw new InvalidDataException($"{field} heeft een onbekend tijdformaat.");
                }

                strategy = "tekst als TimeOnly";
                break;
        }

        observations.Add(
            $"Plenion {field}: CLR-type {value.GetType().Name}, geïnterpreteerd via {strategy}.");
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static int ParseDuration(
        object value,
        string field,
        int grossMinutes,
        ISet<string> observations,
        List<string> normalization)
    {
        if (value is DBNull)
        {
            observations.Add($"Plenion {field}: NULL is als 0 minuten geïnterpreteerd.");
            return 0;
        }

        if (value is TimeSpan timeSpan)
        {
            observations.Add($"Plenion {field}: TimeSpan, rechtstreeks naar minuten.");
            return CheckedMinutes(timeSpan, field);
        }

        if (value is DateTime dateTime)
        {
            observations.Add($"Plenion {field}: DateTime.TimeOfDay, naar minuten.");
            return CheckedMinutes(dateTime.TimeOfDay, field);
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (text.Contains(':', StringComparison.Ordinal) &&
            TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsedTimeSpan))
        {
            observations.Add($"Plenion {field}: tekstduur, als TimeSpan naar minuten.");
            return CheckedMinutes(parsedTimeSpan, field);
        }

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var numeric))
        {
            throw new InvalidDataException($"{field} heeft een onbekend duurformaat.");
        }

        int minutes;
        if (numeric != decimal.Truncate(numeric) && numeric is >= 0 and <= 24)
        {
            minutes = checked((int)Math.Round(numeric * 60, MidpointRounding.AwayFromZero));
            normalization.Add($"{field} decimale waarde {numeric} is als uren geïnterpreteerd.");
            observations.Add($"Plenion {field}: decimaal getal met fractie, uren × 60.");
        }
        else
        {
            minutes = checked((int)numeric);
            normalization.Add($"{field} gehele numerieke waarde {numeric} is als minuten geïnterpreteerd.");
            observations.Add($"Plenion {field}: geheel numeriek getal, als minuten.");
        }

        if (minutes < 0 || minutes > Math.Max(grossMinutes, 24 * 60))
        {
            throw new InvalidDataException($"{field} levert een onwaarschijnlijke duur op.");
        }

        return minutes;
    }

    private static decimal ParseDecimal(
        object value,
        string field,
        ISet<string> observations)
    {
        if (value is DBNull)
        {
            observations.Add($"Plenion {field}: NULL is als 0 geïnterpreteerd.");
            return 0;
        }

        try
        {
            var result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            observations.Add($"Plenion {field}: CLR-type {value.GetType().Name}, als decimal.");
            return result;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            throw new InvalidDataException($"{field} is niet numeriek.", exception);
        }
    }

    private static int CheckedMinutes(TimeSpan value, string field)
    {
        var result = (int)Math.Round(value.TotalMinutes, MidpointRounding.AwayFromZero);
        return result is >= 0 and <= 24 * 60
            ? result
            : throw new InvalidDataException($"{field} levert een onwaarschijnlijke duur op.");
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:PlenionOdbc ontbreekt; er is geen verbinding gemaakt.");
        }
    }

    private static string RequiredText(DbDataReader reader, string name) =>
        OptionalText(reader, name)
        ?? throw new InvalidDataException($"{name} ontbreekt in het Plenion-record.");

    private static string? OptionalText(DbDataReader reader, string name) =>
        reader[name] is DBNull
            ? null
            : Convert.ToString(reader[name], CultureInfo.InvariantCulture)?.Trim();

    private static readonly string[] FieldNames =
    [
        "IDPROJ_PREST",
        "DATUM",
        "VAN",
        "TOT",
        "PAUZE",
        "KM",
        "IDRESOURCE",
        "IDPROJ",
        "IDHFDTAAK",
        "BONNR",
        "OMSCHR",
        "OPMERKING",
    ];
}

internal sealed record PlenionPilotReadResult(
    Technician Technician,
    IReadOnlyList<PilotRawRecord> RawRecords,
    IReadOnlyList<NormalizedPilotPerformance> NormalizedRecords,
    IReadOnlyList<PilotIssue> Issues,
    IReadOnlyList<string> Observations,
    int ReadCount,
    int RejectedCount);

internal sealed record ProjectCandidate(
    string Id,
    string? Number,
    string? Name);

internal sealed record WorkOrderCandidate(
    string Id,
    string? Number,
    string? ProjectId,
    string? DeliveryAddressExternalId);

internal sealed record AddressCandidate(
    string Id,
    string? Name,
    string? Street,
    string? PostalCode,
    string? City,
    string? Country);
