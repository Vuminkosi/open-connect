using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Catalog;

namespace OpenConnect.Agents.Templates;

/// <summary>Registers agents and templates in the Agents package's DI container.</summary>
public static class AgentRegistrationExtensions
{
    /// <summary>Registers an agent definition using the supplied configuration callback.</summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="id">Agent identifier.</param>
    /// <param name="configure">Optional definition configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddOpenConnectAgent(this IServiceCollection services, string id, Action<AgentDefinitionBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = new AgentDefinitionBuilder(id);
        configure?.Invoke(builder);
        EnsureRegistries(services);
        GetCatalog(services).Register(builder.Build());
        return services;
    }

    /// <summary>Registers the single lead agent using the supplied configuration callback.</summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="id">Lead agent identifier.</param>
    /// <param name="configure">Optional definition configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddOpenConnectLeadAgent(this IServiceCollection services, string id, Action<AgentDefinitionBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(LeadAgentRegistration)))
            throw new InvalidOperationException("Only one lead agent may be registered.");
        var builder = new AgentDefinitionBuilder(id);
        configure?.Invoke(builder);
        EnsureRegistries(services);
        var definition = builder.Build();
        GetCatalog(services).Register(definition);
        services.AddSingleton(new LeadAgentRegistration(definition.Id));
        return services;
    }

    /// <summary>Registers an agent template using the supplied configuration callback.</summary>
    /// <param name="services">Service collection to configure.</param>
    /// <param name="id">Template identifier.</param>
    /// <param name="configure">Optional template configuration.</param>
    /// <param name="allowWidening">Whether trusted approval permits widening its base template.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddOpenConnectAgentTemplate(this IServiceCollection services, string id, Action<AgentTemplateBuilder>? configure = null, bool allowWidening = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = new AgentTemplateBuilder(id);
        configure?.Invoke(builder);
        EnsureRegistries(services);
        GetTemplateRegistry(services).Register(builder.Build(), allowWidening);
        return services;
    }

    private static void EnsureRegistries(IServiceCollection services)
    {
        services.TryAddSingleton<AgentCatalog>();
        services.TryAddSingleton<IAgentDefinitionRegistry>(provider => provider.GetRequiredService<AgentCatalog>());
        services.TryAddSingleton<TemplateRegistry>();
    }

    // Registrations are applied eagerly to the shared singleton instance, making duplicate IDs fail during composition.
    private static AgentCatalog GetCatalog(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(item => item.ServiceType == typeof(AgentCatalog));
        if (descriptor?.ImplementationInstance is AgentCatalog instance)
            return instance;
        instance = new AgentCatalog();
        services.Replace(ServiceDescriptor.Singleton(instance));
        services.Replace(ServiceDescriptor.Singleton<IAgentDefinitionRegistry>(instance));
        return instance;
    }

    private static TemplateRegistry GetTemplateRegistry(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(item => item.ServiceType == typeof(TemplateRegistry));
        if (descriptor?.ImplementationInstance is TemplateRegistry instance)
            return instance;
        instance = new TemplateRegistry();
        services.Replace(ServiceDescriptor.Singleton(instance));
        return instance;
    }

    /// <summary>Mutable configuration builder for an agent definition.</summary>
    public sealed class AgentDefinitionBuilder
    {
        private readonly string _id;
        /// <summary>Creates a builder for the specified agent identifier.</summary>
        /// <param name="id">Agent identifier.</param>
        public AgentDefinitionBuilder(string id) => _id = id;
        /// <summary>Gets or sets the display name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Gets or sets the agent version.</summary>
        public string Version { get; set; } = "1";
        /// <summary>Gets or sets the model requirement.</summary>
        public ModelRequirement? ModelRequirement { get; set; }
        /// <summary>Gets or sets the tool policy.</summary>
        public AgentToolPolicy ToolPolicy { get; set; } = new();
        /// <summary>Gets or sets the context policy.</summary>
        public AgentContextPolicy ContextPolicy { get; set; } = new();
        /// <summary>Gets or sets the memory policy.</summary>
        public AgentMemoryPolicy MemoryPolicy { get; set; } = new();
        /// <summary>Gets or sets the execution budget.</summary>
        public AgentBudget Budget { get; set; } = new();

        internal AgentDefinition Build() => new(new AgentId(_id), string.IsNullOrWhiteSpace(Name) ? _id : Name)
        {
            Version = Version,
            ModelRequirement = ModelRequirement,
            ToolPolicy = ToolPolicy,
            ContextPolicy = ContextPolicy,
            MemoryPolicy = MemoryPolicy,
            Budget = Budget
        };
    }

    /// <summary>Mutable configuration builder for an agent template.</summary>
    public sealed class AgentTemplateBuilder
    {
        private readonly string _id;
        /// <summary>Creates a builder for the specified template identifier.</summary>
        /// <param name="id">Template identifier.</param>
        public AgentTemplateBuilder(string id) => _id = id;
        /// <summary>Gets or sets the template version.</summary>
        public string Version { get; set; } = "1";
        /// <summary>Gets or sets the template description.</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>Gets or sets the relative instruction file path.</summary>
        public string InstructionsPath { get; set; } = string.Empty;
        /// <summary>Gets or sets model requirements.</summary>
        public ModelRequirement? ModelRequirements { get; set; }
        /// <summary>Gets or sets allowed tools.</summary>
        public IReadOnlySet<ToolId> AllowedToolIds { get; set; } = new HashSet<ToolId>();
        /// <summary>Gets or sets allowed scopes.</summary>
        public IReadOnlySet<string>? AllowedScopes { get; set; }
        /// <summary>Gets or sets the optional output schema reference.</summary>
        public string? OutputSchemaRef { get; set; }
        /// <summary>Gets or sets the resource budget.</summary>
        public AgentBudget Budget { get; set; } = new();
        /// <summary>Gets or sets the memory policy.</summary>
        public AgentMemoryPolicy MemoryPolicy { get; set; } = new();
        /// <summary>Gets or sets the context sharing mode.</summary>
        public AgentContextSharingMode ContextSharing { get; set; } = AgentContextSharingMode.TaskSummary;
        /// <summary>Gets or sets the tool acknowledgement mode.</summary>
        public ToolAcknowledgementMode AckPolicy { get; set; } = ToolAcknowledgementMode.Automatic;
        /// <summary>Gets or sets the safety label.</summary>
        public string SafetyLabel { get; set; } = "standard";
        /// <summary>Gets or sets the optional base template identifier.</summary>
        public string? BaseTemplateId { get; set; }

        internal AgentTemplate Build() => new(_id, Version, Description, InstructionsPath, ModelRequirements,
            AllowedToolIds, OutputSchemaRef, Budget, MemoryPolicy, ContextSharing, AckPolicy, SafetyLabel,
            BaseTemplateId, AllowedScopes);
    }

    /// <summary>Identifies the application lead agent.</summary>
    /// <param name="AgentId">Registered lead agent identifier.</param>
    public sealed record LeadAgentRegistration(AgentId AgentId);
}
