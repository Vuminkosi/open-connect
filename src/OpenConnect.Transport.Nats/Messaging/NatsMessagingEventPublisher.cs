using System.Text.Json;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Transport.Nats.Messaging;

/// <summary>
/// Publishes validated, ciphertext-only messaging event envelopes to NATS with bounded event-ID
/// deduplication and fail-closed subject/payload inspection.
/// </summary>
/// <remarks>
/// Deduplication is scoped to tenant and event id. The JetStream <c>Nats-Msg-Id</c> is
/// <c>{tenantId}:{eventId}</c>, identical to the in-process key. A duplicate dispatch that overlaps an
/// in-flight publish waits for it and returns <see langword="false"/> only if that publish succeeded;
/// if it failed, the waiting caller performs its own publish attempt. Upgrading changes the broker
/// dedup identity, so events published by an older version within the stream duplicate window are not
/// recognised as duplicates of the same event.
/// </remarks>
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
    private readonly Dictionary<string, TaskCompletionSource<bool>> _inFlight = new(StringComparer.Ordinal);

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

        var messageId = BuildMessageId(envelope);

        while (true)
        {
            Task<bool>? inFlight = null;
            TaskCompletionSource<bool>? owned = null;

            lock (_dedupLock)
            {
                if (_seenEventIds.Contains(messageId))
                    return false;

                if (_inFlight.TryGetValue(messageId, out var existing))
                {
                    inFlight = existing.Task;
                }
                else
                {
                    owned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _inFlight[messageId] = owned;
                }
            }

            if (owned is null)
            {
                // A concurrent duplicate waits for the first attempt. It reports "already published"
                // only after that attempt succeeded; otherwise it retries the publish itself.
                if (await inFlight!.WaitAsync(cancellationToken).ConfigureAwait(false))
                    return false;
                continue;
            }

            try
            {
                await _publisher.PublishAsync(subject, bytes, messageId, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                CompleteInFlight(messageId, owned, published: false);
                throw;
            }

            CompleteInFlight(messageId, owned, published: true);
            return true;
        }
    }

    private void CompleteInFlight(string messageId, TaskCompletionSource<bool> attempt, bool published)
    {
        lock (_dedupLock)
        {
            _inFlight.Remove(messageId);

            if (published && _seenEventIds.Add(messageId))
            {
                _seenOrder.Enqueue(messageId);
                while (_seenOrder.Count > _dedupCapacity)
                {
                    var oldest = _seenOrder.Dequeue();
                    _seenEventIds.Remove(oldest);
                }
            }
        }

        attempt.TrySetResult(published);
    }

    /// <summary>
    /// Builds the tenant-scoped JetStream <c>Nats-Msg-Id</c> and in-process deduplication key:
    /// <c>{tenantId}:{eventId}</c>. JetStream deduplicates per stream on this header regardless of
    /// subject, so the tenant prefix prevents one tenant's event from suppressing another tenant's event
    /// that reuses the same <see cref="MessagingEventEnvelope.EventId"/>. The value is deterministic,
    /// so retries of the same tenant event reuse the same id. It contains only ASCII letters, digits,
    /// '-', '_' and ':' and is bounded by the tenant id length plus <see cref="MessagingNatsSubjects.MaxTokenLength"/>.
    /// </summary>
    private static string BuildMessageId(MessagingEventEnvelope envelope)
        => $"{envelope.TenantId.Value}:{envelope.EventId}";

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
