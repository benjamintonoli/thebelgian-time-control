using System.Data.Odbc;
using System.Globalization;
using System.Text;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Sources;

/// <summary>
/// Resolves postcodes for payroll performances using a bounded number of batched SELECT queries.
/// Precedence: BON→LEVADR, PROJ.A_PC, PROJ.LACLEUNIK→LEVADR.
/// </summary>
public static class PlenionPostcodeResolver
{
    private const int BatchSize = 200;

    public static async Task<IReadOnlyDictionary<long, PostcodeResolutionResult>> ResolveForRowsAsync(
        OdbcConnection connection,
        IReadOnlyList<PlenionPayrollPerformanceRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return new Dictionary<long, PostcodeResolutionResult>();
        }

        var uniqueBonNumbers = rows
            .Select(row => row.BonNr)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var uniqueProjectIds = rows
            .Select(row => row.IdProj)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var bonPostcodes = await ReadBonPostcodesAsync(connection, uniqueBonNumbers, cancellationToken);
        var projectFallbacks = await ReadProjectFallbacksAsync(connection, uniqueProjectIds, cancellationToken);

        var resolved = new Dictionary<long, PostcodeResolutionResult>(rows.Count);
        foreach (var row in rows)
        {
            resolved[row.IdProjPrest] = ResolveRow(row, bonPostcodes, projectFallbacks);
        }

        return resolved;
    }

    public static PostcodeResolutionResult ResolveRow(
        PlenionPayrollPerformanceRow row,
        IReadOnlyDictionary<string, string> bonPostcodes,
        IReadOnlyDictionary<string, ProjectPostcodeFallback> projectFallbacks)
    {
        if (!string.IsNullOrWhiteSpace(row.BonNr))
        {
            var bonKey = row.BonNr.Trim();
            if (bonPostcodes.TryGetValue(bonKey, out var bonPostcode))
            {
                var normalized = PostcodeNormalizer.TryNormalize(bonPostcode);
                if (normalized is not null)
                {
                    return new PostcodeResolutionResult(normalized, PostcodeResolutionSource.BonDeliveryAddress);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(row.IdProj))
        {
            var projectKey = row.IdProj.Trim();
            if (projectFallbacks.TryGetValue(projectKey, out var fallback))
            {
                var fromProjectPostal = PostcodeNormalizer.TryNormalize(fallback.ProjectPostalCode);
                if (fromProjectPostal is not null)
                {
                    return new PostcodeResolutionResult(
                        fromProjectPostal,
                        PostcodeResolutionSource.ProjectPostalCode);
                }

                var fromProjectDelivery = PostcodeNormalizer.TryNormalize(fallback.ProjectDeliveryPostcode);
                if (fromProjectDelivery is not null)
                {
                    return new PostcodeResolutionResult(
                        fromProjectDelivery,
                        PostcodeResolutionSource.ProjectDeliveryAddress);
                }
            }
        }

        return PostcodeResolutionResult.Unresolved;
    }

    public static PostcodeCoverageSummary SummarizeCoverage(
        IReadOnlyList<PlenionPayrollPerformanceRow> rows,
        IReadOnlyDictionary<long, PostcodeResolutionResult> resolutions)
    {
        var summary = new PostcodeCoverageSummary();
        foreach (var row in rows)
        {
            summary.TotalRows++;
            if (!resolutions.TryGetValue(row.IdProjPrest, out var resolution))
            {
                summary.UnresolvedRows++;
                continue;
            }

            switch (resolution.Source)
            {
                case PostcodeResolutionSource.BonDeliveryAddress:
                    summary.BonDeliveryRows++;
                    break;
                case PostcodeResolutionSource.ProjectPostalCode:
                    summary.ProjectPostalCodeRows++;
                    break;
                case PostcodeResolutionSource.ProjectDeliveryAddress:
                    summary.ProjectDeliveryAddressRows++;
                    break;
                default:
                    summary.UnresolvedRows++;
                    break;
            }

            if (resolution.IsResolved && resolution.Postcode is null)
            {
                summary.InvalidSourceRows++;
            }
        }

        return summary;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadBonPostcodesAsync(
        OdbcConnection connection,
        string[] bonNumbers,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var batch in bonNumbers.Chunk(BatchSize))
        {
            var sql = BuildBonSql(batch);
            await using var command = new OdbcCommand(sql, connection);
            for (var i = 0; i < batch.Length; i++)
            {
                command.Parameters.Add($"bon{i}", OdbcType.VarChar).Value = batch[i];
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var bonNr = PlenionPayrollFieldReader.OptionalText(reader["BONNR"]);
                var lapost = PlenionPayrollFieldReader.OptionalText(reader["LAPOST"]);
                if (string.IsNullOrWhiteSpace(bonNr) || string.IsNullOrWhiteSpace(lapost))
                {
                    continue;
                }

                map[bonNr.Trim()] = lapost.Trim();
            }
        }

        return map;
    }

    private static async Task<IReadOnlyDictionary<string, ProjectPostcodeFallback>> ReadProjectFallbacksAsync(
        OdbcConnection connection,
        string[] projectIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, ProjectPostcodeFallback>(StringComparer.Ordinal);
        foreach (var batch in projectIds.Chunk(BatchSize))
        {
            var sql = BuildProjectSql(batch);
            await using var command = new OdbcCommand(sql, connection);
            for (var i = 0; i < batch.Length; i++)
            {
                command.Parameters.Add($"proj{i}", OdbcType.VarChar).Value = batch[i];
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = PlenionPayrollFieldReader.OptionalText(reader["IDProj"]);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                map[id.Trim()] = new ProjectPostcodeFallback(
                    PlenionPayrollFieldReader.OptionalText(reader["A_PC"]),
                    PlenionPayrollFieldReader.OptionalText(reader["ProjLevPost"]));
            }
        }

        return map;
    }

    private static string BuildBonSql(string[] bonNumbers)
    {
        var filter = new StringBuilder();
        for (var i = 0; i < bonNumbers.Length; i++)
        {
            if (i > 0)
            {
                filter.Append(" OR ");
            }

            filter.Append("B.BONNR = ?");
        }

        return $"""
            SELECT B.BONNR, L.LAPOST
            FROM BON B
            LEFT JOIN LEVADR L ON B.LACLEUNIK = L.LACLEUNIK
            WHERE {filter}
            """;
    }

    private static string BuildProjectSql(string[] projectIds)
    {
        var filter = new StringBuilder();
        for (var i = 0; i < projectIds.Length; i++)
        {
            if (i > 0)
            {
                filter.Append(" OR ");
            }

            filter.Append("PRJ.IDProj = ?");
        }

        return $"""
            SELECT PRJ.IDProj, PRJ.A_PC, PL.LAPOST AS ProjLevPost
            FROM PROJ PRJ
            LEFT JOIN LEVADR PL ON PRJ.LACLEUNIK = PL.LACLEUNIK
            WHERE {filter}
            """;
    }

    public sealed record ProjectPostcodeFallback(
        string? ProjectPostalCode,
        string? ProjectDeliveryPostcode);
}

public sealed record PostcodeCoverageSummary
{
    public int TotalRows { get; set; }
    public int BonDeliveryRows { get; set; }
    public int ProjectPostalCodeRows { get; set; }
    public int ProjectDeliveryAddressRows { get; set; }
    public int UnresolvedRows { get; set; }
    public int InvalidSourceRows { get; set; }

    public int ResolvedRows =>
        BonDeliveryRows + ProjectPostalCodeRows + ProjectDeliveryAddressRows;
}
