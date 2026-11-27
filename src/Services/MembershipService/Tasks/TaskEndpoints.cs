using System.Text.Json;
using System.Text.RegularExpressions;
using KJWebsite.BuildingBlocks;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using MembershipService.Self;
using Microsoft.EntityFrameworkCore;

namespace MembershipService.Tasks;

public sealed record TaskRequest(string? Title, string? Description, string? AssigneeMemberId, string? ProjectSlug, string? CommitteeId, DateOnly? DueOn, string? Priority, string? Status);

public sealed record StatusRequest(string? Status);

public sealed record TaskView(string Id, string Title, string Description, string AssigneeMemberId, string AssigneeName, string? ProjectSlug, string? CommitteeId, DateOnly? DueOn, string Priority, string Status, bool Overdue, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt);

public sealed record WorkloadRow(string MemberId, string MemberCode, string Name, int Open, int Todo, int InProgress, int Blocked, int Overdue, int DueSoon, int High);

/// <summary>
/// Lightweight tasks for committees and projects (BE-074). Four statuses, one assignee, an optional due date: deliberately not a
/// project-management product. Assigning a task notifies the assignee in their notification centre in the same commit; due-soon and
/// overdue notices are produced by `POST .../tasks/reminders/run` (once per due date), which a timer calls. The workload view exists
/// because work in volunteer organisations concentrates on whoever says yes.
/// </summary>
public static partial class TaskEndpoints
{
    private const string Admin = "/api/v1/admin/membership/tasks";
    private const string Me = "/api/v1/members/me";
    private const string Tag = "Task assignment";
    private const int DueSoonDays = 2;
    private static readonly string[] Priorities = ["low", "normal", "high"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public static void MapTasks(this IEndpointRouteBuilder app)
    {
        app.MapGet(Admin, async (MembershipDbContext db, TimeProvider clock, string? project, string? committeeId, string? assignee, string? status, int? limit, int? offset) =>
        {
            var members = await db.Members.AsNoTracking().ToDictionaryAsync(m => m.Id);
            var today = Today(clock);
            var rows = (await db.Tasks.AsNoTracking().ToListAsync())
                .Where(t => (project is null || t.ProjectSlug == project) && (committeeId is null || t.CommitteeId == committeeId) && (assignee is null || t.AssigneeMemberId == assignee) && (status is null || t.Status == status))
                .OrderBy(t => t.Status == TaskStatuses.Done ? 1 : 0).ThenBy(t => t.DueOn ?? DateOnly.MaxValue).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(offset ?? 0, 0);
            return Results.Ok(new { items = rows.Skip(skip).Take(take).Select(t => ToView(t, members, today)), total = rows.Count, limit = take, offset = skip });
        })
        .WithName("ListTasks").WithSummary("Tasks by project, committee, assignee or status")
        .WithTags(Tag).RequirePermission(Permissions.MembershipManage);

        app.MapPost(Admin, async (HttpContext context, TaskRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var task = new TaskAssignmentEntity { Id = $"tsk_{Guid.NewGuid():N}", CreatedAt = clock.GetUtcNow(), CreatedBy = SelfEndpoints.Subject(context) ?? "unknown" };
            if (await Apply(task, payload, db, creating: true) is { } error)
            {
                return Invalid(context, error);
            }

            task.UpdatedAt = task.CreatedAt;
            db.Tasks.Add(task);
            Notify(db, task.AssigneeMemberId, "task.assigned", task, clock);
            await db.SaveChangesAsync();
            return Results.Created($"{Admin}/{task.Id}", await Single(db, task, clock));
        })
        .WithName("CreateTask").WithSummary("Assign a task to a member for a project or a committee")
        .WithDescription("Exactly one of `projectSlug` and `committeeId`. The assignee must be an active member and is notified.")
        .WithTags(Tag).RequirePermission(Permissions.MembershipManage);

        app.MapPut($"{Admin}/{{id}}", async (HttpContext context, string id, TaskRequest payload, MembershipDbContext db, TimeProvider clock) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
            if (task is null)
            {
                return NotFound(context, "Task not found.");
            }

            var previousAssignee = task.AssigneeMemberId;
            var previousDue = task.DueOn;
            if (await Apply(task, payload, db, creating: false) is { } error)
            {
                return Invalid(context, error);
            }

            Touch(task, clock);
            if (task.AssigneeMemberId != previousAssignee)
            {
                Notify(db, task.AssigneeMemberId, "task.assigned", task, clock);
            }
            else if (task.DueOn != previousDue && task.Status != TaskStatuses.Done)
            {
                Notify(db, task.AssigneeMemberId, "task.due_changed", task, clock);
            }

            return await Save(context, db, task, clock);
        })
        .WithName("UpdateTask").WithSummary("Edit, reassign or move a task; fields left out stay")
        .WithDescription("Reassigning notifies the new assignee; changing the due date notifies the assignee.")
        .WithTags(Tag).RequirePermission(Permissions.MembershipManage);

        app.MapDelete($"{Admin}/{{id}}", async (HttpContext context, string id, MembershipDbContext db) =>
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
            if (task is null)
            {
                return NotFound(context, "Task not found.");
            }

            db.Tasks.Remove(task);
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .WithName("DeleteTask").WithSummary("Delete a task")
        .WithTags(Tag).RequirePermission(Permissions.MembershipManage);

        app.MapGet($"{Admin}/workload", async (MembershipDbContext db, TimeProvider clock) =>
        {
            var today = Today(clock);
            var members = await db.Members.AsNoTracking().ToDictionaryAsync(m => m.Id);
            var open = (await db.Tasks.AsNoTracking().ToListAsync()).Where(t => TaskStatuses.Open.Contains(t.Status)).ToList();
            var rows = open.GroupBy(t => t.AssigneeMemberId).Select(g =>
            {
                members.TryGetValue(g.Key, out var m);
                return new WorkloadRow(g.Key, m?.MemberCode ?? string.Empty, m is null ? string.Empty : $"{m.FirstName} {m.LastName}", g.Count(),
                    g.Count(t => t.Status == TaskStatuses.Todo), g.Count(t => t.Status == TaskStatuses.InProgress), g.Count(t => t.Status == TaskStatuses.Blocked),
                    g.Count(t => t.DueOn < today), g.Count(t => t.DueOn >= today && t.DueOn <= today.AddDays(DueSoonDays)), g.Count(t => t.Priority == "high"));
            }).OrderByDescending(r => r.Open).ThenByDescending(r => r.Overdue).ThenBy(r => r.MemberCode, StringComparer.Ordinal).ToList();
            return Results.Ok(new { items = rows, openTotal = open.Count });
        })
        .WithName("TaskWorkload").WithSummary("Open tasks per member, busiest first")
        .WithDescription("Counts open tasks (todo, in progress, blocked) per assignee with overdue, due-soon and high-priority counts, so an overloaded volunteer shows up before they burn out.")
        .WithTags(Tag).RequirePermission(Permissions.MembershipManage);

        app.MapPost($"{Admin}/reminders/run", async (MembershipDbContext db, TimeProvider clock) =>
        {
            var today = Today(clock);
            var open = (await db.Tasks.ToListAsync()).Where(t => TaskStatuses.Open.Contains(t.Status) && t.DueOn is not null).ToList();
            var soon = 0;
            var overdue = 0;
            foreach (var task in open)
            {
                if (task.DueOn < today && task.OverdueNoticeForDueOn != task.DueOn)
                {
                    task.OverdueNoticeForDueOn = task.DueOn;
                    Notify(db, task.AssigneeMemberId, "task.overdue", task, clock);
                    overdue++;
                }
                else if (task.DueOn >= today && task.DueOn <= today.AddDays(DueSoonDays) && task.ReminderForDueOn != task.DueOn)
                {
                    task.ReminderForDueOn = task.DueOn;
                    Notify(db, task.AssigneeMemberId, "task.due_soon", task, clock);
                    soon++;
                }
            }

            await db.SaveChangesAsync();
            return Results.Ok(new { dueSoon = soon, overdue });
        })
        .WithName("RunTaskReminders").WithSummary("Create due-soon and overdue notices, once per due date")
        .WithDescription("Safe to call repeatedly: a task is reminded once per due date. Run it daily from a timer.")
        .WithTags(Tag).RequirePermission(Permissions.MembershipManage);

        // ---- the assignee

        app.MapGet($"{Me}/tasks", (HttpContext context, MembershipDbContext db, TimeProvider clock, string? status) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var members = new Dictionary<string, MemberEntity> { [member.Id] = member };
                var today = Today(clock);
                var rows = (await db.Tasks.AsNoTracking().Where(t => t.AssigneeMemberId == member.Id).ToListAsync())
                    .Where(t => status is null || t.Status == status).OrderBy(t => t.Status == TaskStatuses.Done ? 1 : 0).ThenBy(t => t.DueOn ?? DateOnly.MaxValue).ToList();
                return Results.Ok(new { items = rows.Select(t => ToView(t, members, today)) });
            }))
        .WithName("ListMyTasks").WithSummary("My tasks")
        .WithTags(Tag).Own();

