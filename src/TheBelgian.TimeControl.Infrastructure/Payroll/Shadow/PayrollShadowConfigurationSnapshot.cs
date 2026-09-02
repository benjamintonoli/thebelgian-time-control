using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheBelgian.TimeControl.Core.Payroll.Configuration;
using TheBelgian.TimeControl.Core.Payroll.Models;

namespace TheBelgian.TimeControl.Infrastructure.Payroll.Shadow;

public static class PayrollShadowConfigurationSnapshot
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Build(
        PayrollPeriodSnapshot period,
        KmAllowanceConfiguration kmConfiguration,
        CityAllowanceConfiguration cityConfiguration,
        int eligibilityConfigurationCount,
        string eligibilityConfigurationHash)
    {
        var payload = new
        {
            period.Year,
            period.Month,
            period.PeriodStart,
            period.PeriodEnd,
            period.EvaluationDate,
            kmRate = kmConfiguration.RatePerKm,
            cityTripAmount = cityConfiguration.TripAmount,
            cityPostcodeSet = "July2026Qualifying",
            cityPostcodeSetHash = ComputePostcodeSetHash(cityConfiguration.QualifyingPostcodes),
            eligibilityConfigurationCount,
            eligibilityConfigurationHash,
            calculationVersion = CurrentCalculationVersion(),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static string ComputeEligibilityHash(IReadOnlyList<PayrollEmployeeConfiguration> configurations)
    {
        var canonical = configurations
            .OrderBy(item => item.ResourceId, StringComparer.Ordinal)
            .ThenBy(item => item.ValidFrom)
            .Select(item =>
                $"{item.ResourceId}|{item.ValidFrom:O}|{item.ValidTo:O}|{item.EligibilityStatus}|{item.ReasonCode}")
            .ToArray();
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', canonical))));
    }

    public static string ComputePostcodeSetHash(IReadOnlySet<int> postcodes)
    {
        var canonical = string.Join(',', postcodes.OrderBy(item => item));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static CityAllowanceConfiguration CreateCityConfiguration(PayrollPeriodSnapshot period) =>
        new(
            period.PeriodStart,
            period.PeriodEnd,
            5.00m,
            LegacyCityPostcodes.July2026Qualifying);

    public static KmAllowanceConfiguration ResolveKmConfiguration(PayrollPeriodSnapshot period)
    {
        var configuration = KmAllowanceConfiguration.Year2026Legacy;
        if (!configuration.IsActiveOn(period.PeriodStart))
        {
            throw new InvalidOperationException(
                $"Geen actieve KM-configuratie voor loonperiode {period.Year}-{period.Month:00}.");
        }

        return configuration;
    }

    /// <summary>
    /// Returns assembly informational version with SourceRevisionId (e.g. 1.0.0+e30d365...).
    /// Never invokes git at runtime.
    /// </summary>
    public static string CurrentCalculationVersion()
    {
        var assembly = typeof(PayrollShadowConfigurationSnapshot).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?
            .Trim();
        if (IsReproducibleVersion(informational))
        {
            return informational!;
        }

        var fileVersion = assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var sourceRevision = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "SourceRevisionId")
            ?.Value?
            .Trim();
        if (!string.IsNullOrWhiteSpace(sourceRevision))
        {
            return $"{fileVersion}+{sourceRevision}";
        }

        throw new InvalidOperationException(
            "Payroll shadow CalculationVersion is not reproducible. " +
            "Build must embed SourceRevisionId in AssemblyInformationalVersion.");
    }

    public static bool IsReproducibleVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus <= 0 || plus >= version.Length - 1)
        {
            return false;
        }

        var revision = version[(plus + 1)..];
        return revision.Length >= 7
            && !revision.Equals("unspecified", StringComparison.OrdinalIgnoreCase)
            && !revision.Equals("local", StringComparison.OrdinalIgnoreCase);
    }
}
