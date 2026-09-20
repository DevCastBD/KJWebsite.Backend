using System.Text.Json.Serialization;

namespace KJWebsite.BuildingBlocks;

public sealed record PagingEnvelope<T>(
    [property: JsonPropertyName("items")] IReadOnlyList<T> Items,
    [property: JsonPropertyName("total")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Total,
    [property: JsonPropertyName("nextCursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextCursor,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("offset")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Offset);