        app.MapPut($"{Me}/tasks/{{id}}/status", (HttpContext context, string id, StatusRequest payload, MembershipDbContext db, TimeProvider clock) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id && t.AssigneeMemberId == member.Id);
                if (task is null)
                {
                    return NotFound(context, "Task not found.");
                }

                if (payload.Status is null || !TaskStatuses.All.Contains(payload.Status))
                {
                    return Invalid(context, "status must be todo, in_progress, blocked or done.");
                }

                task.Status = payload.Status;
                Touch(task, clock);
                return await Save(context, db, task, clock);
            }))
        .WithName("SetMyTaskStatus").WithSummary("Move my task between todo, in progress, blocked and done")
        .WithTags(Tag).Own();

        app.MapGet($"{Me}/inbox", (HttpContext context, MembershipDbContext db, bool? unread) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var rows = (await db.MemberNotifications.AsNoTracking().Where(n => n.MemberId == member.Id).ToListAsync())
                    .Where(n => unread != true || n.ReadAt is null).OrderByDescending(n => n.Id).Take(100).ToList();
                return Results.Ok(new
                {
                    items = rows.Select(n => new { n.Id, n.Template, payload = JsonSerializer.Deserialize<JsonElement>(n.PayloadJson, Json), n.CreatedAt, read = n.ReadAt is not null }),
                    unread = await db.MemberNotifications.CountAsync(n => n.MemberId == member.Id && n.ReadAt == null)
                });
            }))
        .WithName("ListMyInbox").WithSummary("My notification centre (the preferences stay at /members/me/notifications)")
        .WithTags(Tag).Own();

        app.MapPost($"{Me}/inbox/read", (HttpContext context, MembershipDbContext db, TimeProvider clock) =>
            SelfEndpoints.WithMember(context, db, async member =>
            {
                var now = clock.GetUtcNow();
                foreach (var note in await db.MemberNotifications.Where(n => n.MemberId == member.Id && n.ReadAt == null).ToListAsync())
                {
                    note.ReadAt = now;
                }

                await db.SaveChangesAsync();
                return Results.NoContent();
            }))
        .WithName("MarkMyInboxRead").WithSummary("Mark all my notifications read")
        .WithTags(Tag).Own();
    }

    // ---- logic

    private static async Task<IResult> Save(HttpContext context, MembershipDbContext db, TaskAssignmentEntity task, TimeProvider clock)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiError.Create(context, StatusCodes.Status409Conflict, "CONFLICT", "The task changed while you were editing it. Reload it.");
        }

        return Results.Ok(await Single(db, task, clock));
    }

    private static void Touch(TaskAssignmentEntity task, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        task.UpdatedAt = now;
        task.CompletedAt = task.Status == TaskStatuses.Done ? task.CompletedAt ?? now : null;
    }

    private static void Notify(MembershipDbContext db, string memberId, string template, TaskAssignmentEntity task, TimeProvider clock) =>
        db.MemberNotifications.Add(new MemberNotificationEntity
        {
            MemberId = memberId,
            Template = template,
            PayloadJson = JsonSerializer.Serialize(new { taskId = task.Id, title = task.Title, dueOn = task.DueOn, task.ProjectSlug, task.CommitteeId, task.Priority }, Json),
            CreatedAt = clock.GetUtcNow()
        });

    private static async Task<string?> Apply(TaskAssignmentEntity task, TaskRequest request, MembershipDbContext db, bool creating)
    {
        if (creating && (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.AssigneeMemberId)))
        {
            return "title and assigneeMemberId are required.";
        }

        if (request.Title?.Trim().Length > 200 || request.Description?.Length > 2000)
        {
            return "A field is too long.";
        }

        if (request.Priority is not null && !Priorities.Contains(request.Priority))
        {
            return "priority must be low, normal or high.";
        }

        if (request.Status is not null && !TaskStatuses.All.Contains(request.Status))
        {
            return "status must be todo, in_progress, blocked or done.";
        }

        var project = request.ProjectSlug is null ? task.ProjectSlug : request.ProjectSlug.Trim().Length == 0 ? null : request.ProjectSlug.Trim();
        var committee = request.CommitteeId is null ? task.CommitteeId : request.CommitteeId.Trim().Length == 0 ? null : request.CommitteeId.Trim();
        if (request.ProjectSlug is { Length: > 0 } && request.CommitteeId is null)
        {
            committee = null;
        }

        if (request.CommitteeId is { Length: > 0 } && request.ProjectSlug is null)
        {
            project = null;
        }

        if (project is null == committee is null)
        {
            return "A task belongs to exactly one of projectSlug and committeeId.";
        }

        if (project is not null && !SlugPattern().IsMatch(project))
        {
            return "projectSlug must be a project slug.";
        }

        if (committee is not null && !await db.Committees.AnyAsync(c => c.Id == committee && c.ArchivedAt == null))
        {
            return "committeeId must be an active committee.";
        }

        var assignee = request.AssigneeMemberId?.Trim() ?? task.AssigneeMemberId;
        if (assignee != task.AssigneeMemberId && !await db.Members.AnyAsync(m => m.Id == assignee && m.Status == "active"))
        {
            return "assigneeMemberId must be an active member.";
        }

        task.Title = request.Title?.Trim() ?? task.Title;
        task.Description = request.Description?.Trim() ?? task.Description;
        task.AssigneeMemberId = assignee;
        task.ProjectSlug = project;
        task.CommitteeId = committee;
        task.DueOn = request.DueOn ?? task.DueOn;
        task.Priority = request.Priority ?? task.Priority;
        task.Status = request.Status ?? task.Status;
        return null;
    }

    private static async Task<TaskView> Single(MembershipDbContext db, TaskAssignmentEntity task, TimeProvider clock)
    {
        var members = await db.Members.AsNoTracking().Where(m => m.Id == task.AssigneeMemberId).ToDictionaryAsync(m => m.Id);
        return ToView(task, members, Today(clock));
    }

    private static TaskView ToView(TaskAssignmentEntity t, Dictionary<string, MemberEntity> members, DateOnly today)
    {
        members.TryGetValue(t.AssigneeMemberId, out var m);
        return new TaskView(t.Id, t.Title, t.Description, t.AssigneeMemberId, m is null ? string.Empty : $"{m.FirstName} {m.LastName}", t.ProjectSlug, t.CommitteeId, t.DueOn, t.Priority, t.Status,
            t.Status != TaskStatuses.Done && t.DueOn < today, t.UpdatedAt, t.CompletedAt);
    }

    private static IResult Invalid(HttpContext context, string message) => ApiError.Create(context, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", message);

    private static IResult NotFound(HttpContext context, string message) => ApiError.Create(context, StatusCodes.Status404NotFound, "NOT_FOUND", message);
}
