using OpenConnect.Components.Blazor.Voice;

namespace OpenConnect.Docs.Services.Voice;

/// <summary>Thin docs-site adapter over the SDK <see cref="OpenConnectVoiceTicketStore"/>.</summary>
internal sealed class DocumentationVoiceTickets(TimeProvider? clock = null)
{
    public OpenConnectVoiceTicketStore Store { get; } = new(clock);

    public string Issue(string sessionId, string visitorToken) => Store.Issue(sessionId, visitorToken);

    public bool TryConsume(string ticket, string sessionId, out string? visitorToken) =>
        Store.TryConsume(ticket, sessionId, out visitorToken);
}
