using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace KJWebsite.BuildingBlocks;

public static class ProblemDetailsFactory
{
    public static IResult Create(HttpContext context, int statusCode, string code, string detail)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["error"] = new LegacyApiError(code, detail)
        };

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
