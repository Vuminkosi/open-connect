using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class MiniComposerOriginPresenceTests
{
    [Fact]
    public void HostTaskExecution_defaults_to_noninteractive_origin()
    {
        var execution = new HostTaskExecution(
            new TaskId(Ulid.NewUlid()),
            new TenantId(Ulid.NewUlid()),
            new PrincipalId(Ulid.NewUlid()),
            new SessionId(Ulid.NewUlid()),
            "message",
            "idempotency");

        Assert.Equal(HostTaskOrigin.NonInteractive, execution.Origin);
    }

    [Fact]
    public void Presence_claim_requires_exactly_one_connection_and_is_single_use()
    {
        var journal = new InMemoryEventJournal();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.Null(journal.TryClaimSoleConnection(session));
        using var first = journal.TryRegisterBoundConnection(session, tenant, principal);
        using (var claim = journal.TryClaimSoleConnection(session))
        {
            Assert.NotNull(claim);
            Assert.Null(journal.TryClaimSoleConnection(session));
        }

        using (journal.TryRegisterBoundConnection(session, tenant, principal))
            Assert.Null(journal.TryClaimSoleConnection(session));

        using var nextClaim = journal.TryClaimSoleConnection(session);
        Assert.NotNull(nextClaim);
    }

    [Fact]
    public void Connection_presence_is_removed_after_disconnect()
    {
        var journal = new InMemoryEventJournal();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        using (journal.TryRegisterBoundConnection(session, tenant, principal))
            Assert.Equal(1, journal.GetConnectionCount(session));

        Assert.Equal(0, journal.GetConnectionCount(session));
        Assert.Null(journal.TryClaimSoleConnection(session));
    }
}
