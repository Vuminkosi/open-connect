namespace OpenConnect.Transport.Nats;

public interface IJetStreamPublisher
{
    ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken);
}

/// <summary>Publishes validated metadata envelopes using JetStream message-id deduplication.</summary>
public sealed class NatsPublisher(IJetStreamPublisher broker, int maxMessageBytes = 64 * 1024)
{
    public async ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> envelopeJson, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (envelopeJson.Length > maxMessageBytes) throw new ArgumentOutOfRangeException(nameof(envelopeJson), "Envelope exceeds the configured size limit.");
        var envelope = WorkerEnvelope.Parse(envelopeJson.Span);
        await broker.PublishAsync(subject, envelopeJson, envelope.MessageId.ToString("D"), cancellationToken).ConfigureAwait(false);
    }
}
