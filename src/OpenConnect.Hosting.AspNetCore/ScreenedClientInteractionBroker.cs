using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Hosting.AspNetCore;

/// <summary>Delegates interactions and independently screens page responses before trusted reuse.</summary>
internal sealed class ScreenedClientInteractionBroker(InMemorySessionStore store, IEgressGate? gate)
    : IClientInteractionBroker, IScreenedClientPageBroker
{
    public Task<ClientInteractionResponse?> RequestAsync(SessionId sessionId, ClientInteractionRequest request,
        string expectedResultKind, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        store.RequestAsync(sessionId, request, expectedResultKind, timeout, cancellationToken);

    public async Task<ClientInteractionResponse?> RequestScreenedPageAsync(SessionId sessionId,
        ClientInteractionRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operation != "get_current_page" || gate is null)
            return null;
        var response = await store.RequestAsync(sessionId, request, "page", timeout, cancellationToken).ConfigureAwait(false);
        var session = store.Get(sessionId.Value);
        if (session is null || response is null || response.CorrelationId != request.CorrelationId || response.ResultKind != "page")
            return null;
        var result = await ClientPageEgressScreen.EvaluateAsync(response.PayloadJson, session, gate, cancellationToken)
            .ConfigureAwait(false);
        return result == ClientPageScreenResult.Allowed ? response : null;
    }
}
