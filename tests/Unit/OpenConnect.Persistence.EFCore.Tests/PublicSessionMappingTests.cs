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

    [Fact]
    public async Task Overlapping_insert_race_identical_binding_loser_resolves_already_mapped()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = Context(connection);
        await setup.Database.EnsureCreatedAsync();

        var publicId = Guid.NewGuid();
        var session = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var tenantText = tenant.Value.ToString();
        var internalText = session.Value.ToString();
        var principalText = principal.Value.ToString();

        // True overlap: two independent contexts share one connection. Both
        // pre-reads are issued before either is awaited, both observe absent,
        // and both stage an insert before either saves.
        await using var first = Context(connection);
        await using var second = Context(connection);
        var probeFirst = first.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantText && x.PublicId == publicId);
        var probeSecond = second.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantText && x.PublicId == publicId);
        Assert.Null(await probeFirst);
        Assert.Null(await probeSecond);

        first.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().Add(
            new OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity
            {
                PublicId = publicId,
                InternalSessionId = internalText,
                TenantId = tenantText,
                PrincipalId = principalText,
                CreatedAtUtc = DateTime.UtcNow,
                Version = 1,
            });
        var loserEntity = new OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity
        {
            PublicId = publicId,
            InternalSessionId = internalText,
            TenantId = tenantText,
            PrincipalId = principalText,
            CreatedAtUtc = DateTime.UtcNow,
            Version = 1,
        };
        second.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().Add(loserEntity);

        Assert.Equal(1, await first.SaveChangesAsync());

        // Loser hits the (TenantId, PublicId) primary-key backstop: an authentic
        // SQLite unique violation (extended code 1555/2067).
        var race = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.True(IsSqliteUniqueViolation(race), "Expected a SQLite unique-constraint violation (1555/2067).");
        second.Entry(loserEntity).State = EntityState.Detached;

        // Winner classification mirrors the store catch-re-read comparison.
        await using var reader = Context(connection);
        var readerStore = new EfPublicSessionMappingStore<MappingContext>(reader);
        Assert.Equal(
            PublicSessionMappingSaveOutcome.AlreadyMapped,
            await readerStore.TryAddAsync(publicId, session, tenant, principal));

        // Drive the store's actual catch-re-read path: pre-check observes absent
        // on a fresh key, SaveChangesAsync throws the authentic violation after
        // the winner lands, and the re-read classifies identically.
        var faultPublicId = Guid.NewGuid();
        var faultSession = new SessionId(Ulid.NewUlid());
        var faultInternalText = faultSession.Value.ToString();
        await using var faulted = FaultContext(connection);
        faulted.Fault = race;
        faulted.BeforeSaveAsync = async () =>
        {
            await using var seeder = Context(connection);
            seeder.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().Add(
                new OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity
                {
                    PublicId = faultPublicId,
                    InternalSessionId = faultInternalText,
                    TenantId = tenantText,
                    PrincipalId = principalText,
                    CreatedAtUtc = DateTime.UtcNow,
                    Version = 1,
                });
            await seeder.SaveChangesAsync();
        };
        var faultedStore = new EfPublicSessionMappingStore<RaceFaultContext>(faulted);
        Assert.Equal(
            PublicSessionMappingSaveOutcome.AlreadyMapped,
            await faultedStore.TryAddAsync(faultPublicId, faultSession, tenant, principal));
    }

    [Fact]
    public async Task Overlapping_insert_race_divergent_binding_loser_conflicts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = Context(connection);
        await setup.Database.EnsureCreatedAsync();

        var publicId = Guid.NewGuid();
        var winnerSession = new SessionId(Ulid.NewUlid());
        var loserSession = new SessionId(Ulid.NewUlid());
        var tenant = new TenantId(Ulid.NewUlid());
        var principal = new PrincipalId(Ulid.NewUlid());
        var tenantText = tenant.Value.ToString();
        var principalText = principal.Value.ToString();

        // True overlap with divergent bindings: same key, different session.
        await using var first = Context(connection);
        await using var second = Context(connection);
        var probeFirst = first.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantText && x.PublicId == publicId);
        var probeSecond = second.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantText && x.PublicId == publicId);
        Assert.Null(await probeFirst);
        Assert.Null(await probeSecond);

        first.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().Add(
            new OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity
            {
                PublicId = publicId,
                InternalSessionId = winnerSession.Value.ToString(),
                TenantId = tenantText,
                PrincipalId = principalText,
                CreatedAtUtc = DateTime.UtcNow,
                Version = 1,
            });
        var loserEntity = new OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity
        {
            PublicId = publicId,
            InternalSessionId = loserSession.Value.ToString(),
            TenantId = tenantText,
            PrincipalId = principalText,
            CreatedAtUtc = DateTime.UtcNow,
            Version = 1,
        };
        second.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().Add(loserEntity);

        Assert.Equal(1, await first.SaveChangesAsync());

        var race = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.True(IsSqliteUniqueViolation(race), "Expected a SQLite unique-constraint violation (1555/2067).");
        second.Entry(loserEntity).State = EntityState.Detached;

        await using var reader = Context(connection);
        var readerStore = new EfPublicSessionMappingStore<MappingContext>(reader);
        Assert.Equal(
            PublicSessionMappingSaveOutcome.Conflict,
            await readerStore.TryAddAsync(publicId, loserSession, tenant, principal));

        // Drive the store's actual catch-re-read path with a divergent winner.
        var faultPublicId = Guid.NewGuid();
        var faultWinnerSession = new SessionId(Ulid.NewUlid());
        var faultWinnerText = faultWinnerSession.Value.ToString();
        await using var faulted = FaultContext(connection);
        faulted.Fault = race;
        faulted.BeforeSaveAsync = async () =>
        {
            await using var seeder = Context(connection);
            seeder.Set<OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity>().Add(
                new OpenConnect.Persistence.EFCore.Entities.PublicSessionMappingEntity
                {
                    PublicId = faultPublicId,
                    InternalSessionId = faultWinnerText,
                    TenantId = tenantText,
                    PrincipalId = principalText,
                    CreatedAtUtc = DateTime.UtcNow,
                    Version = 1,
                });
            await seeder.SaveChangesAsync();
        };
        var faultedStore = new EfPublicSessionMappingStore<RaceFaultContext>(faulted);
        Assert.Equal(
            PublicSessionMappingSaveOutcome.Conflict,
            await faultedStore.TryAddAsync(faultPublicId, loserSession, tenant, principal));
    }

    private static bool IsSqliteUniqueViolation(DbUpdateException exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite &&
                (sqlite.SqliteExtendedErrorCode == 1555 || sqlite.SqliteExtendedErrorCode == 2067))
                return true;
        }

        return false;
    }

    private static RaceFaultContext FaultContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<RaceFaultContext>().UseSqlite(connection).Options);

    /// <summary>
    /// Fault-injecting context: throws the supplied authentic unique-violation
    /// on the first <c>SaveChangesAsync</c> (after running the seeder), then behaves
    /// normally. Forces the store through its catch-re-read path deterministically.
    /// </summary>
    private sealed class RaceFaultContext(DbContextOptions<RaceFaultContext> options) : DbContext(options)
    {
        public DbUpdateException? Fault { get; set; }

        public Func<Task>? BeforeSaveAsync { get; set; }

        private bool _armed = true;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfiguration(
                new PublicSessionMappingEntityConfiguration(OpenConnectModelOptions.Default));
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_armed)
            {
                _armed = false;
                if (BeforeSaveAsync is not null)
                    await BeforeSaveAsync().ConfigureAwait(false);
                throw Fault ?? new DbUpdateException("No fault configured.");
            }

            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
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
