using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Plenion;

public sealed class OdbcPlenionReader(
    IOptions<PlenionOptions> options,
    ILogger<OdbcPlenionReader> logger) : IPlenionReader
{
    private readonly string _connectionString = options.Value.PlenionOdbc;

    public async Task<IReadOnlyList<Technician>> GetTechniciansAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT IDRESOURCE, RESCODE, RESTYPE, OMSCHR, HFDGRP, PLOEG,
                   FUNCTIE, EMAIL, DATUMINDIENST, DATUMUITDIENST, SOORT
            FROM Resource
            WHERE SOORT = 1
            """;
        var result = new List<Technician>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new Technician
            {
                ExternalId = RequiredString(reader, "IDRESOURCE"),
                Code = OptionalString(reader, "RESCODE") ?? string.Empty,
                Name = OptionalString(reader, "OMSCHR") ?? string.Empty,
                ResourceType = OptionalString(reader, "RESTYPE"),
                MainGroup = OptionalString(reader, "HFDGRP"),
                Team = OptionalString(reader, "PLOEG"),
                Function = OptionalString(reader, "FUNCTIE"),
                Email = OptionalString(reader, "EMAIL"),
                EmploymentStart = OptionalDate(reader, "DATUMINDIENST"),
                EmploymentEnd = OptionalDate(reader, "DATUMUITDIENST"),
                Kind = ToInt32(reader, "SOORT"),
            });
        }

        logger.LogInformation("{Count} medewerkers read-only uit Plenion gelezen.", result.Count);
        return result;
    }

    public async Task<IReadOnlyList<PlenionPerformance>> GetPerformancesAsync(
        DateOnly fromDate,
        DateOnly throughDate,
        CancellationToken cancellationToken)
    {
        if (throughDate < fromDate)
        {
            throw new ArgumentException("Einddatum ligt vóór begindatum.", nameof(throughDate));
        }

        const string sql = """
            SELECT IDPROJ_PREST, DATUM, VAN, TOT, OMSCHR, IDPROJ,
                   IDRESOURCE, PAUZE, KM
            FROM PROJ_Prest
            WHERE DATUM >= ? AND DATUM <= ?
            ORDER BY DATUM, IDRESOURCE, VAN
            """;
        var result = new List<PlenionPerformance>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        command.Parameters.Add("fromDate", OdbcType.Date).Value = fromDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("throughDate", OdbcType.Date).Value = throughDate.ToDateTime(TimeOnly.MinValue);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = DateOnly.FromDateTime(
                Convert.ToDateTime(reader["DATUM"], CultureInfo.InvariantCulture));
            result.Add(new PlenionPerformance
            {
                ExternalId = Convert.ToInt64(reader["IDPROJ_PREST"], CultureInfo.InvariantCulture),
                TechnicianExternalId = RequiredString(reader, "IDRESOURCE"),
                Date = date,
                Start = ToLocalDateTimeOffset(date, reader["VAN"]),
                End = ToLocalDateTimeOffset(date, reader["TOT"]),
                Description = OptionalString(reader, "OMSCHR"),
                ProjectExternalId = OptionalString(reader, "IDPROJ"),
                BreakMinutes = ToInt32(reader, "PAUZE"),
                Kilometres = ToDecimal(reader, "KM"),
            });
        }

        logger.LogInformation(
            "{Count} prestaties read-only uit Plenion gelezen voor de gevraagde periode.",
            result.Count);
        return result;
    }

    public async Task<IReadOnlyList<CustomerLocation>> GetCustomerLocationsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT LACLEUNIK, LANAAM, LAADRES, LAPOST, LAWNPL, LALAND
            FROM LEVADR
            """;
        var result = new List<CustomerLocation>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var addressParts = new[]
            {
                OptionalString(reader, "LAADRES"),
                OptionalString(reader, "LAPOST"),
                OptionalString(reader, "LAWNPL"),
                OptionalString(reader, "LALAND"),
            };
            result.Add(new CustomerLocation
            {
                ExternalId = RequiredString(reader, "LACLEUNIK"),
                Name = OptionalString(reader, "LANAAM") ?? string.Empty,
                Address = string.Join(", ", addressParts.Where(value => !string.IsNullOrWhiteSpace(value))),
                LocationType = CustomerLocationType.Customer,
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<PlenionWorkOrder>> GetWorkOrdersAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT BOCLEUNIK, BSRTCD, BONNR, CDATUM, AFDATUM, KLCLEUNIK,
                   PROJ, AF, MEMO, LACLEUNIK, IDPROJ, COCLEUNIK, PRIORITEIT
            FROM BON
            WHERE BSRTCD IN (61, 62, 63)
            """;
        var result = new List<PlenionWorkOrder>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PlenionWorkOrder
            {
                ExternalId = RequiredString(reader, "BOCLEUNIK"),
                TypeCode = ToInt32(reader, "BSRTCD"),
                Number = OptionalString(reader, "BONNR"),
                CreatedDate = OptionalDate(reader, "CDATUM"),
                CompletionDate = OptionalDate(reader, "AFDATUM"),
                CustomerExternalId = OptionalString(reader, "KLCLEUNIK"),
                ProjectCode = OptionalString(reader, "PROJ"),
                CompletionCode = OptionalString(reader, "AF"),
                ProjectExternalId = OptionalString(reader, "IDPROJ"),
                ContactExternalId = OptionalString(reader, "COCLEUNIK"),
                Memo = OptionalString(reader, "MEMO"),
                DeliveryAddressExternalId = OptionalString(reader, "LACLEUNIK"),
                Priority = OptionalString(reader, "PRIORITEIT"),
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<PlenionProject>> GetProjectsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT IDPROJ, PROJNR, NAAM, KLCLEUNIK, COCLEUNIK, PRL
            FROM PROJ
            """;
        var result = new List<PlenionProject>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new OdbcCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PlenionProject
            {
                ExternalId = RequiredString(reader, "IDPROJ"),
                Number = OptionalString(reader, "PROJNR"),
                Name = OptionalString(reader, "NAAM"),
                CustomerExternalId = OptionalString(reader, "KLCLEUNIK"),
                ContactExternalId = OptionalString(reader, "COCLEUNIK"),
                PlanningCode = OptionalString(reader, "PRL"),
            });
        }

        return result;
    }

    private async Task<OdbcConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:PlenionOdbc is niet geconfigureerd. Er is geen verbinding gemaakt.");
        }

        var connection = new OdbcConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string RequiredString(DbDataReader reader, string name) =>
        OptionalString(reader, name)
        ?? throw new InvalidDataException($"Plenion-kolom {name} bevat geen waarde.");

    private static string? OptionalString(DbDataReader reader, string name) =>
        reader[name] is DBNull
            ? null
            : Convert.ToString(reader[name], CultureInfo.InvariantCulture)?.Trim();

    private static int ToInt32(DbDataReader reader, string name) =>
        reader[name] is DBNull ? 0 : Convert.ToInt32(reader[name], CultureInfo.InvariantCulture);

    private static decimal ToDecimal(DbDataReader reader, string name) =>
        reader[name] is DBNull ? 0 : Convert.ToDecimal(reader[name], CultureInfo.InvariantCulture);

    private static DateOnly? OptionalDate(DbDataReader reader, string name) =>
        reader[name] is DBNull
            ? null
            : DateOnly.FromDateTime(
                Convert.ToDateTime(reader[name], CultureInfo.InvariantCulture));

    private static DateTimeOffset ToLocalDateTimeOffset(DateOnly date, object value)
    {
        var time = value switch
        {
            TimeSpan timeSpan => TimeOnly.FromTimeSpan(timeSpan),
            DateTime dateTime => TimeOnly.FromDateTime(dateTime),
            _ when TimeOnly.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed) => parsed,
            _ => throw new InvalidDataException("Plenion bevat een ongeldige tijdwaarde."),
        };
        var unspecified = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
