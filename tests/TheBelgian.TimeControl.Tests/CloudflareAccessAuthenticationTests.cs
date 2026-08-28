using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Authentication;
using TheBelgian.TimeControl.Infrastructure.Configuration;
using TheBelgian.TimeControl.Web.Authentication;
using TheBelgian.TimeControl.Web.Services;

namespace TheBelgian.TimeControl.Tests;

public sealed class CloudflareAccessAuthenticationTests
{
    private const string TeamDomain = "thebelgian.cloudflareaccess.com";
    private const string Audience = "test-application-audience";
    private const string Issuer = "https://thebelgian.cloudflareaccess.com";

    [Fact]
    public async Task ValidSignedAccessJwt_AuthenticatesWithEmailAndSubject()
    {
        using var rsa = RSA.Create(2048);
        var provider = new StaticCertificateProvider(new RsaSecurityKey(rsa) { KeyId = "kid-1" });
        var validator = CreateValidator(provider);
        var jwt = CreateToken(
            rsa,
            Issuer,
            Audience,
            [
                new Claim("email", "benjamin.tonoli@thebelgian.be"),
                new Claim("sub", "entra-subject-123"),
                new Claim("name", "Benjamin Tonoli"),
            ],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10));

        var result = await validator.ValidateAsync(jwt, default);

