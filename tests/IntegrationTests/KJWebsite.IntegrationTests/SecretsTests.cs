using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using KJWebsite.BuildingBlocks.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace KJWebsite.IntegrationTests;

/// <summary>Keeps secrets out of the repository: nothing sensitive may be committed in a settings file.</summary>
public sealed class ConfigurationHygieneTests
{
    private static readonly string[] SensitiveNames = ["password", "secret", "token", "privatekey", "apikey", "pem", "credential"];

    // Values that are structure, not secrets: empty, or an obvious placeholder.
    private static bool IsEmptyOrPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith('<') || value.Contains("CHANGE_ME", StringComparison.Ordinal);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KJWebsite.Backend.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<(string Path, string Value)> Leaves(string path, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var leaf in Leaves($"{path}:{property.Name}", property.Value))
                    {
                        yield return leaf;
                    }
                }

                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var leaf in Leaves($"{path}:{index++}", item))
                    {
                        yield return leaf;
                    }
                }

                break;
            case JsonValueKind.String:
                yield return (path, element.GetString()!);
                break;
        }
    }

    [Fact]
    public void NoCommittedSettingsFileHoldsASecret()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "appsettings*.json", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "launchSettings.json", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        files.Should().NotBeEmpty();

        var offenders = new List<string>();
        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var (path, value) in Leaves(string.Empty, document.RootElement))
            {
                var name = path.ToLowerInvariant();
                if (SensitiveNames.Any(name.Contains) && !IsEmptyOrPlaceholder(value))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)} {path}");
                }

                if (value.Contains("Password=", StringComparison.OrdinalIgnoreCase) || value.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), file)} {path} (embedded credential)");
                }
            }
        }

        offenders.Should().BeEmpty("secrets load from user-secrets or the environment, never from a committed file");
    }
}

public sealed class KeyRotationTests
{
    private static readonly string OldPrivate = TestJwtKeys.Primary;
    private static readonly string NewPrivate = TestJwtKeys.Other;
    private static readonly string OldPublic = TestTokens.PublicKeyPem(OldPrivate);
    private static readonly string NewPublic = TestTokens.PublicKeyPem(NewPrivate);

    private static readonly string[] CtaView = [Permissions.CtaView];

    private static WebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint> Cta(string publicKey, string? retired = null) =>
        new CtaApiFactory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?> { ["Jwt:PublicKeyPem"] = publicKey };
            if (retired is not null)
            {
                settings["Jwt:RetiredPublicKeysPem:0"] = retired;
            }

            configuration.AddInMemoryCollection(settings);
        }));

    private static async Task<HttpStatusCode> CallWith(WebApplicationFactory<CtaSubmissionService.CtaSubmissionServiceTestEntryPoint> factory, string token)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/cta/submissions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task TheRotationProcedureNeverInvalidatesALiveSession()
    {
        var oldToken = TestTokens.Issue(CtaView, OldPrivate);
        var newToken = TestTokens.Issue(CtaView, NewPrivate);

        // Before: only the old key exists.
        using (var before = Cta(OldPublic))
        {
            (await CallWith(before, oldToken)).Should().Be(HttpStatusCode.OK);
            (await CallWith(before, newToken)).Should().Be(HttpStatusCode.Unauthorized);
        }

        // Step 2 of docs/SECRETS.md: verifiers trust the new key and keep the old one retired. Live sessions survive.
        using (var overlap = Cta(NewPublic, retired: OldPublic))
        {
            (await CallWith(overlap, oldToken)).Should().Be(HttpStatusCode.OK, "tokens issued before the rotation are still valid");
            (await CallWith(overlap, newToken)).Should().Be(HttpStatusCode.OK);
        }

        // Step 4: the retired key is dropped after the access-token lifetime; what it signed is now refused.
        using (var after = Cta(NewPublic))
        {
            (await CallWith(after, oldToken)).Should().Be(HttpStatusCode.Unauthorized);
            (await CallWith(after, newToken)).Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task TheIssuingServiceSignsWithTheNewKeyAndPublishesBothDuringTheOverlap()
    {
        using var auth = new AuthApiFactory().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:PrivateKeyPem"] = NewPrivate,
                ["Jwt:RetiredPublicKeysPem:0"] = OldPublic
            })));
        using var client = auth.CreateClient();

        using var jwks = JsonDocument.Parse(await client.GetStringAsync("/.well-known/jwks.json"));
        var published = jwks.RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray();
        published.Should().HaveCount(2).And.OnlyHaveUniqueItems();

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "admin@site.org", password = "admin123" });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        var header = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(access);
        header.Kid.Should().Be(published[0], "the current key is listed first and is the one that signs");

        // A verifier that only trusts the new key accepts it; one that only has the old key does not.
        using var verifier = Cta(NewPublic);
        (await CallWith(verifier, access)).Should().NotBe(HttpStatusCode.Unauthorized);
        using var stale = Cta(OldPublic);
        (await CallWith(stale, access)).Should().Be(HttpStatusCode.Unauthorized);
    }
}

public sealed class StartupSecretsTests
{
    [Fact]
    public void ContentAndCtaRefuseToStartOutsideDevelopmentWithoutTheAuthPublicKey()
    {
        using var content = new WebApplicationFactory<ContentService.ContentServiceTestEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("ConnectionStrings:ContentDb", "Host=localhost;Database=x");
        });

        var start = () => content.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:PublicKeyPem*");
    }

    [Fact]
    public void AuthRefusesToStartOutsideDevelopmentWithoutASigningKey()
    {
        using var auth = new WebApplicationFactory<AuthIdentityService.AuthIdentityServiceTestEntryPoint>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("ConnectionStrings:AuthDb", "Host=localhost;Database=x");
        });

        var start = () => auth.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:PrivateKeyPem*");
    }
}
