using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = ExportOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine("Usage: dotnet run --project tools/OpenApiExporter [--verify] [--output <path>]");
    Console.WriteLine("Starts the development services on temporary loopback ports and writes the aggregated OpenAPI YAML.");
    return 0;
}

try
{
    var repositoryRoot = FindRepositoryRoot();
    var outputPath = options.OutputPath is null
        ? Path.Combine(repositoryRoot, "openapi.v1.yaml")
        : Path.GetFullPath(options.OutputPath, Directory.GetCurrentDirectory());

    var document = await RuntimeOpenApiExporter.ExportAsync(repositoryRoot);
    var yaml = YamlWriter.Write(document);

    if (options.Verify)
    {
        if (!File.Exists(outputPath) || !string.Equals(File.ReadAllText(outputPath), yaml, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"OpenAPI contract is out of date: regenerate with dotnet run --project tools/OpenApiExporter -- --output {outputPath}");
            return 1;
        }

        Console.WriteLine($"OpenAPI contract is current: {outputPath}");
        return 0;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    var temporaryOutput = $"{outputPath}.{Guid.NewGuid():N}.tmp";
    await File.WriteAllTextAsync(temporaryOutput, yaml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    File.Move(temporaryOutput, outputPath, overwrite: true);
    Console.WriteLine($"Wrote aggregated OpenAPI contract: {outputPath}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"OpenAPI export failed: {exception.Message}");
    return 1;
}

static string FindRepositoryRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KJWebsite.Backend.slnx")))
            {
                return directory.FullName;
            }
        }
    }

    throw new InvalidOperationException("Could not locate KJWebsite.Backend.slnx. Run the exporter from this repository.");
}

sealed record ExportOptions(bool Verify, string? OutputPath, bool ShowHelp)
{
    public static ExportOptions Parse(string[] arguments)
    {
        var verify = false;
        string? outputPath = null;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--verify":
                    verify = true;
                    break;
                case "--output" when index + 1 < arguments.Length:
                    outputPath = arguments[++index];
                    break;
                case "--help" or "-h":
                    return new ExportOptions(false, null, true);
                default:
                    throw new ArgumentException($"Unknown or incomplete option: {arguments[index]}");
            }
        }

        return new ExportOptions(verify, outputPath, false);
    }
}

static class RuntimeOpenApiExporter
{
    private static readonly ServiceDefinition[] Services =
    [
        new("ApiGateway", "src/Services/ApiGateway/ApiGateway.csproj", IncludeHealth: true),
        new("ContentService", "src/Services/ContentService/ContentService.csproj", IncludeHealth: false),
        new("CtaSubmissionService", "src/Services/CtaSubmissionService/CtaSubmissionService.csproj", IncludeHealth: false),
        new("AuthIdentityService", "src/Services/AuthIdentityService/AuthIdentityService.csproj", IncludeHealth: false)
    ];

