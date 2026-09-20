using System.Collections.Generic;
using System.Text.Json;
using KJWebsite.BuildingBlocks;

namespace KJWebsite.BuildingBlocks.UnitTests;

public sealed class PagingEnvelopeTests
{
    [Fact]
    public void SerializeUsesTheDocumentedEnvelopeShape()
    {
        var envelope = new PagingEnvelope<string>(["one", "two"], 3, "cursor-2", 2, 0);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        var root = document.RootElement;

        Assert.Equal(["items", "total", "nextCursor", "limit", "offset"], GetPropertyNames(root));
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal("cursor-2", root.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public void SerializeOmitsOptionalPagingPropertiesWhenTheyAreUnavailable()
    {
        var envelope = new PagingEnvelope<string>(["one"], null, null, 20, null);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(envelope));

        Assert.Equal(["items", "limit"], GetPropertyNames(document.RootElement));
    }

    private static List<string> GetPropertyNames(JsonElement element)
    {
        var names = new List<string>();
        foreach (var property in element.EnumerateObject())
        {
            names.Add(property.Name);
        }

        return names;
    }
}
