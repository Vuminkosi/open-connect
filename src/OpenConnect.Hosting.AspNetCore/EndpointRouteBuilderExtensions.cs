using Microsoft.AspNetCore.Routing;

namespace OpenConnect.Hosting.AspNetCore;

public static class EndpointRouteBuilderExtensions
{
    /// <summary>Reserves the OpenConnect endpoint mapping extension; no endpoints are exposed yet.</summary>
    public static IEndpointRouteBuilder MapOpenConnect(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints;
    }
}
