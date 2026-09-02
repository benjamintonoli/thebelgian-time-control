using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

public sealed class PlenionPayrollResourceReader(
    IOptions<PlenionOptions> options,
    ILogger<PlenionPayrollResourceReader> logger) : IPayrollResourceReader
{
    private readonly string _connectionString = options.Value.PlenionOdbc;

    public async Task<IReadOnlyList<PayrollEmployeeCandidate>> ReadCandidatesAsync(
        CancellationToken cancellationToken)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("Plenion ODBC connection string ontbreekt.");
        }

        const string sql = """
            SELECT IDRESOURCE, RESCODE, RESTYPE, OMSCHR, HFDGRP, PLOEG,
                   PASS_IDRIJKSREG, FUNCTIE, EMAIL, DATUMUITDIENST, SOORT
            FROM Resource
            ORDER BY OMSCHR
            """;

        var result = new List<PayrollEmployeeCandidate>();
        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identityRaw = OptionalString(reader, "PASS_IDRIJKSREG");
            result.Add(new PayrollEmployeeCandidate(
                RequiredString(reader, "IDRESOURCE"),
                OptionalString(reader, "RESCODE") ?? string.Empty,
                OptionalString(reader, "OMSCHR") ?? string.Empty,
                OptionalString(reader, "EMAIL"),
                OptionalString(reader, "RESTYPE"),
                OptionalString(reader, "HFDGRP"),
                OptionalString(reader, "PLOEG"),
                OptionalString(reader, "FUNCTIE"),
                ToInt32(reader, "SOORT"),
                OptionalDate(reader, "DATUMUITDIENST"),
                string.IsNullOrWhiteSpace(identityRaw)
                    ? AcertaIdentityStatus.Missing
                    : AcertaIdentityStatus.Present));
        }

        logger.LogInformation("{Count} payroll-kandidaten read-only uit Plenion gelezen.", result.Count);
        return result;
    }

    private static string RequiredString(DbDataReader reader, string column) =>
        Convert.ToString(reader[column], CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException($"Kolom {column} ontbreekt.");

    private static string? OptionalString(DbDataReader reader, string column) =>
        reader[column] is DBNull ? null : Convert.ToString(reader[column], CultureInfo.InvariantCulture);

    private static int? ToInt32(DbDataReader reader, string column) =>
        reader[column] is DBNull ? null : Convert.ToInt32(reader[column], CultureInfo.InvariantCulture);

    private static DateOnly? OptionalDate(DbDataReader reader, string column)
    {
        if (reader[column] is DBNull)
        {
            return null;
        }

        return DateOnly.FromDateTime(
            Convert.ToDateTime(reader[column], CultureInfo.InvariantCulture));
    }
}
