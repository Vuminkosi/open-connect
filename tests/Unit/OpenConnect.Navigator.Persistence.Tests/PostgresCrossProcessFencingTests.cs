using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Diagnostics;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Operational;
using OpenConnect.Navigator.Operational.Durable;
using OpenConnect.Navigator.Persistence.Postgres;
using OpenConnect.Persistence.Postgres;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Navigator.Persistence.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class PostgresCrossProcessFencingTests(ITestOutputHelper output)
{
    private const string ChildTestName = "Cross_process_child_operation";

    [Fact]
    public async Task Lease_fence_and_receipt_are_shared_between_os_processes()
    {
        // The filtered child test runs this same test assembly in a distinct OS process.
        // Its database connection comes only from the parent-provided disposable container.
        if (Environment.GetEnvironmentVariable("NAV_XPROC_MODE") is { Length: > 0 } mode)
        {
            await RunChildOperationAsync(mode);
            return;
        }

        await using var postgres = await StartPostgresAsync("navigator_xproc_fencing_test");
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("navigator_xproc_fencing_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var db = CreateDb(connectionString)) await db.Database.MigrateAsync();

        var tenant = Guid.NewGuid().ToString("D");
        var execution = $"{tenant}:xproc-{Guid.NewGuid():N}";
        var first = await RunChildAsync(connectionString, "acquire", execution, "1", "owner-a");
        Assert.Equal("XPROC-ACQUIRED 1", first.Trim());
        var denied = await RunChildAsync(connectionString, "check-stale", execution, "1", "owner-b");
        Assert.Equal("XPROC-DENIED", denied.Trim());

        // Child testhost startup alone can consume most of a one-second lease on CI; use a
        // five-second lease and a 5.5-second wait to retain a 500ms expiry margin.
        await Task.Delay(TimeSpan.FromMilliseconds(5500));
        var advanced = await RunChildAsync(connectionString, "acquire", execution, "2", "owner-c");
        Assert.Equal("XPROC-ACQUIRED 2", advanced.Trim());
        var stale = await RunChildAsync(connectionString, "check-stale", execution, "1", "owner-a");
        Assert.Equal("XPROC-STALE", stale.Trim());

        var key = "xproc-receipt-" + Guid.NewGuid().ToString("N");
        var saved = await RunChildAsync(connectionString, "receipt-save", execution, "2", key);
        Assert.Equal("XPROC-RECEIPT-SAVED", saved.Trim());
        var replay = await RunChildAsync(connectionString, "replay-read", execution, "2", key);
        Assert.Equal("XPROC-REPLAY-OK", replay.Trim());
    }

    [Fact]
    public async Task Cross_process_child_operation()
    {
        var mode = Environment.GetEnvironmentVariable("NAV_XPROC_MODE");
        if (string.IsNullOrEmpty(mode)) return; // Included in normal suite, inert outside the parent protocol.
        await RunChildOperationAsync(mode);
    }

    private static async Task RunChildOperationAsync(string mode)
    {
        var connectionString = Environment.GetEnvironmentVariable("NAV_XPROC_CONN")
            ?? throw new InvalidOperationException("NAV_XPROC_CONN is required for child test.");
        var execution = Environment.GetEnvironmentVariable("NAV_XPROC_EXEC")
            ?? throw new InvalidOperationException("NAV_XPROC_EXEC is required for child test.");
        var token = long.Parse(Environment.GetEnvironmentVariable("NAV_XPROC_TOKEN") ?? "0");
        var ownerOrKey = Environment.GetEnvironmentVariable("NAV_XPROC_OWNER_OR_KEY") ?? "child";
        if (!connectionString.Contains("navigator_xproc_fencing_test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing non-disposable cross-process test database.");
        await using var db = CreateDb(connectionString);
        var store = new PostgresDurableNavigatorStore(db);
        var now = DateTimeOffset.UtcNow;
        switch (mode)
        {
            case "acquire":
                var lease = await store.TryAcquireLeaseAsync(execution, ownerOrKey,
                    ownerOrKey == "owner-a" ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1), now, default);
                if (lease is null || lease.FencingToken != token) throw new InvalidOperationException("Expected lease acquisition/token was not observed.");
                Console.WriteLine($"XPROC-ACQUIRED {lease.FencingToken}");
                break;
            case "check-stale":
                if (ownerOrKey == "owner-b")
                {
                    if (await store.TryAcquireLeaseAsync(execution, ownerOrKey, TimeSpan.FromSeconds(5), now, default) is not null)
                        throw new InvalidOperationException("Active lease was not exclusive.");
                    Console.WriteLine("XPROC-DENIED");
                }
                else
                {
                    var current = await store.IsLeaseCurrentAsync(new OperationalLease(execution, execution, token, now), now, default);
                    if (current) throw new InvalidOperationException("Expired/replaced fencing token remained current.");
                    Console.WriteLine("XPROC-STALE");
                }
                break;
            case "receipt-save":
                var receipt = new OperationalReceipt(execution, OperationalRunStatus.Completed, 3, "saved", token);
                if (!await store.TrySaveReceiptAsync(execution[..execution.IndexOf(':')], ownerOrKey, receipt, now, default))
                    throw new InvalidOperationException("Receipt save failed.");
                Console.WriteLine("XPROC-RECEIPT-SAVED");
                break;
            case "replay-read":
                var read = await store.GetReceiptAsync(execution[..execution.IndexOf(':')], ownerOrKey, default);
                if (read is null || read.ExecutionId != execution || read.FencingToken != token || read.Status != OperationalRunStatus.Completed)
                    throw new InvalidOperationException("Persisted receipt did not match expected replay data.");
                Console.WriteLine("XPROC-REPLAY-OK");
                break;
            default: throw new InvalidOperationException($"Unsupported child mode: {mode}");
        }
    }

    private static async Task<string> RunChildAsync(string connectionString, string mode, string execution, string token, string ownerOrKey)
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "tests", "Unit", "OpenConnect.Navigator.Persistence.Tests", "OpenConnect.Navigator.Persistence.Tests.csproj");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("test"); start.ArgumentList.Add(project); start.ArgumentList.Add("-c"); start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--no-build"); start.ArgumentList.Add("--filter");
        start.ArgumentList.Add($"FullyQualifiedName~{nameof(Cross_process_child_operation)}");
        start.ArgumentList.Add("--logger"); start.ArgumentList.Add("console;verbosity=detailed");
        start.Environment["NAV_XPROC_CONN"] = connectionString;
        start.Environment["NAV_XPROC_MODE"] = mode;
        start.Environment["NAV_XPROC_EXEC"] = execution;
        start.Environment["NAV_XPROC_TOKEN"] = token;
        start.Environment["NAV_XPROC_OWNER_OR_KEY"] = ownerOrKey;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start child dotnet test process.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var markers = stdout.Split('\n').Where(line => line.Trim().StartsWith("XPROC-", StringComparison.Ordinal)).ToArray();
        if (process.ExitCode != 0 || markers.Length != 1)
            throw new InvalidOperationException($"Cross-process child failed ({mode}), exit={process.ExitCode}, markers={markers.Length}. stdout={stdout} stderr={stderr}");
        return markers[0].Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenConnect.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
    [Fact]
    public async Task Lease_acquisition_fences_stale_host_after_expiry_on_independent_connections()
    {
        await using var postgres = await StartPostgresAsync("navigator_cross_fence_test");
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("navigator_cross_fence_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migrationDb = CreateDb(connectionString))
            await migrationDb.GetService<IMigrator>().MigrateAsync();

        var tenant = Guid.NewGuid().ToString("D");
        var executionId = $"{tenant}:cross-host-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        OperationalLease hostA;
        await using (var dbA = CreateDb(connectionString))
        {
            var storeA = new PostgresDurableNavigatorStore(dbA);
            hostA = Assert.IsType<OperationalLease>(await storeA.TryAcquireLeaseAsync(executionId, "host-a",
                TimeSpan.FromMilliseconds(100), now, CancellationToken.None));
        }

        await using (var dbB = CreateDb(connectionString))
        {
            var storeB = new PostgresDurableNavigatorStore(dbB);
            Assert.Null(await storeB.TryAcquireLeaseAsync(executionId, "host-b", TimeSpan.FromSeconds(5), now,
                CancellationToken.None));
        }

        await Task.Delay(500);
        OperationalLease hostB;
        await using (var dbB = CreateDb(connectionString))
        {
            var storeB = new PostgresDurableNavigatorStore(dbB);
            hostB = Assert.IsType<OperationalLease>(await storeB.TryAcquireLeaseAsync(executionId, "host-b",
                TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow, CancellationToken.None));
            Assert.True(await storeB.IsLeaseCurrentAsync(hostB, DateTimeOffset.UtcNow, CancellationToken.None));
        }
        Assert.True(hostB.FencingToken > hostA.FencingToken);
        await using (var staleHostDb = CreateDb(connectionString))
        {
            var staleHost = new PostgresDurableNavigatorStore(staleHostDb);
            Assert.False(await staleHost.IsLeaseCurrentAsync(hostA, DateTimeOffset.UtcNow, CancellationToken.None));
        }
    }

    [Fact]
    public async Task Concurrent_same_key_handlers_on_independent_contexts_return_one_replay_receipt()
    {
        await using var postgres = await StartPostgresAsync("navigator_cross_receipt_test");
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("navigator_cross_receipt_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migrationDb = CreateDb(connectionString))
            await migrationDb.GetService<IMigrator>().MigrateAsync();

        var tenant = Guid.NewGuid().ToString("D");
        var key = "same-key-" + Guid.NewGuid().ToString("N");
        var firstJob = CreateJob(tenant, $"{tenant}:host-a-{Guid.NewGuid():N}", key);
        var secondJob = CreateJob(tenant, $"{tenant}:host-b-{Guid.NewGuid():N}", key);
        var executor = new RendezvousExecutor(2);
        await using var dbA = CreateDb(connectionString);
        await using var dbB = CreateDb(connectionString);
        var handlerA = CreateHandler(dbA, tenant, executor);
        var handlerB = CreateHandler(dbB, tenant, executor);

        var firstTask = handlerA.HandleAsync(firstJob);
        var secondTask = handlerB.HandleAsync(secondJob);
        var receipts = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.All(receipts, receipt => Assert.Equal(OperationalRunStatus.Completed, receipt.Status));
        Assert.Equal(1, receipts.Count(receipt => receipt.IsReplay));
        Assert.Equal(1, receipts.Count(receipt => !receipt.IsReplay));
        Assert.Equal(2, executor.Calls);
        Assert.Equal(receipts[0] with { IsReplay = receipts[1].IsReplay }, receipts[1]);
        Assert.Contains(receipts[0].ExecutionId, new[] { firstJob.ExecutionId, secondJob.ExecutionId });
    }

    [Fact]
    public async Task Concurrent_same_key_handlers_for_different_tenants_return_independent_receipts()
    {
        await using var postgres = await StartPostgresAsync("navigator_cross_tenant_receipt_test");
        if (postgres is null) return;
        var connectionString = postgres.GetConnectionString();
        Assert.Contains("navigator_cross_tenant_receipt_test", connectionString, StringComparison.OrdinalIgnoreCase);
        await using (var migrationDb = CreateDb(connectionString))
            await migrationDb.GetService<IMigrator>().MigrateAsync();

        var tenantA = Guid.NewGuid().ToString("D");
        var tenantB = Guid.NewGuid().ToString("D");
        var key = "same-key-" + Guid.NewGuid().ToString("N");
        var jobA = CreateJob(tenantA, $"{tenantA}:host-a-{Guid.NewGuid():N}", key);
        var jobB = CreateJob(tenantB, $"{tenantB}:host-b-{Guid.NewGuid():N}", key);
        var executor = new RendezvousExecutor(2);
        await using var dbA = CreateDb(connectionString);
        await using var dbB = CreateDb(connectionString);
        var handlerA = CreateHandler(dbA, tenantA, executor);
        var handlerB = CreateHandler(dbB, tenantB, executor);

        var receipts = await Task.WhenAll(handlerA.HandleAsync(jobA), handlerB.HandleAsync(jobB))
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, executor.Calls);
        Assert.All(receipts, receipt => Assert.Equal(OperationalRunStatus.Completed, receipt.Status));
        Assert.All(receipts, receipt => Assert.False(receipt.IsReplay));
        Assert.Equal(jobA.ExecutionId, receipts[0].ExecutionId);
        Assert.Equal(jobB.ExecutionId, receipts[1].ExecutionId);
        Assert.NotEqual(receipts[0].ExecutionId, receipts[1].ExecutionId);
    }

    private async Task<PostgreSqlContainer?> StartPostgresAsync(string database)
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase(database)
            .WithUsername("test").WithPassword("test").Build();
        Exception? startupError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try { await postgres.StartAsync(); startupError = null; break; }
            catch (Exception e) { startupError = e; }
        }
        if (startupError is null) return postgres;
        output.WriteLine($"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine after 2 attempts: {startupError.Message}");
        await postgres.DisposeAsync();
        return null;
    }

    private static OpenConnectDbContext CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);

    private static OperationalJob CreateJob(string tenant, string executionId, string key) => new(tenant,
        "service-principal", "plan", new HashSet<string> { "app" }, executionId,
        DateTimeOffset.UtcNow.AddMinutes(2), key, "policy");

    private static OperationalNavigatorHandler CreateHandler(OpenConnectDbContext db, string tenant, IOperationalStepExecutor executor)
    {
        var store = new PostgresDurableNavigatorStore(db);
        return new(new DurableOperationalLeaseStore(store, "cross-process-test"), new IdentityProvider(tenant),
            new PlanProvider(), executor, new DurableIdempotencyStore(store));
    }

    private sealed class IdentityProvider(string tenant) : IOperationalWorkloadIdentityProvider
    {
        public ValueTask<IOperationalWorkloadIdentity?> ResolveAsync(string servicePrincipalRef, string tenantScope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IOperationalWorkloadIdentity?>(new Identity(tenant));
    }

    private sealed class Identity(string tenant) : IOperationalWorkloadIdentity
    {
        public string TenantScope { get; } = tenant;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PlanProvider : IOperationalPlanProvider
    {
        public ValueTask<NavigatorTourPlan?> GetPlanAsync(string planId, string tenantScope, string policyRevision, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NavigatorTourPlan?>(new("plan", 1,
                [new NavigatorTourStep(NavigatorTourStepType.Explain)], "home"));
    }

    private sealed class RendezvousExecutor(int participants) : IOperationalStepExecutor
    {
        private readonly TaskCompletionSource _bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async ValueTask<OperationalStepResult> ExecuteReadOnlyAsync(NavigatorTourStep step,
            IOperationalWorkloadIdentity identity, IReadOnlySet<string> applicationAllowlist,
            CancellationToken cancellationToken)
        {
            Assert.Equal(NavigatorTourStepType.Explain, step.Type);
            Assert.Contains("app", applicationAllowlist);
            if (Interlocked.Increment(ref _calls) == participants) _bothEntered.TrySetResult();
            await _bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new OperationalStepResult(true, false, "ok");
        }
    }
}