        Assert.True(result.IsValid);
        Assert.Equal("benjamin.tonoli@thebelgian.be", result.Email);
        Assert.Equal("entra-subject-123", result.Subject);
        Assert.Equal("Benjamin Tonoli", result.DisplayName);
    }

    [Theory]
    [InlineData("https://wrong.cloudflareaccess.com")]
    [InlineData("https://other-team.cloudflareaccess.com")]
    public async Task WrongIssuer_IsRejected(string issuer)
    {
        using var rsa = RSA.Create(2048);
        var provider = new StaticCertificateProvider(new RsaSecurityKey(rsa) { KeyId = "kid-1" });
        var validator = CreateValidator(provider);
        var jwt = CreateToken(
            rsa,
            issuer,
            Audience,
            [new Claim("email", "user@thebelgian.be"), new Claim("sub", "abc")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10));

        var result = await validator.ValidateAsync(jwt, default);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task WrongAudience_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var provider = new StaticCertificateProvider(new RsaSecurityKey(rsa) { KeyId = "kid-1" });
        var validator = CreateValidator(provider);
        var jwt = CreateToken(
            rsa,
            Issuer,
            "other-audience",
            [new Claim("email", "user@thebelgian.be"), new Claim("sub", "abc")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10));

        var result = await validator.ValidateAsync(jwt, default);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ExpiredToken_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var provider = new StaticCertificateProvider(new RsaSecurityKey(rsa) { KeyId = "kid-1" });
        var validator = CreateValidator(provider);
        var jwt = CreateToken(
            rsa,
            Issuer,
            Audience,
            [new Claim("email", "user@thebelgian.be"), new Claim("sub", "abc")],
            DateTime.UtcNow.AddMinutes(-20),
            DateTime.UtcNow.AddMinutes(-5));

        var result = await validator.ValidateAsync(jwt, default);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task NotYetValidToken_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var provider = new StaticCertificateProvider(new RsaSecurityKey(rsa) { KeyId = "kid-1" });
        var validator = CreateValidator(provider);
        var jwt = CreateToken(
            rsa,
            Issuer,
            Audience,
            [new Claim("email", "user@thebelgian.be"), new Claim("sub", "abc")],
            DateTime.UtcNow.AddMinutes(10),
            DateTime.UtcNow.AddMinutes(20));

        var result = await validator.ValidateAsync(jwt, default);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task BadSignature_IsRejected()
    {
        using var signingKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        var provider = new StaticCertificateProvider(new RsaSecurityKey(otherKey) { KeyId = "kid-1" });
        var validator = CreateValidator(provider);
        var jwt = CreateToken(
            signingKey,
            Issuer,
            Audience,
            [new Claim("email", "user@thebelgian.be"), new Claim("sub", "abc")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10));

        var result = await validator.ValidateAsync(jwt, default);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task MissingEmailClaim_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var provider = new StaticCertificateProvider(new RsaSecurityKey(rsa) { KeyId = "kid-1" });
        var validator = CreateValidator(provider);
        var jwt = CreateToken(
            rsa,
            Issuer,
            Audience,
            [new Claim("sub", "abc")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10));

        var result = await validator.ValidateAsync(jwt, default);

        Assert.False(result.IsValid);
        Assert.Contains("email", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownKid_RefreshesKeysOnce()
    {
        using var firstKey = RSA.Create(2048);
        using var secondKey = RSA.Create(2048);
        var provider = new RotatingCertificateProvider(
            new RsaSecurityKey(firstKey) { KeyId = "old" },
            new RsaSecurityKey(secondKey) { KeyId = "new" });
        var validator = CreateValidator(provider);
        var jwt = CreateToken(
            secondKey,
            Issuer,
            Audience,
            [new Claim("email", "user@thebelgian.be"), new Claim("sub", "abc")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(10),
            "new");

        var result = await validator.ValidateAsync(jwt, default);

        Assert.True(result.IsValid);
        Assert.Equal(1, provider.RefreshCount);
    }

    [Fact]
    public void ProductionEnabledWithoutJwt_RejectsActor()
    {
        var context = CreateUserContext(
            cloudflareEnabled: true,
            environment: Environments.Production,
            authenticatedUser: null);

        var exception = Assert.Throws<UnauthorizedAccessException>(() =>
            context.RequireActor("Benjamin Tonoli"));

        Assert.Contains("Cloudflare Access", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentDisabled_AllowsConfiguredFallback()
    {
        var context = CreateUserContext(
            cloudflareEnabled: false,
            environment: Environments.Development,
            authenticatedUser: null);

        var actor = context.RequireActor("Benjamin Tonoli");

        Assert.Equal("Benjamin Tonoli", actor.Email);
        Assert.StartsWith("dev:", actor.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionDisabled_AllowsConfiguredFallback()
    {
        var context = CreateUserContext(
            cloudflareEnabled: false,
            environment: Environments.Production,
            authenticatedUser: null);

        var actor = context.RequireActor("Benjamin Tonoli");

        Assert.Equal("Benjamin Tonoli", actor.Email);
        Assert.StartsWith("config:", actor.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizationPolicy_WhenDisabled_AllowsAnonymous()
    {
        var handler = new CloudflareAccessAuthorizationHandler(
            Options.Create(new CloudflareAccessOptions { Enabled = false }));
        var requirement = new CloudflareAccessAuthorizationRequirement();
        var context = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
            null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task AuthorizationPolicy_WhenEnabled_RequiresAuthenticatedUser()
    {
        var handler = new CloudflareAccessAuthorizationHandler(
            Options.Create(new CloudflareAccessOptions { Enabled = true }));
        var requirement = new CloudflareAccessAuthorizationRequirement();
        var anonymous = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity()),
            null);
        await handler.HandleAsync(anonymous);
        Assert.False(anonymous.HasSucceeded);

        var authenticated = new AuthorizationHandlerContext(
            [requirement],
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Email, "user@thebelgian.be")],
                CloudflareAccessAuthenticationDefaults.AuthenticationScheme)),
            null);
        await handler.HandleAsync(authenticated);
        Assert.True(authenticated.HasSucceeded);
    }

    [Fact]
    public async Task SpoofedEmailHeaderWithoutValidJwt_IsRejectedWhenEnabled()
    {
        using var rsa = RSA.Create(2048);
        var provider = new StaticCertificateProvider(new RsaSecurityKey(rsa) { KeyId = "kid-1" });
        var validator = CreateValidator(provider);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Cf-Access-Authenticated-User-Email"] = "attacker@evil.example";
        var handler = new CloudflareAccessAuthenticationHandler(
            new TestOptionsMonitor(new AuthenticationSchemeOptions()),
            NullLoggerFactory.Instance,
            System.Text.Encodings.Web.UrlEncoder.Default,
            validator,
            Options.Create(new CloudflareAccessOptions
            {
                Enabled = true,
                TeamDomain = TeamDomain,
                Audience = Audience,
            }));
        await handler.InitializeAsync(new AuthenticationScheme(
            CloudflareAccessAuthenticationDefaults.AuthenticationScheme,
            null,
            typeof(CloudflareAccessAuthenticationHandler)), httpContext);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
    }

    private static CloudflareAccessJwtValidator CreateValidator(
        ICloudflareAccessCertificateProvider provider) =>
        new(provider, Options.Create(new CloudflareAccessOptions
        {
            Enabled = true,
            TeamDomain = TeamDomain,
            Audience = Audience,
        }));

    private static HttpCurrentUserContext CreateUserContext(
        bool cloudflareEnabled,
        string environment,
        AuthenticatedActor? authenticatedUser)
    {
        var httpContext = new DefaultHttpContext();
        if (authenticatedUser is not null)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Email, authenticatedUser.Email),
                new Claim(ClaimTypes.NameIdentifier, authenticatedUser.Subject),
            ],
            CloudflareAccessAuthenticationDefaults.AuthenticationScheme));
        }

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var hostEnvironment = new FakeHostEnvironment { EnvironmentName = environment };
        return new HttpCurrentUserContext(
            accessor,
            Options.Create(new CloudflareAccessOptions { Enabled = cloudflareEnabled }),
            hostEnvironment);
    }

    private static string CreateToken(
        RSA rsa,
        string issuer,
        string audience,
        Claim[] claims,
        DateTime notBefore,
        DateTime expires,
        string? kid = "kid-1")
    {
        var key = new RsaSecurityKey(rsa) { KeyId = kid };
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            notBefore,
            expires,
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class StaticCertificateProvider(params SecurityKey[] keys)
        : ICloudflareAccessCertificateProvider
    {
        public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SecurityKey>>(keys);

        public Task<IReadOnlyList<SecurityKey>> RefreshSigningKeysAsync(CancellationToken cancellationToken) =>
            GetSigningKeysAsync(cancellationToken);
    }

    private sealed class RotatingCertificateProvider(
        SecurityKey initial,
        SecurityKey refreshed) : ICloudflareAccessCertificateProvider
    {
        public int RefreshCount { get; private set; }

        public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SecurityKey>>([initial]);

        public Task<IReadOnlyList<SecurityKey>> RefreshSigningKeysAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.FromResult<IReadOnlyList<SecurityKey>>([refreshed]);
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class TestOptionsMonitor(AuthenticationSchemeOptions options)
        : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue => options;

        public AuthenticationSchemeOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
