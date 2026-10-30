namespace KJWebsite.BuildingBlocks.Security;

/// <summary>
/// Permission keys. Endpoints require a key, not a role, so duties can be split (a finance officer who approves an
/// expense need not be able to submit it). New keys are added here and seeded into the roles that should hold them.
/// </summary>
public static class Permissions
{
    public const string ContentView = "content.view";
    public const string ContentCreate = "content.create";
    public const string ContentUpdate = "content.update";
    public const string ContentDelete = "content.delete";
    public const string ContentPublish = "content.publish";
    public const string CtaView = "cta.view";
    public const string UsersView = "users.view";
    public const string UsersManage = "users.manage";
    public const string RolesManage = "roles.manage";
    public const string MembershipManage = "membership.manage";
    public const string EventsManage = "events.manage";
    public const string FinanceView = "finance.view";
    public const string FinanceExpenseSubmit = "finance.expense.submit";
    public const string FinanceExpenseApprove = "finance.expense.approve";
    public const string AuditView = "audit.view";
    public const string CtaManage = "cta.manage";
    public const string MediaView = "media.view";
    public const string MediaUpload = "media.upload";
    public const string MediaDelete = "media.delete";
    public const string SettingsView = "settings.view";
    public const string SettingsManage = "settings.manage";
    public const string DashboardView = "dashboard.view";

    /// <summary>Self-service (P3): holding it lets a member edit their OWN profile; see <see cref="ResourceOwnership"/> for the owner check.</summary>
    public const string MemberProfileEdit = "member.profile.edit";
    public const string MemberProfileView = "member.profile.view";

    public static readonly IReadOnlyList<string> All = [.. PermissionCatalog.Entries.Select(e => e.Key)];
}

/// <summary>One permission with the module it belongs to, for the role-management UI and API.</summary>
public sealed record PermissionInfo(string Module, string Key, string Description);

/// <summary>Every permission key, grouped by module. A test keeps this and the constants in <see cref="Permissions"/> identical.</summary>
public static class PermissionCatalog
{
    public static readonly IReadOnlyList<PermissionInfo> Entries =
    [
        new("content", Permissions.ContentView, "View content in the back office"),
        new("content", Permissions.ContentCreate, "Create content"),
        new("content", Permissions.ContentUpdate, "Edit content, tags, related items and slugs"),
        new("content", Permissions.ContentDelete, "Delete content, tags and redirects"),
        new("content", Permissions.ContentPublish, "Publish and unpublish content"),
        new("media", Permissions.MediaView, "Browse the media library"),
        new("media", Permissions.MediaUpload, "Upload media"),
        new("media", Permissions.MediaDelete, "Delete media"),
        new("engagement", Permissions.CtaView, "View form and contact submissions"),
        new("engagement", Permissions.CtaManage, "Assign, reply to and close submissions"),
        new("users", Permissions.UsersView, "View back-office users and their roles"),
        new("users", Permissions.UsersManage, "Assign roles to users"),
        new("users", Permissions.RolesManage, "Create roles and change what a role may do"),
        new("membership", Permissions.MembershipManage, "Handle membership applications"),
        new("membership", Permissions.MemberProfileView, "View member profiles"),
        new("membership", Permissions.MemberProfileEdit, "Edit a member profile (members: their own only)"),
        new("events", Permissions.EventsManage, "Manage events"),
        new("finance", Permissions.FinanceView, "View finance records"),
        new("finance", Permissions.FinanceExpenseSubmit, "Submit expenses"),
        new("finance", Permissions.FinanceExpenseApprove, "Approve expenses (separate from submitting)"),
        new("settings", Permissions.SettingsView, "View site settings"),
        new("settings", Permissions.SettingsManage, "Change site settings and maintenance mode"),
        new("dashboard", Permissions.DashboardView, "See the back-office dashboard and global search"),
        new("audit", Permissions.AuditView, "Read the audit log")
    ];
}

/// <summary>The seeded roles (plan §3.1) and the permissions each holds.</summary>
public static class KjRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Moderator = "Moderator";
    public const string FinanceOfficer = "FinanceOfficer";
    public const string ContentEditor = "ContentEditor";
    public const string EventManager = "EventManager";
    public const string MembershipOfficer = "MembershipOfficer";
    public const string Member = "Member";
    public const string Volunteer = "Volunteer";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Seed = new Dictionary<string, IReadOnlyList<string>>
    {
        [SuperAdmin] = Permissions.All,
        [Admin] = [.. Permissions.All.Where(p => p != Permissions.RolesManage)],
        [Moderator] = [Permissions.ContentView, Permissions.ContentUpdate, Permissions.CtaView, Permissions.CtaManage, Permissions.DashboardView],
        [FinanceOfficer] = [Permissions.FinanceView, Permissions.FinanceExpenseApprove],
        [ContentEditor] = [Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate, Permissions.MediaView, Permissions.MediaUpload, Permissions.DashboardView],
        [EventManager] = [Permissions.EventsManage, Permissions.FinanceExpenseSubmit],
        [MembershipOfficer] = [Permissions.MembershipManage, Permissions.MemberProfileView, Permissions.UsersView],
        [Member] = [Permissions.MemberProfileEdit],
        [Volunteer] = []
    };
}
