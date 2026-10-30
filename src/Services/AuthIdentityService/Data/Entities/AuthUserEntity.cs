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
