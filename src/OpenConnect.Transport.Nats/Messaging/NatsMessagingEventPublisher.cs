using System.Text.Json;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Transport.Nats.Messaging;

/// <summary>
/// Publishes validated, ciphertext-only messaging event envelopes to NATS with bounded event-ID
/// deduplication and fail-closed subject/payload inspection.
/// </summary>
public sealed class NatsMessagingEventPublisher : IMessagingEventDispatcher
{
    private static readonly HashSet<string> ForbiddenPlaintextProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "plaintext",
        "body",
        "text",
        "messageText",
        "prompt",
        "accessToken",
        "refreshToken",
        "privateKey",
        "pushToken",
        "secret"
    };

    private readonly IJetStreamPublisher _publisher;
    private readonly int _maxEventBytes;
    private readonly int _dedupCapacity;
    private readonly string? _liveGatewayId;
    private readonly object _dedupLock = new();
    private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();

    public NatsMessagingEventPublisher(
        IJetStreamPublisher publisher,
        int maxEventBytes = 64 * 1024,
        int dedupCapacity = 4096,
        string? liveGatewayId = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        if (maxEventBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEventBytes), "Maximum event size must be positive.");
        if (dedupCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(dedupCapacity), "Deduplication capacity must be positive.");
        if (liveGatewayId is not null)
            MessagingNatsSubjects.ValidateOpaqueToken(liveGatewayId, nameof(liveGatewayId));

        _publisher = publisher;
        _maxEventBytes = maxEventBytes;
        _dedupCapacity = dedupCapacity;
        _liveGatewayId = liveGatewayId;
    }

    public async ValueTask<bool> DispatchAsync(
        MessagingEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelope(envelope);

        string subject;
        if (envelope.ConversationId.HasValue)
        {
            subject = MessagingNatsSubjects.DurableConversation(envelope.TenantId, envelope.ConversationId.Value);
        }
        else if (_liveGatewayId is not null)
        {
            subject = MessagingNatsSubjects.LiveGateway(envelope.TenantId, _liveGatewayId);
        }
        else
        {
            throw new ArgumentException(
                "Envelope without a ConversationId requires a configured liveGatewayId for routing.",
                nameof(envelope));
        }

        return await PublishCoreAsync(subject, envelope, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> PublishLiveAsync(
        MessagingEventEnvelope envelope,
        string gatewayId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelope(envelope);
        var subject = MessagingNatsSubjects.LiveGateway(envelope.TenantId, gatewayId);
        return PublishCoreAsync(subject, envelope, cancellationToken);
    }

    private async ValueTask<bool> PublishCoreAsync(
        string subject,
        MessagingEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        MessagingNatsSubjects.ValidateSubject(subject);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        if (bytes.Length > _maxEventBytes)
            throw new ArgumentOutOfRangeException(nameof(envelope), $"Serialized event exceeds maximum size of {_maxEventBytes} bytes.");

        var dedupKey = $"{envelope.TenantId.Value}:{envelope.EventId}";
        if (!TryMarkSeen(dedupKey))
            return false;

        try
        {
            await _publisher.PublishAsync(subject, bytes, envelope.EventId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            UnmarkSeen(dedupKey);
            throw;
        }
    }

    private bool TryMarkSeen(string dedupKey)
    {
        lock (_dedupLock)
        {
            if (!_seenEventIds.Add(dedupKey))
                return false;

            _seenOrder.Enqueue(dedupKey);
            while (_seenOrder.Count > _dedupCapacity)
            {
                var oldest = _seenOrder.Dequeue();
                _seenEventIds.Remove(oldest);
            }

            return true;
        }
    }

    private void UnmarkSeen(string dedupKey)
    {
        lock (_dedupLock)
        {
            _seenEventIds.Remove(dedupKey);
        }
    }

    private static void ValidateEnvelope(MessagingEventEnvelope envelope)
    {
        if (envelope.SchemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(envelope), "SchemaVersion must be at least 1.");

        MessagingNatsSubjects.ValidateOpaqueToken(envelope.EventId, nameof(envelope.EventId));

        if (string.IsNullOrWhiteSpace(envelope.Type) ||
            !envelope.Type.StartsWith("messaging.", StringComparison.Ordinal))
        {
            throw new ArgumentException("Messaging event type must start with 'messaging.'.", nameof(envelope));
        }

        if (envelope.TenantId.Value == default)
            throw new ArgumentException("TenantId must not be empty.", nameof(envelope));

        if (string.IsNullOrWhiteSpace(envelope.PayloadJson))
            throw new ArgumentException("PayloadJson must not be null or empty.", nameof(envelope));

        using var doc = JsonDocument.Parse(envelope.PayloadJson);
        RejectForbiddenPlaintextFields(doc.RootElement);
    }

    private static void RejectForbiddenPlaintextFields(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (ForbiddenPlaintextProperties.Contains(prop.Name))
                        throw new JsonException($"Forbidden plaintext or secret field '{prop.Name}' is not permitted in NATS messaging events.");
                    RejectForbiddenPlaintextFields(prop.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    RejectForbiddenPlaintextFields(item);
                }
                break;
        }
    }
}
