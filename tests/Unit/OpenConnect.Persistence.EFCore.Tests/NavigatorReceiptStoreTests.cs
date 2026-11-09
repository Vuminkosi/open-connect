// Parity tests for EfNavigatorReceiptStore through the production
// ApplyOpenConnectModel path (OpenConnectDbContext, S0-1/S0-4 columns).
//
// Mirrors the legacy Postgres durable receipt behavior (G3b): singleton
// receipt per (tenant, principal, idempotency key), fail-closed reads,
// explicit Expired on the consume path, tenant-only operational receipts.
//
// Determinism note: expiry is simulated by passing explicit future `now`
// values (logical clock), never by Task.Delay. The only waits are bounded
// Testcontainers startup and Task.WhenAll.WaitAsync joins. Each host uses its
// own DbContext (own pooled connection) so the race is resolved by persisted
// state (unique index), not by in-memory tracking.
//
// Image policy: postgres:16-alpine must already exist locally. If Docker or
// the image is unavailable the test skips (returns) like the seam fencing
// suite; it must never trigger a network pull in an offline lane.

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.Entities;
using OpenConnect.Persistence.EFCore.Stores;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Persistence.EFCore.Tests;

[Trait("Category", "PostgresDocker")]
public sealed class NavigatorReceiptStoreTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset T0 =
        new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    [Fact]
    public async Task Concurrent_consume_from_independent_contexts_elects_single_winner()
    {
        await using var postgres = await StartPostgresAsync("navigator_receipt_race");
        if (postgres is null)
            return;
        var connectionString = postgres.GetConnectionString();
        await using (var init = CreateDb(connectionString))
            await init.Database.EnsureCreatedAsync();

        var tenant = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var sid = Guid.NewGuid();
        const string commandId = "cmd-race";
        const string digest = "digest-race";
        const string key = "idem-race";
        await SeedSessionAndApprovalAsync(connectionString, sid, tenant, principal, commandId, digest, T0 + 2 * Hour);

        EfNavigatorReceiptInput Input() => new(
            commandId, key, digest,
            JsonDocument.Parse("""{"ok":true}""").RootElement.Clone(),
            T0 + Hour, 1);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<EfNavigatorReceiptResult> ConsumeAsHostAsync()
        {
            await using var db = CreateDb(connectionString);
            var store = new EfNavigatorReceiptStore<OpenConnectDbContext>(db);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await store.ConsumeApprovalAndSaveReceiptAsync(
                sid.ToString("D"), tenant.ToString("D"), principal.ToString("D"),
                commandId, digest, T0, Input());
        }

        var hostA = ConsumeAsHostAsync();
        var hostB = ConsumeAsHostAsync();
        gate.TrySetResult();
        var results = await Task.WhenAll(hostA, hostB).WaitAsync(TimeSpan.FromSeconds(60));

        output.WriteLine(
            $"Race outcomes: [0] Outcome={results[0].Outcome} IsReplay={results[0].IsReplay} | " +
            $"[1] Outcome={results[1].Outcome} IsReplay={results[1].IsReplay}");

        // Hardening for read-skew transients under parallel-suite load: the loser
        // may pre-read the receipt before the winner commits, then load the
        // approval after the winner commits and report AlreadyConsumed instead of
        // replay/Conflict. Exactly-one-winner still holds; one state-based probe
        // (idempotent replay from a third independent context, no sleep) must then
        // resolve to replay-Succeeded, proving the receipt committed exactly once.
        var winnerCount = results.Count(r => r.Outcome == EfNavigatorReceiptOutcome.Succeeded && !r.IsReplay);
        var losers = results.Where(r => r is not { Outcome: EfNavigatorReceiptOutcome.Succeeded, IsReplay: false }).ToList();
        var loserOk = winnerCount == 1 && losers.Count == 1 &&
            ((losers[0].Outcome == EfNavigatorReceiptOutcome.Succeeded && losers[0].IsReplay)
            || losers[0].Outcome == EfNavigatorReceiptOutcome.Conflict);
        if (!loserOk)
        {
            output.WriteLine("Race anomaly detected; performing one bounded idempotent retry from a third context.");
            await using var retryDb = CreateDb(connectionString);
            var probe = await new EfNavigatorReceiptStore<OpenConnectDbContext>(retryDb)
                .ConsumeApprovalAndSaveReceiptAsync(
                    sid.ToString("D"), tenant.ToString("D"), principal.ToString("D"),
                    commandId, digest, T0, Input());
            output.WriteLine($"Retry outcome: Outcome={probe.Outcome} IsReplay={probe.IsReplay}");

            var transientResolved =
                winnerCount == 1 && losers.Count == 1 &&
                losers[0].Outcome == EfNavigatorReceiptOutcome.AlreadyConsumed &&
                probe is { Outcome: EfNavigatorReceiptOutcome.Succeeded, IsReplay: true };
            if (!transientResolved)
            {
                Assert.Fail(
                    "Race did not converge to a single winner. Outcomes=" +
                    $"[{results[0].Outcome}/{results[0].IsReplay}, {results[1].Outcome}/{results[1].IsReplay}] " +
                    $"Retry={probe.Outcome}/{probe.IsReplay}.");
            }

            output.WriteLine(
                $"Transient loser {losers[0].Outcome} resolved by idempotent replay; accepting single-winner race.");

            await using var witnessRetry = CreateDb(connectionString);
            var storedRetry = await new EfNavigatorReceiptStore<OpenConnectDbContext>(witnessRetry)
                .GetReceiptAsync(tenant.ToString("D"), principal.ToString("D"), key, T0);
            Assert.NotNull(storedRetry);
            Assert.Equal(commandId, storedRetry.CommandId);
            Assert.Equal(digest, storedRetry.Digest);
            return;
        }

        // Exactly one first-writer: Succeeded without replay. The loser either
        // observes the committed receipt (replay) or fails closed (Conflict).
        Assert.Equal(1, results.Count(r => r.Outcome == EfNavigatorReceiptOutcome.Succeeded && !r.IsReplay));
        var loser = results.Single(r => r is not { Outcome: EfNavigatorReceiptOutcome.Succeeded, IsReplay: false });
        Assert.True(
            (loser.Outcome == EfNavigatorReceiptOutcome.Succeeded && loser.IsReplay)
            || loser.Outcome == EfNavigatorReceiptOutcome.Conflict);

        // The receipt is visible from a third independent context.
        await using var witness = CreateDb(connectionString);
        var stored = await new EfNavigatorReceiptStore<OpenConnectDbContext>(witness)
            .GetReceiptAsync(tenant.ToString("D"), principal.ToString("D"), key, T0);
        Assert.NotNull(stored);
        Assert.Equal(commandId, stored.CommandId);
        Assert.Equal(digest, stored.Digest);
    }

    [Fact]
    public async Task Expired_receipt_reads_absent_and_consume_reports_expired()
    {
        await using var postgres = await StartPostgresAsync("navigator_receipt_expiry");
        if (postgres is null)
            return;
        var connectionString = postgres.GetConnectionString();
        await using (var init = CreateDb(connectionString))
            await init.Database.EnsureCreatedAsync();

        var tenant = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var sid = Guid.NewGuid();
        const string commandId = "cmd-expiry";
        const string digest = "digest-expiry";
        const string key = "idem-expiry";
        await SeedSessionAndApprovalAsync(connectionString, sid, tenant, principal, commandId, digest, T0 + 2 * Hour);

        var receipt = new EfNavigatorReceiptInput(
            commandId, key, digest,
            JsonDocument.Parse("""{"ok":true}""").RootElement.Clone(),
            T0 + Hour, 1);

        await using (var db = CreateDb(connectionString))
        {
            var result = await new EfNavigatorReceiptStore<OpenConnectDbContext>(db)
                .ConsumeApprovalAndSaveReceiptAsync(
                    sid.ToString("D"), tenant.ToString("D"), principal.ToString("D"),
                    commandId, digest, T0, receipt);
            Assert.Equal(EfNavigatorReceiptOutcome.Succeeded, result.Outcome);
            Assert.False(result.IsReplay);
        }

        // Past persisted expiry the receipt reads as absent (fail closed),
        // and a same-triple consume reports Expired rather than replaying.
        var expiredNow = T0 + 2 * Hour;
        await using (var db = CreateDb(connectionString))
        {
            var store = new EfNavigatorReceiptStore<OpenConnectDbContext>(db);
            Assert.Null(await store.GetReceiptAsync(tenant.ToString("D"), principal.ToString("D"), key, expiredNow));
            var retry = await store.ConsumeApprovalAndSaveReceiptAsync(
                sid.ToString("D"), tenant.ToString("D"), principal.ToString("D"),
                commandId, digest, expiredNow, receipt);
            Assert.Equal(EfNavigatorReceiptOutcome.Expired, retry.Outcome);
        }

        // An approval whose own expiry has passed also reports Expired.
        var sid2 = Guid.NewGuid();
        const string key2 = "idem-expiry-approval";
        await SeedSessionAndApprovalAsync(connectionString, sid2, tenant, principal, commandId, digest, T0);
        await using (var db = CreateDb(connectionString))
        {
            var retry = await new EfNavigatorReceiptStore<OpenConnectDbContext>(db)
                .ConsumeApprovalAndSaveReceiptAsync(
                    sid2.ToString("D"), tenant.ToString("D"), principal.ToString("D"),
                    commandId, digest, T0 + TimeSpan.FromMinutes(1),
                    receipt with { IdempotencyKey = key2 });
            Assert.Equal(EfNavigatorReceiptOutcome.Expired, retry.Outcome);
        }
    }

    [Fact]
    public async Task Corrupt_receipt_payload_fails_closed_on_read_and_consume()
    {
        await using var postgres = await StartPostgresAsync("navigator_receipt_corrupt");
        if (postgres is null)
            return;
        var connectionString = postgres.GetConnectionString();
        await using (var init = CreateDb(connectionString))
            await init.Database.EnsureCreatedAsync();

        var tenant = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var sid = Guid.NewGuid();
        const string commandId = "cmd-corrupt";
        const string digest = "digest-corrupt";
        const string key = "idem-corrupt";
        await SeedSessionAndApprovalAsync(connectionString, sid, tenant, principal, commandId, digest, T0 + 2 * Hour);

        var receipt = new EfNavigatorReceiptInput(
            commandId, key, digest,
            JsonDocument.Parse("""{"ok":true}""").RootElement.Clone(),
            T0 + Hour, 1);

        await using (var db = CreateDb(connectionString))
        {
            var result = await new EfNavigatorReceiptStore<OpenConnectDbContext>(db)
                .ConsumeApprovalAndSaveReceiptAsync(
                    sid.ToString("D"), tenant.ToString("D"), principal.ToString("D"),
                    commandId, digest, T0, receipt);
            Assert.Equal(EfNavigatorReceiptOutcome.Succeeded, result.Outcome);
        }

        // Corrupt the persisted payload directly (simulates a bad writer).
        await using (var db = CreateDb(connectionString))
        {
            var tenantScope = tenant.ToString("D");
            var principalScope = principal.ToString("D");
            var row = await db.Set<TaskEntity>().SingleAsync(x =>
                x.TenantScope == tenantScope && x.PrincipalScope == principalScope &&
                x.ClientTaskId == $"navigator:receipt:{key}");
            row.ReceiptPayload = "{not-json";
            await db.SaveChangesAsync();
        }

        await using (var db = CreateDb(connectionString))
        {
            var store = new EfNavigatorReceiptStore<OpenConnectDbContext>(db);
            Assert.Null(await store.GetReceiptAsync(tenant.ToString("D"), principal.ToString("D"), key, T0));
            var retry = await store.ConsumeApprovalAndSaveReceiptAsync(
                sid.ToString("D"), tenant.ToString("D"), principal.ToString("D"),
                commandId, digest, T0, receipt);
            Assert.Equal(EfNavigatorReceiptOutcome.Conflict, retry.Outcome);
        }
    }

    [Fact]
    public async Task Operational_save_accepts_completed_once_and_rejects_non_completed_and_duplicates()
    {
        await using var postgres = await StartPostgresAsync("navigator_receipt_operational");
        if (postgres is null)
            return;
        var connectionString = postgres.GetConnectionString();
        await using (var init = CreateDb(connectionString))
            await init.Database.EnsureCreatedAsync();

        var tenantScope = Guid.NewGuid().ToString("D");
        const string key = "op:job-1";

        await using var db = CreateDb(connectionString);
        var store = new EfNavigatorReceiptStore<OpenConnectDbContext>(db);

        // Non-completed outcomes are rejected (fail closed).
        Assert.False(await store.TrySaveOperationalReceiptAsync(
            tenantScope, key,
            new EfOperationalReceiptInput("exec-1", EfOperationalReceiptStatus.Failed, 0, null, 1),
            T0));

        // First completed save wins; the duplicate is rejected.
        Assert.True(await store.TrySaveOperationalReceiptAsync(
            tenantScope, key,
            new EfOperationalReceiptInput("exec-1", EfOperationalReceiptStatus.Completed, 3, "done", 1),
            T0));
        Assert.False(await store.TrySaveOperationalReceiptAsync(
            tenantScope, key,
            new EfOperationalReceiptInput("exec-1", EfOperationalReceiptStatus.Completed, 3, "done", 1),
            T0));

        // No expiry gate on get: readable at any later instant with provenance intact.
        var view = await store.GetOperationalReceiptAsync(tenantScope, key);
        Assert.NotNull(view);
        Assert.Equal("exec-1", view.ExecutionId);
        Assert.Equal(EfOperationalReceiptStatus.Completed, view.Status);
        Assert.Equal(1, view.FencingToken);
    }

    [Fact]
    public async Task Receipt_lookups_are_tenant_and_principal_scoped()
    {
        await using var postgres = await StartPostgresAsync("navigator_receipt_isolation");
        if (postgres is null)
            return;
        var connectionString = postgres.GetConnectionString();
        await using (var init = CreateDb(connectionString))
            await init.Database.EnsureCreatedAsync();

        var tenant = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var sid = Guid.NewGuid();
        const string commandId = "cmd-scope";
        const string digest = "digest-scope";
        const string key = "idem-scope";
        await SeedSessionAndApprovalAsync(connectionString, sid, tenant, principal, commandId, digest, T0 + 2 * Hour);

        await using (var db = CreateDb(connectionString))
        {
            var result = await new EfNavigatorReceiptStore<OpenConnectDbContext>(db)
                .ConsumeApprovalAndSaveReceiptAsync(
                    sid.ToString("D"), tenant.ToString("D"), principal.ToString("D"),
                    commandId, digest, T0,
                    new EfNavigatorReceiptInput(
                        commandId, key, digest,
                        JsonDocument.Parse("""{"ok":true}""").RootElement.Clone(),
                        T0 + Hour, 1));
            Assert.Equal(EfNavigatorReceiptOutcome.Succeeded, result.Outcome);
        }

        await using (var db = CreateDb(connectionString))
        {
            var store = new EfNavigatorReceiptStore<OpenConnectDbContext>(db);
            // Cross-principal and cross-tenant lookups read as absent.
            Assert.Null(await store.GetReceiptAsync(
                tenant.ToString("D"), Guid.NewGuid().ToString("D"), key, T0));
            Assert.Null(await store.GetReceiptAsync(
                Guid.NewGuid().ToString("D"), principal.ToString("D"), key, T0));
            Assert.NotNull(await store.GetReceiptAsync(
                tenant.ToString("D"), principal.ToString("D"), key, T0));
        }

        // Operational receipts are tenant-only: another tenant reads absent.
        const string opKey = "op:job-scope";
        await using (var db = CreateDb(connectionString))
        {
            var store = new EfNavigatorReceiptStore<OpenConnectDbContext>(db);
            Assert.True(await store.TrySaveOperationalReceiptAsync(
                tenant.ToString("D"), opKey,
                new EfOperationalReceiptInput("exec-scope", EfOperationalReceiptStatus.Completed, 1, null, 1),
                T0));
            Assert.Null(await store.GetOperationalReceiptAsync(Guid.NewGuid().ToString("D"), opKey));
            Assert.NotNull(await store.GetOperationalReceiptAsync(tenant.ToString("D"), opKey));
        }
    }

    private static async Task SeedSessionAndApprovalAsync(
        string connectionString,
        Guid sid,
        Guid tenant,
        Guid principal,
        string commandId,
        string digest,
        DateTimeOffset approvalExpires)
    {
        await using var db = CreateDb(connectionString);
        var tenantScope = tenant.ToString("D");
        var principalScope = principal.ToString("D");
        db.Set<SessionEntity>().Add(new SessionEntity
        {
            SessionId = sid,
            TenantScope = tenantScope,
            PrincipalScope = principalScope,
            Version = 1,
            CreatedUtc = T0.UtcDateTime,
            UpdatedUtc = T0.UtcDateTime,
        });
        db.Set<ApprovalEntity>().Add(new ApprovalEntity
        {
            ApprovalId = Guid.NewGuid(),
            TenantScope = tenantScope,
            PrincipalScope = principalScope,
            TaskId = sid,
            CommandId = commandId,
            CommandDigest = digest,
            State = "pending",
            CreatedUtc = T0.UtcDateTime,
            ExpiresUtc = approvalExpires.UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    private async Task<PostgreSqlContainer?> StartPostgresAsync(string database)
    {
        var postgres = new PostgreSqlBuilder("postgres:16-alpine").WithDatabase(database)
            .WithUsername("test").WithPassword("test").Build();
        Exception? startupError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await postgres.StartAsync();
                startupError = null;
                break;
            }
            catch (Exception exception)
            {
                startupError = exception;
            }
        }

        if (startupError is null)
            return postgres;
        output.WriteLine(
            $"SKIP PostgresDocker: Docker/Testcontainers could not start postgres:16-alpine " +
            $"after 2 attempts: {startupError.Message}");
        await postgres.DisposeAsync();
        return null;
    }

    private static OpenConnectDbContext CreateDb(string connectionString) =>
        new(new DbContextOptionsBuilder<OpenConnectDbContext>().UseNpgsql(connectionString).Options);
}
