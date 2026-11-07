using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Stores;
using OpenConnect.Persistence.EFCore.Transactions;

namespace OpenConnect.Persistence.EFCore.DependencyInjection;

public static class OpenConnectEfServiceCollectionExtensions
{
    private static readonly object RegistrationLock = new();
    private static readonly HashSet<Type> RegisteredContexts = [];

    public static IServiceCollection AddOpenConnectEntityFramework<TContext>(this IServiceCollection services, Action<OpenConnectEfOptions>? configure = null) where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        lock (RegistrationLock)
        {
            if (RegisteredContexts.Contains(typeof(TContext))) return services;
            if (RegisteredContexts.Count > 0) throw new InvalidOperationException("ef_ambiguous_registration: only one OpenConnect EF context may be registered.");
            if (!services.Any(d => d.ServiceType == typeof(TContext)))
                throw new InvalidOperationException($"{OpenConnect.Abstractions.ErrorCodes.EfContextMissing}: Register ApplicationDbContext before AddOpenConnectEntityFramework.");
            RegisteredContexts.Add(typeof(TContext));
        }

        services.AddSingleton<IValidateOptions<OpenConnectEfOptions>, OpenConnectEfOptionsValidator>();
        services.AddOptions<OpenConnectEfOptions>().Configure(configure ?? (_ => { })).ValidateOnStart();
        services.TryAddScoped<OpenConnect.Abstractions.ITaskStore, EfTaskStore<TContext>>();
        services.TryAddScoped<OpenConnect.Abstractions.IEventJournal, EfEventJournal<TContext>>();
        services.TryAddScoped<OpenConnect.Abstractions.IOutboxStore, EfOutboxStore<TContext>>();
        services.TryAddScoped<ISessionStore, EfSessionStore<TContext>>();
        services.TryAddScoped<ILeaseStore, EfLeaseStore<TContext>>();
        services.TryAddScoped<IApprovalStore, EfApprovalStore<TContext>>();
        services.TryAddScoped<IPrivacyTokenVault, EfPrivacyTokenVault<TContext>>();
        services.TryAddScoped<IOpenConnectPersistenceUnitOfWork, EfUnitOfWork<TContext>>();
        return services;
    }
}
