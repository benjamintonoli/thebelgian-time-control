using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using TheBelgian.TimeControl.Infrastructure.Authentication;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Authentication;

public sealed class CloudflareAccessAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ICloudflareAccessJwtValidator jwtValidator,
    IOptions<CloudflareAccessOptions> cloudflareOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!cloudflareOptions.Value.Enabled)
            return AuthenticateResult.NoResult();

        if (!Request.Headers.TryGetValue(
                cloudflareOptions.Value.JwtHeaderName,
                out var headerValues) ||
            string.IsNullOrWhiteSpace(headerValues.ToString()))
        {
            return AuthenticateResult.Fail("Cloudflare Access JWT ontbreekt.");
        }

        var validation = await jwtValidator.ValidateAsync(
            headerValues.ToString(),
            Context.RequestAborted);
        if (!validation.IsValid)
            return AuthenticateResult.Fail(validation.Error ?? "Cloudflare Access JWT is ongeldig.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, validation.Email!),
            new("email", validation.Email!),
            new(ClaimTypes.NameIdentifier, validation.Subject!),
            new("sub", validation.Subject!),
        };
        if (!string.IsNullOrWhiteSpace(validation.DisplayName))
        {
            claims.Add(new Claim(ClaimTypes.Name, validation.DisplayName));
            claims.Add(new Claim("name", validation.DisplayName));
        }

        var identity = new ClaimsIdentity(
            claims,
            CloudflareAccessAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(
            principal,
            CloudflareAccessAuthenticationDefaults.AuthenticationScheme);
        return AuthenticateResult.Success(ticket);
    }
}
