using Microsoft.AspNetCore.Http;

namespace KJWebsite.BuildingBlocks;

public static class ApiError
{
    public static IResult Create(HttpContext context, int statusCode, string code, string message) =>
        ProblemDetailsFactory.Create(context, statusCode, code, message);
}
