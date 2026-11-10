using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Stores;
using OpenConnect.Persistence.EFCore.Transactions;

namespace OpenConnect.Persistence.EFCore.DependencyInjection;

public static class OpenConnectEfServiceCollectionExtensions
{
    private sealed record EfContextRegistrationMarker(Type ContextType);

    public static IServiceCollection AddOpenConnectEntityFramework<TContext>(this IServiceCollection services, Action<OpenConnectEfOptions>? configure = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var existingMarker = services
            .FirstOrDefault(d => d.ServiceType == typeof(EfContextRegistrationMarker))?
            .ImplementationInstance as EfContextRegistrationMarker;
        if (existingMarker is not null && existingMarker.ContextType != typeof(TContext))
            throw new InvalidOperationException("ef_ambiguous_registration: only one OpenConnect EF context may be registered.");
        if (!services.Any(d => d.ServiceType == typeof(TContext)))
            throw new InvalidOperationException($"{OpenConnect.Abstractions.ErrorCodes.EfContextMissing}: Register ApplicationDbContext before AddOpenConnectEntityFramework.");
        if (existingMarker is null)
            services.AddSingleton(new EfContextRegistrationMarker(typeof(TContext)));

        // TryAdd* keeps repeats on the same collection safe. This static set only rejects a
        // second, different context because named stores are not supported.
        services.AddSingleton<IValidateOptions<OpenConnectEfOptions>, OpenConnectEfOptionsValidator>();
        services.AddOptions<OpenConnectEfOptions>().Configure(configure ?? (_ => { })).ValidateOnStart();
        services.TryAddScoped<EfTaskStore<TContext>>();
        services.TryAddScoped<OpenConnect.Abstractions.ITaskStore>(provider => provider.GetRequiredService<EfTaskStore<TContext>>());
        services.TryAddScoped<ITaskLifecycleStore>(provider => provider.GetRequiredService<EfTaskStore<TContext>>());
        services.TryAddScoped<OpenConnect.Abstractions.IEventJournal, EfEventJournal<TContext>>();
        services.TryAddScoped<OpenConnect.Abstractions.IOutboxStore, EfOutboxStore<TContext>>();
        services.TryAddScoped<ISessionStore, EfSessionStore<TContext>>();
        services.TryAddScoped<ILeaseStore, EfLeaseStore<TContext>>();
        services.TryAddScoped<IApprovalStore, EfApprovalStore<TContext>>();
        services.TryAddScoped<IPrivacyTokenVault, EfPrivacyTokenVault<TContext>>();
        services.TryAddScoped<INativeAgentSessionStore, EfNativeAgentSessionStore<TContext>>();
        services.TryAddScoped<ISessionUuidMappingStore, EfSessionUuidMappingStore<TContext>>();
        services.TryAddScoped<IDurableSessionUuidResolver, EfSessionUuidResolver<TContext>>();
        services.TryAddScoped<IOpenConnectPersistenceUnitOfWork, EfUnitOfWork<TContext>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Health.EfStartupValidator<TContext>>());
        return services;
    }
}
