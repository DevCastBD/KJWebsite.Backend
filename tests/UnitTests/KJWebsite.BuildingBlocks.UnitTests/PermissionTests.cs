using System;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using KJWebsite.BuildingBlocks.Security;

namespace KJWebsite.BuildingBlocks.UnitTests;

public sealed class PermissionTests
{
    [Fact]
    public void EveryPermissionConstantIsInTheCatalogExactlyOnce()
    {
        var constants = typeof(Permissions).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();

        Assert.Equal(constants.Order(StringComparer.Ordinal), PermissionCatalog.Entries.Select(e => e.Key).Order(StringComparer.Ordinal));
        Assert.Equal(constants.Count, Permissions.All.Distinct().Count());
        Assert.All(PermissionCatalog.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Description)));
    }

    [Fact]
    public void KeysAreLowerCaseDottedNames()
    {
        Assert.All(PermissionCatalog.Entries, e => Assert.Matches("^[a-z]+(\\.[a-z]+)+$", e.Key));
    }

    [Fact]
    public void SeededRolesOnlyHoldKnownPermissionsAndSuperAdminHoldsAll()
    {
        Assert.All(KjRoles.Seed.Values.SelectMany(v => v), p => Assert.Contains(p, Permissions.All));
        Assert.Equal(Permissions.All.Count, KjRoles.Seed[KjRoles.SuperAdmin].Count);
    }

    [Fact]
    public void SelfServiceNeedsAnOwnerCheckBecauseThePermissionAloneIsNotEnough()
    {
        var member = Principal("usr_1", Permissions.MemberProfileEdit);
        var officer = Principal("usr_2", Permissions.MembershipManage);

        Assert.True(member.CanAccess("usr_1", Permissions.MembershipManage), "own profile");
        Assert.False(member.CanAccess("usr_9", Permissions.MembershipManage), "another member's profile");
        Assert.True(officer.CanAccess("usr_9", Permissions.MembershipManage), "a manager may act on any profile");
        Assert.False(member.CanAccess(null, Permissions.MembershipManage));
        Assert.False(member.CanAccess(string.Empty, Permissions.MembershipManage));
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity()).CanAccess("usr_1", Permissions.MembershipManage));
    }

    private static ClaimsPrincipal Principal(string subject, params string[] permissions) => new(new ClaimsIdentity(
        [new Claim(KjClaims.Subject, subject), .. permissions.Select(p => new Claim(KjClaims.Permission, p))], "test"));
}
