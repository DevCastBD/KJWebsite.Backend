using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using YamlDotNet.RepresentationModel;
using static KJWebsite.IntegrationTests.PrivacyFlows;

namespace KJWebsite.IntegrationTests;

public sealed partial class PrivacyPublicRouteTests(PrivacyStack stack, ITestOutputHelper output) : IClassFixture<PrivacyStack>
{
    private const string Donors = "/api/v1/admin/finance/donors";
    private const string Students = "/api/v1/admin/sponsorship/students";
    // No operations are skipped. Add an explicit reason here if an exception is ever necessary.
    private const string SkippedOperations = "";
    private readonly List<(string Label, string Value)> _secrets = [];
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
        { "get", "post", "put", "patch", "delete", "head", "options", "trace" };
    private static readonly Dictionary<string, string> PathValues = new(StringComparer.Ordinal)
    {
        ["id"] = "00000000-0000-0000-0000-000000000001",
        ["slug"] = "privacy-scan-missing",
        ["type"] = "page",
        ["token"] = "invalid-preview-token",
        ["kind"] = "photo",
        ["reference"] = "PRIVACY-SCAN-MISSING",
        ["gateway"] = "stripe"
    };
    private static readonly Dictionary<string, string> QueryValues = new(StringComparer.Ordinal)
    {
        ["lang"] = "en",
        ["status"] = "published",
        ["limit"] = "100",
        ["offset"] = "0",
        ["tag"] = "",
        ["path"] = "/privacy-scan-missing",
        ["q"] = "privacy-scan-missing",
        ["type"] = "",
        ["chapter"] = "",
        ["page"] = "1",
        ["result"] = "cancel",
        ["token"] = "invalid-download-token"
    };

    [Fact]
    public async Task T21EveryPublicOpenApiOperationKeepsSeededDataPrivate()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var reader = File.OpenText(Path.Combine(AppContext.BaseDirectory, "openapi.v1.yaml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var paths = (YamlMappingNode)((YamlMappingNode)yaml.Documents.Single().RootNode)["paths"];
        var operations = paths.Children.Where(p => IsPublic(((YamlScalarNode)p.Key).Value!))
            .SelectMany(p => ((YamlMappingNode)p.Value).Children
                .Where(o => Methods.Contains(((YamlScalarNode)o.Key).Value!))
                .Select(o => new Operation(((YamlScalarNode)p.Key).Value!, ((YamlScalarNode)o.Key).Value!.ToUpperInvariant(),
                    (YamlMappingNode)p.Value, (YamlMappingNode)o.Value))).ToArray();
        Assert.True(operations.Length >= 60, $"Public operations shrank to {operations.Length}; expected at least 60.");
        Assert.True(SkippedOperations.Length == 0, "Skipped operations require an explicit operation name and reason.");
        await SeedPrivateData();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            var query = Parameters(operation.PathItem).Concat(Parameters(operation.Node))
                .Where(p => Scalar(p, "in") == "query").Select(p => Scalar(p, "name")).Distinct().ToArray();
            await Scan(operation, query, null);
            requested.Add(operation.Name);
            if (query.Contains("q", StringComparer.Ordinal))
            {
                var names = _secrets.Where(s => s.Label is "donor name" or "student name" or "preferred name" or "guardian name");
                foreach (var name in names)
                {
                    // NewSeed ends in a 32-hex Guid. Probe strict substrings, never the registered value.
                    var guid = name.Value[^32..];
                    foreach (var term in new[] { guid, guid[..16], name.Value[..(name.Value.Length - 32)] })
                    {
                        Assert.True(term.Length < name.Value.Length && name.Value.Contains(term, StringComparison.Ordinal),
                            $"{operation.Name}: search probes must be strict seed substrings.");
                        await Scan(operation, query, term, expectNoResults: true);
                    }
                }
                foreach (var term in new[] { "education", "school", "donor", "student" })
                    await Scan(operation, query, term);
            }
            output.WriteLine($"Scanned {operation.Name}");
        }
        Assert.True(requested.SetEquals(operations.Select(o => o.Name)),
            "Unrequested public operations: " + string.Join(", ", operations.Select(o => o.Name).Except(requested)));
        output.WriteLine($"Scanned {requested.Count} public operations; skipped: none; elapsed: {started.Elapsed}.");
        GlobalLogSink.Default.AssertNone();
    }

