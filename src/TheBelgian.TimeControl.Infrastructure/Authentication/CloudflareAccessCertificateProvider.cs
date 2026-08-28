using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Authentication;

internal sealed class CloudflareAccessCertificateProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<CloudflareAccessOptions> options,
    TimeProvider timeProvider) : ICloudflareAccessCertificateProvider, IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<SecurityKey> _cachedKeys = [];
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(
        CancellationToken cancellationToken)
    {
        if (_cachedKeys.Count > 0 && timeProvider.GetUtcNow() < _expiresAt)
            return _cachedKeys;

        return await RefreshSigningKeysAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SecurityKey>> RefreshSigningKeysAsync(
        CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var client = httpClientFactory.CreateClient(nameof(CloudflareAccessCertificateProvider));
            using var response = await client.GetAsync(
                options.Value.CertificateEndpoint,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var keys = ParseKeys(document.RootElement);
            if (keys.Count == 0)
                throw new InvalidOperationException("Cloudflare Access certificaatendpoint bevat geen signing keys.");

            _cachedKeys = keys;
            _expiresAt = timeProvider.GetUtcNow().AddMinutes(
                Math.Max(1, options.Value.CertificateCacheMinutes));
            return _cachedKeys;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    internal static IReadOnlyList<SecurityKey> ParseKeys(JsonElement root)
    {
        var keys = new List<SecurityKey>();
        if (root.TryGetProperty("keys", out var jwks) &&
            jwks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jwks.EnumerateArray())
            {
                var key = CreateKey(item);
                if (key is not null)
                    keys.Add(key);
            }
        }

        if (keys.Count == 0 &&
            root.TryGetProperty("public_certs", out var publicCerts) &&
            publicCerts.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in publicCerts.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                keys.Add(new X509SecurityKey(
                    System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
                        item.GetString()!)));
            }
        }

        if (keys.Count == 0 &&
            root.TryGetProperty("public_cert", out var publicCert) &&
            publicCert.ValueKind == JsonValueKind.String)
        {
            keys.Add(new X509SecurityKey(
                System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
                    publicCert.GetString()!)));
        }

        return keys;
    }

    public void Dispose() => _refreshLock.Dispose();

    private static RsaSecurityKey? CreateKey(JsonElement item)
    {
        if (!item.TryGetProperty("kty", out var kty) ||
            !string.Equals(kty.GetString(), "RSA", StringComparison.Ordinal))
        {
            return null;
        }

        if (!item.TryGetProperty("n", out var modulusElement) ||
            !item.TryGetProperty("e", out var exponentElement))
        {
            return null;
        }

        var parameters = new RSAParameters
        {
            Modulus = Base64UrlEncoder.DecodeBytes(modulusElement.GetString()),
            Exponent = Base64UrlEncoder.DecodeBytes(exponentElement.GetString()),
        };
        var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportParameters(parameters);
        var key = new RsaSecurityKey(rsa);
        if (item.TryGetProperty("kid", out var kid) &&
            !string.IsNullOrWhiteSpace(kid.GetString()))
        {
            key.KeyId = kid.GetString();
        }

        return key;
    }
}
