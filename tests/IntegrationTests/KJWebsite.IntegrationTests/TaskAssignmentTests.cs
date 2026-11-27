using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using MembershipService.Data;
using MembershipService.Data.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace KJWebsite.IntegrationTests;

public sealed class TaskAssignmentTests : IDisposable
{
    private const string Admin = "/api/v1/admin/membership/tasks";
    private const string Me = "/api/v1/members/me";
    private readonly MembershipApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly string _officer = TestTokens.Issue([Permissions.MembershipManage], subject: "usr_officer");

    public TaskAssignmentTests()
    {
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MembershipDbContext>();
        foreach (var key in new[] { "amin", "bina", "chandra" })
        {
            db.Members.Add(new MemberEntity { Id = $"mem_{key}", UserId = $"usr_{key}", MemberCode = $"KJ-2026-{key}", ApplicationId = $"app_{key}", FirstName = key, LastName = "T", Status = "active", JoinedAt = DateTimeOffset.UtcNow });
        }

        db.Committees.Add(new CommitteeEntity { Id = "com_one", Slug = "media", Kind = "committee", NameEn = "Media", NameBn = "মিডিয়া", CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
    }

    [Fact]
    public async Task TasksAttachToProjectsAndCommitteesWithAnAssigneeAndDueDate()
    {
        var due = Today(5);
        var project = await Create(new { title = "Order books", assigneeMemberId = "mem_amin", projectSlug = "pathshala", dueOn = due, priority = "high" });
        var committee = await Create(new { title = "Design poster", assigneeMemberId = "mem_bina", committeeId = "com_one" });
        project.GetProperty("assigneeName").GetString().Should().Be("amin T");
        committee.GetProperty("committeeId").GetString().Should().Be("com_one");

        (await Send(_officer, HttpMethod.Post, Admin, new { title = "Both", assigneeMemberId = "mem_amin", projectSlug = "p", committeeId = "com_one" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(_officer, HttpMethod.Post, Admin, new { title = "Neither", assigneeMemberId = "mem_amin" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(_officer, HttpMethod.Post, Admin, new { title = "Nobody", assigneeMemberId = "mem_ghost", projectSlug = "p" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(_officer, HttpMethod.Post, Admin, new { title = "Bad status", assigneeMemberId = "mem_amin", projectSlug = "p", status = "review" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var byProject = await Send(_officer, HttpMethod.Get, $"{Admin}?project=pathshala");
        JsonDocument.Parse(await byProject.Content.ReadAsStringAsync()).RootElement.GetProperty("total").GetInt32().Should().Be(1);
        (await _client.GetAsync(Admin)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_amin"), HttpMethod.Get, Admin)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WorkloadShowsOpenTasksPerMemberAndIgnoresDoneOnes()
    {
        await Create(new { title = "A1", assigneeMemberId = "mem_amin", projectSlug = "p", dueOn = Today(-1), priority = "high" });
        await Create(new { title = "A2", assigneeMemberId = "mem_amin", projectSlug = "p", dueOn = Today(1), status = "in_progress" });
        await Create(new { title = "A3", assigneeMemberId = "mem_amin", projectSlug = "p", status = "blocked" });
        var done = await Create(new { title = "A4", assigneeMemberId = "mem_amin", projectSlug = "p" });
        await Create(new { title = "B1", assigneeMemberId = "mem_bina", projectSlug = "p" });
        (await Send(TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_amin"), HttpMethod.Put, $"{Me}/tasks/{done.GetProperty("id").GetString()}/status", new { status = "done" })).StatusCode.Should().Be(HttpStatusCode.OK);

        using var workload = await Send(_officer, HttpMethod.Get, $"{Admin}/workload");
        var rows = JsonDocument.Parse(await workload.Content.ReadAsStringAsync()).RootElement.GetProperty("items").EnumerateArray().ToList();
        rows[0].GetProperty("memberCode").GetString().Should().Be("KJ-2026-amin", "the busiest member is listed first");
        rows[0].GetProperty("open").GetInt32().Should().Be(3);
        rows[0].GetProperty("overdue").GetInt32().Should().Be(1);
        rows[0].GetProperty("dueSoon").GetInt32().Should().Be(1);
        rows[0].GetProperty("blocked").GetInt32().Should().Be(1);
        rows[0].GetProperty("high").GetInt32().Should().Be(1);
        rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task AssignmentAndDueDatesNotifyTheAssigneeOnce()
    {
        var task = await Create(new { title = "Print flyers", assigneeMemberId = "mem_chandra", projectSlug = "p", dueOn = Today(1) });
        var id = task.GetProperty("id").GetString();
        var chandra = TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_chandra");

        using var first = await Send(chandra, HttpMethod.Get, $"{Me}/inbox");
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var items = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        items.GetProperty("items")[0].GetProperty("template").GetString().Should().Be("task.assigned");
        items.GetProperty("items")[0].GetProperty("payload").GetProperty("title").GetString().Should().Be("Print flyers");
        items.GetProperty("unread").GetInt32().Should().Be(1);

        using var run = await Send(_officer, HttpMethod.Post, $"{Admin}/reminders/run");
        run.StatusCode.Should().Be(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        JsonDocument.Parse(await run.Content.ReadAsStringAsync()).RootElement.GetProperty("dueSoon").GetInt32().Should().Be(1);
        using var again = await Send(_officer, HttpMethod.Post, $"{Admin}/reminders/run");
        JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement.GetProperty("dueSoon").GetInt32().Should().Be(0, "a due date is reminded once");

        // Reassigning notifies the new assignee; a moved due date notifies the current one.
        (await Send(_officer, HttpMethod.Put, $"{Admin}/{id}", new { assigneeMemberId = "mem_bina" })).StatusCode.Should().Be(HttpStatusCode.OK);
        using var bina = await Send(TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_bina"), HttpMethod.Get, $"{Me}/inbox");
        (await bina.Content.ReadAsStringAsync()).Should().Contain("task.assigned");
        (await Send(_officer, HttpMethod.Put, $"{Admin}/{id}", new { dueOn = Today(9) })).StatusCode.Should().Be(HttpStatusCode.OK);
        using var binaAgain = await Send(TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_bina"), HttpMethod.Get, $"{Me}/inbox");
        (await binaAgain.Content.ReadAsStringAsync()).Should().Contain("task.due_changed");

        (await Send(chandra, HttpMethod.Post, $"{Me}/inbox/read")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var read = await Send(chandra, HttpMethod.Get, $"{Me}/inbox?unread=true");
        JsonDocument.Parse(await read.Content.ReadAsStringAsync()).RootElement.GetProperty("unread").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task AssigneesMoveOnlyTheirOwnTasksThroughTheFourStatuses()
    {
        var task = await Create(new { title = "Mine", assigneeMemberId = "mem_amin", projectSlug = "p" });
        var id = task.GetProperty("id").GetString();
        var amin = TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_amin");
        var bina = TestTokens.Issue([Permissions.MemberProfileEdit], subject: "usr_bina");

        (await Send(bina, HttpMethod.Put, $"{Me}/tasks/{id}/status", new { status = "done" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(amin, HttpMethod.Put, $"{Me}/tasks/{id}/status", new { status = "review" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var done = await Send(amin, HttpMethod.Put, $"{Me}/tasks/{id}/status", new { status = "done" });
        JsonDocument.Parse(await done.Content.ReadAsStringAsync()).RootElement.GetProperty("completedAt").ValueKind.Should().Be(JsonValueKind.String);
        using var mine = await Send(amin, HttpMethod.Get, $"{Me}/tasks");
        JsonDocument.Parse(await mine.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength().Should().Be(1);
        using var theirs = await Send(bina, HttpMethod.Get, $"{Me}/tasks");
        JsonDocument.Parse(await theirs.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    private static string Today(int plusDays) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(plusDays).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<JsonElement> Create(object body)
    {
        using var response = await Send(_officer, HttpMethod.Post, Admin, body);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> Send(string token, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
