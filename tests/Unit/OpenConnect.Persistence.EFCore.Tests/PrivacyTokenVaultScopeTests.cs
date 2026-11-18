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
    public async Task Restoring_same_binding_replaces_it_without_exception()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        var vault = new EfPrivacyTokenVault<RelationalPersistenceTests.ApplicationDbContext>(db, new TestClock(Start));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 1, Start.AddHours(1)));
        await vault.StoreAsync(Binding("tok", TenantA, "alice", Session, 2, Start.AddHours(2)));
        var got = await vault.GetAsync("tok", TenantA, "alice", Session);
        Assert.Equal([2], got!.EncryptedValue);
        Assert.Equal(Start.AddHours(2), got.ExpiresAt);
        Assert.Equal(1, await db.Set<OpenConnect.Persistence.EFCore.Entities.PrivacyTokenEntity>().CountAsync());
    }

    [Fact]
    public async Task Unique_index_rejects_duplicate_rows_with_null_turn()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        await InsertRawAsync(db, Guid.NewGuid(), 1);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => InsertRawAsync(db, Guid.NewGuid(), 2));
    }

    [Fact]
    public async Task Dedupe_sql_keeps_newest_row_per_key_before_unique_index()
    {
        var (connection, db) = await OpenAsync();
        await using var _ = connection; await using var __ = db;
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_oc_privacytokens_TenantScope_PrincipalOrWorkloadScope_TokenId_SessionId\"");
        var older = Guid.NewGuid(); var newest = Guid.NewGuid(); var other = Guid.NewGuid();
        await InsertRawAsync(db, older, 1, created: "2030-01-01 00:00:00");
        await InsertRawAsync(db, newest, 2, created: "2030-01-02 00:00:00");
        await InsertRawAsync(db, other, 3, token: "other", created: "2030-01-01 00:00:00");
        await db.Database.ExecuteSqlRawAsync(SqliteDedupeSql);
        var ids = await db.Set<OpenConnect.Persistence.EFCore.Entities.PrivacyTokenEntity>().Select(x => x.Id).ToListAsync();
        Assert.Equal(2, ids.Count);
        Assert.Contains(newest, ids);
        Assert.Contains(other, ids);
    }

    [Fact]
    public void Ms_and_Pg_migrations_dedupe_before_creating_unique_index()
    {
        foreach (var ops in new[] { new MigrationsMs.PrivacyTokenScopeUniqueness().UpOperations, new MigrationsPg.PrivacyTokenScopeUniqueness().UpOperations })
        {
            var list = ops.ToList();
            var sql = list.FindIndex(o => o is Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation s && s.Sql.Contains("ROW_NUMBER()", StringComparison.Ordinal));
            var create = list.FindIndex(o => o is Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation { IsUnique: true });
            Assert.True(sql >= 0 && create > sql);
        }
    }

    private const string SqliteDedupeSql = "DELETE FROM \"oc_privacytokens\" WHERE \"Id\" IN (SELECT \"Id\" FROM (SELECT \"Id\", ROW_NUMBER() OVER (PARTITION BY \"TenantScope\", \"PrincipalOrWorkloadScope\", \"TokenId\", \"SessionId\" ORDER BY \"CreatedUtc\" DESC, \"Id\" ASC) AS rn FROM \"oc_privacytokens\") AS d WHERE d.rn > 1)";

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
