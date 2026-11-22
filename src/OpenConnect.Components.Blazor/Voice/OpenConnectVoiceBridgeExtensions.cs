using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenConnect.Components.Blazor.Voice;

/// <summary>DI and middleware registration helpers for the OpenConnect Blazor voice WebSocket bridge.</summary>
public static class OpenConnectVoiceBridgeExtensions
{
    /// <summary>
    /// Registers <see cref="OpenConnectVoiceTicketStore"/>, <see cref="OpenConnectVoiceBridge"/>,
    /// and a startup filter that wires WebSockets and the voice bridge middleware automatically.
    /// </summary>
    public static IServiceCollection AddOpenConnectVoiceBridge(
        this IServiceCollection services,
        Action<OpenConnectVoiceBridgeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<OpenConnectVoiceBridgeOptions>()
            .Configure(configure)
            .Validate(options =>
            {
                options.Validate();
                return true;
            })
            .ValidateOnStart();

        services.TryAddSingleton<OpenConnectVoiceTicketStore>();
        services.TryAddSingleton<OpenConnectVoiceBridge>();
        services.AddSingleton<IStartupFilter, OpenConnectVoiceBridgeStartupFilter>();
        return services;
    }

    /// <summary>Adds the OpenConnect voice WebSocket bridge middleware explicitly to the request pipeline.</summary>
    public static IApplicationBuilder UseOpenConnectVoiceBridge(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseWebSockets();
        var bridge = app.ApplicationServices.GetRequiredService<OpenConnectVoiceBridge>();
        return app.Use((context, next) => bridge.InvokeAsync(context, _ => next()));
    }

    private sealed class OpenConnectVoiceBridgeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseOpenConnectVoiceBridge();
            next(app);
        };
    }
}
