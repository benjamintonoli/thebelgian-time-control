using System.Security.Claims;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Core.Interfaces;
using TheBelgian.TimeControl.Core.Models;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Services;

public sealed class HttpCurrentUserContext(
    IHttpContextAccessor httpContextAccessor,
    IOptions<CloudflareAccessOptions> cloudflareOptions,
    IHostEnvironment environment) : ICurrentUserContext
{
    public AuthenticatedActor? CurrentUser
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
                return null;

            var email = user.FindFirst(ClaimTypes.Email)?.Value
                ?? user.FindFirst("email")?.Value;
            var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(subject))
                return null;

            var displayName = user.FindFirst(ClaimTypes.Name)?.Value
                ?? user.FindFirst("name")?.Value;
            return new AuthenticatedActor(
                email.Trim(),
                subject.Trim(),
                string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim());
        }
    }

    public AuthenticatedActor RequireActor(string developmentFallbackReviewer)
    {
        var current = CurrentUser;
        if (current is not null)
            return current;

        if (cloudflareOptions.Value.Enabled)
        {
            throw new UnauthorizedAccessException(
                "Geen geldige Cloudflare Access-identiteit beschikbaar voor deze actie.");
        }

        if (!environment.IsDevelopment())
        {
            if (string.IsNullOrWhiteSpace(developmentFallbackReviewer))
            {
                throw new InvalidOperationException(
                    "AdminReview:DefaultReviewer ontbreekt.");
            }

            return new AuthenticatedActor(
                developmentFallbackReviewer.Trim(),
                $"config:{developmentFallbackReviewer.Trim()}",
                developmentFallbackReviewer.Trim());
        }

        if (string.IsNullOrWhiteSpace(developmentFallbackReviewer))
        {
            throw new InvalidOperationException(
                "AdminReview:DefaultReviewer ontbreekt.");
        }

        return new AuthenticatedActor(
            developmentFallbackReviewer.Trim(),
            $"dev:{developmentFallbackReviewer.Trim()}",
            developmentFallbackReviewer.Trim());
    }
}
