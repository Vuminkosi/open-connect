using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.DependencyInjection;
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
        await using var _ = connection;
        await using var __ = db;
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
        await using var _ = connection;
        await using var __ = db;
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
        await using var _ = connection;
        await using var __ = db;
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
        await using var _ = connection;
        await using var __ = db;
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
        await using var _ = connection;
        await using var __ = db;
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        Assert.Null(await vault.GetAsync("tok", TenantB, "alice", Session));
    }

    [Fact]
    public async Task Restoring_same_binding_replaces_it_without_exception()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 2, Start.AddHours(2)));
        var got = await vault.GetAsync("tok", TenantA, "alice", Session);
        Assert.Equal([2], got!.EncryptedValue);
        Assert.Equal(Start.AddHours(2), got.ExpiresAt);
        Assert.Equal(1, await db.Set<OpenConnect.Persistence.EFCore.Entities.PrivacyTokenEntity>().CountAsync());
    }

    [Fact]
    public async Task Dependency_injection_uses_registered_time_provider_for_expiry()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        var clock = new TestClock(Start);
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<TimeProvider>(clock);
        services.AddOpenConnectEntityFramework<RelationalPersistenceTests.ApplicationDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var vault = scope.ServiceProvider.GetRequiredService<IPrivacyTokenVault>();
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddMinutes(5)));
        Assert.NotNull(await vault.GetAsync("tok", TenantA, "alice", Session));
        clock.Now = Start.AddMinutes(5);
        Assert.Null(await vault.GetAsync("tok", TenantA, "alice", Session));
    }

    [Fact]
    public async Task Restoring_binding_with_global_no_tracking_persists_replacement()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        db.ChangeTracker.Clear();
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 2, Start.AddHours(2)));
        db.ChangeTracker.Clear();
        var got = await vault.GetAsync("tok", TenantA, "alice", Session);
        Assert.Equal([2], got!.EncryptedValue);
        Assert.Equal(Start.AddHours(2), got.ExpiresAt);
        Assert.Equal(1, await db.Set<OpenConnect.Persistence.EFCore.Entities.PrivacyTokenEntity>().CountAsync());
    }

    [Theory]
    [InlineData("TOK", "alice")]
    [InlineData("tok ", "alice")]
    [InlineData("tok", "ALICE")]
    [InlineData("tok", "alice ")]
    public async Task Collation_equivalent_but_nonidentical_keys_never_read_or_overwrite_binding(string token, string principal)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        // Deliberately emulate case-insensitive, trailing-space-insensitive comparisons.
        // This exercises the fail-closed guard, not SQL Server's full collation semantics.
        connection.CreateCollation("SCOPE_TEST", (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.TrimEnd(' '), right.TrimEnd(' ')));
        await using var db = new CollationDbContext(new DbContextOptionsBuilder<CollationDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var vault = new EfPrivacyTokenVault<CollationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        db.ChangeTracker.Clear();
        // Prove the database comparison admits the wrong key before checking the adapter.
        Assert.Equal(1, await db.Set<OpenConnect.Persistence.EFCore.Entities.PrivacyTokenEntity>()
            .CountAsync(x => x.TokenId == token && x.PrincipalOrWorkloadScope == principal));
        Assert.Null(await vault.GetAsync(token, TenantA, principal, Session));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vault.StoreAsync(Binding(token, TenantA, principal, Session, 2, Start.AddHours(2))));
        Assert.Equal("Privacy token key conflicts with the database collation.", error.Message);
        // Even a subsequent save cannot persist an unauthorized mutation.
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var original = await vault.GetAsync("tok", TenantA, "alice", Session);
        Assert.Equal([1], original!.EncryptedValue);
        Assert.Equal(Start.AddHours(1), original.ExpiresAt);
        Assert.Equal(1, await db.Set<OpenConnect.Persistence.EFCore.Entities.PrivacyTokenEntity>().CountAsync());
    }

    private sealed class CollationDbContext(DbContextOptions<CollationDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            OpenConnect.Persistence.EFCore.Model.OpenConnectModelBuilderExtensions.ApplyOpenConnectModel(modelBuilder);
            var entity = modelBuilder.Entity<OpenConnect.Persistence.EFCore.Entities.PrivacyTokenEntity>();
            entity.Property(x => x.TenantScope).UseCollation("SCOPE_TEST");
            entity.Property(x => x.PrincipalOrWorkloadScope).UseCollation("SCOPE_TEST");
            entity.Property(x => x.TokenId).UseCollation("SCOPE_TEST");
        }
    }

    [Fact]
    public async Task Unique_index_rejects_duplicate_rows_with_null_turn()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        await InsertRawAsync(db, Guid.NewGuid(), 1);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => InsertRawAsync(db, Guid.NewGuid(), 2));
    }

    private static Task InsertRawAsync(RelationalPersistenceTests.ApplicationDbContext db, Guid id, byte value, string token = "tok", string created = "2030-01-01 00:00:00")
    {
        var session = UlidGuidMapper.ToGuid(Session.Value).ToString().ToUpperInvariant();
        return db.Database.ExecuteSqlAsync($"INSERT INTO oc_privacytokens (Id, TenantScope, PrincipalOrWorkloadScope, SessionId, TurnId, TokenId, EntityType, EncryptedValue, Nonce, AuthenticationTag, WrappedDataKey, KeyProviderId, KeyVersion, BindingDigest, CreatedUtc, ExpiresUtc) VALUES ({id.ToString().ToUpperInvariant()}, 't', 'alice', {session}, NULL, {token}, 'email', {new[] { value }}, {Array.Empty<byte>()}, {Array.Empty<byte>()}, {Array.Empty<byte>()}, 'envelope', 'v1', 'd', {created}, '2031-01-01 00:00:00')");
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
