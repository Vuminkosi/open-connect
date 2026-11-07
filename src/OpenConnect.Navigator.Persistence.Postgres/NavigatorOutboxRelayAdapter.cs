using OpenConnect.Navigator.AspNetCore.Outbox;
using OpenConnect.Transport.Nats;

namespace OpenConnect.Navigator.Persistence.Postgres;

/// <summary>Bridges Navigator's worker-facing relay contract to the shared ACK-aware NATS outbox relay.</summary>
public sealed class NavigatorOutboxRelayAdapter(IOutboxRelaySource source, NatsPublisher publisher) : INavigatorOutboxRelay
{
    private const string Subject = "oc.v1.navigator.event";

    public Task<int> RelayOnceAsync(int limit, Guid leaseId, CancellationToken cancellationToken) =>
        new OutboxRelay(source, publisher).RelayOnceAsync(Subject, limit, leaseId, cancellationToken);
}
