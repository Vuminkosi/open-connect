using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class PrivacyTokenVaultScopeTests
{
    private static readonly TenantId TenantA = new(Ulid.NewUlid());
    private static readonly TenantId TenantB = new(Ulid.NewUlid());
    private static readonly SessionId Session = new(Ulid.NewUlid());
    private static readonly DateTimeOffset Start = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static async Task<(Microsoft.Data.Sqlite.SqliteConnection Connection, RelationalPersistenceTests.ApplicationDbContext Db)> OpenAsync()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new RelationalPersistenceTests.ApplicationDbContext(new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>().UseSqlite(connection)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnect.Persistence.EFCore.Context.OpenConnectModelCacheKeyFactory>().Options, true);
        await db.Database.EnsureCreatedAsync();
        return (connection, db);
    }

    private static PrivacyTokenBinding Binding(string id, TenantId tenant, string principal, SessionId session, byte value, DateTimeOffset expires) =>
        new(id, tenant, principal, session, "email", [value], expires);

    [Fact]
    public async Task Same_token_id_for_two_principals_resolves_each_without_exception()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        await vault.StoreAsync(Binding("tok", TenantA, "bob", Session, 2, Start.AddHours(1)));
        Assert.Equal([1], (await vault.GetAsync("tok", TenantA, "alice", Session))!.EncryptedValue);
        Assert.Equal([2], (await vault.GetAsync("tok", TenantA, "bob", Session))!.EncryptedValue);
    }

    [Fact]
    public async Task Same_token_id_for_two_sessions_resolves_by_session()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        var other = new SessionId(Ulid.NewUlid());
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", other, 2, Start.AddHours(1)));
        Assert.Equal([1], (await vault.GetAsync("tok", TenantA, "alice", Session))!.EncryptedValue);
        Assert.Equal([2], (await vault.GetAsync("tok", TenantA, "alice", other))!.EncryptedValue);
    }

    [Fact]
    public async Task Expired_binding_is_hidden_before_purge_runs()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        var clock = new TestClock(Start);
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, clock);
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddMinutes(5)));
        Assert.NotNull(await vault.GetAsync("tok", TenantA, "alice", Session));
        clock.Now = clock.Now.AddMinutes(5);
        Assert.Null(await vault.GetAsync("tok", TenantA, "alice", Session));
        Assert.Equal(1, await db.Set<OpenConnect.Persistence.EFCore.Entities.PrivacyTokenEntity>().CountAsync());
    }

    [Fact]
    public async Task Lookup_with_other_principal_or_session_returns_null()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        Assert.Null(await vault.GetAsync("tok", TenantA, "bob", Session));
        Assert.Null(await vault.GetAsync("tok", TenantA, "alice", new SessionId(Ulid.NewUlid())));
        Assert.Null(await vault.GetAsync("tok", TenantA, "", Session));
    }

    [Fact]
    public async Task Lookup_from_other_tenant_returns_null()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        Assert.Null(await vault.GetAsync("tok", TenantB, "alice", Session));
    }

    [Fact]
    public async Task Duplicate_binding_with_null_turn_is_rejected()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        await Assert.ThrowsAsync<DbUpdateException>(() => vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 2, Start.AddHours(1))));
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
