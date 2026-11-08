using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.EntityConfigurations;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.Persistence.EFCore.Stores;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>
/// Verifies the durable public-UUID to runtime-ULID mapping (contract SESSION-UUID-MAPPING).
/// Uses sqlite in-memory with the real relational model; no live database connection.
/// </summary>
public sealed class PublicSessionMappingTests
{
    [Fact]
    public async Task Mapping_round_trips_public_uuid_to_internal_ulid()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfPublicSessionMappingStore<MappingContext>(db);
        var publicId = Guid.NewGuid();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.Equal(PublicSessionMappingSaveOutcome.Saved, await store.TryAddAsync(publicId, session, tenant, principal));
        Assert.Equal(session, await store.GetInternalSessionIdAsync(publicId, tenant, principal));
    }

    [Fact]
    public async Task Identical_mapping_is_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfPublicSessionMappingStore<MappingContext>(db);
        var publicId = Guid.NewGuid();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.Equal(PublicSessionMappingSaveOutcome.Saved, await store.TryAddAsync(publicId, session, tenant, principal));
        Assert.Equal(PublicSessionMappingSaveOutcome.AlreadyMapped, await store.TryAddAsync(publicId, session, tenant, principal));
        Assert.Equal(session, await store.GetInternalSessionIdAsync(publicId, tenant, principal));
    }

    [Fact]
    public async Task Different_internal_session_for_same_public_uuid_conflicts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfPublicSessionMappingStore<MappingContext>(db);
        var publicId = Guid.NewGuid();
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.Equal(
            PublicSessionMappingSaveOutcome.Saved,
            await store.TryAddAsync(publicId, new SessionId(Ulid.NewUlid()), tenant, principal));
        Assert.Equal(
            PublicSessionMappingSaveOutcome.Conflict,
            await store.TryAddAsync(publicId, new SessionId(Ulid.NewUlid()), tenant, principal));
    }

    [Fact]
    public async Task Same_internal_session_for_second_public_uuid_conflicts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfPublicSessionMappingStore<MappingContext>(db);
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.Equal(PublicSessionMappingSaveOutcome.Saved, await store.TryAddAsync(Guid.NewGuid(), session, tenant, principal));
        Assert.Equal(
            PublicSessionMappingSaveOutcome.Conflict,
            await store.TryAddAsync(Guid.NewGuid(), session, tenant, principal));
    }

    [Fact]
    public async Task Cross_tenant_and_cross_principal_reads_resolve_as_absent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfPublicSessionMappingStore<MappingContext>(db);
        var publicId = Guid.NewGuid();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.Equal(PublicSessionMappingSaveOutcome.Saved, await store.TryAddAsync(publicId, session, tenant, principal));
        Assert.Null(await store.GetInternalSessionIdAsync(publicId, new TenantId(Ulid.NewUlid()), principal));
        Assert.Null(await store.GetInternalSessionIdAsync(publicId, tenant, new PrincipalId(Ulid.NewUlid())));
        Assert.Null(await store.GetInternalSessionIdAsync(Guid.NewGuid(), tenant, principal));
    }

    [Fact]
    public async Task Same_public_uuid_in_different_tenant_is_independent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfPublicSessionMappingStore<MappingContext>(db);
        var publicId = Guid.NewGuid();
        var firstTenant = new TenantId(Ulid.NewUlid());
        var secondTenant = new TenantId(Ulid.NewUlid());
        var firstPrincipal = new PrincipalId(Ulid.NewUlid());
        var secondPrincipal = new PrincipalId(Ulid.NewUlid());
        var firstSession = new SessionId(Ulid.NewUlid());
        var secondSession = new SessionId(Ulid.NewUlid());

        Assert.Equal(PublicSessionMappingSaveOutcome.Saved, await store.TryAddAsync(publicId, firstSession, firstTenant, firstPrincipal));
        Assert.Equal(PublicSessionMappingSaveOutcome.Saved, await store.TryAddAsync(publicId, secondSession, secondTenant, secondPrincipal));
        Assert.Equal(firstSession, await store.GetInternalSessionIdAsync(publicId, firstTenant, firstPrincipal));
        Assert.Equal(secondSession, await store.GetInternalSessionIdAsync(publicId, secondTenant, secondPrincipal));
    }

    [Fact]
    public async Task Invalid_arguments_are_rejected_before_storage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var store = new EfPublicSessionMappingStore<MappingContext>(db);
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAddAsync(Guid.Empty, session, tenant, principal));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAddAsync(Guid.NewGuid(), default, tenant, principal));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetInternalSessionIdAsync(Guid.Empty, tenant, principal));
        Assert.Null(await store.GetInternalSessionIdAsync(Guid.NewGuid(), tenant, principal));
    }

    private static MappingContext Context(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<MappingContext>().UseSqlite(connection).Options);

    private sealed class MappingContext(DbContextOptions<MappingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfiguration(
                new PublicSessionMappingEntityConfiguration(OpenConnectModelOptions.Default));
        }
    }
}
