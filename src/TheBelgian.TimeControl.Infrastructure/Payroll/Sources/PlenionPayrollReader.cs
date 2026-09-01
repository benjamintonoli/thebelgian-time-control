using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Payroll.Interfaces;
using TheBelgian.TimeControl.Core.Payroll.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Infrastructure.Pilot;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

public sealed class PlenionPayrollReader(
    IOptions<PlenionOptions> options,
    ILogger<PlenionPayrollReader> logger) : IPayrollPerformanceSource
{
    private readonly string _connectionString = options.Value.PlenionOdbc;

    public async Task<IReadOnlyList<NormalizedPerformanceEntry>> ReadPerformancesAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();
        if (resourceIds.Count == 0)
        {
            throw new ArgumentException("Minstens één resourceId is vereist.", nameof(resourceIds));
        }

        var distinctIds = resourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (distinctIds.Length == 0)
        {
            throw new ArgumentException("Geen geldige resourceIds opgegeven.", nameof(resourceIds));
        }

        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var projectNumbers = await ReadProjectNumbersAsync(connection, cancellationToken);
        var rows = await ReadRawRowsAsync(
            connection,
            fromDate,
            throughDate,
            distinctIds,
            projectNumbers,
            cancellationToken);

        logger.LogInformation(
            "Plenion payroll source: {Count} prestaties gelezen voor {ResourceCount} resources ({From}..{Through}).",
            rows.Count,
            distinctIds.Length,
            fromDate,
            throughDate);

        var postcodes = await PlenionPostcodeResolver.ResolveForRowsAsync(
            connection,
            rows,
            cancellationToken);

        logger.LogInformation(
            "Plenion payroll postcodes: {Resolved}/{Total} resolved via batched lookup.",
            postcodes.Values.Count(result => result.IsResolved),
            rows.Count);

        return PayrollPerformanceMapper.MapMany(rows, postcodes);
    }

    public async Task<(IReadOnlyList<PlenionPayrollPerformanceRow> Rows, PostcodeCoverageSummary Coverage)>
        ReadRawRowsWithPostcodeCoverageAsync(
            DateOnly fromDate,
            DateOnly throughDate,
            IReadOnlyCollection<string> resourceIds,
            CancellationToken cancellationToken = default)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();
        var distinctIds = resourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var projectNumbers = await ReadProjectNumbersAsync(connection, cancellationToken);
        var rows = await ReadRawRowsAsync(
            connection,
            fromDate,
            throughDate,
            distinctIds,
            projectNumbers,
            cancellationToken);
        var postcodes = await PlenionPostcodeResolver.ResolveForRowsAsync(
            connection,
            rows,
            cancellationToken);
        var coverage = PlenionPostcodeResolver.SummarizeCoverage(rows, postcodes);
        return (rows, coverage);
    }

    public async Task<IReadOnlyList<PlenionPayrollPerformanceRow>> ReadRawRowsForDiagnosticsAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        IReadOnlyCollection<string> resourceIds,
        CancellationToken cancellationToken = default)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();
        var distinctIds = resourceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var projectNumbers = await ReadProjectNumbersAsync(connection, cancellationToken);
        return await ReadRawRowsAsync(
            connection,
            fromDate,
            throughDate,
            distinctIds,
            projectNumbers,
            cancellationToken);
    }

    public async Task<IReadOnlyList<HfdTaakDefinition>> ReadHfdTaakDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();
        const string sql = """
            SELECT IDHFDTAAK, CODE, OMSCHR
            FROM HFDTAAK
            ORDER BY IDHFDTAAK
            """;
        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        var definitions = new List<HfdTaakDefinition>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add(new HfdTaakDefinition(
                Convert.ToInt32(reader["IDHFDTAAK"], CultureInfo.InvariantCulture),
                PlenionPayrollFieldReader.OptionalText(reader["CODE"]),
                PlenionPayrollFieldReader.OptionalText(reader["OMSCHR"])));
        }

        return definitions;
    }

    public async Task<IReadOnlyDictionary<string, string>> VerifyResourceNamesAsync(
        IReadOnlyDictionary<string, string> expectedNamesById,
        CancellationToken cancellationToken = default)
    {
        OfflineOnlyGuard.EnsureLiveAccessAllowed("PlenionODBC");
        ValidateConfiguration();
        var verified = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = new OdbcConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var (resourceId, expectedName) in expectedNamesById.OrderBy(pair => pair.Key))
        {
            const string sql = """
                SELECT OMSCHR
                FROM Resource
                WHERE IDRESOURCE = ?
                """;
            await using var command = new OdbcCommand(sql, connection);
            command.Parameters.Add("id", OdbcType.VarChar).Value = resourceId;
            var actual = await command.ExecuteScalarAsync(cancellationToken);
            verified[resourceId] = PlenionPayrollFieldReader.OptionalText(actual) ?? "(missing)";
            if (!string.Equals(verified[resourceId], expectedName, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Resource {ResourceId} name mismatch: expected '{Expected}', actual '{Actual}'.",
                    resourceId,
                    expectedName,
                    verified[resourceId]);
            }
        }

        return verified;
    }

    internal static PlenionPayrollPerformanceRow MapReaderRow(
        DbDataReader reader,
        IReadOnlyDictionary<string, int?> projectNumbersByProjId)
    {
        var idProj = PlenionPayrollFieldReader.OptionalText(reader["IDPROJ"]);
        projectNumbersByProjId.TryGetValue(idProj ?? string.Empty, out var projNr);
        return new PlenionPayrollPerformanceRow(
            Convert.ToInt64(reader["IDPROJ_PREST"], CultureInfo.InvariantCulture),
            PlenionPayrollFieldReader.ParseDate(reader["DATUM"]),
            reader["VAN"],
            reader["TOT"],
            reader["PAUZE"],
            PlenionPayrollFieldReader.ParseAtl(reader["ATL"]),
            PlenionPayrollFieldReader.ParseOptionalDecimal(reader["KM"]),
            PlenionPayrollFieldReader.OptionalText(reader["IDRESOURCE"])
            ?? throw new InvalidDataException("IDRESOURCE ontbreekt."),
            idProj,
            PlenionPayrollFieldReader.ParseOptionalInt(reader["IDHFDTAAK"]),
            PlenionPayrollFieldReader.OptionalText(reader["BONNR"]),
            PlenionPayrollFieldReader.OptionalText(reader["OMSCHR"]),
            PlenionPayrollFieldReader.OptionalText(reader["MEMO"]),
            PlenionPayrollFieldReader.ParseOptionalDateTime(reader["DATCRE"]),
            projNr);
    }

    private static async Task<IReadOnlyList<PlenionPayrollPerformanceRow>> ReadRawRowsAsync(
        OdbcConnection connection,
        DateOnly fromDate,
        DateOnly throughDate,
        string[] resourceIds,
        IReadOnlyDictionary<string, int?> projectNumbersByProjId,
        CancellationToken cancellationToken)
    {
        var sql = BuildPerformanceSql(resourceIds);
        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("fromDate", OdbcType.Date).Value =
            fromDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("throughDate", OdbcType.Date).Value =
            throughDate.ToDateTime(TimeOnly.MinValue);
        for (var i = 0; i < resourceIds.Length; i++)
        {
            command.Parameters.Add($"resource{i}", OdbcType.VarChar).Value = resourceIds[i];
        }

        var rows = new List<PlenionPayrollPerformanceRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(MapReaderRow(reader, projectNumbersByProjId));
        }

        return rows;
    }

    private static string BuildPerformanceSql(string[] resourceIds)
    {
        var filter = new StringBuilder();
        for (var i = 0; i < resourceIds.Length; i++)
        {
            if (i > 0)
            {
                filter.Append(" OR ");
            }

            filter.Append("P.IDRESOURCE = ?");
        }

        return $"""
            SELECT P.IDPROJ_PREST, P.DATUM, P.VAN, P.TOT, P.PAUZE, P.ATL, P.KM,
                   P.IDRESOURCE, P.IDPROJ, P.IDHFDTAAK, P.BONNR, P.OMSCHR, P.MEMO, P.DATCRE
            FROM PROJ_Prest P
            WHERE P.DATUM >= ? AND P.DATUM <= ?
              AND ({filter})
            ORDER BY P.IDRESOURCE, P.DATUM, P.VAN, P.IDPROJ_PREST
            """;
    }

    private static async Task<IReadOnlyDictionary<string, int?>> ReadProjectNumbersAsync(
        OdbcConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT IDPROJ, PROJNR
            FROM PROJ
            """;
        var map = new Dictionary<string, int?>(StringComparer.Ordinal);
        try
        {
            await using var command = new OdbcCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = PlenionPayrollFieldReader.OptionalText(reader["IDPROJ"]);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                map[id] = PlenionPayrollFieldReader.ParseOptionalInt(reader["PROJNR"]);
            }
        }
        catch (Exception)
        {
            // Project lookup is optional for source parity; leave ProjectNumber null.
        }

        return map;
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "Plenion ODBC connection string ontbreekt (ConnectionStrings:PlenionOdbc).");
        }
    }
}
