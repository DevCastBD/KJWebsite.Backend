using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.BuildingBlocks.Security;

public static class PermissionAuthorization
{
    /// <summary>
    /// One policy per permission key, plus a deny-by-default fallback: an endpoint that declares neither
    /// `AllowAnonymous()` nor an authorization requirement is refused for everyone, never served open.
    /// </summary>
    public static IServiceCollection AddKjPermissionPolicies(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAssertion(_ => false).Build());

        services.AddOptions<AuthorizationOptions>().Configure(options =>
        {
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission, policy => policy.RequireAuthenticatedUser().RequireClaim(KjClaims.Permission, permission));
            }
        });
        return services;
    }

    /// <summary>Requires the caller's access token to carry the permission claim.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder
    {
        if (!Permissions.All.Contains(permission))
        {
            throw new ArgumentException($"Unknown permission '{permission}'.", nameof(permission));
        }

        return builder.RequireAuthorization(permission);
    }
}
