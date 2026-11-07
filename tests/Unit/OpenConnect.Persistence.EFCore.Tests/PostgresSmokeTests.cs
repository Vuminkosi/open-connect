using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Outbox;
using OpenConnect.Persistence.EFCore.Providers;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class PostgresSmokeTests
{
    private const string ConnectionString = "Host=localhost;Port=55432;Database=oc_smoke;Username=postgres;Password=postgres";

    private static OpenConnectDbContext CreateContext() => new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(ConnectionString).Options);

    [Fact]
    public async Task PostgresSmoke_MigrationContent_has_expected_openconnect_tables()
    {
        await using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();
        var lines = script.Split('\n');
        await File.WriteAllLinesAsync(Path.Combine(Path.GetTempPath(), "efv10-createscript.txt"), lines.Take(60));
        Assert.All(db.Model.GetEntityTypes(), e => Assert.Equal("openconnect", e.GetSchema()));
        Assert.Equal(9, db.Model.GetEntityTypes().Count(e => e.GetSchema() == "openconnect"));
        // PgContext defines no business entities; 9 = all OpenConnect tables, zero coupling
        Assert.Equal(9, System.Text.RegularExpressions.Regex.Matches(script, @"CREATE TABLE").Count);
        Assert.Equal(9, db.Model.GetEntityTypes().Count());
    }

    [Fact]
    public async Task PostgresSmoke_RoundTrip_persists_openconnect_workflows()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        var scope = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var sessionId = new SessionId(Ulid.NewUlid());
        var sessions = new EfSessionStore<OpenConnectDbContext>(db);
        await sessions.UpsertAsync(new(sessionId, scope, principal, 1));
        var taskId = new TaskId(Ulid.NewUlid());
        var tasks = new EfTaskStore<OpenConnectDbContext>(db);
        Assert.Equal(taskId, (await tasks.AcceptAsync(new(taskId, scope, principal, "pg-smoke-key"))).Id);
        var journal = new EfEventJournal<OpenConnectDbContext>(db);
        Assert.Equal(1, await journal.AppendAsync(new(sessionId, 0, "first", "one")));
        Assert.Equal(2, await journal.AppendAsync(new(sessionId, 0, "second", "two")));
        var events = new List<EventRecord>();
        await foreach (var item in journal.ReplayAsync(sessionId)) events.Add(item);
        Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.Sequence));
        var outbox = new EfOutboxStore<OpenConnectDbContext>(db);
        var entry = new OutboxEntry(Guid.NewGuid(), "pg-smoke", "payload");
        await outbox.EnqueueAsync(entry);
        Assert.Single(await outbox.GetPendingAsync(10));
        var leases = new EfLeaseStore<OpenConnectDbContext>(db);
        Assert.True(await leases.TryAcquireAsync(new("pg-smoke-resource", scope, "lease-owner", DateTimeOffset.UtcNow.AddMinutes(2), 1)));
        var approvals = new EfApprovalStore<OpenConnectDbContext>(db);
        var approval = new ApprovalRecord(Guid.NewGuid(), scope, principal, taskId, "pending", null);
        await approvals.CreateAsync(approval);
        await approvals.DecideAsync(approval.Id, scope, principal, "approved");
        Assert.Equal("approved", (await approvals.GetAsync(approval.Id, scope, principal))!.Decision);
        var vault = new EfPrivacyTokenVault<OpenConnectDbContext>(db);
        var encrypted = new byte[] { 1, 2, 3 };
        await vault.StoreAsync(new("pg-token", scope, "principal", sessionId, "email", encrypted, DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(encrypted, (await vault.GetAsync("pg-token", scope))!.EncryptedValue);
    }

    [Fact]
    public async Task PostgresSmoke_DispatcherNoDoubleDeliver_marks_claimed_rows_delivered()
    {
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();
        for (var i = 0; i < 3; i++) await new EfOutboxStore<OpenConnectDbContext>(db).EnqueueAsync(new(Guid.NewGuid(), "dispatch", $"payload-{i}"));
        var publisher = new CountingPublisher();
        var dispatcher = new EfOutboxDispatcher();
        var request = new OutboxClaimRequest(10, DateTime.UtcNow.AddMinutes(1), "smoke-worker", TimeSpan.FromMinutes(1));
        Assert.Equal(3, await dispatcher.ExecuteBatchAsync(db, OpenConnectOutboxClaimStrategy.OptimisticConcurrency, request, publisher, 3, CancellationToken.None));
        Assert.Equal(0, await dispatcher.ExecuteBatchAsync(db, OpenConnectOutboxClaimStrategy.OptimisticConcurrency, request, publisher, 3, CancellationToken.None));
        Assert.Equal(3, publisher.Count);
    }

    private sealed class CountingPublisher : ICommittedOutboxPublisher
    {
        public int Count { get; private set; }
        public Task PublishAsync(string subject, string eventId, string payload, CancellationToken ct = default) { Count++; return Task.CompletedTask; }
    }
}
