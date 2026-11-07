using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OpenConnect.QdrantVactor;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQdrantVactor(this IServiceCollection services, Action<QdrantVactorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddSingleton<IValidateOptions<QdrantVactorOptions>, QdrantVactorOptionsValidator>();
        services.AddOptions<QdrantVactorOptions>().Configure(configure).ValidateOnStart();
        services.TryAddSingleton<IEmbeddingGenerator, DeterministicEmbeddingStub>();
        services.TryAddSingleton<IVectorStore, InMemoryVectorStore>();
        services.TryAddSingleton<IProjectionReader, ProjectionReader>();
        services.TryAddSingleton<IAgentScopeAuthorizer, DenyAgentScopeAuthorizer>();
        services.TryAddSingleton<QdrantVactorIndexer>();
        services.TryAddSingleton<OpenConnect.Abstractions.IAgentSemanticMemory, SemanticMemorySearchProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, QdrantVactorService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<QdrantVactorOptions>, SubscriptionOptionsValidator>());
        return services;
    }

    /// <summary>Replaces the in-memory store with the Qdrant REST store. Requires an API key and a valid endpoint.</summary>
    public static IServiceCollection UseQdrantRestVectorStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Replace(ServiceDescriptor.Singleton<IVectorStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<QdrantVactorOptions>>().Value;
            if (options.Endpoint is null || string.IsNullOrWhiteSpace(options.ApiKey))
                throw new InvalidOperationException("Qdrant REST store requires Endpoint and ApiKey.");
            // Singleton store owns one long-lived client (avoids a new package dependency).
            return new QdrantVectorStoreRest(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, options.Endpoint, options.ApiKey, options.CollectionName, options.VectorSize);
        }));
        return services;
    }

    private sealed class SubscriptionOptionsValidator(IServiceProvider services) : IValidateOptions<QdrantVactorOptions>
    {
        public ValidateOptionsResult Validate(string? name, QdrantVactorOptions options) =>
            options.Enabled && options.SubscribeToAgentEvents && services.GetService<OpenConnect.Abstractions.IAgentEventSubscriptionV2>() is null
                ? ValidateOptionsResult.Fail("QdrantVactor requires IAgentEventSubscriptionV2 when enabled for agent event subscriptions.")
                : ValidateOptionsResult.Success;
    }
}
