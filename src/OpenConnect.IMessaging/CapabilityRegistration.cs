using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.IMessaging;

/// <summary>
/// Registers verified IMessaging worker capabilities into <see cref="ICapabilityRegistry"/>.
/// Strictly advertises only operationally verified in-app capabilities and never advertises
/// unverified or planned external channel providers (WhatsApp, Telegram, Discord).
/// </summary>
public sealed class CapabilityRegistration
{
    private static readonly string[] CoreMessagingCapabilities =
    [
        MessagingCapabilityIds.InAppSend,
        MessagingCapabilityIds.InAppReceive,
        MessagingCapabilityIds.InAppCall,
        MessagingCapabilityIds.E2EE,
        MessagingCapabilityIds.PushFcm,
        MessagingCapabilityIds.PushWeb,
        MessagingCapabilityIds.DeviceEnrollment
    ];

    private readonly ICapabilityRegistry _capabilityRegistry;
    private readonly IWorkerCapabilityRegistry? _workerCapabilityRegistry;

    public CapabilityRegistration(
        ICapabilityRegistry capabilityRegistry,
        IWorkerCapabilityRegistry? workerCapabilityRegistry = null)
    {
        _capabilityRegistry = capabilityRegistry ?? throw new ArgumentNullException(nameof(capabilityRegistry));
        _workerCapabilityRegistry = workerCapabilityRegistry;
    }

    /// <summary>
    /// Updates <see cref="ICapabilityRegistry"/> based on verified worker readiness.
    /// When the worker is unavailable or disabled, marks capabilities degraded/disabled (never ready).
    /// </summary>
    public IReadOnlyList<CapabilityDescriptor> ApplyReadiness(
        MessagingWorkerReadiness readiness,
        bool messagingEnabled = true,
        bool e2eeVerified = false)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        var registered = new List<CapabilityDescriptor>(CoreMessagingCapabilities.Length);
        if (!messagingEnabled)
        {
            foreach (var capId in CoreMessagingCapabilities)
            {
                var desc = new CapabilityDescriptor(new CapabilityId(capId), CapabilityKind.Transport, DetectorState.Disabled);
                _capabilityRegistry.Register(desc);
                registered.Add(desc);
            }
            return registered;
        }

        var verifiedSet = new HashSet<string>(
            readiness.VerifiedCapabilities.Where(WorkerResponseValidator.IsAllowedCapabilityId),
            StringComparer.Ordinal);

        if (e2eeVerified && readiness.IsReady)
            verifiedSet.Add(MessagingCapabilityIds.E2EE);
        else
            verifiedSet.Remove(MessagingCapabilityIds.E2EE);

        foreach (var capId in CoreMessagingCapabilities)
        {
            var isReady = readiness.IsReady && verifiedSet.Contains(capId);
            var state = isReady
                ? DetectorState.Ready
                : (readiness.IsReady ? DetectorState.Disabled : DetectorState.Degraded);

            var desc = new CapabilityDescriptor(new CapabilityId(capId), CapabilityKind.Transport, state);
            _capabilityRegistry.Register(desc);
            registered.Add(desc);
        }

        return registered;
    }

    /// <summary>Returns whether the underlying outbox worker (if registered) is ready in <see cref="IWorkerCapabilityRegistry"/>.</summary>
    public bool IsOutboxWorkerReady() =>
        _workerCapabilityRegistry?.Get(OpenConnectWorkerKind.Outbox)?.Readiness == WorkerReadiness.Ready;
}
