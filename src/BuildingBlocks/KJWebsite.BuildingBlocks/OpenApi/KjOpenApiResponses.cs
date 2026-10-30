using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace KJWebsite.BuildingBlocks.OpenApi;

/// <summary>
/// Documents the error responses every endpoint can return, so the committed contract (and the types the frontend generates
/// from it) describe 400, 401, 403 and 404 as RFC 9457 Problem Details instead of leaving them out. Success responses are
/// declared on each endpoint with <c>Produces&lt;T&gt;</c>.
/// </summary>
public static class KjOpenApiResponses
{
    public const string ProblemJson = "application/problem+json";

    public static OpenApiOptions AddKjErrorResponses(this OpenApiOptions options)
    {
        // PagingEnvelope omits null members from its JSON (total, offset, nextCursor), so only items and limit are guaranteed.
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (context.JsonTypeInfo.Type is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(PagingEnvelope<>))
            {
                schema.Required = new HashSet<string> { "items", "limit" };
            }

            return Task.CompletedTask;
        });

        return options.AddOperationTransformer(async (operation, context, cancellationToken) =>
        {
            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            var method = context.Description.HttpMethod ?? string.Empty;
            var route = context.Description.RelativePath ?? string.Empty;
            var secured = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
            var hasInput = HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method)
                || (operation.Parameters?.Any(p => p.In == ParameterLocation.Query) ?? false);
            var addressesOne = route.Contains("{id}", StringComparison.Ordinal);

            var codes = new List<(int Status, string Description)>();
            if (hasInput)
            {
                codes.Add((StatusCodes.Status400BadRequest, "Validation failed. `errors` lists each field."));
            }

            if (secured)
            {
                codes.Add((StatusCodes.Status401Unauthorized, "Authentication is required."));
                codes.Add((StatusCodes.Status403Forbidden, "The caller lacks the required permission."));
            }

            if (addressesOne)
            {
                codes.Add((StatusCodes.Status404NotFound, "Not found."));
            }

            if (codes.Count == 0)
            {
                return;
            }

            // Makes sure the ProblemDetails schema is in components, then points every response at it.
            _ = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, cancellationToken);
            operation.Responses ??= [];
            foreach (var (status, description) in codes)
            {
                var key = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                operation.Responses.TryAdd(key, new OpenApiResponse
                {
                    Description = description,
                    Content = new Dictionary<string, OpenApiMediaType> { [ProblemJson] = new OpenApiMediaType { Schema = new OpenApiSchemaReference(nameof(ProblemDetails)) } }
                });
            }
        });
    }
}
