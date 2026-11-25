using OpenConnect.Components.Blazor.Voice;

namespace OpenConnect.Docs.Services.Voice;

/// <summary>Thin docs-site adapter over the SDK <see cref="OpenConnectVoiceBridge"/>.</summary>
internal sealed class DocumentationVoiceBridge(DocumentationVoiceTickets tickets, Uri gateway)
{
    private readonly OpenConnectVoiceBridge _bridge = new(tickets.Store, new OpenConnectVoiceBridgeOptions
    {
        GatewayBaseAddress = gateway,
        AllowLoopbackHttp = true
    });

    public Task InvokeAsync(HttpContext context, RequestDelegate next) => _bridge.InvokeAsync(context, next);
}
