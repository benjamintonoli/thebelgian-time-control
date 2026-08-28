using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using TheBelgian.TimeControl.Infrastructure.Configuration;

namespace TheBelgian.TimeControl.Web.Authentication;

public sealed class CloudflareAccessAuthorizationRequirement : IAuthorizationRequirement;

public sealed class CloudflareAccessAuthorizationHandler(
    IOptions<CloudflareAccessOptions> options) :
    AuthorizationHandler<CloudflareAccessAuthorizationRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CloudflareAccessAuthorizationRequirement requirement)
    {
        if (!options.Value.Enabled)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (context.User.Identity?.IsAuthenticated == true)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