    private async Task Scan(Operation operation, string[] query, string? search, bool expectNoResults = false)
    {
        var path = PathParameter().Replace(operation.Path, match => Uri.EscapeDataString(Value(PathValues, match.Groups[1].Value, operation)));
        // Empty table entries omit optional filters so the baseline covers the whole public list.
        var suppliedQuery = query.Where(name => Value(QueryValues, name, operation).Length != 0).ToArray();
        var url = path + (suppliedQuery.Length == 0 ? "" : "?" + string.Join("&", suppliedQuery.Select(name =>
            $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(name == "q" && search is not null ? search : Value(QueryValues, name, operation))}")));
        foreach (var secret in _secrets)
            Assert.False(Uri.UnescapeDataString(url).Contains(secret.Value, StringComparison.OrdinalIgnoreCase),
                $"{operation.Name}: request URLs must not contain a whole registered seed ({secret.Label}).");
        using var request = new HttpRequestMessage(new HttpMethod(operation.Method), url);
        if (operation.Method is "POST" or "PUT" or "PATCH" or "DELETE") request.Content = JsonContent.Create(new { });
        using var response = await stack.Gateway.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var headers = string.Join("\n", response.Headers.Concat(response.Content.Headers).Concat(response.TrailingHeaders)
            .Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));
        // Keep the entire response, including echoed q, in the whole-seed checks.
        if (search is not null)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = JsonNode.Parse(body)!.AsObject();
            Assert.True(json["items"] is JsonArray, $"{operation.Name} must expose search items.");
            var count = json["items"]!.AsArray().Count;
            Assert.True(json["total"]?.GetValue<int>() >= count, $"{operation.Name} must expose a total covering returned items.");
            if (expectNoResults)
            {
                Assert.True(json["total"]?.GetValue<int>() == 0, $"{operation.Name} leaked '{search}' (search total)");
                Assert.True(count == 0, $"{operation.Name} leaked '{search}' (search items)");
            }
        }
        foreach (var secret in _secrets)
        {
            var message = $"{operation.Name} leaked '{secret.Value}' ({secret.Label})";
            Assert.False(body.Contains(secret.Value, StringComparison.OrdinalIgnoreCase), message);
            Assert.False(headers.Contains(secret.Value, StringComparison.OrdinalIgnoreCase), message);
            var escaped = JsonEncodedText.Encode(secret.Value).ToString();
            Assert.False(body.Contains(escaped, StringComparison.OrdinalIgnoreCase), message);
        }
        Assert.True((int)response.StatusCode < 500, $"{operation.Name} returned {(int)response.StatusCode}: {body}");
        GlobalLogSink.Default.AssertNone();
    }

    private async Task SeedPrivateData()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        _secrets.Add(("donor email", donor.Email));
        _secrets.Add(("password", donor.Password));
        _ = await stack.LoginAsync(donor.Email, donor.Password);
        var phone = Seed("donor phone", "+8801798765432");
        var address = Seed("donor postal address", stack.NewSeed("donor-address"));
        using (var current = await Send(stack, HttpMethod.Get, $"{Donors}/{donor.Id}", staff.Token))
        {
            var detail = await Json(current, HttpStatusCode.OK);
            _secrets.Add(("donor name", detail.GetProperty("donor").GetProperty("name").GetString()!));
            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Donors}/{donor.Id}") { Content = JsonContent.Create(new { phone, address }) };
            patch.Headers.Authorization = new("Bearer", staff.Token);
            patch.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
            using var changed = await stack.Gateway.SendAsync(patch);
            await Json(changed, HttpStatusCode.OK);
        }
        Seed("guardian phone", "+8801711000000");
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        await UploadPhoto(stack, staff.Token, student);
        using (var current = await Send(stack, HttpMethod.Get, $"{Students}/{student}", staff.Token))
        {
            var detail = await Json(current, HttpStatusCode.OK);
            foreach (var (field, label) in new[] { ("name", "student name"), ("preferredName", "preferred name"),
                         ("school", "school"), ("address", "student address"), ("guardianName", "guardian name") })
                _secrets.Add((label, detail.GetProperty(field).GetString()!));
            var storyEn = Seed("student story", stack.NewSeed("student-story"));
            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"{Students}/{student}") { Content = JsonContent.Create(new { storyEn }) };
            patch.Headers.Authorization = new("Bearer", staff.Token);
            patch.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
            using var changed = await stack.Gateway.SendAsync(patch);
            await Json(changed, HttpStatusCode.OK);
        }
        using var gift = await Send(stack, HttpMethod.Post, "/api/v1/admin/finance/donations", staff.Token,
            new { donorId = donor.Id, amount = "500.00", currency = "BDT", method = "cash", designation = "student", designationId = student });
        await Json(gift, HttpStatusCode.Created);
    }

    private string Seed(string label, string value)
    {
        stack.Seed(label, value);
        _secrets.Add((label, value));
        return value;
    }
    private static bool IsPublic(string path) => !path.StartsWith("/api/v1/admin/", StringComparison.Ordinal)
        && !path.Split('/').Contains("me", StringComparer.Ordinal)
        && path != "/api/v1/auth/sessions" && !path.StartsWith("/api/v1/auth/sessions/", StringComparison.Ordinal)
        && path is not "/api/v1/auth/logout-all" and not "/api/v1/auth/change-password";
    private static IEnumerable<YamlMappingNode> Parameters(YamlMappingNode node) =>
        node.Children.TryGetValue(new YamlScalarNode("parameters"), out var parameters)
            ? ((YamlSequenceNode)parameters).Children.Cast<YamlMappingNode>() : [];
    private static string Scalar(YamlMappingNode node, string key) => ((YamlScalarNode)node[key]).Value!;
    private static string Value(Dictionary<string, string> table, string name, Operation operation)
    {
        Assert.True(table.TryGetValue(name, out var value), $"{operation.Name}: extend the parameter table for '{name}'.");
        return value!;
    }
    [GeneratedRegex(@"\{([^}]+)\}")]
    private static partial Regex PathParameter();
    private sealed record Operation(string Path, string Method, YamlMappingNode PathItem, YamlMappingNode Node)
    {
        public string Name => $"{Method} {Path}";
    }
}
