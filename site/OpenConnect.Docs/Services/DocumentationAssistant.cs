using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using OpenConnect.Client;
using OpenConnect.Components.Blazor;
using OpenConnect.Components.Contracts;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Blazor;
using OpenConnect.LiveUI.Inference;
using Microsoft.Extensions.Options;
using OpenConnect.Docs.Services.Voice;
using OpenConnect.Docs.Components.AssistantShowcase;

namespace OpenConnect.Docs.Services;

public sealed record DocumentationAssistantAvailability(bool Configured);

public static class DocumentationAssistant
{
    public static IServiceCollection AddDocumentationAssistant(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var gateway = ResolveGateway(configuration, environment);
        var configured = gateway is not null;
        services.AddSingleton(new DocumentationAssistantAvailability(configured));
        services.AddSingleton(provider => DocumentationLiveUIManifest.Create(
            provider.GetRequiredService<DocumentationContentStore>().Documents,
            provider.GetRequiredService<GeneratedApiStore>().Documents.Count > 0));
        services.AddMudServices();
        services.AddOpenConnectComponent<DocumentationGuidesCard>(new OpenConnectComponentDescriptor(
            "documentation-guides", "Integration guides", "Links to getting started, privacy and LiveUI documentation."));
        if (configured)
        {
            var credential = configuration["OpenConnect:Assistant:GatewayBearer"];
            var visitorKey = configuration["OpenConnect:Assistant:VisitorSigningKey"];
            var visitorTenant = configuration["OpenConnect:Assistant:VisitorTenant"];
            var visitorIdentityEnabled = !string.IsNullOrWhiteSpace(visitorKey) || !string.IsNullOrWhiteSpace(visitorTenant);
            if (visitorIdentityEnabled && (!string.IsNullOrEmpty(credential) ||
                string.IsNullOrWhiteSpace(visitorKey) || string.IsNullOrWhiteSpace(visitorTenant) || gateway!.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("The documentation visitor identity requires HTTPS, a tenant and signing key, without a shared gateway bearer.");
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
            if (visitorIdentityEnabled)
            {
                services.AddSingleton(new DocumentationGatewayVisitorTokens(visitorTenant!, visitorKey!));
                services.AddSingleton<DocumentationVoiceTickets>();
                services.AddSingleton(provider => new DocumentationVoiceBridge(
                    provider.GetRequiredService<DocumentationVoiceTickets>(), gateway!));
                services.AddSingleton<IStartupFilter, DocumentationVoiceStartupFilter>();
                services.AddScoped<DocumentationGatewayVisitorIdentity>();
                services.AddScoped<IOpenConnectClient>(provider => new OpenConnectClient(
                    CreateVisitorClient(provider), Options.Create(provider.GetRequiredService<OpenConnectClientOptions>())));
                services.AddScoped(provider =>
                {
                    var client = CreateVisitorClient(provider);
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    return new GatewayWorkerLiveUIInferenceProvider(client);
                });
                services.AddScoped<ILiveUIInferenceProvider>(provider => provider.GetRequiredService<GatewayWorkerLiveUIInferenceProvider>());
            }
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
        services.Configure<LiveUIInferenceOptions>(options =>
            options.Worker.Enabled = configured && configuration.GetValue<bool>("OpenConnect:Assistant:LiveUIWorkerEnabled"));
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

    private static HttpClient CreateVisitorClient(IServiceProvider provider)
    {
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IOpenConnectClient));
        client.BaseAddress = provider.GetRequiredService<OpenConnectClientOptions>().ApiBaseAddress;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            provider.GetRequiredService<DocumentationGatewayVisitorIdentity>().Token);
        return client;
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

internal sealed class DocumentationGatewayVisitorIdentity(DocumentationGatewayVisitorTokens tokens)
{
    public string Token { get; } = tokens.Issue();
}
