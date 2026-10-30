using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CtaSubmissionService.Data;
using CtaSubmissionService.Email;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Email;
using KJWebsite.BuildingBlocks.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace KJWebsite.IntegrationTests;

public sealed class EmailTemplateTests
{
    private static readonly CtaEmailData Data = new("subm_abc123", "contact", "রহিম উদ্দিন", "rahim@example.com", "+8801712345678", "Hello <b>team</b>", new DateTimeOffset(2026, 10, 30, 9, 30, 0, TimeSpan.Zero));

    public static TheoryData<CtaEmailKind, string> All()
    {
        var data = new TheoryData<CtaEmailKind, string>();
        foreach (var kind in Enum.GetValues<CtaEmailKind>())
        {
            data.Add(kind, "bn");
            data.Add(kind, "en");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(All))]
    public void EveryTemplateRendersInBothLanguages(CtaEmailKind kind, string language)
    {
        var mail = CtaEmailTemplates.Render(kind, language, "to@example.com", Data with { Name = "Rahim Uddin", Message = "Hello" });

        mail.Subject.Should().NotBeNullOrWhiteSpace();
        mail.HtmlBody.Should().Contain($"<html lang=\"{language}\"").And.Contain("subm_abc123");
        mail.TextBody.Should().Contain("subm_abc123");
        mail.To.Should().Be("to@example.com");
        var hasBengali = mail.HtmlBody.Any(c => c is >= 'ঀ' and <= '৿');
        hasBengali.Should().Be(language == "bn", "Bengali text appears in Bengali mail and never in English mail");
    }

    [Fact]
    public void BengaliTextAndNamesSurviveIntactAndMarkupIsEscaped()
    {
        var ack = CtaEmailTemplates.Render(CtaEmailKind.ContactAck, "bn", "to@example.com", Data);
        ack.HtmlBody.Should().Contain("প্রিয় রহিম উদ্দিন,").And.Contain("আপনার রেফারেন্স নম্বর");

        var admin = CtaEmailTemplates.Render(CtaEmailKind.AdminNotification, "en", "org@example.com", Data);
        admin.HtmlBody.Should().Contain("Hello &lt;b&gt;team&lt;/b&gt;").And.NotContain("<b>team</b>");
        admin.TextBody.Should().Contain("Phone: +8801712345678");
    }

    [Fact]
    public void AcknowledgementsAreAddressedGenericallyWhenThereIsNoName()
    {
        CtaEmailTemplates.Render(CtaEmailKind.ContactAck, "en", "x@example.com", Data with { Name = null }).TextBody.Should().Contain("Hello,");
        CtaEmailTemplates.Render(CtaEmailKind.RegistrationReceived, "bn", "x@example.com", Data with { Name = " " }).TextBody.Should().Contain("প্রিয় বন্ধু,");
    }
}

public sealed class EmailDeliveryTests : IDisposable
{
    private readonly string _mailDir = Path.Combine(Path.GetTempPath(), $"kj-mail-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_mailDir))
        {
            Directory.Delete(_mailDir, recursive: true);
        }
    }

    private WebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint> Factory(string? orgInbox = "team@example.org", IEmailSender? sender = null) =>
        new CtaApiFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("Email:File:Directory", _mailDir);
            if (orgInbox is not null)
            {
                b.UseSetting("Email:OrgInbox", orgInbox);
            }

