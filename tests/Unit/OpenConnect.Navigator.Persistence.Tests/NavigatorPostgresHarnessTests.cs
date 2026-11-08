// Non-durable EF InMemory harness behavior tests; not proof of PostgreSQL durability,
// SQL translation, transactional guarantees, or cross-process concurrency.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.InMemory.Diagnostics;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using OpenConnect.Persistence.Postgres.Entities;
using Xunit;

namespace OpenConnect.Navigator.Persistence.Tests;

public sealed class NavigatorPostgresHarnessTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");

    private static (OpenConnectDbContext Db, PostgresDurableNavigatorStore Store) CreateStore()
    {
        var options = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var db = new OpenConnectDbContext(options);
        return (db, new PostgresDurableNavigatorStore(db));
    }

    private static async Task<string> SeedSession(PostgresDurableNavigatorStore store, string tenant, string principal) =>
        await store.CreateSessionAsync(tenant, principal, "client", "route");

    [Fact]
    public async Task Receipt_RoundTripsNullResultAndIdenticalRetryIsReplay()
    {
        await using var db = CreateStore().Db;
        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var session = await SeedSession(store, tenant, principal);
        var approval = new NavigatorApprovalRecord(session, "command-1", "digest-1", Now.AddHours(1));
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.CreatePendingApprovalAsync(session, tenant, principal, approval));
        var receipt = new NavigatorReceiptRecord("command-1", "key-1", "digest-1", null, Now.AddHours(1), 4);

        var first = await store.ConsumeApprovalAndSaveReceiptAsync(session, tenant, principal, "command-1", "digest-1", Now, receipt);
        var replay = await store.ConsumeApprovalAndSaveReceiptAsync(session, tenant, principal, "command-1", "digest-1", Now, receipt);
        var read = await store.GetReceiptAsync(tenant, principal, "key-1", Now);

        Assert.Equal(NavigatorStoreOutcome.Succeeded, first.Outcome);
        Assert.False(first.IsReplay);
        Assert.Equal(new NavigatorStoreResult(NavigatorStoreOutcome.Succeeded, true), replay);
        Assert.NotNull(read);
        Assert.Equal(receipt, read);
        Assert.Null(read.Result);
    }

    [Fact]
    public async Task ExpiredReceipt_IsAbsentAndConsumeReportsExpired()
    {
        await using var db = CreateStore().Db;
        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var session = await SeedSession(store, tenant, principal);
        var receipt = new NavigatorReceiptRecord("command-exp", "expired-key", "digest", "result", Now, 1);
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.CreatePendingApprovalAsync(session, tenant, principal,
            new NavigatorApprovalRecord(session, "command-exp", "digest", Now.AddHours(1))));
        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            (await store.ConsumeApprovalAndSaveReceiptAsync(session, tenant, principal, "command-exp", "digest", Now.AddMinutes(-1), receipt)).Outcome);

        Assert.Null(await store.GetReceiptAsync(tenant, principal, "expired-key", Now));
        Assert.Equal(NavigatorStoreOutcome.Expired,
            (await store.ConsumeApprovalAndSaveReceiptAsync(session, tenant, principal, "command-exp", "digest", Now, receipt)).Outcome);
    }

    [Fact]
    public async Task SnapshotUpdate_PreservesUnrelatedCheckpointJsonAndRejectsStaleRevision()
    {
        await using var db = CreateStore().Db;
        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var session = await SeedSession(store, tenant, principal);
        var row = await db.Sessions.SingleAsync(x => x.Id == Guid.Parse(session));
        row.ContextCheckpoint = "{\"unrelated\":{\"keep\":\"yes\"},\"navigator\":{\"state\":\"Created\",\"clientInstance\":\"client\",\"routeId\":\"route\",\"revision\":0}}";
        await db.SaveChangesAsync();

        var snapshot = new NavigatorSessionSnapshot(session, tenant, principal, NavigatorSessionState.Pairing, "client", "route", 1);
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.UpdateSessionSnapshotAsync(session, tenant, principal, snapshot, 0));
        var stored = await db.Sessions.AsNoTracking().SingleAsync(x => x.Id == Guid.Parse(session));
        Assert.Contains("\"unrelated\":{\"keep\":\"yes\"}", stored.ContextCheckpoint);
        Assert.Equal(NavigatorStoreOutcome.StaleFence, await store.UpdateSessionSnapshotAsync(session, tenant, principal, snapshot, 0));
    }

    [Fact]
    public async Task MissingLegacyNavigatorCheckpoint_FailsClosed()
    {
        await using var db = CreateStore().Db;
        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var session = await SeedSession(store, tenant, principal);
        var row = await db.Sessions.SingleAsync(x => x.Id == Guid.Parse(session));
        row.ContextCheckpoint = "{\"other\":true}";
        await db.SaveChangesAsync();

        Assert.Null(await store.GetSessionAsync(session, tenant, principal));
    }

    [Fact]
    public async Task EventAppend_RequiresOrderedUniqueSequenceAndReadsInOrder()
    {
        await using var db = CreateStore().Db;
        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var session = await SeedSession(store, tenant, principal);
        NavigatorEvent Event(long sequence) => new(Guid.NewGuid().ToString("D"), "navigator.session.updated", session, null, sequence, null, Now);

        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.AppendEventAsync(session, tenant, principal, 0, Event(0)));
        Assert.Equal(NavigatorStoreOutcome.Conflict, await store.AppendEventAsync(session, tenant, principal, 0, Event(0)));
        Assert.Equal(NavigatorStoreOutcome.Conflict, await store.AppendEventAsync(session, tenant, principal, 2, Event(2)));
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.AppendEventAsync(session, tenant, principal, 1, Event(1)));
        Assert.Equal(new long[] { 0, 1 }, (await store.GetEventsAsync(session, tenant, principal, -1)).Select(x => x.Sequence));
    }

    [Fact]
    public async Task ReceiptAndSessionReads_AreIsolatedByTenantAndPrincipal()
    {
        await using var db = CreateStore().Db;
        var store = new PostgresDurableNavigatorStore(db);
        var tenant = Guid.NewGuid().ToString("D");
        var principal = Guid.NewGuid().ToString("D");
        var otherTenant = Guid.NewGuid().ToString("D");
        var otherPrincipal = Guid.NewGuid().ToString("D");
        var session = await SeedSession(store, tenant, principal);
        var receipt = new NavigatorReceiptRecord("command", "tenant-key", "digest", "result", Now.AddHours(1), 1);
        Assert.Equal(NavigatorStoreOutcome.Succeeded, await store.CreatePendingApprovalAsync(session, tenant, principal,
            new NavigatorApprovalRecord(session, "command", "digest", Now.AddHours(1))));
        Assert.Equal(NavigatorStoreOutcome.Succeeded,
            (await store.ConsumeApprovalAndSaveReceiptAsync(session, tenant, principal, "command", "digest", Now, receipt)).Outcome);

        Assert.Null(await store.GetSessionAsync(session, otherTenant, principal));
        Assert.Null(await store.GetSessionAsync(session, tenant, otherPrincipal));
        Assert.Null(await store.GetReceiptAsync(otherTenant, principal, "tenant-key", Now));
        Assert.Null(await store.GetReceiptAsync(tenant, otherPrincipal, "tenant-key", Now));
    }
}
