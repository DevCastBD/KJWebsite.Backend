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

    public static readonly IReadOnlyList<string> All =
    [
        ContentView, ContentCreate, ContentUpdate, ContentDelete, ContentPublish,
        CtaView, UsersView, UsersManage, RolesManage, MembershipManage, EventsManage,
        FinanceView, FinanceExpenseSubmit, FinanceExpenseApprove, AuditView
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
        [Moderator] = [Permissions.ContentView, Permissions.ContentUpdate, Permissions.CtaView],
        [FinanceOfficer] = [Permissions.FinanceView, Permissions.FinanceExpenseApprove],
        [ContentEditor] = [Permissions.ContentView, Permissions.ContentCreate, Permissions.ContentUpdate],
        [EventManager] = [Permissions.EventsManage, Permissions.FinanceExpenseSubmit],
        [MembershipOfficer] = [Permissions.MembershipManage, Permissions.UsersView],
        [Member] = [],
        [Volunteer] = []
    };
}
