using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using OpenConnect.Navigator.Events;

namespace OpenConnect.Navigator.AspNetCore.Outbox;

/// <summary>Persists a committed Navigator event through the transaction-owning host's outbox seam.</summary>
public interface INavigatorOutboxWriter
{
    ValueTask WriteAsync(string topic, string messageId, string payload, CancellationToken cancellationToken = default);
}

/// <summary>Small adapter that serializes only the transport-safe committed event DTO.</summary>
public sealed class NavigatorOutboxWriter(INavigatorOutboxWriter writer)
{
    private static readonly JsonSerializerOptions EnvelopeOptions = new(JsonSerializerDefaults.Web);

    public ValueTask WriteAsync(CommittedNavigatorEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.EventId) || string.IsNullOrWhiteSpace(value.Type))
            throw new ArgumentException("A committed Navigator event requires an id and type.", nameof(value));

        // A deterministic ID makes a retry of the same event idempotent at the outbox boundary.
        var messageId = Guid.TryParse(value.EventId, out var eventGuid) && eventGuid != Guid.Empty
            ? eventGuid
            : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(value.EventId)).AsSpan(0, 16));
        var issuedAt = DateTimeOffset.UtcNow;
        var envelope = new
        {
            schemaVersion = "openconnect.v1",
            messageId,
            taskId = value.SessionId,
            kind = $"navigator.{value.Type}",
            issuedAt,
            expiresAt = issuedAt.AddHours(1),
            // Temporary valid relay lease fields; durable outbox lease fencing is not wired here.
            lease = new { leaseId = Guid.NewGuid(), fencingToken = 1L },
            // The payload is resolved from the authorized session event read; broker bodies contain metadata only.
            payloadRef = $"navigator/{value.SessionId}/{value.EventId}",
            correlationId = value.CorrelationId
        };

        return writer.WriteAsync(envelope.kind, messageId.ToString("D"),
            JsonSerializer.Serialize(envelope, EnvelopeOptions), cancellationToken);
    }
}