            if (sender is not null)
            {
                b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton(sender)));
            }
        });

    private static Task<HttpResponseMessage> Submit(HttpClient client, string form, string language, object? email) =>
        client.PostAsJsonAsync("/api/v1/cta/submissions", new
        {
            formName = form,
            ctaType = form == "registration" ? "join" : "contact",
            language,
            sourcePath = "/x",
            values = new Dictionary<string, object?> { ["firstName"] = "Rahim", ["lastName"] = "Uddin", ["email"] = email, ["message"] = "Hi" },
            submittedAt = DateTimeOffset.UtcNow
        });

    private static async Task<int> Dispatch(WebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint> factory)
    {
        var handlers = factory.Services.GetServices<IOutboxHandler>();
        var dispatcher = new OutboxDispatcher<CtaDbContext>(factory.Services.GetRequiredService<IServiceScopeFactory>(), handlers,
            Options.Create(new OutboxOptions { Jitter = 0 }), TimeProvider.System, NullLogger<OutboxDispatcher<CtaDbContext>>.Instance);
        return await dispatcher.RunAsync();
    }

    private string[] Mail(string extension) => Directory.Exists(_mailDir) ? [.. Directory.GetFiles(_mailDir, "*." + extension).Select(File.ReadAllText)] : [];

    [Fact]
    public async Task AContactSubmissionProducesABilingualAcknowledgementAndAnOrgNotification()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        (await Submit(client, "contact", "bn", "rahim@example.com")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Dispatch(factory)).Should().Be(2);

        var text = Mail("txt");
        text.Should().HaveCount(2);
        text.Should().Contain(t => t.StartsWith("To: rahim@example.com", StringComparison.Ordinal) && t.Contains("আপনার বার্তা পেয়েছি"));
        text.Should().Contain(t => t.StartsWith("To: team@example.org", StringComparison.Ordinal) && t.Contains("নতুন জমা: contact") && t.Contains("rahim@example.com"));
    }

    [Fact]
    public async Task EnglishSubmittersGetEnglishAndVolunteerApplicationsGetTheirOwnTemplate()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        (await Submit(client, "registration", "en", "rahim@example.com")).StatusCode.Should().Be(HttpStatusCode.Created);
        await Dispatch(factory);

        Mail("txt").Should().Contain(t => t.StartsWith("To: rahim@example.com", StringComparison.Ordinal) && t.Contains("volunteer application"));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("not-an-address", 1)]
    [InlineData("rahim@example.com", 2)]
    public async Task OnlyAUsableAddressGetsAnAcknowledgement(string? email, int expectedMessages)
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        (await Submit(client, "contact", "en", email)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await factory.Services.CreateAsyncScope().ServiceProvider.GetRequiredService<CtaDbContext>().OutboxMessages.CountAsync()).Should().Be(expectedMessages);
    }

    [Fact]
    public async Task WithoutAnOrgInboxOnlyTheAcknowledgementIsQueued()
    {
        using var factory = Factory(orgInbox: null);
        using var client = factory.CreateClient();

        await Submit(client, "contact", "en", "rahim@example.com");

        (await factory.Services.CreateAsyncScope().ServiceProvider.GetRequiredService<CtaDbContext>().OutboxMessages.CountAsync()).Should().Be(1);
    }

    private sealed class FailingSender : IEmailSender
    {
        public int Attempts;

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Attempts);
            throw new HttpRequestException("provider outage");
        }
    }

    [Fact]
    public async Task AProviderOutageDoesNotFailTheRequestAndTheMailIsRetried()
    {
        var sender = new FailingSender();
        using var factory = Factory(sender: sender);
        using var client = factory.CreateClient();

        (await Submit(client, "contact", "en", "rahim@example.com")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Dispatch(factory)).Should().Be(0);

        sender.Attempts.Should().Be(2);
        await using var scope = factory.Services.CreateAsyncScope();
        var pending = await scope.ServiceProvider.GetRequiredService<CtaDbContext>().OutboxMessages.AsNoTracking().ToListAsync();
        pending.Should().OnlyContain(m => m.ProcessedAt == null && m.Attempts == 1 && m.AvailableAt > DateTime.UtcNow);
    }
}

public sealed class PostmarkSenderTests
{
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent("{\"To\":\"rahim@example.com\"}") };
        }
    }

    private static PostmarkEmailSender Sender(StubHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.postmarkapp.com/") },
        Options.Create(new EmailOptions { FromAddress = "no-reply@example.org", Postmark = new PostmarkOptions { ServerToken = "tok-123" } }));

    [Fact]
    public async Task SendsTheMessageWithTheServerToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        await Sender(handler).SendAsync(new EmailMessage("rahim@example.com", "Subject বাংলা", "<p>hi</p>", "hi", "contact-ack"), CancellationToken.None);

        handler.Request!.RequestUri!.AbsoluteUri.Should().Be("https://api.postmarkapp.com/email");
        handler.Request.Headers.GetValues("X-Postmark-Server-Token").Single().Should().Be("tok-123");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("From").GetString().Should().Be("Kolpojontro <no-reply@example.org>");
        body.RootElement.GetProperty("To").GetString().Should().Be("rahim@example.com");
        body.RootElement.GetProperty("Subject").GetString().Should().Be("Subject বাংলা");
        body.RootElement.GetProperty("MessageStream").GetString().Should().Be("outbound");
    }

    [Fact]
    public async Task AFailureThrowsWithoutLeakingTheRecipient()
    {
        var send = () => Sender(new StubHandler(HttpStatusCode.UnprocessableEntity)).SendAsync(new EmailMessage("rahim@example.com", "s", "<p/>", "t"), CancellationToken.None);

        var error = (await send.Should().ThrowAsync<HttpRequestException>()).Which;
        error.Message.Should().Contain("422").And.NotContain("rahim@example.com");
    }

    [Fact]
    public void OutsideDevelopmentStartupFailsWithoutAProviderToken()
    {
        using var factory = new WebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("ConnectionStrings:CtaDb", "Host=localhost;Database=x");
            b.UseSetting("Jwt:PublicKeyPem", TestTokens.PublicKeyPem(TestJwtKeys.Primary));
        });

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*Email:Postmark:ServerToken*");
    }
}
