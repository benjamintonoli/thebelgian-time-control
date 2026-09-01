using System.Data.Odbc;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

public sealed class PlenionPayrollCalendarReader(
    IOptions<PlenionOptions> options,
    ILogger<PlenionPayrollCalendarReader> logger) : IPayrollCalendarSource
{
    private readonly string _connectionString = options.Value.PlenionOdbc;

    public async Task<IReadOnlyList<PlenionCalendarRow>> ReadCalendarRowsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        CancellationToken cancellationToken = default)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();

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
                K.VOLLEDIGEDAG,
                K.ONDERWERP,
                K.CDATUM
            FROM KALENDER K
            WHERE K.GESCHRAPT = 0
              AND K.IDTYPTAAK IN (3, 5, 8)
              AND K.DATUM <= ?
              AND (K.DATUMTOT IS NULL OR K.DATUMTOT >= ?)
            ORDER BY K.IDKALENDER, K.DATUM
            """;

        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("throughDate", OdbcType.Date).Value = throughDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("fromDate", OdbcType.Date).Value = fromDate.ToDateTime(TimeOnly.MinValue);

        var rows = new List<PlenionCalendarRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(MapReaderRow(reader));
        }

        logger.LogInformation(
            "Plenion calendar source: {Count} KALENDER rows gelezen ({From}..{Through}).",
            rows.Count,
            fromDate,
            throughDate);

        return rows;
    }

    internal static PlenionCalendarRow MapReaderRow(System.Data.Common.DbDataReader reader) =>
        new(
            Convert.ToInt64(reader["IDKALENDER"], CultureInfo.InvariantCulture),
            PlenionPayrollFieldReader.OptionalText(reader["IDRESOURCE"]),
            PlenionPayrollFieldReader.OptionalText(reader["RESOURCES"]),
            PlenionPayrollFieldReader.ParseDate(reader["DATUM"]),
            PlenionPayrollFieldReader.ParseOptionalDateOnly(reader["DATUMTOT"]),
            PlenionPayrollFieldReader.ParseOptionalTimeOnly(reader["UURVAN"]),
            PlenionPayrollFieldReader.ParseOptionalTimeOnly(reader["UURTOT"]),
            Convert.ToInt32(reader["IDTYPTAAK"], CultureInfo.InvariantCulture),
            PlenionPayrollFieldReader.OptionalText(reader["VOLLEDIGEDAG"]),
            PlenionPayrollFieldReader.OptionalText(reader["ONDERWERP"]),
            PlenionPayrollFieldReader.ParseOptionalDateTime(reader["CDATUM"]));

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("PlenionOdbc connection string ontbreekt.");
        }
    }
}
