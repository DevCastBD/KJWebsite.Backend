using System.Security.Cryptography;
using System.Text.Json;
using KJWebsite.BuildingBlocks;

namespace ContentService;

/// <summary>Response helpers shared by the content endpoints.</summary>
public static class ContentHttp
{
    public const int MaxPageSize = 100;

    public static void SetETag(HttpRequest request, string? etag)
    {
        if (etag is not null)
        {
            request.HttpContext.Response.Headers.ETag = etag;
        }
    }

    public static IResult ConflictResponse(HttpRequest request) =>
        ApiError.Create(request.HttpContext, StatusCodes.Status409Conflict, "CONFLICT", "The item was changed by someone else. Reload it and apply your edit again.");

    public static IResult Invalid(HttpRequest request, string message) =>
        ApiError.Create(request.HttpContext, StatusCodes.Status400BadRequest, "VALIDATION_ERROR", message);

    public static IResult NotFound(HttpRequest request, string message) =>
        ApiError.Create(request.HttpContext, StatusCodes.Status404NotFound, "NOT_FOUND", message);

    public static bool TryReadStatus(string? status, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant();
        return normalized is "active" or "all";
    }

    public static bool TryReadPaging(int? limit, int? offset, out int safeLimit, out int safeOffset)
    {
        safeLimit = limit.GetValueOrDefault(20);
        safeOffset = offset.GetValueOrDefault(0);
        return safeLimit is >= 1 and <= MaxPageSize && safeOffset >= 0;
    }

    public static object Page<T>(List<T> rows, int limit, int offset, string language)
    {
        var paged = rows.Skip(offset).Take(limit).ToArray();
        return new { items = paged, total = rows.Count, limit, offset, lang = language };
    }

    public static object PageAll<T>(List<T> rows, int limit, int offset) =>
        new PagingEnvelope<T>(rows.Skip(offset).Take(limit).ToArray(), rows.Count, null, limit, offset);

    public static IResult Cached(HttpRequest request, object body)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerOptions.Web);
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(json))[..32].ToLowerInvariant()}\"";
        var headers = request.HttpContext.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = "public, max-age=60";
        headers.Vary = "Accept-Language";

        var ifNoneMatch = request.Headers.IfNoneMatch;
        return ifNoneMatch.Any(v => v is not null && (v == "*" || v.Split(',').Select(t => t.Trim()).Contains(etag)))
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Bytes(json, "application/json");
    }
}
