namespace TheBelgian.TimeControl.Infrastructure.Configuration;

public sealed class CloudflareAccessOptions
{
    public const string SectionName = "CloudflareAccess";

    public bool Enabled { get; init; }

    public string TeamDomain { get; init; } = string.Empty;

    public string Audience { get; init; } = string.Empty;

    public string JwtHeaderName { get; init; } = "Cf-Access-Jwt-Assertion";

    public int CertificateCacheMinutes { get; init; } = 60;

    public string NormalizeTeamDomain()
    {
        var value = TeamDomain.Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = value["https://".Length..];
        return value.TrimEnd('/');
    }

    public string Issuer => $"https://{NormalizeTeamDomain()}";

    public Uri CertificateEndpoint =>
        new($"https://{NormalizeTeamDomain()}/cdn-cgi/access/certs");
}
