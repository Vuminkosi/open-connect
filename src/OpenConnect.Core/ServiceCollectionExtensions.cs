using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Core.Configuration;

namespace OpenConnect.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnect(this IServiceCollection services, Action<OpenConnectOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ThrowOnConflictingRepeatRegistration(services, configure);
        services.TryAddSingleton(OptionsConflictGuard.Capture(Materialize(configure)));
        services.AddSingleton<IValidateOptions<OpenConnectOptions>, OpenConnectOptionsValidator>();
        services.AddOptions<OpenConnectOptions>().Configure(options => configure?.Invoke(options)).ValidateOnStart();
        services.TryAddSingleton<IAgentDefinitionRegistry>(provider =>
        {
            var registry = new AgentDefinitionRegistry();
            if (provider.GetRequiredService<IOptions<OpenConnectOptions>>().Value.Agents.RegisterLeadAgent)
                registry.Register(OpenConnectLeadAgent.Definition);
            return registry;
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<OpenConnectOptions>, PrivacyWarningConfiguration>());
        return services;
    }

    private static void ThrowOnConflictingRepeatRegistration(IServiceCollection services, Action<OpenConnectOptions>? configure)
    {
        // A null configure delegate cannot introduce a conflict; treat as idempotent.
        if (configure is null)
            return;
        var registered = services
            .FirstOrDefault(d => d.ServiceType == typeof(OpenConnectRegistrationSnapshot))?
            .ImplementationInstance as OpenConnectRegistrationSnapshot;
        if (registered is null)
            return;
        var conflict = OptionsConflictGuard.FindConflict(registered, OptionsConflictGuard.Capture(Materialize(configure)));
        if (conflict.HasValue)
            throw OptionsConflictGuard.ConflictException(conflict.Value.Field, conflict.Value.OldValue, conflict.Value.NewValue);
    }

    private static OpenConnectOptions Materialize(Action<OpenConnectOptions>? configure)
    {
        var options = new OpenConnectOptions();
        configure?.Invoke(options);
        return options;
    }
    private sealed class PrivacyWarningConfiguration : IPostConfigureOptions<OpenConnectOptions>
    {
        public void PostConfigure(string? name, OpenConnectOptions options)
        {
            if (!options.Privacy.Enabled)
                System.Diagnostics.Trace.TraceWarning("OpenConnect privacy enforcement is explicitly disabled. Authentication and authorization remain enabled.");
        }
    }
}

public sealed class AgentDefinitionRegistry : IAgentDefinitionRegistry
{
    private readonly Dictionary<AgentId, AgentDefinition> _agents = new();
    private readonly object _gate = new();

    public AgentDefinition? Get(AgentId agentId)
    {
        lock (_gate) return _agents.GetValueOrDefault(agentId);
    }

    public IReadOnlyList<AgentDefinition> GetAll()
    {
        lock (_gate) return _agents.Values.ToArray();
    }

    public void Register(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (!_agents.TryAdd(definition.Id, definition))
                throw new InvalidOperationException($"Agent '{definition.Id.Value}' is already registered.");
        }
    }
}

public static class OpenConnectLeadAgent
{
    public static readonly AgentDefinition Definition = new(new AgentId("lead"), "Lead");
}
