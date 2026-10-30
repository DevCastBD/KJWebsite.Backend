using System.Security.Claims;

namespace KJWebsite.BuildingBlocks.Security;

/// <summary>
/// Object-level checks. A permission such as `member.profile.edit` says WHAT a caller may do, not to WHOM: without an owner check it
/// would let any member edit every profile. Handlers for owned resources call <see cref="CanAccess"/> with the manager permission
/// that lifts the restriction (for a profile, `membership.manage`).
/// </summary>
public static class ResourceOwnership
{
    public static bool HasPermission(this ClaimsPrincipal principal, string permission) =>
        principal.HasClaim(KjClaims.Permission, permission);

    public static bool IsOwner(this ClaimsPrincipal principal, string? ownerId) =>
        !string.IsNullOrEmpty(ownerId) && string.Equals(principal.FindFirstValue(KjClaims.Subject), ownerId, StringComparison.Ordinal);

    /// <summary>True for the owner, or for a caller holding the manager permission. An unknown owner is never owned.</summary>
    public static bool CanAccess(this ClaimsPrincipal principal, string? ownerId, string managerPermission) =>
        principal.IsOwner(ownerId) || principal.HasPermission(managerPermission);
}
