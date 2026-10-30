using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks;
using Serilog;
using Serilog.Context;

namespace KJWebsite.IntegrationTests;

public sealed class LoggingRedactionTests
{
    private const string Email = "rahim.uddin@example.com";
    private const string Phone = "+8801712345678";
    private const string Jwt = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ1c3JfMSJ9.c2lnbmF0dXJlLXZhbHVl";

    private static (ILogger Logger, StringWriter Output) Create()
    {
        var output = new StringWriter();
        var logger = KjObservability.ConfigureLogging(new LoggerConfiguration(), "TestService", output: output).CreateLogger();
        return (logger, output);
    }

    private static JsonElement[] Lines(StringWriter output) =>
        [.. output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    private static readonly string[] Pii = [Email, Phone, Jwt, "Rahim", "Uddin", "A+", "House 4, Road 2", "hunter2"];

    [Fact]
    public void ObjectsLoggedWithDestructuringHaveTheirPersonalFieldsRedacted()
    {
        var (log, output) = Create();
        var applicant = new
        {
            FirstName = "Rahim",
            LastName = "Uddin",
            Email,
            PhoneNumber = Phone,
            BloodGroup = "A+",
            PermanentAddress = "House 4, Road 2",
            Password = "hunter2",
            Id = "usr_123",
            Nested = new { RefreshToken = Jwt, City = "Dhaka" }
        };

        log.Information("Applicant received {@Applicant}", applicant);

        var text = output.ToString();
        text.Should().NotContainAny(Pii);
        text.Should().Contain("usr_123", "non-personal identifiers stay queryable").And.Contain("Dhaka").And.Contain("[REDACTED]");
    }

    [Fact]
    public void PropertiesNamedAfterPersonalDataAreRedactedWhateverTheirType()
    {
        var (log, output) = Create();

        log.Information("Login for {Email} with {Authorization} from {Phone}", Email, "Bearer " + Jwt, Phone);

        var line = Lines(output).Single();
        line.GetProperty("Email").GetString().Should().Be("[REDACTED]");
        line.GetProperty("Authorization").GetString().Should().Be("[REDACTED]");
        line.GetProperty("Phone").GetString().Should().Be("[REDACTED]");
        output.ToString().Should().NotContainAny(Pii);
    }

    [Fact]
    public void PersonalDataEmbeddedInOrdinaryStringsIsScrubbed()
    {
        var (log, output) = Create();

        log.Warning("Confirmation failed {Detail}", $"user {Email} called {Phone} with Bearer abcdefghijkl and {Jwt}");

        var text = output.ToString();
        text.Should().NotContainAny(Email, Phone, Jwt, "abcdefghijkl");
        text.Should().Contain("user [REDACTED] called [REDACTED]");
    }

    [Fact]
    public void DictionariesAndSequencesAreWalkedToo()
    {
        var (log, output) = Create();

        log.Information("Submission {@Form} {@Recipients}", new Dictionary<string, string> { ["firstName"] = "Rahim", ["topic"] = "volunteering" }, new[] { Email, "none" });

        var text = output.ToString();
        text.Should().NotContainAny("Rahim", Email);
        text.Should().Contain("volunteering");
    }

    [Fact]
    public void EveryEventCarriesTheServiceAndCorrelationId()
    {
        var (log, output) = Create();

        using (LogContext.PushProperty("CorrelationId", "req-0123456789"))
        {
            log.Information("Something happened {Count}", 3);
        }

        var line = Lines(output).Single();
        line.GetProperty("Service").GetString().Should().Be("TestService");
        line.GetProperty("CorrelationId").GetString().Should().Be("req-0123456789");
        line.GetProperty("Count").GetInt32().Should().Be(3);
        line.TryGetProperty("@m", out _).Should().BeTrue("logs are structured JSON with a rendered message");
    }
}
