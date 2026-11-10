using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.TryAddSingleton(new OpenConnectComposerOptions());
        services.AddScoped<OpenConnectComponentRegistry>();
        services.AddScoped<ComposerSession>();
        return services;
    }

    /// <summary>
    /// Registers the anchored composer (FAB + bubble) session and registry.
    /// Text-only against the real gateway; no workers required.
    /// </summary>
    public static IServiceCollection AddOpenConnectComposer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddOpenConnectComponents();
    }

    /// <summary>
    /// Registers and configures the anchored composer (FAB + bubble) session, options, and registry.
    /// </summary>
    public static IServiceCollection AddOpenConnectComposer(
        this IServiceCollection services,
        Action<OpenConnectComposerOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new OpenConnectComposerOptions();
        configure?.Invoke(options);

        services.RemoveAll<OpenConnectComposerOptions>();
        services.AddSingleton(options);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        return services.AddOpenConnectComponents();
    }

    /// <summary>
    /// Registers a Blazor component type under a convention-derived descriptor name
    /// (or its <see cref="IOpenConnectComponent.Descriptor"/> when implemented) so the
    /// assistant may propose it and <see cref="SafeComponentRenderer"/> may render it.
    /// </summary>
    public static IServiceCollection AddOpenConnectComponent<TComponent>(this IServiceCollection services)
        where TComponent : class, Microsoft.AspNetCore.Components.IComponent
    {
        ArgumentNullException.ThrowIfNull(services);
        var descriptor = ResolveDefaultDescriptor(typeof(TComponent));
        return services.AddOpenConnectComponent<TComponent>(descriptor);
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

    private static OpenConnectComponentDescriptor ResolveDefaultDescriptor(Type componentType)
    {
        if (typeof(IOpenConnectComponent).IsAssignableFrom(componentType)
            && componentType.GetConstructor(Type.EmptyTypes) is not null
            && Activator.CreateInstance(componentType) is IOpenConnectComponent instance
            && instance.Descriptor is { IsValid: true } explicitDescriptor)
        {
            return explicitDescriptor;
        }

        var rawName = componentType.Name;
        var backtick = rawName.IndexOf('`');
        if (backtick > 0)
        {
            rawName = rawName[..backtick];
        }

        if (rawName.EndsWith("Component", StringComparison.Ordinal) && rawName.Length > "Component".Length)
        {
            rawName = rawName[..^"Component".Length];
        }

        var slug = new StringBuilder(rawName.Length + 8);
        var title = new StringBuilder(rawName.Length + 8);
        for (var i = 0; i < rawName.Length; i++)
        {
            var c = rawName[i];
            if (!char.IsLetterOrDigit(c))
            {
                continue;
            }

            if (char.IsUpper(c) && slug.Length > 0 && (char.IsLower(rawName[i - 1]) || char.IsDigit(rawName[i - 1])))
            {
                slug.Append('-');
                title.Append(' ');
            }

            slug.Append(char.ToLowerInvariant(c));
            title.Append(c);
        }

        var safeName = slug.Length > 0 ? slug.ToString() : "component";
        if (safeName.Length > 64)
        {
            safeName = safeName[..64].TrimEnd('-');
        }

        var safeTitle = title.Length > 0 ? title.ToString() : componentType.Name;
        return new OpenConnectComponentDescriptor(safeName, safeTitle, $"Registered component {componentType.Name}.");
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
