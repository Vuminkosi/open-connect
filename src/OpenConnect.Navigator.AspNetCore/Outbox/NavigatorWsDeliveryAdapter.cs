using OpenConnect.Navigator.Events;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Authorized WebSocket projection boundary; callers send only returned safe committed events.</summary>
public sealed class NavigatorWsDeliveryAdapter(NavigatorWsProjector projector)
{
    public Task<CommittedNavigatorEvent?> ProjectAsync(
        NavigatorEvent value,
        NavigatorServerAuthContext serverContext,
        string consumerId,
        CancellationToken cancellationToken = default) =>
        projector.ProjectAsync(value, serverContext, cancellationToken, consumerId);

    public static object ProjectProgress(string sessionId, long sequence, string type, IReadOnlyDictionary<string, string> metadata) =>
        NavigatorWsProjector.ProjectProgress(sessionId, sequence, type, metadata);
}
