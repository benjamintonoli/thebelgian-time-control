using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Infrastructure.Authentication;

internal sealed class CloudflareAccessJwtValidator(
    ICloudflareAccessCertificateProvider certificateProvider,
    IOptions<CloudflareAccessOptions> options) : ICloudflareAccessJwtValidator
{
    public async Task<CloudflareAccessJwtValidationResult> ValidateAsync(
        string jwt,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jwt))
            return CloudflareAccessJwtValidationResult.Failure("Cloudflare Access JWT ontbreekt.");

        var accessOptions = options.Value;
        if (string.IsNullOrWhiteSpace(accessOptions.TeamDomain))
            return CloudflareAccessJwtValidationResult.Failure("CloudflareAccess:TeamDomain ontbreekt.");
        if (string.IsNullOrWhiteSpace(accessOptions.Audience))
            return CloudflareAccessJwtValidationResult.Failure("CloudflareAccess:Audience ontbreekt.");

        try
        {
            return await ValidateInternalAsync(jwt, refreshKeysOnMiss: true, cancellationToken);
        }
        catch (SecurityTokenException exception)
        {
            return CloudflareAccessJwtValidationResult.Failure(exception.Message);
        }
        catch (Exception exception)
        {
            return CloudflareAccessJwtValidationResult.Failure(
                $"Cloudflare Access JWT kon niet worden gevalideerd: {exception.Message}");
        }
    }

    private async Task<CloudflareAccessJwtValidationResult> ValidateInternalAsync(
        string jwt,
        bool refreshKeysOnMiss,
        CancellationToken cancellationToken)
    {
        var accessOptions = options.Value;
        var keys = await certificateProvider.GetSigningKeysAsync(cancellationToken);
        try
        {
            return ValidateWithKeys(jwt, keys, accessOptions);
        }
        catch (SecurityTokenSignatureKeyNotFoundException) when (refreshKeysOnMiss)
        {
            keys = await certificateProvider.RefreshSigningKeysAsync(cancellationToken);
            return ValidateWithKeys(jwt, keys, accessOptions);
        }
    }

    private CloudflareAccessJwtValidationResult ValidateWithKeys(
        string jwt,
        IReadOnlyList<SecurityKey> keys,
        CloudflareAccessOptions accessOptions)
    {
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false,
        };
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = accessOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = accessOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            ClockSkew = TimeSpan.FromMinutes(1),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        };

        var principal = handler.ValidateToken(jwt, parameters, out _);
        var email = FindClaim(principal, "email", ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return CloudflareAccessJwtValidationResult.Failure(
                "Cloudflare Access JWT bevat geen geldig email-claim.");
        }

        var subject = FindClaim(principal, "sub", ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return CloudflareAccessJwtValidationResult.Failure(
                "Cloudflare Access JWT bevat geen geldig sub-claim.");
        }

        var displayName = FindClaim(principal, "name", ClaimTypes.Name);
        return CloudflareAccessJwtValidationResult.Success(
            email.Trim(),
            subject.Trim(),
            string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim());
    }

    private static string? FindClaim(
        ClaimsPrincipal principal,
        string jwtClaimType,
        string fallbackClaimType) =>
        principal.FindFirst(jwtClaimType)?.Value
        ?? principal.FindFirst(fallbackClaimType)?.Value;
}
