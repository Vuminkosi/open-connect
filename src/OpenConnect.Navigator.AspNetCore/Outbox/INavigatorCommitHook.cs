using OpenConnect.Navigator;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Host integration point invoked only after a session commit has succeeded.</summary>
public interface INavigatorCommitHook
{
    /// <summary>
    /// Publishes safe committed-event notices. Host code must invoke this after the session transaction
    /// commits; true atomic persistence requires the supplied writer to enlist in that same transaction.
    /// </summary>
    Task AfterCommitAsync(string sessionId, IReadOnlyList<NavigatorEvent> committedEvents, CancellationToken cancellationToken = default);
}
