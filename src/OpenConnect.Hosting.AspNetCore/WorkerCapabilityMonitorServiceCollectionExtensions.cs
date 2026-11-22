using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>Registers explicitly configured, private worker capability monitoring.</summary>
public static class WorkerCapabilityMonitorServiceCollectionExtensions
{
    /// <summary>Registers one monitor; its source IDs must be exclusively owned and the host must register a capability registry.</summary>
    public static IServiceCollection AddOpenConnectWorkerCapabilityMonitor(this IServiceCollection services,
        IReadOnlyList<WorkerCapabilitySource> sources, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(sources);
        var snapshot = Array.AsReadOnly(sources.ToArray());
        if (services.Any(item => item.ServiceType == typeof(Marker)))
            throw new InvalidOperationException("A worker capability monitor is already registered.");
        services.AddSingleton(new Marker());
        services.AddSingleton<IHostedService>(provider => new WorkerCapabilityMonitor(
            provider.GetRequiredService<ICapabilityRegistry>(), snapshot, interval));
        return services;
    }

    private sealed class Marker;
}
