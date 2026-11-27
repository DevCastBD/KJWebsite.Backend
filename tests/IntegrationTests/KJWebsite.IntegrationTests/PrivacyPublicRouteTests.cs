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
    private readonly List<(string Label, string Value)> _secrets = [];
    private readonly Dictionary<string, string> _realPaths = new(StringComparer.Ordinal);
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
        ["gateway"] = "stripe",
        ["code"] = "PRIVACYSCANMISSING"
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
        ["token"] = "invalid-download-token",
        ["from"] = "2026-01-01T00:00:00Z",
        ["to"] = "2026-02-01T00:00:00Z",
        ["since"] = "2026-01-01T00:00:00Z",
        ["featured"] = "",
        ["coverageLanguage"] = "",
        ["tier"] = "",
        ["unreadOnly"] = "",
        ["endpoint"] = "https://push.example.org/privacy-scan-missing",
        ["assignee"] = "",
        ["category"] = "",
        ["slaState"] = ""
    };

    [Fact]
    public async Task T21EveryPublicOpenApiOperationKeepsSeededDataPrivate()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var reader = File.OpenText(Path.Combine(AppContext.BaseDirectory, "openapi.v1.yaml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var paths = (YamlMappingNode)((YamlMappingNode)yaml.Documents.Single().RootNode)["paths"];
        // Derive expected names directly from the parsed contract, independently of request construction.
        var expectedOperations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths.Children)
            if (IsPublic(path.Key.ToString()))
                foreach (var method in Methods)
                    if (((YamlMappingNode)path.Value).Children.ContainsKey(new YamlScalarNode(method)))
                        expectedOperations.Add($"{method.ToUpperInvariant()} {path.Key}");
        var operations = paths.Children.Where(p => IsPublic(((YamlScalarNode)p.Key).Value!))
            .SelectMany(p => ((YamlMappingNode)p.Value).Children
                .Where(o => Methods.Contains(((YamlScalarNode)o.Key).Value!))
                .Select(o => new Operation(((YamlScalarNode)p.Key).Value!, ((YamlScalarNode)o.Key).Value!.ToUpperInvariant(),
                    (YamlMappingNode)p.Value, (YamlMappingNode)o.Value))).ToArray();
        Assert.True(operations.Length >= 60, $"Public operations shrank to {operations.Length}; expected at least 60.");
        await SeedPrivateData();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            var query = Parameters(operation.PathItem).Concat(Parameters(operation.Node))
                .Where(p => Scalar(p, "in") == "query").Select(p => Scalar(p, "name")).Distinct().ToArray();
            await Scan(operation, query, null);
            requested.Add(operation.Name);
            if (PathParameter().IsMatch(operation.Path))
            {
                foreach (var values in SeededPaths(operation))
                {
                    var status = await Scan(operation, query, null, pathValues: values);
                    if (status != HttpStatusCode.NotFound)
                        output.WriteLine($"Seeded pass reached {operation.Name}: {(int)status}");
                }
            }
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
        Assert.True(requested.SetEquals(expectedOperations),
            "Unrequested public operations: " + string.Join(", ", expectedOperations.Except(requested)));
        output.WriteLine($"Scanned {requested.Count} public operations; skipped: none; elapsed: {started.Elapsed}.");
        GlobalLogSink.Default.AssertNone();
    }

    private async Task<HttpStatusCode> Scan(Operation operation, string[] query, string? search, bool expectNoResults = false, Dictionary<string, string>? pathValues = null)
    {
        var path = PathParameter().Replace(operation.Path, match => Uri.EscapeDataString(Value(pathValues ?? PathValues, match.Groups[1].Value, operation)));
        // Empty table entries omit optional filters so the baseline covers the whole public list.
        var suppliedQuery = query.Where(name => Value(QueryValues, name, operation).Length != 0).ToArray();
        var url = path + (suppliedQuery.Length == 0 ? "" : "?" + string.Join("&", suppliedQuery.Select(name =>
            $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(name == "q" && search is not null ? search : Value(QueryValues, name, operation))}")));
        foreach (var secret in _secrets)
            Assert.False(ResponseContains(Uri.UnescapeDataString(url), secret.Value),
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
            Assert.False(ResponseContains(body, secret.Value), message);
            Assert.False(ResponseContains(headers, secret.Value), message);
            var escaped = JsonEncodedText.Encode(secret.Value).ToString();
            Assert.False(ResponseContains(body, escaped), message);
        }
        Assert.True((int)response.StatusCode < 500, $"{operation.Name} returned {(int)response.StatusCode}: {body}");
        GlobalLogSink.Default.AssertNone();
        return response.StatusCode;
    }

    private IEnumerable<Dictionary<string, string>> SeededPaths(Operation operation)
    {
        // Identifiers are deliberately never registered as private log values.
        var names = PathParameter().Matches(operation.Path).Select(m => m.Groups[1].Value).ToArray();
        var baseline = new Dictionary<string, string>(PathValues, StringComparer.Ordinal);
        foreach (var name in names)
            if (_realPaths.TryGetValue(name, out var value)) baseline[name] = value;
        yield return baseline;
        // Generic {id} routes can refer to any seeded entity, including future public routes.
        if (names.Contains("id", StringComparer.Ordinal))
            foreach (var id in _realPaths.Where(p => p.Key.EndsWith("Id", StringComparison.Ordinal)).Select(p => p.Value).Distinct())
                yield return new Dictionary<string, string>(baseline, StringComparer.Ordinal) { ["id"] = id };
    }

    private async Task SeedPrivateData()
    {
        var staff = await stack.NewAdminAsync();
        var donor = await CreateDonor(stack, staff.Token);
        _secrets.Add(("donor email", donor.Email));
        _secrets.Add(("password", donor.Password));
        var login = await stack.LoginAsync(donor.Email, donor.Password);
        using (var me = await Send(stack, HttpMethod.Get, "/api/v1/auth/me", login))
            _realPaths["userId"] = (await Json(me, HttpStatusCode.OK)).GetProperty("id").GetString()!;
        _realPaths["donorId"] = donor.Id;
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
        var student = await LinkStudent(stack, staff.Token, donor.Id);
        _realPaths["studentId"] = student;
        await UploadPhoto(stack, staff.Token, student);
        using (var current = await Send(stack, HttpMethod.Get, $"{Students}/{student}", staff.Token))
        {
            var detail = await Json(current, HttpStatusCode.OK);
            foreach (var (field, label) in new[] { ("name", "student name"), ("preferredName", "preferred name"),
                         ("school", "school"), ("address", "student address"), ("guardianName", "guardian name"),
                         ("dateOfBirth", "date of birth"), ("guardianPhone", "guardian phone") })
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
        var donation = await Json(gift, HttpStatusCode.Created);
        _realPaths["donationId"] = donation.GetProperty("id").GetString()!;
        _realPaths["reference"] = donation.GetProperty("reference").GetString()!;
    }

    private static bool ResponseContains(string response, string value) => value.Length >= 12
        ? response.Contains(value, StringComparison.OrdinalIgnoreCase)
        : Regex.IsMatch(response, $@"(?<![0-9-]){Regex.Escape(value)}(?![0-9-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private string Seed(string label, string value)
    {
        if (value.Length >= 12) stack.Seed(label, value);
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
