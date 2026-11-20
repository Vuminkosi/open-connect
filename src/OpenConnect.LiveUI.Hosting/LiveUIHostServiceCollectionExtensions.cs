using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Hosting.AspNetCore.Execution;

namespace OpenConnect.LiveUI.Hosting;

/// <summary>Registers the connection-bound LiveUI host executor.</summary>
public static class LiveUIHostServiceCollectionExtensions
{
    /// <summary>Registers the execution adapter without replacing host registrations.</summary>
    /// <remarks>The host must separately register an authenticated task authorizer, task execution,
    /// a client interaction broker, and an <see cref="ILiveUIHostExecutionInputsFactory"/> that supplies
    /// trusted model, authorization, persistence and turn-gate inputs.</remarks>
    public static IServiceCollection AddOpenConnectLiveUIHostExecution(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IHostTaskAgentExecutionFactory, LiveUIHostTaskAgentExecutionFactory>();
        services.AddMafHostTaskExecutor();
        return services;
    }
}
