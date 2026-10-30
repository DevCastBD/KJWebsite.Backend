using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace KJWebsite.BuildingBlocks;

public static class ProblemDetailsFactory
{
    public static IResult Create(HttpContext context, int statusCode, string code, string detail) =>
        Create(context, statusCode, code, detail, null);

    /// <summary>
    /// RFC 9457 body. The legacy <c>error { code, message }</c> member stays alongside the standard fields
    /// until the v1 → v2 boundary, so existing clients keep working.
    /// </summary>
    public static IResult Create(
        HttpContext context,
        int statusCode,
        string code,
        string detail,
        IReadOnlyDictionary<string, object?>? additionalExtensions)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["error"] = new LegacyApiError(code, detail)
        };

        if (additionalExtensions is not null)
        {
            foreach (var (key, value) in additionalExtensions)
            {
                extensions[key] = value;
            }
        }

        return Results.Problem(
            detail: detail,
            instance: context.Request.Path,
            statusCode: statusCode,
            title: ReasonPhrases.GetReasonPhrase(statusCode),
            type: $"urn:kjwebsite:error:{code.Replace('_', '-').ToLowerInvariant()}",
            extensions: extensions);
    }
}

public sealed record LegacyApiError(string Code, string Message);
