using Microsoft.AspNetCore.Identity;

namespace AuthIdentityService.Data.Entities;

/// <summary>
/// The identity account. Credentials (hash, lockout, security stamp) are owned by ASP.NET Core Identity;
/// the legacy profile columns live in <see cref="MemberProfileEntity"/>.
/// </summary>
public sealed class AuthUserEntity : IdentityUser
{
    public string Status { get; set; } = "active";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when the account was created by approving a membership application (its id): makes provisioning idempotent.</summary>
    public string? ProvisionedFor { get; set; }

    /// <summary>
    /// Set while the account still has the initial password staff chose for it (donors, #209 D6). The access token then carries
    /// <c>pwd_change</c> and every endpoint except change-password, logout and `/auth/me` refuses it until the holder chooses their own.
    /// </summary>
    public bool MustChangePassword { get; set; }

    public MemberProfileEntity? Profile { get; set; }
}

public sealed class AuthRoleEntity : IdentityRole
{
    public AuthRoleEntity()
    {
    }

    public AuthRoleEntity(string name) : base(name)
    {
    }
}