    public static async Task<JsonObject> ExportAsync(string repositoryRoot)
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"kj-openapi-export-{Guid.NewGuid():N}");
        var processes = new List<StartedService>();

        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach (var service in Services)
            {
                var port = GetUnusedLoopbackPort();
                processes.Add(StartService(service, repositoryRoot, temporaryDirectory, port));
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var documents = new List<(ServiceDefinition Service, JsonObject Document)>();
            foreach (var process in processes)
            {
                documents.Add((process.Definition, await WaitForDocumentAsync(client, process)));
            }

            var gateway = documents.Single(document => document.Service.Name == "ApiGateway").Document;
            var merged = gateway.DeepClone().AsObject();
            foreach (var document in documents.Where(document => document.Service.Name != "ApiGateway"))
            {
                MergeDocument(merged, document.Document, document.Service.IncludeHealth);
            }

            ConfigureAggregateMetadata(merged);
            ValidateOperationIds(merged);
            return merged;
        }
        finally
        {
            foreach (var service in processes)
            {
                if (!service.Process.HasExited)
                {
                    service.Process.Kill(entireProcessTree: true);
                    await service.Process.WaitForExitAsync();
                }

                service.Process.Dispose();
            }

            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static StartedService StartService(ServiceDefinition definition, string repositoryRoot, string temporaryDirectory, int port)
    {
        var output = new ConcurrentQueue<string>();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = temporaryDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, definition.ProjectPath));
        startInfo.ArgumentList.Add("--launch-profile");
        startInfo.ArgumentList.Add("http");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add($"http://127.0.0.1:{port}");
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        if (definition.Name == "AuthIdentityService")
        {
            startInfo.Environment["ConnectionStrings__AuthDb"] = $"Data Source={Path.Combine(temporaryDirectory, "auth.db")}";
        }
        else if (definition.Name == "CtaSubmissionService")
        {
            startInfo.Environment["ConnectionStrings__CtaDb"] = $"Data Source={Path.Combine(temporaryDirectory, "cta.db")}";
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {definition.Name}.");
        process.OutputDataReceived += (_, eventArgs) => Capture(eventArgs.Data, output);
        process.ErrorDataReceived += (_, eventArgs) => Capture(eventArgs.Data, output);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new StartedService(definition, process, port, output);
    }

    private static void Capture(string? line, ConcurrentQueue<string> output)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            output.Enqueue(line);
            while (output.Count > 12 && output.TryDequeue(out _)) { }
        }
    }

    private static async Task<JsonObject> WaitForDocumentAsync(HttpClient client, StartedService service)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        Exception? lastFailure = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (service.Process.HasExited)
            {
                throw new InvalidOperationException($"{service.Definition.Name} exited with code {service.Process.ExitCode}: {string.Join(" | ", service.Output)}");
            }

            try
            {
                var json = await client.GetStringAsync($"http://127.0.0.1:{service.Port}/openapi/v1.json");
                return JsonNode.Parse(json)?.AsObject()
                    ?? throw new InvalidOperationException($"{service.Definition.Name} returned an empty OpenAPI document.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastFailure = exception;
                await Task.Delay(250);
            }
        }

        throw new TimeoutException($"Timed out waiting for {service.Definition.Name} OpenAPI document. Last error: {lastFailure?.Message}");
    }

    private static void MergeDocument(JsonObject target, JsonObject source, bool includeHealth)
    {
        var targetPaths = GetObject(target, "paths");
        foreach (var (path, pathItem) in GetObject(source, "paths"))
        {
            if (!includeHealth && path == "/health")
            {
                continue;
            }

            if (targetPaths.ContainsKey(path))
            {
                throw new InvalidOperationException($"Duplicate OpenAPI path while merging: {path}");
            }

            targetPaths[path] = pathItem?.DeepClone();
        }

        var targetComponents = GetOrCreateObject(target, "components");
        foreach (var (componentType, sourceGroup) in GetObject(source, "components"))
        {
            if (sourceGroup is not JsonObject sourceEntries)
            {
                continue;
            }

            var targetEntries = GetOrCreateObject(targetComponents, componentType);
            foreach (var (name, component) in sourceEntries)
            {
                if (targetEntries[name] is { } existing)
                {
                    if (!JsonNode.DeepEquals(existing, component))
                    {
                        throw new InvalidOperationException($"Conflicting OpenAPI component while merging: {componentType}/{name}");
                    }

                    continue;
                }

                targetEntries[name] = component?.DeepClone();
            }
        }

        var targetTags = GetOrCreateArray(target, "tags");
        var targetTagNames = targetTags
            .OfType<JsonObject>()
            .Select(tag => tag["name"]?.GetValue<string>())
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var tag in GetOrCreateArray(source, "tags").OfType<JsonObject>())
        {
            var name = tag["name"]?.GetValue<string>();
            if (name is not null && targetTagNames.Add(name))
            {
                targetTags.Add(tag.DeepClone());
            }
        }
    }

    private static void ConfigureAggregateMetadata(JsonObject document)
    {
        document["info"] = new JsonObject
        {
            ["title"] = "KJWebsite Backend API",
            ["version"] = "1.0.0",
            ["description"] = "Public and admin API contract for KJWebsite microservices via API Gateway. Generated from the development service OpenAPI documents."
        };
        document["servers"] = new JsonArray
        {
            new JsonObject
            {
                ["url"] = "http://localhost:7000",
                ["description"] = "Local API Gateway"
            }
        };
    }

    private static void ValidateOperationIds(JsonObject document)
    {
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, pathItem) in GetObject(document, "paths"))
        {
            if (pathItem is not JsonObject pathObject)
            {
                continue;
            }

            foreach (var (_, operation) in pathObject)
            {
                if (operation is JsonObject operationObject && operationObject["operationId"]?.GetValue<string>() is { } operationId && !operationIds.Add(operationId))
                {
                    throw new InvalidOperationException($"Duplicate OpenAPI operationId while merging: {operationId}");
                }
            }
        }
    }

    private static JsonObject GetObject(JsonObject document, string propertyName) =>
        document[propertyName] as JsonObject ?? throw new InvalidOperationException($"OpenAPI document is missing '{propertyName}'.");

    private static JsonObject GetOrCreateObject(JsonObject document, string propertyName)
    {
        if (document[propertyName] is not JsonObject value)
        {
            value = new JsonObject();
            document[propertyName] = value;
        }

        return value;
    }

    private static JsonArray GetOrCreateArray(JsonObject document, string propertyName)
    {
        if (document[propertyName] is not JsonArray value)
        {
            value = new JsonArray();
            document[propertyName] = value;
        }

        return value;
    }

    private static int GetUnusedLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed record ServiceDefinition(string Name, string ProjectPath, bool IncludeHealth);

    private sealed record StartedService(ServiceDefinition Definition, Process Process, int Port, ConcurrentQueue<string> Output);
}

