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

    private sealed class SubscriptionOptionsValidator(IServiceProvider services) : IValidateOptions<QdrantVactorOptions>
    {
        public ValidateOptionsResult Validate(string? name, QdrantVactorOptions options) =>
            options.Enabled && options.SubscribeToAgentEvents && services.GetService<OpenConnect.Abstractions.IAgentEventSubscriptionV2>() is null
                ? ValidateOptionsResult.Fail("QdrantVactor requires IAgentEventSubscriptionV2 when enabled for agent event subscriptions.")
                : ValidateOptionsResult.Success;
    }
}
