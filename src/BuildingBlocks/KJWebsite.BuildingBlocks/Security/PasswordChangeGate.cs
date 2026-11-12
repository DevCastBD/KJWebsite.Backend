using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace KJWebsite.BuildingBlocks.Security;

/// <summary>Policy names that are not permission keys.</summary>
public static class KjPolicies
{
    /// <summary>
    /// Any signed-in caller, even one who must still change the initial password. Only change-password, logout and `/auth/me` use it:
    /// everything else goes through the permission policies and the default policy, which all carry <see cref="PasswordChangeGateRequirement"/>.
    /// </summary>
    public const string AuthenticatedAllowingPasswordChange = "kj.authenticated";

    /// <summary>
    /// A signed-in holder of the Donor role, for the read-only self-service under <c>/api/v1/donors/me</c> (#212). The Donor role holds no
    /// permission key, so access is the role itself; ownership is the endpoint's job (the donor is found from the token's subject).
    /// </summary>
    public const string DonorSelf = "kj.donor";
}

/// <summary>
/// Refuses an access token that still carries the <c>pwd_change</c> claim (#209 D6). Staff choose a donor's first password, so the
/// account may not be used for anything until its holder has chosen their own. The check sits in the shared policies, so every
/// service enforces it without its own code.
/// </summary>
public sealed class PasswordChangeGateRequirement : IAuthorizationRequirement;

public sealed class PasswordChangeGateHandler : AuthorizationHandler<PasswordChangeGateRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PasswordChangeGateRequirement requirement)
    {
        if (context.User.HasClaim(KjClaims.PasswordChangeRequired, "true"))
        {
            context.Fail(new AuthorizationFailureReason(this, "A new password must be chosen first."));
        }
        else
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Answers a refusal by the password-change gate with 403 PASSWORD_CHANGE_REQUIRED instead of the generic FORBIDDEN.</summary>
public sealed class PasswordChangeResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure is { } failure
            && failure.FailureReasons.Any(reason => reason.Handler is PasswordChangeGateHandler))
        {
            await ApiError.Create(context, StatusCodes.Status403Forbidden, "PASSWORD_CHANGE_REQUIRED", "Choose a new password before continuing.")
                .ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
