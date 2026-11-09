using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Persistence.EFCore.Health;
using OpenConnect.Persistence.EFCore.Providers;
using Xunit;

namespace OpenConnect.Persistence.EFCore.Tests;

/// <summary>
/// Registration invariants for OC-PERSIST-001..003 (contract OC-PERSIST-001):
/// no provider reference in the adapter graph, no connection/DDL/migration on
/// registration, Manual default, ApplyOnStartup rejected on shared contexts,
/// stable ef_* fail-closed errors, and explicit InMemory-durable rejection.
/// Pure unit scope: no database connection is opened by any test here.
/// </summary>
public sealed class EFCoreRegistrationInvariantsTests
{
    // OC-PERSIST-001: adapter carries no provider reference.
    [Fact]
    public void Adapter_assembly_references_only_base_and_relational_EF()
    {
        var referenced = typeof(OpenConnectEfOptions).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.Contains(referenced, n => n == "Microsoft.EntityFrameworkCore");
        Assert.Contains(referenced, n => n == "Microsoft.EntityFrameworkCore.Relational");

        string[] providerMarkers =
        [
            "Sqlite", "SqlServer", "Npgsql", "Pomelo", "MySql", "Oracle",
            "InMemory", "Cosmos", "Design",
        ];
        var leak = referenced
            .Where(n => providerMarkers.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.True(leak.Length == 0, $"Adapter references provider assemblies: {string.Join(", ", leak)}");
    }

    // OC-PERSIST-002: Manual is the default; nothing migrates unless explicitly configured.
    [Fact]
    public void Registration_defaults_to_Manual_migrations()
    {
        Assert.Equal(OpenConnectMigrationMode.Manual, new OpenConnectEfOptions().Migrations);
    }

    // OC-PERSIST-002: registration performs no connection, DDL, or migration.
    // The context points at an unreachable server; mere registration plus store
    // resolution must succeed without touching the network.
    [Fact]
    public void Registration_resolves_stores_without_connecting_or_migrating()
    {
        var options = new DbContextOptionsBuilder<RelationalPersistenceTests.ApplicationDbContext>()
            .UseNpgsql("Host=invalid.invalid;Port=5432;Database=oc_probe;Username=u;Password=p")
            .Options;
        using var db = new RelationalPersistenceTests.ApplicationDbContext(options);

        var services = new ServiceCollection();
        services.AddScoped(_ => db);
        services.AddOpenConnectEntityFramework<RelationalPersistenceTests.ApplicationDbContext>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // The app's provider choice is respected; the adapter supplies no provider default.
        Assert.Contains("Npgsql", db.Database.ProviderName ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ITaskStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEventJournal>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOutboxStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISessionStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ILeaseStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IApprovalStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPrivacyTokenVault>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INativeAgentSessionStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISessionUuidMappingStore>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IDurableSessionUuidResolver>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IOpenConnectPersistenceUnitOfWork>());
    }

    // OC-PERSIST-002: ApplyOnStartup is rejected on a shared (existing) context.
    [Fact]
    public void ApplyOnStartup_rejected_for_shared_existing_context()
    {
        var validator = new OpenConnectEfOptionsValidator();
        var result = validator.Validate(null, new OpenConnectEfOptions
        {
            IntegrationMode = OpenConnectEfIntegrationMode.ExistingDbContext,
            Migrations = OpenConnectMigrationMode.ApplyOnStartup,
        });

        Assert.NotNull(result);
        Assert.True(result.Failed, "ExistingDbContext + ApplyOnStartup must fail validation.");
        Assert.Contains(ErrorCodes.EfMigrationNotAllowed, result.FailureMessage ?? string.Empty);
        Assert.Contains("ef_migration_not_allowed", result.FailureMessage ?? string.Empty);
    }

    // Contract rule 3 narrow exception: dedicated context may coordinate its own pipeline.
    [Fact]
    public void ApplyOnStartup_allowed_for_dedicated_context()
    {
        var validator = new OpenConnectEfOptionsValidator();
        var result = validator.Validate(null, new OpenConnectEfOptions
        {
            IntegrationMode = OpenConnectEfIntegrationMode.DedicatedDbContext,
            Migrations = OpenConnectMigrationMode.ApplyOnStartup,
        });

        Assert.NotNull(result);
        Assert.True(result.Succeeded, $"DedicatedDbContext + ApplyOnStartup must validate: {result.FailureMessage}");
    }

    // OC-PERSIST-003: missing model fails closed with a stable error before queries.
    [Fact]
    public void Missing_model_fails_closed_with_stable_error_before_queries()
    {
        var options = new DbContextOptionsBuilder<BareProbeContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new BareProbeContext(options);

        var ex = Assert.Throws<InvalidOperationException>(() => EfRuntimeModelValidator.Validate(db));
        Assert.Contains(ErrorCodes.EfModelMissing, ex.Message);
        Assert.Contains("ef_model_missing", ex.Message);
    }

    // OC-PERSIST-003: startup validator fails closed when the EF model is
    // missing expected OpenConnect entities (BareProbeContext maps none).
    [Fact]
    public async Task Startup_validator_rejects_context_with_missing_model()
    {
        var options = new DbContextOptionsBuilder<BareProbeContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new BareProbeContext(options));
        services.Configure<OpenConnectEfOptions>(_ => { });
        using var provider = services.BuildServiceProvider();

        var validator = new EfStartupValidator<BareProbeContext>(provider);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));
        Assert.Contains(ErrorCodes.EfModelMissing, ex.Message);
        Assert.Contains("ef_model_missing", ex.Message);
    }

    // OC-PERSIST-003: durable-mode InMemory use is rejected explicitly at startup,
    // with no connection strings or payloads in the error.
    [Fact]
    public async Task InMemory_durable_use_rejected_explicitly_at_startup()
    {
        var options = new DbContextOptionsBuilder<InMemoryProbeContext>()
            .UseInMemoryDatabase($"probe-{Guid.NewGuid():N}")
            .Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new InMemoryProbeContext(options));
        services.Configure<OpenConnectEfOptions>(_ => { });
        using var provider = services.BuildServiceProvider();

        var validator = new EfStartupValidator<InMemoryProbeContext>(provider);
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => validator.StartAsync(CancellationToken.None));
        Assert.Contains("ef_inmemory_not_durable", ex.Message);
        Assert.DoesNotContain("Password", ex.Message);
        Assert.DoesNotContain("Host=", ex.Message);
    }

    // Development-only consistency still permits InMemory for local/test loops.
    [Fact]
    public async Task InMemory_allowed_for_development_only_consistency()
    {
        var options = new DbContextOptionsBuilder<InMemoryProbeContext>()
            .UseInMemoryDatabase($"probe-{Guid.NewGuid():N}")
            .Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new InMemoryProbeContext(options));
        services.Configure<OpenConnectEfOptions>(o =>
        {
            o.Consistency = OpenConnectPersistenceConsistency.DevelopmentOnly;
            o.ValidateSchema = OpenConnectSchemaValidationMode.None;
        });
        using var provider = services.BuildServiceProvider();

        var validator = new EfStartupValidator<InMemoryProbeContext>(provider);
        await validator.StartAsync(CancellationToken.None);
    }

    // Contract rule 7: Auto resolves to the conservative compare-and-swap dialect.
    [Fact]
    public void Auto_outbox_strategy_selects_conservative_dialect()
    {
        var options = new DbContextOptionsBuilder<BareProbeContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using var db = new BareProbeContext(options);

        var dialect = EfOutboxClaimDialectSelector.Select(db, OpenConnectOutboxClaimStrategy.Auto);
        Assert.IsType<OptimisticConcurrencyClaimDialect>(dialect);
    }

    // OC-PERSIST-003: unverified provider + ProviderOptimized fails closed, no secrets.
    [Fact]
    public void ProviderOptimized_on_unverified_provider_fails_closed()
    {
        var options = new DbContextOptionsBuilder<InMemoryProbeContext>()
            .UseInMemoryDatabase($"probe-{Guid.NewGuid():N}")
            .Options;
        using var db = new InMemoryProbeContext(options);

        var ex = Assert.Throws<InvalidOperationException>(
            () => EfOutboxClaimDialectSelector.Select(db, OpenConnectOutboxClaimStrategy.ProviderOptimized));
        Assert.Contains(ErrorCodes.EfProviderUnsupported, ex.Message);
        Assert.Contains("ef_provider_unsupported", ex.Message);
        Assert.DoesNotContain("Password", ex.Message);
    }

    // Stable wire values for the ef_* contract surface.
    [Fact]
    public void Stable_error_codes_match_contract_wire_values()
    {
        Assert.Equal("ef_context_missing", ErrorCodes.EfContextMissing);
        Assert.Equal("ef_model_missing", ErrorCodes.EfModelMissing);
        Assert.Equal("ef_provider_unsupported", ErrorCodes.EfProviderUnsupported);
        Assert.Equal("ef_schema_missing", ErrorCodes.EfSchemaMissing);
        Assert.Equal("ef_schema_mismatch", ErrorCodes.EfSchemaMismatch);
        Assert.Equal("ef_migration_not_allowed", ErrorCodes.EfMigrationNotAllowed);
        Assert.Equal("ef_transaction_unavailable", ErrorCodes.EfTransactionUnavailable);
        Assert.Equal("ef_outbox_claim_conflict", ErrorCodes.EfOutboxClaimConflict);
        Assert.Equal("ef_lease_expired", ErrorCodes.EfLeaseExpired);
        Assert.Equal("ef_persistence_unavailable", ErrorCodes.EfPersistenceUnavailable);
        Assert.Equal("ef_inmemory_not_durable", ErrorCodes.EfInmemoryNotDurable);
        Assert.Equal("ef_concurrency_conflict", ErrorCodes.EfConcurrencyConflict);
    }

    private sealed class BareProbeContext(DbContextOptions<BareProbeContext> options) : DbContext(options);

    private sealed class InMemoryProbeContext(DbContextOptions<InMemoryProbeContext> options) : DbContext(options);
}
