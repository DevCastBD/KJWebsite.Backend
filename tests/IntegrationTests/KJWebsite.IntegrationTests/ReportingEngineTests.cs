using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReportingService.Data;
using ReportingService.Data.Entities;
using ReportingService.Reports;

namespace KJWebsite.IntegrationTests;

public sealed class ReportingEngineTests : IDisposable
{
    private const string Route = "/api/v1/admin/reporting";
    private const string Bengali = "বিদ্যালয় পরিদর্শন";
    private static readonly string[] Viewer = [Permissions.ReportingView];
    private static readonly string[] Manager = [Permissions.ReportingView, Permissions.ReportingManage];
    private static readonly string[] FinanceManager = [Permissions.ReportingView, Permissions.ReportingManage, Permissions.FinanceView];
    private static readonly string[] FinanceRecipients = [Permissions.ReportingView, Permissions.ReportingManage, Permissions.FinanceView, Permissions.ReportingRecipients];
    private readonly ReportingApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly DateOnly _thisMonth;

    public ReportingEngineTests()
    {
        _client = _factory.CreateClient();
        _thisMonth = new DateOnly(_factory.Clock.GetUtcNow().Year, _factory.Clock.GetUtcNow().Month, 1);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
        foreach (var offset in new[] { 3, 2, 1 })
        {
            db.AggregatePoints.Add(new AggregatePointEntity { Source = "membership", Metric = "sessions.delivered", Period = _thisMonth.AddMonths(-offset), Dimension = Bengali, Value = 10 * offset });
            db.AggregatePoints.Add(new AggregatePointEntity { Source = "finance", Metric = "donations.confirmed.amount", Period = _thisMonth.AddMonths(-offset), Dimension = "general", Currency = "BDT", Value = 1000 * offset });
            db.AggregatePoints.Add(new AggregatePointEntity { Source = "finance", Metric = "donations.confirmed.count", Period = _thisMonth.AddMonths(-offset), Dimension = "general", Currency = "BDT", Value = offset });
        }

        db.SaveChanges();
    }