static class YamlWriter
{
    public static string Write(JsonNode document)
    {
        var output = new StringBuilder();
        WriteNode(output, document, 0);
        return output.ToString();
    }

    private static void WriteNode(StringBuilder output, JsonNode? node, int indent)
    {
        switch (node)
        {
            case JsonObject objectNode:
                foreach (var (key, value) in objectNode)
                {
                    Indent(output, indent);
                    output.Append(FormatKey(key)).Append(':');
                    WritePropertyValue(output, value, indent);
                }
                break;
            case JsonArray arrayNode:
                foreach (var value in arrayNode)
                {
                    Indent(output, indent);
                    output.Append('-');
                    WriteArrayValue(output, value, indent);
                }
                break;
            default:
                Indent(output, indent);
                output.Append(FormatScalar(node)).AppendLine();
                break;
        }
    }

    private static void WritePropertyValue(StringBuilder output, JsonNode? value, int indent)
    {
        if (IsScalar(value))
        {
            output.Append(' ').Append(FormatScalar(value)).AppendLine();
        }
        else if (IsEmpty(value))
        {
            output.Append(value is JsonArray ? " []" : " {}").AppendLine();
        }
        else
        {
            output.AppendLine();
            WriteNode(output, value, indent + 2);
        }
    }

    private static void WriteArrayValue(StringBuilder output, JsonNode? value, int indent)
    {
        if (IsScalar(value))
        {
            output.Append(' ').Append(FormatScalar(value)).AppendLine();
        }
        else if (IsEmpty(value))
        {
            output.Append(value is JsonArray ? " []" : " {}").AppendLine();
        }
        else
        {
            output.AppendLine();
            WriteNode(output, value, indent + 2);
        }
    }

    private static bool IsScalar(JsonNode? node) => node is null or JsonValue;

    private static bool IsEmpty(JsonNode? node) => node is JsonObject { Count: 0 } or JsonArray { Count: 0 };

    private static string FormatScalar(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    private static string FormatKey(string key) => key.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or '$') && !char.IsDigit(key[0])
        ? key
        : JsonSerializer.Serialize(key);

    private static void Indent(StringBuilder output, int spaces) => output.Append(' ', spaces);
}
