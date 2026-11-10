using System.Buffers;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace OpenConnect.WorkerHosts.Outbox;

/// <summary>
/// JetStream publisher with message-id deduplication and PUBACK gating: the awaited
/// publish only returns after the server acknowledges persistence, and
/// <see cref="Transport.Nats.IJetStreamPublisher.PublishAsync"/> callers mark entries
/// delivered only after this returns. Transport-level retries are bounded by
/// <see cref="NatsJSPubOpts"/> defaults; entry retry/backoff stays in the store.
/// </summary>
public sealed class NatsJetStreamPublisher : Transport.Nats.IJetStreamPublisher, IAsyncDisposable
{
    private readonly NatsConnection _connection;
    private readonly NatsJSContext _jetStream;
    private readonly RawBytesSerializer _serializer = new();
    private bool _connected;

    /// <summary>Creates a publisher. Connects lazily on <see cref="ConnectAsync"/>.</summary>
    public NatsJetStreamPublisher(string url, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _connection = new NatsConnection(NatsOpts.Default with { Url = url, Name = name });
        _jetStream = new NatsJSContext(_connection);
    }

    /// <summary>Connects to NATS. Failure throws, failing host startup fail-closed.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connection.ConnectAsync().ConfigureAwait(false);
        _connected = true;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(string subject, ReadOnlyMemory<byte> payload, string messageId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (!_connected) await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var headers = new NatsHeaders();
        headers.Add("Nats-Msg-Id", messageId);
        PubAckResponse ack = await _jetStream.PublishAsync(
            subject,
            payload.ToArray(),
            serializer: _serializer,
            opts: null,
            headers: headers,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ack.EnsureSuccess();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private sealed class RawBytesSerializer : INatsSerialize<byte[]>
    {
        public void Serialize(IBufferWriter<byte> writer, byte[] value) => writer.Write(value);
    }
}
