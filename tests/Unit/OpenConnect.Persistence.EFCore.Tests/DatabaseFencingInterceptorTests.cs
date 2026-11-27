using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Fencing;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

public sealed class DatabaseFencingInterceptorTests
{
    private static readonly TenantId TenantA = new(Ulid.NewUlid());
    private static readonly TenantId TenantB = new(Ulid.NewUlid());
    private static readonly PrincipalId Principal1 = new(Ulid.NewUlid());

    [Fact]
    public void RejectsRegisteringControlPlaneOpenConnectDbContext()
    {
        var options = new OpenConnectDatabaseFencingOptions();
        Assert.Throws<ArgumentException>(() => options.RegisterFencedContext<OpenConnectDbContext>());

        var accessor = new AsyncLocalDatabaseFencingContextAccessor();
        var interceptor = new OpenConnectFencedDbCommandInterceptor(options, accessor);
        var builder = new DbContextOptionsBuilder<OpenConnectDbContext>();
        Assert.Throws<InvalidOperationException>(() => builder.UseOpenConnectDatabaseFencing(interceptor));
    }

    [Fact]
    public async Task ControlPlaneOpenConnectDbContextIsNeverBlockedEvenIfInterceptorAttachedDirectly()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var fencingOptions = new OpenConnectDatabaseFencingOptions();
        fencingOptions.RegisterFencedContext<SampleAppDbContext>();
        var accessor = new AsyncLocalDatabaseFencingContextAccessor();
        var sink = new InMemoryDatabaseFencingEvidenceSink();
        var interceptor = new OpenConnectFencedDbCommandInterceptor(fencingOptions, accessor, sink);

        var dbOptions = new DbContextOptionsBuilder<OpenConnectDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var controlPlane = new OpenConnectDbContext(dbOptions);
        await controlPlane.Database.EnsureCreatedAsync();

        // No operation context is set, yet control-plane persistence works uninterrupted.
        var count = await controlPlane.Database.ExecuteSqlRawAsync("SELECT 1;");
        Assert.Equal(-1, count);
        Assert.Empty(sink.Snapshot());
    }

    [Fact]
    public async Task FencedApplicationContextEnforcesContextTenantReadOnlyAndContentFreeEvidence()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var fencingOptions = new OpenConnectDatabaseFencingOptions { DefaultMaxCommandTimeoutSeconds = 10 };
        fencingOptions.RegisterFencedContext<SampleAppDbContext>();
        var readOp = new FencedDatabaseOperationPolicy
        {
            OperationId = "orders.list_summary",
            ReadOnly = true,
            MaxCommandTimeoutSeconds = 5,
            MaxRows = 100
        };
        readOp.AllowedTenants.Add(TenantA);
        fencingOptions.RegisterOperation(readOp);

        var accessor = new AsyncLocalDatabaseFencingContextAccessor();
        var sink = new InMemoryDatabaseFencingEvidenceSink();
        var canonicalSink = new RecordingSecurityEvidenceSink();
        var interceptor = new OpenConnectFencedDbCommandInterceptor(fencingOptions, accessor, sink, canonicalSink);

        // Create schema without interceptor (like migrations/setup)
        var setupOptions = new DbContextOptionsBuilder<SampleAppDbContext>().UseSqlite(connection).Options;
        await using (var setup = new SampleAppDbContext(setupOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Items.Add(new SampleItem { Id = 1, SecretLabel = "TOP-SECRET-VALUE" });
            await setup.SaveChangesAsync();
        }

        var fencedBuilder = new DbContextOptionsBuilder<SampleAppDbContext>().UseSqlite(connection);
        fencedBuilder.UseOpenConnectDatabaseFencing(interceptor);
        await using var appDb = new SampleAppDbContext(fencedBuilder.Options);

        // 1. Missing operation context -> denied before SQL executes
        var missingEx = await Assert.ThrowsAsync<DatabaseFencingDeniedException>(() =>
            appDb.Items.ToListAsync());
        Assert.Equal(DatabaseFencingReasonCodes.MissingOperationContext, missingEx.ReasonCode);

        // 2. Wrong tenant -> denied
        using (accessor.BeginScope(new DatabaseFencingOperationContext(TenantB, Principal1, "orders.list_summary")))
        {
            var tenantEx = await Assert.ThrowsAsync<DatabaseFencingDeniedException>(() =>
                appDb.Items.ToListAsync());
            Assert.Equal(DatabaseFencingReasonCodes.TenantMismatch, tenantEx.ReasonCode);
        }

        // 3. Unregistered operation -> denied
        using (accessor.BeginScope(new DatabaseFencingOperationContext(TenantA, Principal1, "orders.drop_table")))
        {
            var opEx = await Assert.ThrowsAsync<DatabaseFencingDeniedException>(() =>
                appDb.Items.ToListAsync());
            Assert.Equal(DatabaseFencingReasonCodes.OperationNotRegistered, opEx.ReasonCode);
        }

        // 4. Write attempted under read-only operation -> denied before SQL executes
        using (accessor.BeginScope(new DatabaseFencingOperationContext(TenantA, Principal1, "orders.list_summary")))
        {
            var writeEx = await Assert.ThrowsAsync<DatabaseFencingDeniedException>(() =>
                appDb.Database.ExecuteSqlRawAsync("DELETE FROM Items WHERE SecretLabel = {0}", "TOP-SECRET-VALUE"));
            Assert.Equal(DatabaseFencingReasonCodes.WriteNotAllowed, writeEx.ReasonCode);
        }

        // 5. Authorized scoped read -> succeeds and records content-free evidence (no SQL or parameter values)
        using (accessor.BeginScope(new DatabaseFencingOperationContext(
            TenantA, Principal1, "orders.list_summary", ApplicationId: "demo-app", RequestId: "db-req-1")))
        {
            var rows = await appDb.Items.Where(x => x.SecretLabel == "TOP-SECRET-VALUE").ToListAsync();
            Assert.Single(rows);
        }

        // Verify item was never deleted
        await using (var verify = new SampleAppDbContext(setupOptions))
        {
            Assert.Equal(1, await verify.Items.CountAsync());
        }

        var events = sink.Snapshot();
        Assert.Equal(5, events.Count);
        Assert.Equal("allow", events[^1].Decision);
        Assert.Equal("executed", events[^1].EnforcementOutcome);
        Assert.Equal(5, events[^1].AppliedTimeoutSeconds);
        Assert.Equal(100, events[^1].MaxRows);
        Assert.All(events, e =>
        {
            Assert.DoesNotContain("TOP-SECRET-VALUE", e.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SELECT", e.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", e.ToString(), StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(5, canonicalSink.Events.Count);
    }

    private sealed class SampleItem
    {
        public int Id { get; set; }
        public string SecretLabel { get; set; } = string.Empty;
    }

    private sealed class SampleAppDbContext(DbContextOptions<SampleAppDbContext> options) : DbContext(options)
    {
        public DbSet<SampleItem> Items => Set<SampleItem>();
    }

    private sealed class RecordingSecurityEvidenceSink : ISecurityEvidenceSink
    {
        public List<SecurityEvidenceEvent> Events { get; } = new();
        public ValueTask<SecurityEvidenceRecordResult> RecordAsync(SecurityEvidenceEvent evidence, CancellationToken cancellationToken = default)
        {
            Events.Add(evidence);
            return ValueTask.FromResult(new SecurityEvidenceRecordResult(true, false));
        }
    }
}
