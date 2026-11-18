using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
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

    private static string Envelope(Guid id, string payloadRef = "opaque://payload/ref") => $$"""{"schemaVersion":"openconnect.v1","messageId":"{{id:D}}","taskId":"task-1","kind":"classification","issuedAt":"2026-01-01T00:00:00Z","expiresAt":"2026-01-01T00:01:00Z","lease":{"leaseId":"123e4567-e89b-42d3-a456-426614174001","fencingToken":1},"payloadRef":"{{payloadRef}}"}""";

    private sealed class HostFixture(string path, ServiceProvider services, Broker broker, CaptureLogger log) : IAsyncDisposable
    {
        public Broker Broker => broker;
        public CaptureLogger Log => log;
        public static async Task<HostFixture> CreateAsync()
        {
            var path = Path.Combine(Path.GetTempPath(), $"oc-host-poison-{Guid.NewGuid():N}.db");
            var broker = new Broker();
            var log = new CaptureLogger();
            var services = new ServiceCollection();
            services.AddDbContext<OpenConnectDbContext>(options => options.UseSqlite($"Data Source={path};Pooling=False"));
            services.AddOpenConnectEntityFramework<OpenConnectDbContext>();
            services.AddSingleton(new NatsPublisher(broker));
            services.AddSingleton<IOptions<OutboxWorkerOptions>>(Options.Create(new OutboxWorkerOptions { MaxAttempts = 2, Subject = "configured.subject" }));
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
        public async ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
        {
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
