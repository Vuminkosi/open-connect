using OpenConnect.Abstractions;

namespace OpenConnect.Core.Capabilities;

/// <summary>Canonical registry for the 11 OpenConnect worker capability descriptors.</summary>
public sealed class WorkerCapabilityRegistry : IWorkerCapabilityRegistry
{
    private static readonly IReadOnlyDictionary<OpenConnectWorkerKind, string> CanonicalImages =
        new Dictionary<OpenConnectWorkerKind, string>
        {
            [OpenConnectWorkerKind.Security] = "vuminkosimatibe/openconnect-worker-security",
            [OpenConnectWorkerKind.Classification] = "vuminkosimatibe/openconnect-worker-classification",
            [OpenConnectWorkerKind.Nllb] = "vuminkosimatibe/openconnect-worker-nllb",
            [OpenConnectWorkerKind.Inference] = "vuminkosimatibe/openconnect-worker-inference",
            [OpenConnectWorkerKind.Transcription] = "vuminkosimatibe/openconnect-worker-transcription",
            [OpenConnectWorkerKind.RealtimeVoice] = "vuminkosimatibe/openconnect-worker-realtime-voice",
            [OpenConnectWorkerKind.Kokoro] = "vuminkosimatibe/openconnect-worker-kokoro",
            [OpenConnectWorkerKind.MmsTts] = "vuminkosimatibe/openconnect-worker-mms-tts",
            [OpenConnectWorkerKind.NavigatorPlaywright] = "vuminkosimatibe/openconnect-worker-navigator-playwright",
            [OpenConnectWorkerKind.OperationalAgents] = "vuminkosimatibe/openconnect-worker-operational-agents",
            [OpenConnectWorkerKind.Outbox] = "vuminkosimatibe/openconnect-worker-outbox"
        };

    private readonly Dictionary<OpenConnectWorkerKind, WorkerCapabilityDescriptor> _descriptors = new();
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly ICapabilityRegistry? _capabilityRegistry;

    public WorkerCapabilityRegistry(TimeProvider? timeProvider = null, ICapabilityRegistry? capabilityRegistry = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capabilityRegistry = capabilityRegistry;
    }

    public static IReadOnlyDictionary<OpenConnectWorkerKind, string> CanonicalDockerImages => CanonicalImages;

    public static string GetCanonicalDockerImage(OpenConnectWorkerKind kind)
    {
        if (!CanonicalImages.TryGetValue(kind, out var image))
            throw new ArgumentOutOfRangeException(nameof(kind), $"Unknown worker kind '{kind}'.");
        return image;
    }

    public WorkerCapabilityDescriptor? Get(OpenConnectWorkerKind kind)
    {
        lock (_gate)
        {
            RemoveExpired();
            return _descriptors.GetValueOrDefault(kind);
        }
    }

    public IReadOnlyList<WorkerCapabilityDescriptor> GetAll()
    {
        lock (_gate)
        {
            RemoveExpired();
            return _descriptors.Values.OrderBy(d => d.Kind).ToArray();
        }
    }

    public IReadOnlyList<WorkerCapabilityDescriptor> GetReady()
    {
        lock (_gate)
        {
            RemoveExpired();
            return _descriptors.Values
                .Where(d => d.Readiness == WorkerReadiness.Ready)
                .OrderBy(d => d.Kind)
                .ToArray();
        }
    }

    public IReadOnlyList<WorkerCapabilityDescriptor> GetVisibleCapabilities(
        TenantId tenant,
        PrincipalId principal,
        bool allowExternalEgress = false)
    {
        if (tenant.Value == default || principal.Value == default)
            return Array.Empty<WorkerCapabilityDescriptor>();

        lock (_gate)
        {
            RemoveExpired();
            return _descriptors.Values
                .Where(d => d.Readiness == WorkerReadiness.Ready)
                .Where(d => allowExternalEgress || !string.Equals(d.ExecutionLocation, "remote", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Kind)
                .ToArray();
        }
    }

    public void Register(WorkerCapabilityDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!Enum.IsDefined(descriptor.Kind))
            throw new ArgumentException("Worker kind is invalid.", nameof(descriptor));
        if (!Enum.IsDefined(descriptor.Readiness))
            throw new ArgumentException("Worker readiness is invalid.", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.ContractVersion))
            throw new ArgumentException("Contract version is required.", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.EndpointOrHostId))
            throw new ArgumentException("Endpoint or host ID is required.", nameof(descriptor));

        lock (_gate)
        {
            _descriptors[descriptor.Kind] = descriptor;
        }

        if (_capabilityRegistry is not null)
        {
            var mappedKind = descriptor.Kind switch
            {
                OpenConnectWorkerKind.Security => CapabilityKind.Security,
                OpenConnectWorkerKind.Outbox => CapabilityKind.Transport,
                OpenConnectWorkerKind.NavigatorPlaywright or OpenConnectWorkerKind.OperationalAgents => CapabilityKind.Tool,
                _ => CapabilityKind.Model
            };
            var mappedState = descriptor.Readiness switch
            {
                WorkerReadiness.Ready => DetectorState.Ready,
                WorkerReadiness.Loading or WorkerReadiness.Provisioning => DetectorState.Loading,
                WorkerReadiness.Degraded => DetectorState.Degraded,
                WorkerReadiness.Disabled => DetectorState.Disabled,
                _ => DetectorState.Failed
            };
            var idValue = $"worker.{descriptor.Kind.ToString().ToLowerInvariant()}";
            _capabilityRegistry.Register(new CapabilityDescriptor(new CapabilityId(idValue), mappedKind, mappedState));
        }
    }

    public void UpdateReadiness(OpenConnectWorkerKind kind, WorkerReadiness readiness, string? modelId = null, string? modelRevision = null)
    {
        if (!Enum.IsDefined(readiness))
            throw new ArgumentException("Worker readiness is invalid.", nameof(readiness));

        WorkerCapabilityDescriptor? updated = null;
        lock (_gate)
        {
            if (_descriptors.TryGetValue(kind, out var existing))
            {
                updated = existing with
                {
                    Readiness = readiness,
                    ModelId = modelId ?? existing.ModelId,
                    ModelRevision = modelRevision ?? existing.ModelRevision,
                    ExpiresAt = _timeProvider.GetUtcNow().AddMinutes(10)
                };
                _descriptors[kind] = updated;
            }
        }

        if (updated is not null && _capabilityRegistry is not null)
            Register(updated);
    }

    private void RemoveExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var kind in _descriptors.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            _descriptors.Remove(kind);
    }
}
