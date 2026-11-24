using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using OpenConnect.Core.Capabilities;
using Xunit;

namespace OpenConnect.Core.Tests.Capabilities;

public sealed class WorkerCapabilityExpiryTests
{
    private static readonly OpenConnectTenantFixture Ids = new();

    private sealed class OpenConnectTenantFixture
    {
        public TenantId Tenant { get; } = new(Ulid.NewUlid());
        public PrincipalId Principal { get; } = new(Ulid.NewUlid());
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static ServiceCollection RegisterAllWorkers(TimeProvider? clock = null)
    {
        var services = new ServiceCollection();
        if (clock is not null)
            services.AddSingleton(clock);
        services.AddOpenConnect();
        services.AddOpenConnectSecurityWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectClassificationWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectTranslationWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectInferenceWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectTranscriptionWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectRealtimeVoiceWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectKokoroWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectMmsTtsWorker(o => { o.Engine = WorkerEngine.Fake; o.Environment = "Test"; });
        services.AddOpenConnectPlaywrightWorker(o => o.Enabled = true);
        services.AddOpenConnectOperationalAgents(o => o.HostingMode = WorkerHostingMode.Embedded);
        services.AddOpenConnectOutbox(o => o.HostingMode = WorkerHostingMode.Embedded);
        return services;
    }

    private static WorkerCapabilityDescriptor Descriptor(OpenConnectWorkerKind kind, DateTimeOffset expiresAt, WorkerReadiness readiness = WorkerReadiness.Ready) =>
        new(kind, "openconnect.worker.v1", "local-worker", "endpoint", readiness,
            new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), null, null, expiresAt);

    [Fact]
    public void RegisteredWorkersStayDiscoverableAfterOneHour()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var sp = RegisterAllWorkers(clock).BuildServiceProvider();
        var registry = sp.GetRequiredService<IWorkerCapabilityRegistry>();
        var visible = sp.GetRequiredService<WorkerCapabilityRegistry>();
        var readyBefore = registry.GetReady().Select(d => d.Kind).ToArray();
        Assert.NotEmpty(readyBefore);
        Assert.All(registry.GetAll(), d => Assert.Equal(DateTimeOffset.MaxValue, d.ExpiresAt));

        clock.Advance(TimeSpan.FromMinutes(61));

        Assert.Equal(11, registry.GetAll().Count);
        Assert.All(Enum.GetValues<OpenConnectWorkerKind>(), kind => Assert.NotNull(registry.Get(kind)));
        Assert.Equal(readyBefore, registry.GetReady().Select(d => d.Kind).ToArray());
        var visibleKinds = visible.GetVisibleCapabilities(Ids.Tenant, Ids.Principal, allowExternalEgress: true).Select(d => d.Kind).ToArray();
        Assert.Equal(readyBefore, visibleKinds);
    }

    [Fact]
    public void UpdateReadinessKeepsNonExpiringDescriptorDiscoverable()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var registry = new WorkerCapabilityRegistry(clock);
        registry.Register(Descriptor(OpenConnectWorkerKind.Kokoro, DateTimeOffset.MaxValue, WorkerReadiness.Loading));

        registry.UpdateReadiness(OpenConnectWorkerKind.Kokoro, WorkerReadiness.Ready);
        clock.Advance(TimeSpan.FromHours(3));

        var found = registry.Get(OpenConnectWorkerKind.Kokoro);
        Assert.NotNull(found);
        Assert.Equal(DateTimeOffset.MaxValue, found.ExpiresAt);
        Assert.Single(registry.GetReady());
    }

    [Fact]
    public void FiniteExpiryStillExpiresAndMirrorIsMarkedStale()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var mirror = new CapabilityRegistry(timeProvider: clock);
        var registry = new WorkerCapabilityRegistry(clock, mirror);
        registry.Register(Descriptor(OpenConnectWorkerKind.Security, clock.GetUtcNow().AddMinutes(30)));
        var mirrorId = new CapabilityId("worker.security");
        Assert.NotNull(mirror.Get(mirrorId));

        clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Null(registry.Get(OpenConnectWorkerKind.Security));
        Assert.Empty(registry.GetAll());
        Assert.Null(mirror.Get(mirrorId));
    }

    [Fact]
    public void UpdateReadinessDoesNotShortenFiniteExpiry()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var registry = new WorkerCapabilityRegistry(clock);
        registry.Register(Descriptor(OpenConnectWorkerKind.Nllb, clock.GetUtcNow().AddHours(1)));

        registry.UpdateReadiness(OpenConnectWorkerKind.Nllb, WorkerReadiness.Ready);
        clock.Advance(TimeSpan.FromMinutes(59));

        Assert.NotNull(registry.Get(OpenConnectWorkerKind.Nllb));
    }

    [Fact]
    public void MirrorStaysVisibleBeyondItsDefaultLeaseWhileWorkerIsLive()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var sp = RegisterAllWorkers(clock).BuildServiceProvider();
        var mirror = sp.GetRequiredService<ICapabilityRegistry>();
        _ = sp.GetRequiredService<IWorkerCapabilityRegistry>();

        clock.Advance(TimeSpan.FromMinutes(61));

        Assert.All(Enum.GetValues<OpenConnectWorkerKind>(), kind =>
            Assert.NotNull(mirror.Get(new CapabilityId($"worker.{kind.ToString().ToLowerInvariant()}"))));
    }

    [Fact]
    public void DiProvidedTimeProviderIsHonoured()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddOpenConnect();
        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<WorkerCapabilityRegistry>();
        registry.Register(Descriptor(OpenConnectWorkerKind.Outbox, clock.GetUtcNow().AddMinutes(5)));
        Assert.NotNull(registry.Get(OpenConnectWorkerKind.Outbox));

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(registry.Get(OpenConnectWorkerKind.Outbox));
    }

    [Fact]
    public void DiProvidedTimeProviderIsHonouredForWorkerExtensionRegistrations()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var sp = RegisterAllWorkers(clock).BuildServiceProvider();
        var registry = sp.GetRequiredService<WorkerCapabilityRegistry>();
        registry.Register(Descriptor(OpenConnectWorkerKind.Outbox, clock.GetUtcNow().AddMinutes(5)));

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(registry.Get(OpenConnectWorkerKind.Outbox));
        Assert.NotNull(registry.Get(OpenConnectWorkerKind.Kokoro));
    }
}
