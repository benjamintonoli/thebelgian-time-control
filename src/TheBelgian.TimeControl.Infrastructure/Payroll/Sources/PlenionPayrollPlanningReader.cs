using System.Data.Odbc;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Findings;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Payroll.Legacy;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

/// <summary>
/// SELECT-only KALENDER work-reservation reader for special-project findings.
/// Absence/standby types are classified but never treated as supporting work evidence.
/// </summary>
public sealed class PlenionPayrollPlanningReader(
    IOptions<PlenionOptions> options,
    ILogger<PlenionPayrollPlanningReader> logger) : IPayrollPlanningSource
{
    private readonly string _connectionString = options.Value.PlenionOdbc;
    public int LastQueryCount { get; private set; }

    public async Task<IReadOnlyList<PayrollPlanningReservation>> ReadWorkReservationsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("PlenionOdbc connection string ontbreekt.");
        }

        LastQueryCount = 0;
        var filter = resourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (filter.Count == 0)
        {
            return [];
        }

        return await ReadReservationsCoreAsync(fromDate, throughDate, filter, includeSharedAttendees: false, cancellationToken);
    }

    public Task<IReadOnlyList<PayrollPlanningReservation>> ReadSharedAttendeeReservationsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("PlenionOdbc connection string ontbreekt.");
        }

        LastQueryCount = 0;
        var filter = resourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (filter.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PayrollPlanningReservation>>([]);
        }

        return ReadReservationsCoreAsync(fromDate, throughDate, filter, includeSharedAttendees: true, cancellationToken);
    }

    private async Task<IReadOnlyList<PayrollPlanningReservation>> ReadReservationsCoreAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        HashSet<string> filter,
        bool includeSharedAttendees,
        CancellationToken cancellationToken)
    {
        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var projectNumbers = await ReadProjectNumbersAsync(connection, cancellationToken);
        LastQueryCount++;
        var typeNames = await ReadTaskTypeNamesAsync(connection, cancellationToken);
        LastQueryCount++;

        const string sql = """
            SELECT
                K.IDKALENDER,
                K.IDRESOURCE,
                K.RESOURCES,
                K.DATUM,
                K.DATUMTOT,
                K.UURVAN,
                K.UURTOT,
                K.IDTYPTAAK,
                K.IDPROJ,
                K.IDHFDTAAK,
                K.ONDERWERP,
                K.VOLLEDIGEDAG,
                K.CDATUM
            FROM KALENDER K
            WHERE K.GESCHRAPT = 0
              AND K.DATUM <= ?
              AND (K.DATUMTOT IS NULL OR K.DATUMTOT >= ?)
            ORDER BY K.IDKALENDER, K.DATUM
            """;

        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("throughDate", OdbcType.Date).Value = throughDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("fromDate", OdbcType.Date).Value = fromDate.ToDateTime(TimeOnly.MinValue);
        LastQueryCount++;

        var results = new List<PayrollPlanningReservation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var calendarRow = PlenionPayrollCalendarReader.MapReaderRow(reader);
            var projectId = PlenionPayrollFieldReader.OptionalText(reader["IDPROJ"]);
            int? projectNumber = null;
            if (!string.IsNullOrWhiteSpace(projectId)
                && projectNumbers.TryGetValue(projectId, out var mapped))
            {
                projectNumber = mapped;
            }

            var hfdTaak = reader["IDHFDTAAK"] is DBNull
                ? (int?)null
                : Convert.ToInt32(reader["IDHFDTAAK"], CultureInfo.InvariantCulture);
            var classification = PayrollPlanningClassifier.Classify(calendarRow.TaskTypeId);
            typeNames.TryGetValue(calendarRow.TaskTypeId, out var typeName);
            var attendees = LegacyCalendarSynthesis.ExpandResources(calendarRow);
            if (attendees.Count == 0)
            {
                continue;
            }

            var touchesFilter = attendees.Any(filter.Contains);
            if (!touchesFilter)
            {
                continue;
            }

            var emitResources = includeSharedAttendees
                ? attendees
                : attendees.Where(filter.Contains).ToArray();

            foreach (var resourceId in emitResources)
            {
                foreach (var date in ExpandDates(calendarRow, fromDate, throughDate))
                {
                    results.Add(new PayrollPlanningReservation(
                        calendarRow.IdCalendar,
                        resourceId,
                        date,
                        calendarRow.TimeFrom,
                        calendarRow.TimeTo,
                        calendarRow.TaskTypeId,
                        typeName,
                        projectId,
                        projectNumber,
                        hfdTaak,
                        calendarRow.Subject,
                        classification));
                }
            }
        }

        logger.LogInformation(
            "Plenion planning source: {Count} reservation rows for findings ({From}..{Through}), queries={Queries}, sharedAttendees={Shared}.",
            results.Count,
            fromDate,
            throughDate,
            LastQueryCount,
            includeSharedAttendees);

        return results;
    }

    private static IEnumerable<DateOnly> ExpandDates(
        PlenionCalendarRow row,
        DateOnly fromDate,
        DateOnly throughDate)
    {
        var start = row.DateFrom < fromDate ? fromDate : row.DateFrom;
        var end = row.DateTo ?? row.DateFrom;
        if (end > throughDate)
        {
            end = throughDate;
        }

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static async Task<Dictionary<string, int>> ReadProjectNumbersAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT IDPROJ, PROJNR FROM PROJ";
        await using var command = new OdbcCommand(sql, connection);
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Convert.ToString(reader["IDPROJ"], CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(id) || reader["PROJNR"] is DBNull)
            {
                continue;
            }

            map[id] = Convert.ToInt32(reader["PROJNR"], CultureInfo.InvariantCulture);
        }

        return map;
    }

    private static async Task<Dictionary<int, string>> ReadTaskTypeNamesAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT IDTYPTAAK, omschr FROM TYPTAAK";
        await using var command = new OdbcCommand(sql, connection);
        var map = new Dictionary<int, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Convert.ToInt32(reader["IDTYPTAAK"], CultureInfo.InvariantCulture);
            var name = Convert.ToString(reader["omschr"], CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(name))
            {
                map[id] = name;
            }
        }

        return map;
    }
}
