using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Persistence.EFCore.Model;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>
/// Verifies the durable bridge stores (BRIDGE-DURABLE-A) resolve from production DI and
/// round-trip over sqlite through the production <c>ApplyOpenConnectModel</c> path.
/// No live database connection; no entity/config/migration changes.
/// </summary>
public sealed class DurableStoresWiringTests
{
    [Fact]
    public async Task Session_uuid_services_resolve_from_production_di_and_round_trip()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var provider = ServicesForExisting(db).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mapping = scope.ServiceProvider.GetRequiredService<ISessionUuidMappingStore>();
        var resolver = scope.ServiceProvider.GetRequiredService<IDurableSessionUuidResolver>();

        var publicId = Guid.NewGuid();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.Equal(SessionUuidMappingSaveOutcome.Saved, await mapping.TryAddAsync(publicId, session, tenant, principal));
        Assert.Equal(session, await mapping.ResolveAsync(publicId, tenant, principal));
        Assert.Equal(session, await resolver.ResolveAsync(publicId, tenant, principal));
    }

    [Fact]
    public async Task Session_uuid_mapping_is_idempotent_and_conflicts_fail_closed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var provider = ServicesForExisting(db).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mapping = scope.ServiceProvider.GetRequiredService<ISessionUuidMappingStore>();
        var resolver = scope.ServiceProvider.GetRequiredService<IDurableSessionUuidResolver>();

        var publicId = Guid.NewGuid();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());

        Assert.Equal(SessionUuidMappingSaveOutcome.Saved, await mapping.TryAddAsync(publicId, session, tenant, principal));
        Assert.Equal(SessionUuidMappingSaveOutcome.AlreadyMapped, await mapping.TryAddAsync(publicId, session, tenant, principal));
        Assert.Equal(
            SessionUuidMappingSaveOutcome.Conflict,
            await mapping.TryAddAsync(publicId, new SessionId(Ulid.NewUlid()), tenant, principal));
        Assert.Equal(session, await resolver.ResolveAsync(publicId, tenant, principal));

        // Fail closed: cross-tenant, cross-principal, and unknown UUIDs resolve as absent.
        Assert.Null(await resolver.ResolveAsync(publicId, new TenantId(Ulid.NewUlid()), principal));
        Assert.Null(await resolver.ResolveAsync(publicId, tenant, new PrincipalId(Ulid.NewUlid())));
        Assert.Null(await resolver.ResolveAsync(Guid.NewGuid(), tenant, principal));
    }

    [Fact]
    public async Task Session_uuid_mapping_rejects_invalid_keys_before_storage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        var provider = ServicesForExisting(db).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mapping = scope.ServiceProvider.GetRequiredService<ISessionUuidMappingStore>();

        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var session = new SessionId(Ulid.NewUlid());

        await Assert.ThrowsAsync<ArgumentException>(() => mapping.TryAddAsync(Guid.Empty, session, tenant, principal));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.TryAddAsync(Guid.NewGuid(), default, tenant, principal));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.TryAddAsync(Guid.NewGuid(), session, default, principal));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.TryAddAsync(Guid.NewGuid(), session, tenant, default));
        await Assert.ThrowsAsync<ArgumentException>(() => mapping.ResolveAsync(Guid.Empty, tenant, principal));
        Assert.Null(await mapping.ResolveAsync(Guid.NewGuid(), tenant, principal));
    }

    private static ServiceCollection ServicesForExisting(RelationalPersistenceTests.ApplicationDbContext db)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        services.AddOpenConnectEntityFramework<RelationalPersistenceTests.ApplicationDbContext>();
        return services;
    }

    private static RelationalPersistenceTests.ApplicationDbContext Context(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>()
            .UseSqlite(connection)
            .ReplaceService<IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>()
            .Options,
        prefix: true);
}
