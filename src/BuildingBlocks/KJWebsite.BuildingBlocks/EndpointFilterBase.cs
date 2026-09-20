using Microsoft.AspNetCore.Http;

namespace KJWebsite.BuildingBlocks;

public abstract class EndpointFilterBase : IEndpointFilter
{
#pragma warning disable CA1716 // IEndpointFilter exposes this parameter as 'next'.
    public abstract ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next);
#pragma warning restore CA1716
}
