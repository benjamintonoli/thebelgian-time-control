using System.Reflection;
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
            eligibilityConfigurationCount,
            eligibilityConfigurationHash,
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
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join('\n', canonical))));
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

    public static string CurrentCalculationVersion() =>
        typeof(PayrollShadowConfigurationSnapshot).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(PayrollShadowConfigurationSnapshot).Assembly.GetName().Version?.ToString()
        ?? "legacy-shadow-unknown";
}
