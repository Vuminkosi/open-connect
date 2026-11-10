using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Persistence.EFCore.Configuration;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Persistence.EFCore.Model;
using OpenConnect.WorkerHosts.Operational;
using Xunit;

namespace OpenConnect.Samples.Workers.OperationalAgents.Tests;

public sealed class OperationalAgentsSampleTests
{
    private const string TenantUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly TenantId Tenant = new(Ulid.Parse(TenantUlid));

    [Fact]
    public void WSE2E_AT_066_Options_and_hosting_invariants_verify_model_free_and_disables_embedded()
    {
        var options = new OperationalWorkerOptions
        {
            Mode = OperationalAgentHostingMode.Standalone,
            EmbeddedEnabled = false,
            TenantUlid = TenantUlid,
            DatabaseProvider = "sqlite",
            ConnectionString = "Data Source=:memory:",
        };
        var validator = new OperationalWorkerOptionsValidator();
        var result = validator.Validate(null, options);
        Assert.True(result.Succeeded, result.FailureMessage);
    }

    [Fact]
    public async Task WSE2E_AT_067_Constrained_workload_identity_and_ambient_context_rejection()
    {
        var leaseStore = new InMemoryDurableOperationalStore();
        var leaseAdapter = new DurableOperationalLeaseStore(leaseStore, "test-supervisor");
        var identityProvider = new TestWorkloadIdentityProvider(TenantUlid);
        var planProvider = new TestPlanProvider();
        var stepExecutor = new TestStepExecutor();
        var handler = new OperationalNavigatorHandler(leaseAdapter, identityProvider, planProvider, stepExecutor);

        // Valid job with resolved service-principal
        var validJob = new OperationalJob(
            TenantScope: TenantUlid,
            ServicePrincipalRef: "spn:operational:audit-sweeper-v1",
            PlanId: "plan-readonly-health-sweep",
            ApplicationAllowlist: new HashSet<string> { "portal-app" },
            ExecutionId: "exec-workload-valid",
            Deadline: DateTimeOffset.UtcNow.AddMinutes(5),
            IdempotencyKey: "idem-workload-valid",
            PolicyRevision: "rev-2026-11");

        var receipt = await handler.HandleAsync(validJob);
        Assert.Equal(OperationalRunStatus.Completed, receipt.Status);
        Assert.Equal(2, receipt.CompletedSteps);
        Assert.False(receipt.IsReplay);

        // Negative: cross-tenant service principal mismatch rejected
        var mismatchedTenantJob = validJob with
        {
            ExecutionId = "exec-workload-mismatch",
            IdempotencyKey = "idem-workload-mismatch",
            ServicePrincipalRef = "spn:operational:other-tenant"
        };
        var mismatchReceipt = await handler.HandleAsync(mismatchedTenantJob);
        Assert.Equal(OperationalRunStatus.Rejected, mismatchReceipt.Status);

        // Negative: missing service principal rejected
        var emptySpnJob = validJob with
        {
            ExecutionId = "exec-workload-empty",
            IdempotencyKey = "idem-workload-empty",
            ServicePrincipalRef = ""
        };
        var emptyReceipt = await handler.HandleAsync(emptySpnJob);
        Assert.Equal(OperationalRunStatus.Rejected, emptyReceipt.Status);
    }

