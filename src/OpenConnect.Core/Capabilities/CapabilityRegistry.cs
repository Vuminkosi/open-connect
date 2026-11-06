using OpenConnect.Abstractions;

namespace OpenConnect.Core.Capabilities;

/// <summary>Thread-safe in-memory registry of capabilities with expiring health leases.</summary>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<CapabilityId, Registration> _registrations = new();
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    public CapabilityRegistry(TimeSpan? ttl = null, TimeProvider? timeProvider = null)
    {
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        if (_ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Capability TTL must be positive.");

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CapabilityDescriptor? Get(CapabilityId capabilityId)
    {
        lock (_gate)
        {
            RemoveExpired();
            return _registrations.TryGetValue(capabilityId, out var registration) && !registration.Stale
                ? registration.Descriptor
                : null;
        }
    }

    public IReadOnlyList<CapabilityDescriptor> GetReady(CapabilityKind kind)
    {
        lock (_gate)
        {
            RemoveExpired();
            return _registrations.Values
                .Where(registration => !registration.Stale && registration.Descriptor.Kind == kind && registration.Descriptor.State == DetectorState.Ready)
                .Select(registration => registration.Descriptor)
                .ToArray();
        }
    }

    public void Register(CapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.Id.Value))
            throw new ArgumentException("Capability id must not be empty.", nameof(descriptor));
        if (!Enum.IsDefined(descriptor.Kind))
            throw new ArgumentException("Capability kind is invalid.", nameof(descriptor));
        if (!Enum.IsDefined(descriptor.State))
            throw new ArgumentException("Capability health state is invalid.", nameof(descriptor));

        lock (_gate)
            _registrations[descriptor.Id] = new Registration(descriptor, _timeProvider.GetUtcNow() + _ttl);
    }

    public void MarkStale(CapabilityId capabilityId)
    {
        lock (_gate)
        {
            if (_registrations.TryGetValue(capabilityId, out var registration))
                registration.Stale = true;
        }
    }

    private void RemoveExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var id in _registrations.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _registrations.Remove(id);
    }

    private sealed class Registration(CapabilityDescriptor descriptor, DateTimeOffset expiresAt)
    {
        public CapabilityDescriptor Descriptor { get; } = descriptor;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Stale { get; set; }
    }
}
