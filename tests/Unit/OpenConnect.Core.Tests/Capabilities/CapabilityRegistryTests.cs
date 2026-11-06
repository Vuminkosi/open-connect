using OpenConnect.Abstractions;
using OpenConnect.Core.Capabilities;
using Xunit;

namespace OpenConnect.Core.Tests.Capabilities;

public sealed class CapabilityRegistryTests
{
    [Fact]
    public void RegisterAndGetReadyReturnsOnlyReadyMatchingCapabilities()
    {
        var registry = new CapabilityRegistry();
        var ready = Descriptor("model-a", CapabilityKind.Model, DetectorState.Ready);
        registry.Register(ready);
        registry.Register(Descriptor("model-b", CapabilityKind.Model, DetectorState.Degraded));
        registry.Register(Descriptor("tool-a", CapabilityKind.Tool, DetectorState.Ready));

        Assert.Equal(ready, registry.Get(new CapabilityId("model-a")));
        Assert.Equal([ready], registry.GetReady(CapabilityKind.Model));
    }

    [Fact]
    public void RegisterRefreshesExistingCapabilityLeaseAndHealth()
    {
        var clock = new FakeTimeProvider();
        var registry = new CapabilityRegistry(TimeSpan.FromSeconds(10), clock);
        var id = new CapabilityId("model-a");
        registry.Register(Descriptor("model-a", CapabilityKind.Model, DetectorState.Loading));
        clock.Advance(TimeSpan.FromSeconds(9));
        var updated = Descriptor("model-a", CapabilityKind.Model, DetectorState.Ready);
        registry.Register(updated);
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(updated, registry.Get(id));
    }

    [Fact]
    public void ExpiredAndExplicitlyStaleCapabilitiesAreReportedUnavailable()
    {
        var clock = new FakeTimeProvider();
        var registry = new CapabilityRegistry(TimeSpan.FromSeconds(2), clock);
        registry.Register(Descriptor("stale", CapabilityKind.Model, DetectorState.Ready));
        registry.Register(Descriptor("expired", CapabilityKind.Model, DetectorState.Ready));
        registry.MarkStale(new CapabilityId("stale"));
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Null(registry.Get(new CapabilityId("stale")));
        Assert.Null(registry.Get(new CapabilityId("expired")));
        Assert.Empty(registry.GetReady(CapabilityKind.Model));
    }

    [Fact]
    public void RejectsInvalidCapabilityClaims()
    {
        var registry = new CapabilityRegistry();
        Assert.Throws<ArgumentException>(() => registry.Register(Descriptor(" ", CapabilityKind.Model, DetectorState.Ready)));
        Assert.Throws<ArgumentException>(() => registry.Register(new CapabilityDescriptor(new CapabilityId("bad-kind"), (CapabilityKind)999, DetectorState.Ready)));
        Assert.Throws<ArgumentException>(() => registry.Register(new CapabilityDescriptor(new CapabilityId("bad-health"), CapabilityKind.Model, (DetectorState)999)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CapabilityRegistry(TimeSpan.Zero));
    }

    private static CapabilityDescriptor Descriptor(string id, CapabilityKind kind, DetectorState state) =>
        new(new CapabilityId(id), kind, state);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
