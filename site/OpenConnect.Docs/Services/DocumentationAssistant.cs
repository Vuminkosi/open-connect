using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Components.Contracts;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;

namespace OpenConnect.Docs.Services;

public sealed record DocumentationAssistantAvailability(bool Configured);

public static class DocumentationAssistant
{
    public static IServiceCollection AddDocumentationAssistant(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var gateway = ResolveGateway(configuration, environment);
        var configured = gateway is not null;
        services.AddSingleton(new DocumentationAssistantAvailability(configured));
        services.AddSingleton(DocumentationLiveUIManifest.Create());
        services.AddMudServices();
        if (configured)
        {
            var credential = configuration["OpenConnect:Assistant:GatewayBearer"];
            if (!string.IsNullOrEmpty(credential) &&
                (credential.Any(char.IsWhiteSpace) || credential.Any(char.IsControl) ||
                 (gateway!.Scheme != Uri.UriSchemeHttps && !(environment.IsDevelopment() && gateway.IsLoopback))))
                throw new InvalidOperationException("The assistant gateway credential requires HTTPS or a development loopback gateway and a valid bearer token.");
            services.AddOpenConnectClient(options =>
            {
                options.ApiBaseAddress = gateway;
                options.AllowInsecureHttp = environment.IsDevelopment();
            });
            if (!string.IsNullOrEmpty(credential))
                services.AddHttpClient(nameof(IOpenConnectClient), client =>
                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential));
        }
        else
        {
            // Keep the actual composer visible without sending data to an unconfigured endpoint.
            services.AddHttpClient<IOpenConnectClient, OpenConnectClient>(client => client.BaseAddress = new Uri("https://127.0.0.1/"))
                .ConfigurePrimaryHttpMessageHandler(() => new UnavailableAssistantHandler());
        }
        services.AddOpenConnectComposer(options =>
        {
            options.Position = ComposerPosition.BottomRight;
            options.EnableTaskProgress = true;
            options.EnableComponentDiscovery = true;
            options.EnableVoiceInput = true;
            options.EnableVoiceOutput = true;
        });
        services.TryAddScoped<ILiveUIActionAuthorizer, DocumentationLiveUIActionAuthorizer>();
        services.AddOpenConnectLiveUIBlazor(options =>
        {
            options.Discovery.EnableRuntimeFallback = true;
            options.Interaction.EnableNavigation = true;
            options.Interaction.EnableHighlighting = true;
            options.Narration.Enabled = true;
            options.Narration.SynchronizeWithSpeech = true;
        });
        return services;
    }

    private static Uri? ResolveGateway(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>("OpenConnect:Assistant:Enabled")) return null;
        if (!Uri.TryCreate(configuration["OpenConnect:Assistant:GatewayUrl"], UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(environment.IsDevelopment() && uri.Scheme == Uri.UriSchemeHttp)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("The documentation assistant requires a valid application gateway URL.");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0) return new Uri(uri, "/openconnect/v1/");
        if (!path.EndsWith("/openconnect/v1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The documentation assistant requires an OpenConnect application gateway endpoint.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private sealed class UnavailableAssistantHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
