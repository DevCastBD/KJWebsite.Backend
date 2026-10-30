using System.Text.Json.Nodes;
using FluentAssertions;

namespace KJWebsite.IntegrationTests.Contract;

/// <summary>
/// Contract tests (issue #37, plan v2: local, no CI). They fail when the running API diverges from the committed
/// <c>openapi.v1.yaml</c>, and every message names the endpoint and the field. Run: <c>dotnet test --filter Category=Contract</c>.
/// When the API changed on purpose: <c>dotnet run --project tools/OpenApiExporter -- --output openapi.v1.yaml</c>, then re-run.
/// </summary>
[Trait("Category", "Contract")]
public sealed class ApiContractTests(ContractFixture api) : IClassFixture<ContractFixture>
{
    [Fact]
    public void LiveResponsesMatchTheirDocumentedStatusAndSchema()
    {
        api.Violations.Should().BeEmpty("the running API must match openapi.v1.yaml:\n{0}", string.Join("\n", api.Violations.Order()));
    }

    [Fact]
    public void EveryOperationInTheSpecIsExercised()
    {
        var unprobed = api.Spec.Operations().Select(o => $"{o.Method} {o.Path}").Except(api.Probed).Order().ToList();

        unprobed.Should().BeEmpty("a documented operation without a contract probe is not checked at all; add one to ContractFixture:\n{0}", string.Join("\n", unprobed));
    }

    [Fact]
    public void EveryRunningEndpointIsDocumented()
    {
        var documented = api.Spec.Operations().Select(o => $"{o.Method} {o.Path}").ToHashSet();
        var undocumented = api.RuntimeEndpoints.Where(e => !documented.Contains(e)).Distinct().Order().ToList();

        undocumented.Should().BeEmpty("endpoints that run but are missing from openapi.v1.yaml (regenerate it):\n{0}", string.Join("\n", undocumented));
    }

    [Fact]
    public void EveryDocumentedOperationDescribesItsSuccessResponse()
    {
        // A bare "200 OK" tells the frontend nothing; the contract is only as good as its response schemas.
        var vague = api.Spec.Operations()
            .Where(o => o.Path != "/health" && o.Path != "/.well-known/jwks.json")
            .Where(o => ((JsonObject)o.Operation["responses"]!).Where(r => r.Key.StartsWith('2')).All(r => r.Value?["content"] is null && r.Key != "204" && r.Key != "304"))
            .Select(o => $"{o.Method} {o.Path}").Order().ToList();

        vague.Should().BeEmpty("2xx responses without a schema:\n{0}", string.Join("\n", vague));
    }

    [Fact]
    public void ErrorResponsesAreDocumentedAsProblemDetails()
    {
        var spec = api.Spec;
        var secured = (JsonObject)spec.Operation("GET", "/api/v1/admin/cta/submissions")!["responses"]!;

        secured.Select(r => r.Key).Should().Contain(["200", "400", "401", "403"]);
        var problem = spec.Resolve(secured["401"]!["content"]!.AsObject().First().Value!["schema"]!);
        problem["properties"]!.AsObject().Select(p => p.Key).Should().Contain(["type", "title", "status", "detail"]);
    }
}

/// <summary>The detector itself: it must catch the drift it exists for, and say where.</summary>
[Trait("Category", "Contract")]
public sealed class ContractValidatorTests
{
    private static readonly ContractSpec Spec = ContractSpec.Parse("""
        openapi: "3.1.1"
        paths: {}
        components:
          schemas:
            Widget:
              type: object
              required: [id, status, created_at]
              properties:
                id: { type: string }
                status: { enum: [accepted, rejected], type: string }
                created_at: { type: string, format: date-time }
                size: { type: [ "null", integer ] }
                tags: { type: array, items: { type: string } }
        """);

    private static List<string> Check(string json) => SchemaValidator.Validate(Spec, JsonNode.Parse("{\"$ref\":\"#/components/schemas/Widget\"}")!, JsonNode.Parse(json));

    [Fact]
    public void AMatchingBodyPasses() =>
        Check("{\"id\":\"w1\",\"status\":\"accepted\",\"created_at\":\"2026-10-30T10:00:00Z\",\"size\":null,\"tags\":[\"a\"],\"extra\":1}").Should().BeEmpty("extra fields are additive changes");

    [Fact]
    public void ARenamedFieldIsReportedByName() =>
        Check("{\"id\":\"w1\",\"status\":\"accepted\",\"createdAt\":\"2026-10-30T10:00:00Z\"}").Should().Contain("$.created_at: required field is missing");

    [Fact]
    public void AChangedTypeIsReportedWithItsPath() =>
        Check("{\"id\":7,\"status\":\"accepted\",\"created_at\":\"2026-10-30T10:00:00Z\",\"tags\":[\"a\",2]}")
            .Should().Contain(["$.id: expected string, got integer", "$.tags[1]: expected string, got integer"]);

    [Fact]
    public void AnUnknownEnumValueAndABadDateAreCaught() =>
        Check("{\"id\":\"w\",\"status\":\"maybe\",\"created_at\":\"yesterday\"}")
            .Should().Contain(e => e.StartsWith("$.status: 'maybe' is not one of", StringComparison.Ordinal)).And.Contain("$.created_at: 'yesterday' is not an ISO date-time");

    [Fact]
    public void NullWhereNullIsNotAllowedIsCaught() =>
        Check("{\"id\":null,\"status\":\"accepted\",\"created_at\":\"2026-10-30T10:00:00Z\"}").Should().Contain("$.id: expected string, got null");
}
