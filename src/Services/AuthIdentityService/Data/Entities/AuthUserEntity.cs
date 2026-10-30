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