    private static HttpRequestMessage Req(HttpMethod method, string url, string[] permissions, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Issue(permissions, subject: "usr_" + permissions.Length));
        return request;
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string[] permissions, object? body = null) => _client.SendAsync(Req(method, url, permissions, body));

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<string> Save(string name, string template, object? parameters, string[]? permissions = null)
    {
        var response = await Send(HttpMethod.Post, $"{Route}/reports", permissions ?? Manager, new { name, template, parameters });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> Run(string id, object? body = null, string[]? permissions = null)
    {
        var response = await Send(HttpMethod.Post, $"{Route}/reports/{id}/runs", permissions ?? Viewer, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await Json(response);
    }

    private async Task<byte[]> Download(JsonElement run, string format, string[]? permissions = null)
    {
        var url = run.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("format").GetString() == format).GetProperty("url").GetString()!;
        var response = await Send(HttpMethod.Get, url, permissions ?? Viewer);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync();
    }

    [Fact]
    public async Task BengaliSurvivesCsvXlsxAndPdfExports()
    {
        var id = await Save("Sessions report", "aggregate-series", new { metric = "sessions.delivered", months = "6" });
        var run = await Run(id);
        run.GetProperty("status").GetString().Should().Be("succeeded");
        run.GetProperty("rows").GetInt32().Should().BeGreaterThan(3);

        // a report with several tables exports its CSV as one zip with a file per table
        using (var zip = new ZipArchive(new MemoryStream(await Download(run, "csv"))))
        {
            zip.Entries.Select(e => e.Name).Should().Contain("series.csv");
            using var entry = zip.GetEntry("series.csv")!.Open();
            var bytes = new MemoryStream();
            await entry.CopyToAsync(bytes);
            bytes.ToArray().Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
            Encoding.UTF8.GetString(bytes.ToArray()).Should().Contain(Bengali);
        }

        var xlsx = await Download(run, "xlsx");
        using (var zip = new ZipArchive(new MemoryStream(xlsx)))
        {
            using var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8);
            (await sheet.ReadToEndAsync()).Should().Contain(Bengali);
        }

        var pdf = await Download(run, "pdf");
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        Encoding.Latin1.GetString(pdf).Should().Contain("NotoSansBengali", "the Bengali face is embedded, not looked up on the server");
    }

    [Fact]
    public async Task ParametersNarrowARunAndBadOnesAreRefused()
    {
        var id = await Save("Sessions", "aggregate-series", new { metric = "sessions.delivered" });
        var all = await Run(id, new { formats = new[] { "csv" } });
        var one = await Run(id, new { parameters = new { month = _thisMonth.AddMonths(-2).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) }, formats = new[] { "csv" } });
        all.GetProperty("rows").GetInt32().Should().BeGreaterThan(one.GetProperty("rows").GetInt32());
        one.GetProperty("files").GetArrayLength().Should().Be(1);
        one.GetProperty("parameters").GetProperty("month").GetString().Should().Be(_thisMonth.AddMonths(-2).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));

        (await Send(HttpMethod.Post, $"{Route}/reports/{id}/runs", Viewer, new { parameters = new { bogus = "1" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Route}/reports/{id}/runs", Viewer, new { formats = new[] { "docx" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Route}/reports", Manager, new { name = "x", template = "aggregate-series" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Route}/reports", Manager, new { name = "x", template = "nothing" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Route}/reports/{id}/runs", Viewer, new { parameters = new { metric = "donations.confirmed.amount" } })).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "swapping in a financial metric must not turn a general report into a financial one");
    }

    [Fact]
    public async Task FinancialReportsNeedFinanceViewToSaveListRunAndDownload()
    {
        (await Send(HttpMethod.Post, $"{Route}/reports", Manager, new { name = "Money", template = "financial-summary" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var id = await Save("Monthly money", "financial-summary", new { months = "6" }, FinanceManager);
        await Save("Impact", "impact-summary", null);

        (await Json(await Send(HttpMethod.Get, $"{Route}/reports", Viewer))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).Should().Equal("Impact");
        (await Json(await Send(HttpMethod.Get, $"{Route}/reports", FinanceManager))).GetProperty("items").GetArrayLength().Should().Be(2);
        (await Send(HttpMethod.Post, $"{Route}/reports/{id}/runs", Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var run = await Run(id, null, FinanceManager);
        (await Send(HttpMethod.Get, run.GetProperty("files")[0].GetProperty("url").GetString()!, Viewer)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Download(run, "xlsx", FinanceManager)).Should().NotBeEmpty();

        var watch = Stopwatch.StartNew();
        await Run(id, null, FinanceManager);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "report generation target");
    }

    [Fact]
    public async Task ARecipientOfAFinancialReportNeedsTheRecipientsPermissionAndIsAudited()
    {
        var id = await Save("Monthly money", "financial-summary", null, FinanceManager);
        var schedule = await Json(await Send(HttpMethod.Post, $"{Route}/reports/{id}/schedules", FinanceManager, new { dayOfMonth = 5, hourUtc = 4, formats = new[] { "xlsx", "pdf" } }));
        var scheduleId = schedule.GetProperty("id").GetString()!;

        (await Send(HttpMethod.Post, $"{Route}/report-schedules/{scheduleId}/recipients", FinanceManager, new { email = "trustee@example.org" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var added = await Send(HttpMethod.Post, $"{Route}/report-schedules/{scheduleId}/recipients", FinanceRecipients, new { email = "trustee@example.org" });
        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var recipientId = (await Json(added)).GetProperty("id").GetString()!;
        (await Send(HttpMethod.Post, $"{Route}/report-schedules/{scheduleId}/recipients", FinanceRecipients, new { email = "TRUSTEE@example.org" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Send(HttpMethod.Post, $"{Route}/report-schedules/{scheduleId}/recipients", FinanceRecipients, new { email = "not-an-address" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Delete, $"{Route}/report-schedules/{scheduleId}/recipients/{recipientId}", FinanceManager)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var listed = await Json(await Send(HttpMethod.Get, $"{Route}/reports/{id}/schedules", FinanceManager));
        listed.GetProperty("items")[0].GetProperty("recipients")[0].GetProperty("email").GetString().Should().Be("trustee@example.org");

        (await Send(HttpMethod.Delete, $"{Route}/report-schedules/{scheduleId}/recipients/{recipientId}", FinanceRecipients)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var audit = await (await Send(HttpMethod.Get, $"{Route}/audit?entity=ReportRecipient", [Permissions.AuditView])).Content.ReadAsStringAsync();
        audit.Should().Contain("ReportRecipient").And.Contain(recipientId).And.Contain("removedAt", "the removal is logged").And.NotContain("trustee@example.org", "the address stays out of the audit values");
    }

    [Fact]
    public async Task AScheduledReportIsDeliveredEachMonthWithNoManualStep()
    {
        var id = await Save("Impact monthly", "impact-summary", null);
        var scheduleId = await Schedule(id, Manager);
        await AddRecipient(scheduleId, Manager, "chair@example.org");
        await AddRecipient(scheduleId, Manager, "treasurer@example.org");

        // before the due time nothing happens
        (await RunDue()).Should().Be(new DueRunResult(0, 0, 0));

        _factory.Clock.Set(new DateTimeOffset(_thisMonth.Year, _thisMonth.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).AddDays(6));
        (await RunDue()).Should().Be(new DueRunResult(1, 1, 0));
        _factory.Mailer.Sent.Select(m => m.To).Should().BeEquivalentTo("chair@example.org", "treasurer@example.org");
        _factory.Mailer.Sent[0].Attachments.Select(a => a.FileName.Split('.').Last()).Should().BeEquivalentTo("xlsx", "pdf");
        _factory.Mailer.Sent[0].Subject.Should().Contain(_factory.Clock.GetUtcNow().AddMonths(-1).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), "it covers the previous month");

        (await RunDue()).Should().Be(new DueRunResult(0, 0, 0), "the next run is next month");
        var schedule = (await Json(await Send(HttpMethod.Get, $"{Route}/reports/{id}/schedules", Manager))).GetProperty("items")[0];
        schedule.GetProperty("nextRunAt").GetDateTimeOffset().Should().BeAfter(_factory.Clock.GetUtcNow().AddDays(20));
        schedule.GetProperty("failureCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task AFailedDeliveryAlertsRetriesFromTheSameFilesAndSendsOnlyToThoseMissed()
    {
        var id = await Save("Impact monthly", "impact-summary", null);
        var scheduleId = await Schedule(id, Manager);
        await AddRecipient(scheduleId, Manager, "chair@example.org");
        _factory.Clock.Set(new DateTimeOffset(_thisMonth.Year, _thisMonth.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).AddDays(6));

        _factory.Mailer.Fail = true;
        (await RunDue()).Should().Be(new DueRunResult(1, 0, 1));
        _factory.Mailer.Sent.Should().BeEmpty();
        var failing = (await Json(await Send(HttpMethod.Get, $"{Route}/reports/{id}/schedules", Manager))).GetProperty("items")[0];
        failing.GetProperty("failureCount").GetInt32().Should().Be(1);
        failing.GetProperty("active").GetBoolean().Should().BeTrue("a failed month is retried, never skipped");

        await AddRecipient(scheduleId, Manager, "late@example.org");
        _factory.Mailer.Fail = false;
        (await RunDue()).Should().Be(new DueRunResult(0, 0, 0), "the retry is not due yet");

        _factory.Clock.Advance(TimeSpan.FromMinutes(61));
        (await RunDue()).Should().Be(new DueRunResult(1, 1, 0));
        _factory.Mailer.Sent.Select(m => m.To).Should().BeEquivalentTo("chair@example.org", "late@example.org");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportingDbContext>();
        (await db.ReportRuns.CountAsync(r => r.ScheduleId == scheduleId && r.Status == RunStatuses.Succeeded)).Should().Be(1, "the retry reused the files of the first attempt");
        (await db.ReportDeliveries.CountAsync(d => d.Status == DeliveryStatuses.Failed)).Should().Be(1);
        (await db.ReportSchedules.FirstAsync(s => s.Id == scheduleId)).FailureCount.Should().Be(0);
    }

    [Fact]
    public async Task ARemovedRecipientStopsReceivingAndWithoutMailServerTheRunFailsLoudly()
    {
        var id = await Save("Impact monthly", "impact-summary", null);
        var scheduleId = await Schedule(id, Manager);
        var kept = await AddRecipient(scheduleId, Manager, "kept@example.org");
        var gone = await AddRecipient(scheduleId, Manager, "gone@example.org");
        (await Send(HttpMethod.Delete, $"{Route}/report-schedules/{scheduleId}/recipients/{gone}", Manager)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Delete, $"{Route}/report-schedules/{scheduleId}/recipients/{gone}", Manager)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Clock.Set(new DateTimeOffset(_thisMonth.Year, _thisMonth.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).AddDays(6));
        (await RunDue()).Delivered.Should().Be(1);
        _factory.Mailer.Sent.Select(m => m.To).Should().Equal("kept@example.org");
        kept.Should().NotBeNullOrEmpty();

        (await Send(HttpMethod.Post, $"{Route}/reports/{id}/schedules", Manager, new { dayOfMonth = 30, hourUtc = 4 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Route}/reports/{id}/schedules", Manager, new { dayOfMonth = 5, hourUtc = 24 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Post, $"{Route}/reports/{id}/schedules", Manager, new { dayOfMonth = 5, hourUtc = 4, formats = new[] { "doc" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await new NotConfiguredReportMailer().SendAsync(new ReportMail("a@example.org", "s", "b", []), CancellationToken.None)).Error!.Code.Should().Be("MAIL_NOT_CONFIGURED");
    }

    [Fact]
    public async Task ArchivingStopsSchedulesAndAccessIsPermissionGated()
    {
        var id = await Save("Impact monthly", "impact-summary", null);
        var scheduleId = await Schedule(id, Manager);
        (await Send(HttpMethod.Delete, $"{Route}/reports/{id}", Manager)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Post, $"{Route}/reports/{id}/schedules", Manager, new { dayOfMonth = 5, hourUtc = 4 })).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _factory.Clock.Set(new DateTimeOffset(_thisMonth.Year, _thisMonth.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1).AddDays(6));
        (await RunDue()).Due.Should().Be(0);
        (await Send(HttpMethod.Delete, $"{Route}/report-schedules/{scheduleId}", Manager)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.GetAsync($"{Route}/reports")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, $"{Route}/reports", Viewer, new { name = "x", template = "impact-summary" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, $"{Route}/reports", [Permissions.FinanceView])).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Send(HttpMethod.Get, $"{Route}/report-templates", Viewer)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Get, $"{Route}/reports/rpt_missing", Viewer)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<string> Schedule(string reportId, string[] permissions)
    {
        var response = await Send(HttpMethod.Post, $"{Route}/reports/{reportId}/schedules", permissions, new { dayOfMonth = 5, hourUtc = 4, formats = new[] { "xlsx", "pdf" } });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<string> AddRecipient(string scheduleId, string[] permissions, string email)
    {
        var response = await Send(HttpMethod.Post, $"{Route}/report-schedules/{scheduleId}/recipients", permissions, new { email });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private async Task<DueRunResult> RunDue()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ReportScheduler>().RunDueAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
