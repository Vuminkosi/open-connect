using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenConnect.LiveUI.Agents;

public static class LiveUIAgentServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectLiveUIAgentTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<LiveUIAgentTools>();
        return services;
    }
}
