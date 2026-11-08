using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.EntityConfigurations;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>
/// Verifies the durable single-use approval nonce store (WF-04 durable).
/// Uses sqlite in-memory with the real relational mapping; no live database connection.
/// </summary>
public sealed class ApprovalNonceStoreTests
{
    [Fact]
    public async Task Nonce_round_trips_for_audit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var approvalId = Guid.NewGuid();
        const string nonce = "wf04-nonce-001";

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await store.TryConsumeAsync(tenant, principal, nonce, approvalId));

        var receipt = await store.GetAsync(tenant, principal, nonce);
        Assert.NotNull(receipt);
        Assert.Equal(approvalId, receipt.ApprovalId);
    }

    [Fact]
    public async Task Identical_consume_is_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var approvalId = Guid.NewGuid();
        const string nonce = "wf04-nonce-002";

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await store.TryConsumeAsync(tenant, principal, nonce, approvalId));
        Assert.Equal(
            ApprovalNonceConsumeOutcome.AlreadyConsumed,
            await store.TryConsumeAsync(tenant, principal, nonce, approvalId));

        var receipt = await store.GetAsync(tenant, principal, nonce);
        Assert.NotNull(receipt);
        Assert.Equal(approvalId, receipt.ApprovalId);
    }

    [Fact]
    public async Task Same_nonce_for_different_approval_conflicts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        const string nonce = "wf04-nonce-003";

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await store.TryConsumeAsync(tenant, principal, nonce, Guid.NewGuid()));
        Assert.Equal(
            ApprovalNonceConsumeOutcome.Conflict,
            await store.TryConsumeAsync(tenant, principal, nonce, Guid.NewGuid()));
    }

    [Fact]
    public async Task Same_nonce_for_different_principal_conflicts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        var tenant = new TenantId(Ulid.NewUlid());
        const string nonce = "wf04-nonce-004";

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await store.TryConsumeAsync(tenant, new PrincipalId(Ulid.NewUlid()), nonce, Guid.NewGuid()));
        Assert.Equal(
            ApprovalNonceConsumeOutcome.Conflict,
            await store.TryConsumeAsync(tenant, new PrincipalId(Ulid.NewUlid()), nonce, Guid.NewGuid()));
    }

    [Fact]
    public async Task Same_nonce_in_different_tenant_is_independent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        const string nonce = "wf04-nonce-005";
        var firstTenant = new TenantId(Ulid.NewUlid());
        var secondTenant = new TenantId(Ulid.NewUlid());
        var firstPrincipal = new PrincipalId(Ulid.NewUlid());
        var secondPrincipal = new PrincipalId(Ulid.NewUlid());
        var firstApproval = Guid.NewGuid();
        var secondApproval = Guid.NewGuid();

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await store.TryConsumeAsync(firstTenant, firstPrincipal, nonce, firstApproval));
        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await store.TryConsumeAsync(secondTenant, secondPrincipal, nonce, secondApproval));

        Assert.Equal(firstApproval, (await store.GetAsync(firstTenant, firstPrincipal, nonce))?.ApprovalId);
        Assert.Equal(secondApproval, (await store.GetAsync(secondTenant, secondPrincipal, nonce))?.ApprovalId);
    }

    [Fact]
    public async Task Cross_tenant_and_cross_principal_reads_resolve_as_absent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        const string nonce = "wf04-nonce-006";

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await store.TryConsumeAsync(tenant, principal, nonce, Guid.NewGuid()));

        Assert.Null(await store.GetAsync(new TenantId(Ulid.NewUlid()), principal, nonce));
        Assert.Null(await store.GetAsync(tenant, new PrincipalId(Ulid.NewUlid()), nonce));
        Assert.Null(await store.GetAsync(tenant, principal, "wf04-unknown-nonce"));
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_storage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsumeAsync(tenant, principal, "   ", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsumeAsync(tenant, principal, new string('n', 65), Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsumeAsync(tenant, principal, "wf04-nonce-007", Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsumeAsync(default, principal, "wf04-nonce-007", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryConsumeAsync(tenant, default, "wf04-nonce-007", Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(tenant, principal, string.Empty));

        Assert.Null(await store.GetAsync(tenant, principal, "wf04-absent-nonce"));
    }

    [Fact]
    public async Task Corrupt_row_resolves_as_absent_and_conflicts_on_reuse()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        const string nonce = "wf04-nonce-008";

        // Bypass store validation to simulate a corrupt legacy row (empty approval id).
        db.Set<ApprovalNonceEntity>().Add(new ApprovalNonceEntity
        {
            TenantId = tenant.Value.ToString(),
            Nonce = nonce,
            ApprovalId = Guid.Empty,
            PrincipalId = principal.Value.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        Assert.Null(await store.GetAsync(tenant, principal, nonce));
        Assert.Equal(
            ApprovalNonceConsumeOutcome.Conflict,
            await store.TryConsumeAsync(tenant, principal, nonce, Guid.NewGuid()));
    }

    [Fact]
    public async Task Second_context_with_identical_binding_resolves_as_already_consumed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = Context(connection);
        await setup.Database.EnsureCreatedAsync();

        // Two independent contexts over the shared connection: the second store
        // must resolve against real database state (unique key backstop), not a
        // same-context pre-check.
        await using var first = Context(connection);
        await using var second = Context(connection);
        var firstStore = new EfApprovalNonceStore<NonceContext>(first);
        var secondStore = new EfApprovalNonceStore<NonceContext>(second);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var approvalId = Guid.NewGuid();
        const string nonce = "wf04-nonce-race-001";

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await firstStore.TryConsumeAsync(tenant, principal, nonce, approvalId));
        Assert.Equal(
            ApprovalNonceConsumeOutcome.AlreadyConsumed,
            await secondStore.TryConsumeAsync(tenant, principal, nonce, approvalId));
    }

    [Fact]
    public async Task Second_context_with_differing_binding_conflicts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = Context(connection);
        await setup.Database.EnsureCreatedAsync();

        await using var first = Context(connection);
        await using var second = Context(connection);
        var firstStore = new EfApprovalNonceStore<NonceContext>(first);
        var secondStore = new EfApprovalNonceStore<NonceContext>(second);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        const string approvalNonce = "wf04-nonce-race-002";
        const string principalNonce = "wf04-nonce-race-003";

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await firstStore.TryConsumeAsync(tenant, principal, approvalNonce, Guid.NewGuid()));
        Assert.Equal(
            ApprovalNonceConsumeOutcome.Conflict,
            await secondStore.TryConsumeAsync(tenant, principal, approvalNonce, Guid.NewGuid()));

        Assert.Equal(
            ApprovalNonceConsumeOutcome.Consumed,
            await firstStore.TryConsumeAsync(tenant, principal, principalNonce, Guid.NewGuid()));
        Assert.Equal(
            ApprovalNonceConsumeOutcome.Conflict,
            await secondStore.TryConsumeAsync(tenant, new PrincipalId(Ulid.NewUlid()), principalNonce, Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAsync_rejects_invalid_keys_like_consume()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfApprovalNonceStore<NonceContext>(db);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        // Parity with TryConsumeAsync: both share ValidateKey, which throws
        // ArgumentException for malformed keys (never returns null for them).
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(tenant, principal, "   "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(tenant, principal, new string('n', 65)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(default, principal, "wf04-nonce-race-004"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(tenant, default, "wf04-nonce-race-004"));
    }

    private static NonceContext Context(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<NonceContext>().UseSqlite(connection).Options);

    private sealed class NonceContext(DbContextOptions<NonceContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfiguration(
                new ApprovalNonceEntityConfiguration(OpenConnectModelOptions.Default));
        }
    }
}
