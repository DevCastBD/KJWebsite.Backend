using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace KJWebsite.IntegrationTests.Contract;

/// <summary>The committed <c>openapi.v1.yaml</c>, read as a JSON tree so the contract tests judge the file the frontend actually consumes.</summary>
internal sealed class ContractSpec
{
    private ContractSpec(JsonObject root) => Root = root;

    public JsonObject Root { get; }

    public static ContractSpec Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KJWebsite.Backend.slnx")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "openapi.v1.yaml");
        return Parse(File.ReadAllText(path));
    }

    public static ContractSpec Parse(string yaml) =>
        new((JsonObject)ToNode(new DeserializerBuilder().Build().Deserialize<object>(yaml))!);

    // YamlDotNet yields strings for every scalar, which is all the schema keywords used here need.
    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        IDictionary<object, object> map => new JsonObject(map.Select(kv => KeyValuePair.Create(Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!, ToNode(kv.Value)))),
        IEnumerable<object> list => new JsonArray([.. list.Select(ToNode)]),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
    };

    /// <summary>Every (METHOD, path-template) the spec documents.</summary>
    public IEnumerable<(string Method, string Path, JsonObject Operation)> Operations()
    {
        foreach (var (path, item) in (JsonObject)Root["paths"]!)
        {
            foreach (var (verb, operation) in (JsonObject)item!)
            {
                if (operation is JsonObject op && verb is "get" or "post" or "put" or "patch" or "delete")
                {
                    yield return (verb.ToUpperInvariant(), path, (JsonObject)operation!);
                }
            }
        }
    }

    public JsonObject? Operation(string method, string path) =>
        Operations().Where(o => o.Method == method && o.Path == path).Select(o => o.Operation).FirstOrDefault();

    public JsonNode Resolve(JsonNode schema)
    {
        while (schema is JsonObject obj && obj["$ref"] is JsonValue reference)
        {
            var parts = reference.GetValue<string>().TrimStart('#', '/').Split('/');
            JsonNode? target = Root;
            foreach (var part in parts)
            {
                target = target?[part];
            }

            schema = target ?? throw new InvalidOperationException($"Unresolved schema reference {reference}.");
        }

        return schema;
    }
}

/// <summary>
/// Checks a JSON value against an OpenAPI schema and names every divergence by JSON path. Additive changes (extra properties)
/// are allowed, as the contract rules require; a missing required field, a wrong type or a bad enum value are not.
/// </summary>
internal static class SchemaValidator
{
    public static List<string> Validate(ContractSpec spec, JsonNode schema, JsonNode? value, string path = "$")
    {
        var errors = new List<string>();
        Check(spec, schema, value, path, errors, depth: 0);
        return errors;
    }

    private static void Check(ContractSpec spec, JsonNode schemaNode, JsonNode? value, string path, List<string> errors, int depth)
    {
        if (depth > 24)
        {
            return;
        }

        var schema = (JsonObject)spec.Resolve(schemaNode);

        foreach (var keyword in new[] { "oneOf", "anyOf" })
        {
            if (schema[keyword] is JsonArray alternatives)
            {
                if (!alternatives.Any(alt => Try(spec, alt!, value, depth)))
                {
                    errors.Add($"{path}: matches none of the {alternatives.Count} documented alternatives");
                }

                return;
            }
        }

        if (schema["allOf"] is JsonArray all)
        {
            foreach (var part in all)
            {
                Check(spec, part!, value, path, errors, depth + 1);
            }
        }

        var allowed = AllowedTypes(schema);
        var actual = KindOf(value);
        if (allowed.Count > 0 && !allowed.Contains(actual) && !(actual == "integer" && allowed.Contains("number")))
        {
            errors.Add($"{path}: expected {string.Join("|", allowed)}, got {(value is null ? "null" : actual)}");
            return;
        }

        if (value is null)
        {
            return;
        }

        if (schema["enum"] is JsonArray choices && value is JsonValue scalar && !choices.Any(c => c?.ToString() == scalar.ToString()))
        {
            errors.Add($"{path}: '{scalar}' is not one of {string.Join(", ", choices.Select(c => c?.ToString()))}");
        }

        if (value is JsonValue text && actual == "string" && schema["format"]?.ToString() == "date-time" && !DateTimeOffset.TryParse(text.ToString(), CultureInfo.InvariantCulture, out _))
        {
            errors.Add($"{path}: '{text}' is not an ISO date-time");
        }

        if (value is JsonObject obj)
        {
            foreach (var required in (schema["required"] as JsonArray) ?? [])
            {
                var name = required!.ToString();
                if (!obj.ContainsKey(name))
                {
                    errors.Add($"{path}.{name}: required field is missing");
                }
            }

            if (schema["properties"] is JsonObject properties)
            {
                foreach (var (name, propertySchema) in properties)
                {
                    if (obj.ContainsKey(name))
                    {
                        Check(spec, propertySchema!, obj[name], $"{path}.{name}", errors, depth + 1);
                    }
                }
            }
        }
        else if (value is JsonArray array && schema["items"] is { } items)
        {
            for (var i = 0; i < array.Count; i++)
            {
                Check(spec, items, array[i], $"{path}[{i}]", errors, depth + 1);
            }
        }
    }

    private static bool Try(ContractSpec spec, JsonNode schema, JsonNode? value, int depth)
    {
        var errors = new List<string>();
        Check(spec, schema, value, "$", errors, depth + 1);
        return errors.Count == 0;
    }

    private static HashSet<string> AllowedTypes(JsonObject schema) => schema["type"] switch
    {
        JsonArray list => [.. list.Select(t => t!.ToString())],
        JsonValue single => [single.ToString()],
        _ => schema["properties"] is not null ? ["object"] : schema["items"] is not null ? ["array"] : []
    };

    private static string KindOf(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v when v.TryGetValue<bool>(out _) => "boolean",
        JsonValue v when v.TryGetValue<string>(out _) => "string",
        JsonValue v when v.TryGetValue<long>(out _) => "integer",
        _ => "number"
    };
}
