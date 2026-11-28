using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Context;
using OpenConnect.Persistence.EFCore.DependencyInjection;
using OpenConnect.Transport.Nats;
using OpenConnect.WorkerHosts.Outbox;
using Xunit;

namespace OpenConnect.Samples.Workers.Outbox.Tests;

public sealed class OutboxPoisonHostTests
{
    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("identity-mismatch")]
    public async Task Shipped_dispatch_path_defers_poison_delivers_good_and_quarantines(string kind)
    {
        var poison = Guid.NewGuid();
        var good = Guid.NewGuid();
        var payload = kind switch
        {
            "oversized" => Envelope(poison, "opaque://" + new string('x', 65536)),
            "identity-mismatch" => Envelope(Guid.NewGuid()),
            _ => "private-payload-sentinel:not-json",
        };
        await using var fixture = await HostFixture.CreateAsync();
        await fixture.Enqueue(new(poison, "ignored.stored.subject", payload));
        await fixture.Enqueue(new(good, "ignored.stored.subject", Envelope(good)));
        var before = DateTime.UtcNow;
        Assert.Equal(1, await fixture.Dispatch("owner-a"));
        var failed = await fixture.Read(poison);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("pending", failed.Status);
        Assert.True(failed.NextAttempt >= before.AddSeconds(2));
        Assert.Equal("delivered", (await fixture.Read(good)).Status);
        Assert.Equal([good.ToString("D")], fixture.Broker.Delivered.ToArray());
        Assert.Equal(["configured.subject"], fixture.Broker.Subjects.ToArray());
        Assert.Equal(0, await fixture.Dispatch("owner-b")); // persisted backoff, fresh scope/owner
        Assert.Equal(1, (await fixture.Read(poison)).Attempts);
        await fixture.MakeDue(poison);
        Assert.Equal(0, await fixture.Dispatch("owner-c"));
        Assert.Equal("quarantined", (await fixture.Read(poison)).Status);
        Assert.Equal(2, (await fixture.Read(poison)).Attempts);
        await fixture.MakeDue(poison);
        Assert.Equal(0, await fixture.Dispatch("owner-d"));
        Assert.Equal(2, (await fixture.Read(poison)).Attempts);
        Assert.Equal(2, fixture.Log.Messages.Count);
        Assert.All(fixture.Log.Messages, message => Assert.DoesNotContain("private-payload-sentinel", message));
        Assert.All(fixture.Log.Exceptions, exception => Assert.Null(exception));
    }

    [Fact]
    public async Task Shipped_dispatch_path_uses_live_row_claims_across_two_scopes()
    {
        await using var fixture = await HostFixture.CreateAsync();
        var id = Guid.NewGuid();
        await fixture.Enqueue(new(id, "test", Envelope(id)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Broker.BeforeAck = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        var first = fixture.Dispatch("first");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, await fixture.Dispatch("second"));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await first);
        Assert.Single(fixture.Broker.Delivered);
        Assert.Equal("delivered", (await fixture.Read(id)).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Hosting_options_reject_unbounded_or_disabled_quarantine(int maxAttempts)
    {
        var result = new OutboxWorkerOptionsValidator().Validate(null, new OutboxWorkerOptions
        {
            TenantUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV",
            ConnectionString = "Data Source=:memory:",
            MaxAttempts = maxAttempts,
        });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, message => message.StartsWith("outbox_max_attempts_invalid:", StringComparison.Ordinal));
    }

    [Fact]
    public void Shipped_and_container_hosts_share_dispatch_wiring_and_implementation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OpenConnect.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        foreach (var name in new[] { "Program.cs", "OutboxBatchDispatcher.cs", "OutboxDispatcherService.cs", "OutboxWorkerOptions.cs", "NatsJetStreamPublisher.cs" })
        {
            var shipped = File.ReadAllText(Path.Combine(root.FullName, "src", "OpenConnect.WorkerHosts.Outbox", name));
            var container = File.ReadAllText(Path.Combine(root.FullName, "workers", "outbox", name));
            Assert.Equal(shipped.Replace("\r\n", "\n"), container.Replace("\r\n", "\n"));
        }
        var program = File.ReadAllText(Path.Combine(root.FullName, "src", "OpenConnect.WorkerHosts.Outbox", "Program.cs"));
        Assert.Contains("AddScoped<OutboxBatchDispatcher>()", program);
        Assert.DoesNotContain("AddScoped<OutboxRelay>()", program);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Supervisor_loss_cancels_inflight_publish_without_attempt_or_later_publish(bool wrapCancellation)
    {
        await using var fixture = await HostFixture.CreateAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await fixture.Enqueue(new(first, "first", Envelope(first)));
        await fixture.Enqueue(new(second, "second", Envelope(second)));
        await fixture.AcquireSupervisor("owner");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Broker.BeforeAckWithCancellation = async ct =>
        {
            entered.TrySetResult();
            try
            { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) when (wrapCancellation)
            {
                throw new InvalidOperationException("untrusted-broker-secret");
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatch = fixture.DispatchGuarded("owner", timeout.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.ReplaceSupervisor();
        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => dispatch);
        Assert.StartsWith("outbox_supervisor_lease_lost:", failure.Message);
        Assert.Equal(1, fixture.Broker.Calls);
        Assert.Empty(fixture.Broker.Delivered);
        var row = await fixture.Read(first);
        Assert.Equal("pending", row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(0, (await fixture.Read(second)).Attempts);
        Assert.All(fixture.Log.Messages, message => Assert.DoesNotContain("untrusted-broker-secret", message));
    }

    [Fact]
    public async Task Supervisor_is_renewed_while_a_publish_is_blocked()
    {
        await using var fixture = await HostFixture.CreateAsync();
        var id = Guid.NewGuid();
        await fixture.Enqueue(new(id, "first", Envelope(id)));
        await fixture.AcquireSupervisor("owner");
        var initial = await fixture.GetSupervisor();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Broker.BeforeAckWithCancellation = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatch = fixture.DispatchGuarded("owner", timeout.Token);
        var renewed = false;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Initial guarded renewal may advance by milliseconds; require a real heartbeat.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if ((await fixture.GetSupervisor()).ExpiresAt > initial.ExpiresAt.AddSeconds(1))
                {
                    renewed = true;
                    break;
                }
                await Task.Delay(25, timeout.Token);
            }
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await dispatch);
        Assert.True(renewed, "A separate lease scope must renew while the broker call is in flight.");
    }

    [Fact]
    public async Task Supervisor_is_revalidated_before_the_next_publish_without_waiting_for_heartbeat()
    {
        await using var fixture = await HostFixture.CreateAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await fixture.Enqueue(new(first, "first", Envelope(first)));
        await fixture.Enqueue(new(second, "second", Envelope(second)));
        await fixture.AcquireSupervisor("owner");
        fixture.Broker.BeforeAckWithCancellation = _ => fixture.ReplaceSupervisor();
        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.DispatchGuarded("owner", CancellationToken.None));
        Assert.StartsWith("outbox_supervisor_lease_lost:", failure.Message);
        Assert.Equal([first.ToString("D")], fixture.Broker.Delivered.ToArray());
        Assert.Equal("delivered", (await fixture.Read(first)).Status);
        Assert.Equal("pending", (await fixture.Read(second)).Status);
        Assert.Equal(0, (await fixture.Read(second)).Attempts);
    }

    [Fact]
    public async Task Caller_cancellation_stops_supervised_dispatch_without_becoming_lease_loss()
    {
        await using var fixture = await HostFixture.CreateAsync();
        var id = Guid.NewGuid();
        await fixture.Enqueue(new(id, "first", Envelope(id)));
        await fixture.AcquireSupervisor("owner");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Broker.BeforeAckWithCancellation = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dispatch = fixture.DispatchGuarded("owner", stopping.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        Assert.Empty(fixture.Broker.Delivered);
        Assert.Equal(0, (await fixture.Read(id)).Attempts);
    }

    [Fact]
    public void Supervisor_expiry_is_floored_to_utc_microseconds()
    {
        var normalize = typeof(OutboxBatchDispatcher).GetMethod("NormalizeLeaseExpiry",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var input = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(19);
        var actual = Assert.IsType<DateTimeOffset>(normalize.Invoke(null, [input]));
        Assert.Equal(new DateTimeOffset(input.UtcTicks - 9, TimeSpan.Zero), actual);
        Assert.Equal(TimeSpan.Zero, actual.Offset);
        Assert.Equal(0, actual.UtcTicks % 10);
        Assert.True(actual <= input);
        Assert.Equal(actual, normalize.Invoke(null, [actual]));
    }

    [Fact]
    public async Task Supervisor_renewal_accepts_microsecond_precision_persistence()
    {
        var leases = new MicrosecondLeaseStore();
        await using var fixture = await HostFixture.CreateAsync(leases);
        var id = Guid.NewGuid();
        await fixture.Enqueue(new(id, "first", Envelope(id)));
        await fixture.AcquireSupervisor("owner");
        Assert.Equal(1, await fixture.DispatchGuarded("owner", CancellationToken.None));
        Assert.True(leases.Renewals > 0);
        Assert.Equal(0, leases.LastRequestedExpiry.UtcTicks % 10);
        Assert.Equal([id.ToString("D")], fixture.Broker.Delivered.ToArray());
        Assert.Equal("delivered", (await fixture.Read(id)).Status);
        Assert.Equal(0, (await fixture.Read(id)).Attempts);
    }

    private sealed class MicrosecondLeaseStore : ILeaseStore
    {
        private LeaseRecord? current;
        public int Renewals { get; private set; }
        public DateTimeOffset LastRequestedExpiry { get; private set; }
        private static DateTimeOffset Quantize(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
        public Task<LeaseRecord?> GetAsync(string resourceKey, TenantId tenantId, CancellationToken ct = default) => Task.FromResult(current);
        public Task<bool> TryAcquireAsync(LeaseRecord lease, CancellationToken ct = default)
        {
            current = lease with { ExpiresAt = Quantize(lease.ExpiresAt) };
            return Task.FromResult(true);
        }
        public Task<bool> RenewAsync(string resourceKey, TenantId tenantId, string owner, long fencingVersion, DateTimeOffset expiresAt, CancellationToken ct = default)
        {
            if (current is null || current.Owner != owner || current.FencingVersion != fencingVersion || current.ExpiresAt <= DateTimeOffset.UtcNow)
                return Task.FromResult(false);
            LastRequestedExpiry = expiresAt;
            current = current with { ExpiresAt = Quantize(expiresAt) };
            Renewals++;
            return Task.FromResult(true);
        }
        public Task ReleaseAsync(string resourceKey, TenantId tenantId, string owner, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static string Envelope(Guid id, string payloadRef = "opaque://payload/ref") => $$"""{"schemaVersion":"openconnect.v1","messageId":"{{id:D}}","taskId":"task-1","kind":"classification","issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:01:00Z","lease":{"leaseId":"123e4567-e89b-42d3-a456-426614174001","fencingToken":1},"payloadRef":"{{payloadRef}}"}""";

    private sealed class HostFixture(string path, ServiceProvider services, Broker broker, CaptureLogger log) : IAsyncDisposable
    {
        public Broker Broker => broker;
        public CaptureLogger Log => log;
        public static async Task<HostFixture> CreateAsync(ILeaseStore? leaseStore = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"oc-host-poison-{Guid.NewGuid():N}.db");
            var broker = new Broker();
            var log = new CaptureLogger();
            var services = new ServiceCollection();
            services.AddDbContext<OpenConnectDbContext>(options => options.UseSqlite($"Data Source={path};Pooling=False"));
            services.AddOpenConnectEntityFramework<OpenConnectDbContext>();
            if (leaseStore is not null)
                services.AddSingleton(leaseStore);
            services.AddSingleton(new NatsPublisher(broker));
            services.AddSingleton<IOptions<OutboxWorkerOptions>>(Options.Create(new OutboxWorkerOptions { MaxAttempts = 2, Subject = "configured.subject", TenantUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV", SupervisorLeaseDuration = TimeSpan.FromSeconds(5) }));
            services.AddSingleton<ILogger<OutboxBatchDispatcher>>(log);
            services.AddScoped<OutboxBatchDispatcher>();
            var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OpenConnectDbContext>().Database.EnsureCreatedAsync();
            return new(path, provider, broker, log);
        }
        public async Task Enqueue(OutboxEntry entry)
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IOutboxStore>().EnqueueAsync(entry);
        }
        public async Task<int> Dispatch(string owner)
        {
            using var scope = services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<OutboxBatchDispatcher>().DispatchAsync(owner);
        }
        public async Task<(int Attempts, string Status, DateTime NextAttempt)> Read(Guid id)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenConnectDbContext>();
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Attempts, Status, NextAttemptUtc FROM Outbox WHERE Id = $id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$id";
            parameter.Value = id;
            command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0), reader.GetString(1), reader.GetDateTime(2));
        }
        private static TenantId LeaseTenant => new(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV"));
        public async Task AcquireSupervisor(string owner)
        {
            using var scope = services.CreateScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<ILeaseStore>().TryAcquireAsync(
                new LeaseRecord(OutboxWorkerOptions.SupervisorResourceKey, LeaseTenant, owner, DateTimeOffset.UtcNow.AddSeconds(5), 1)));
        }
        public async Task<LeaseRecord> GetSupervisor()
        {
            using var scope = services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<ILeaseStore>().GetAsync(OutboxWorkerOptions.SupervisorResourceKey, LeaseTenant))!;
        }
        public async Task ReplaceSupervisor()
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OpenConnectDbContext>().Database.ExecuteSqlRawAsync(
                "UPDATE Leases SET Owner='replacement', FencingVersion=FencingVersion+1");
        }
        public async Task<int> DispatchGuarded(string owner, CancellationToken ct)
        {
            using var scope = services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<OutboxBatchDispatcher>().DispatchUnderSupervisorAsync(owner, 1, ct);
        }
        public async Task MakeDue(Guid id)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenConnectDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Outbox SET NextAttemptUtc={DateTime.UtcNow.AddMinutes(-1)} WHERE Id={id}");
        }
        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            File.Delete(path);
        }
    }

    private sealed class Broker : IJetStreamPublisher
    {
        public ConcurrentQueue<string> Delivered { get; } = new();
        public ConcurrentQueue<string> Subjects { get; } = new();
        public Func<Task>? BeforeAck { get; set; }
        public Func<CancellationToken, Task>? BeforeAckWithCancellation { get; set; }
        public int Calls { get; private set; }
        public async ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
            Calls++;
            if (BeforeAckWithCancellation is not null)
                await BeforeAckWithCancellation(cancellationToken);
            if (BeforeAck is not null)
                await BeforeAck();
            Subjects.Enqueue(subject);
            Delivered.Enqueue(messageId);
        }
    }

    private sealed class CaptureLogger : ILogger<OutboxBatchDispatcher>
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            Exceptions.Add(exception);
        }
    }
}
