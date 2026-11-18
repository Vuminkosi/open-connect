using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class LiveUIConnectionBindingJournalTests
{
    [Fact]
    public void Connections_are_server_generated_scoped_and_sole_claim_requires_one()
    {
        var journal = new InMemoryEventJournal();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        Assert.Null(journal.TryClaimSoleBoundConnection(session, tenant, principal));
        using var first = journal.TryRegisterBoundConnection(session, tenant, principal)!;
        Assert.False(string.IsNullOrWhiteSpace(first.ConnectionId));
        Assert.Equal(first.ConnectionId, journal.TryClaimSoleBoundConnection(session, tenant, principal));
        using var second = journal.TryRegisterBoundConnection(session, tenant, principal)!;
        Assert.Null(journal.TryClaimSoleBoundConnection(session, tenant, principal));
    }

    [Fact]
    public void Disconnect_expires_bound_work_and_reconnect_does_not_reassign_it()
    {
        var journal = new InMemoryEventJournal();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var old = journal.TryRegisterBoundConnection(session, tenant, principal)!;
        Assert.True(journal.TryBindPendingInteraction(old.ConnectionId, "corr"));
        old.Dispose();
        using var reconnect = journal.TryRegisterBoundConnection(session, tenant, principal)!;
        Assert.NotEqual(old.ConnectionId, reconnect.ConnectionId);
        Assert.False(journal.TryBindPendingInteraction(reconnect.ConnectionId, "corr"));
        Assert.Equal(ClientInteractionResponseAppendResult.RequestNotFound,
            journal.TryAppendBoundClientInteractionResponse(old.ConnectionId, session, tenant, principal,
                new ClientInteractionResponse("corr", "page", "{}"), "{}"));
    }
}
