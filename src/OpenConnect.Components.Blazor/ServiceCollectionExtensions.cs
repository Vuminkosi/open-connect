using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Components.Contracts;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// DI registration for the Blazor component catalogue and session state.
/// The host application owns MudBlazor providers (MudThemeProvider,
/// MudDialogProvider, MudSnackbarProvider, MudPopoverProvider); this library
/// never registers them.
/// </summary>
public static class OpenConnectBlazorServiceCollectionExtensions
{
    public static IServiceCollection AddOpenConnectComponents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<OpenConnectComponentRegistry>();
        services.AddScoped<ComposerSession>();
        return services;
    }

    /// <summary>
    /// Registers a Blazor component type under the given descriptor name so the
    /// assistant may propose it and <see cref="SafeComponentRenderer"/> may render it.
    /// </summary>
    public static IServiceCollection AddOpenConnectComponent<TComponent>(
        this IServiceCollection services,
        OpenConnectComponentDescriptor descriptor)
        where TComponent : class, Microsoft.AspNetCore.Components.IComponent
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.IsValid)
        {
            throw new ArgumentException("Component descriptor is invalid.", nameof(descriptor));
        }

        services.AddTransient<TComponent>();
        services.AddOptions<OpenConnectComponentRegistration<TComponent>>()
            .Configure(registry =>
            {
                registry.Descriptor = descriptor;
            });
        services.AddSingleton<IConfigureOpenConnectComponent>(new OpenConnectComponentConfiguration(typeof(TComponent), descriptor));
        return services;
    }
}

/// <summary>Marker contract used to apply component registrations to the scoped registry.</summary>
public interface IConfigureOpenConnectComponent
{
    void Apply(OpenConnectComponentRegistry registry);
}

internal sealed class OpenConnectComponentConfiguration(Type componentType, OpenConnectComponentDescriptor descriptor)
    : IConfigureOpenConnectComponent
{
    private readonly Type _componentType = componentType;
    private readonly OpenConnectComponentDescriptor _descriptor = descriptor;

    public void Apply(OpenConnectComponentRegistry registry) => registry.Register(_componentType, _descriptor);
}

/// <summary>Options holder so registrations survive DI validation.</summary>
public sealed class OpenConnectComponentRegistration<TComponent>
{
    public OpenConnectComponentDescriptor? Descriptor { get; set; }
}