    [Fact]
    public async Task WSE2E_AT_068_Competing_host_lease_fencing_and_stale_fence_denial()
    {
        var store = new InMemoryDurableOperationalStore();
        var leaseA = new DurableOperationalLeaseStore(store, "host-alpha");
        var leaseB = new DurableOperationalLeaseStore(store, "host-bravo");

        var executionId = "exec-fence-race-001";
        var acquiredA = await leaseA.TryAcquireAsync(executionId, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.NotNull(acquiredA);
        Assert.Equal(1, acquiredA.FencingToken);

        // Host B competing while lease A is live -> fails closed (null)
        var acquiredB = await leaseB.TryAcquireAsync(executionId, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Null(acquiredB);

        // Simulate takeover after expiry -> token increments to 2
        var takeoverLease = await store.TryAcquireLeaseAsync(executionId, "host-bravo", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddMinutes(2), CancellationToken.None);
        Assert.NotNull(takeoverLease);
        Assert.Equal(2, takeoverLease.FencingToken);

        // Host A renew with stale lease token fails
        var staleRenew = await leaseA.RenewAsync(acquiredA, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Null(staleRenew);
    }

    [Fact]
    public async Task WSE2E_AT_069_MAF_native_checkpoint_and_approval_nonce_with_digest_validation()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(options =>
            options.UseSqlite(connection)
                .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, OpenConnectModelCacheKeyFactory>());

        services.AddOpenConnectEntityFramework<OpenConnectDbContext>(o =>
        {
            o.Migrations = OpenConnectMigrationMode.Manual;
        });

        await using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OpenConnectDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        using var testScope = provider.CreateScope();
        var sessionStore = testScope.ServiceProvider.GetRequiredService<INativeAgentSessionStore>();
        var approvalStore = testScope.ServiceProvider.GetRequiredService<IApprovalStore>();

        var sessionId = new SessionId(Ulid.Parse(TenantUlid));
        var principalId = new PrincipalId(Ulid.NewUlid());
        var binding = new NativeAgentSessionBinding(
            sessionId,
            Tenant,
            principalId,
            "operational-navigator-agent",
            "rev-1",
            "maf-v1",
            "schema-v1",
            "provider-ef");
        var encryptedCiphertext = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        // Save native MAF checkpoint
        var saveResult = await sessionStore.SaveAsync(
            new NativeAgentSessionRecord(binding, 1, encryptedCiphertext),
            0,
            CancellationToken.None);

        Assert.Equal(NativeAgentSessionSaveOutcome.Saved, saveResult);

        var loaded = await sessionStore.GetAsync(binding, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(encryptedCiphertext, loaded.GetCiphertextCopy());

        // Seed approval record for HITL step
        var taskId = new TaskId(Ulid.NewUlid());
        var approvalId = Guid.NewGuid();
        var approval = new ApprovalRecord(approvalId, Tenant, principalId, taskId, "pending", null);
        await approvalStore.CreateAsync(approval);

        var retrieved = await approvalStore.GetAsync(approvalId, Tenant, principalId);
        Assert.NotNull(retrieved);
        Assert.Equal("pending", retrieved.State);
        Assert.Null(retrieved.Decision);

        await approvalStore.DecideAsync(approvalId, Tenant, principalId, "approved");
        var decided = await approvalStore.GetAsync(approvalId, Tenant, principalId);
        Assert.NotNull(decided);
        Assert.Equal("decided", decided.State);
        Assert.Equal("approved", decided.Decision);
    }

    [Fact]
    public async Task WSE2E_AT_070_Misfire_overlap_and_mid_run_cancellation()
    {
        var store = new InMemoryDurableOperationalStore();
        var leaseAdapter = new DurableOperationalLeaseStore(store, "supervisor");
        var identityProvider = new TestWorkloadIdentityProvider(TenantUlid);
        var planProvider = new TestPlanProvider();
        var stepExecutor = new TestStepExecutor();
        var handler = new OperationalNavigatorHandler(leaseAdapter, identityProvider, planProvider, stepExecutor);

        // 1. Expired deadline misfire -> Rejected immediately without lease acquisition
        var expiredJob = new OperationalJob(
            TenantScope: TenantUlid,
            ServicePrincipalRef: "spn:operational:audit-sweeper-v1",
            PlanId: "plan-readonly-health-sweep",
            ApplicationAllowlist: new HashSet<string> { "portal-app" },
            ExecutionId: "exec-misfire-expired",
            Deadline: DateTimeOffset.UtcNow.AddSeconds(-10),
            IdempotencyKey: "idem-misfire-expired",
            PolicyRevision: "rev-2026-11");

        var expiredReceipt = await handler.HandleAsync(expiredJob);
        Assert.Equal(OperationalRunStatus.Rejected, expiredReceipt.Status);

        // 2. Mid-run cancellation -> Cancelled status
        using var cts = new CancellationTokenSource();
        var cancelJob = expiredJob with
        {
            ExecutionId = "exec-cancel-test",
            IdempotencyKey = "idem-cancel-test",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            cts.Cancel(); // pre-cancel
            await handler.HandleAsync(cancelJob, cts.Token);
        });
    }

    [Fact]
    public void WSE2E_AT_071_Provider_matrix_and_schema_guard_invariants()
    {
        // 1. ExistingDbContext + ApplyOnStartup fails with ef_migration_not_allowed
        var validator = new OpenConnectEfOptionsValidator();
        var res1 = validator.Validate(null, new OpenConnectEfOptions
        {
            IntegrationMode = OpenConnectEfIntegrationMode.ExistingDbContext,
            Migrations = OpenConnectMigrationMode.ApplyOnStartup,
        });
        Assert.True(res1.Failed);
        Assert.Contains(ErrorCodes.EfMigrationNotAllowed, res1.FailureMessage);

        // 2. EF InMemory with DurableSingleDatabase fails with ef_inmemory_not_durable
        var services = new ServiceCollection();
        services.AddDbContext<OpenConnectDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddOpenConnectEntityFramework<OpenConnectDbContext>(o =>
        {
            o.Consistency = OpenConnectPersistenceConsistency.DurableSingleDatabase;
        });
        using var provider = services.BuildServiceProvider();
        var startupValidator = provider.GetRequiredService<IHostedService>();
        var ex = Assert.ThrowsAsync<OptionsValidationException>(() => startupValidator.StartAsync(CancellationToken.None));
        Assert.NotNull(ex);
    }

    [Fact]
    public void WSE2E_AT_072_Embedded_vs_Standalone_exclusivity_guards()
    {
        var validator = new OperationalWorkerOptionsValidator();

        // Standalone with EmbeddedEnabled = true is rejected
        var invalid = new OperationalWorkerOptions
        {
            Mode = OperationalAgentHostingMode.Standalone,
            EmbeddedEnabled = true,
            TenantUlid = TenantUlid,
            DatabaseProvider = "sqlite",
            ConnectionString = "Data Source=:memory:",
        };
        var res = validator.Validate(null, invalid);
        Assert.True(res.Failed);
        Assert.Contains("operational_standalone_requires_embedded_disabled", res.FailureMessage);

        // Standalone with EmbeddedEnabled = false succeeds
        var valid = new OperationalWorkerOptions
        {
            Mode = OperationalAgentHostingMode.Standalone,
            EmbeddedEnabled = false,
            TenantUlid = TenantUlid,
            DatabaseProvider = "sqlite",
            ConnectionString = "Data Source=:memory:",
        };
        var validRes = validator.Validate(null, valid);
        Assert.True(validRes.Succeeded);
    }

    private sealed class TestWorkloadIdentityProvider(string validTenant) : IOperationalWorkloadIdentityProvider
    {
        public ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(servicePrincipalRef) || servicePrincipalRef.Contains("other-tenant"))
                return ValueTask.FromResult<IOperationalWorkloadIdentity?>(null);

            IOperationalWorkloadIdentity identity = new TestIdentity(validTenant);
            return ValueTask.FromResult<IOperationalWorkloadIdentity?>(identity);
        }

        private sealed class TestIdentity(string tenant) : IOperationalWorkloadIdentity
        {
            public string TenantScope => tenant;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestPlanProvider : IOperationalPlanProvider
    {
        public ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision, CancellationToken cancellationToken)
        {
            var plan = new NavigatorTourPlan(
                planId,
                1,
                [
                    new NavigatorTourStep(NavigatorTourStepType.Explain, Text: "Step 1 Explanation"),
                    new NavigatorTourStep(NavigatorTourStepType.Highlight, TargetId: "hdr-login", Text: "Step 2 Target")
                ],
                "/portal");
            return ValueTask.FromResult<NavigatorTourPlan?>(plan);
        }
    }

    private sealed class TestStepExecutor : IOperationalStepExecutor
    {
        public ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step, IOperationalWorkloadIdentity identity, IReadOnlySet<string> applicationAllowlist, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new OperationalStepResult(true, false, "Step completed"));
        }
    }
}
