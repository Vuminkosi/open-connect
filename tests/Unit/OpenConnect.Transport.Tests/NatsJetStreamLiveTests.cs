using System.Text;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Xunit;
using Xunit.Abstractions;

namespace OpenConnect.Transport.Tests;

// Live-broker test (CF-AT-021 live scope). Skips when no broker is reachable;
// never fails on connection problems. Requires JetStream (broker: -js).
[Trait("Category", "NatsLive")]
public sealed class NatsJetStreamLiveTests(ITestOutputHelper output)
{
    private const string DefaultUrl = "nats://127.0.0.1:4222";

    private static string BrokerUrl =>
        Environment.GetEnvironmentVariable("OPENCONNECT_TEST_NATS_URL") is { Length: > 0 } value
            ? value
            : DefaultUrl;

    [Fact]
    public async Task JetStream_PublishSubscribeAck_RoundTrip()
    {
        var url = BrokerUrl;
        var opts = NatsOpts.Default with
        {
            Url = url,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            RequestTimeout = TimeSpan.FromSeconds(10),
            CommandTimeout = TimeSpan.FromSeconds(10),
        };
        await using var connection = new NatsConnection(opts);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (Exception exception)
        {
            output.WriteLine(
                $"SKIP live NATS: broker at {url} unavailable ({exception.GetType().Name}). " +
                "Start a disposable broker (e.g. docker run --rm -p 4222:4222 <nats-image> -js) " +
                "or set OPENCONNECT_TEST_NATS_URL.");
            return;
        }

        output.WriteLine($"Connected to live NATS at {url} (state: {connection.ConnectionState}).");

        var js = new NatsJSContext(connection);
        var suffix = Guid.NewGuid().ToString("N");
        var stream = $"oc-live-{suffix}";
        var subject = $"oc.live.{suffix}.events";
        var durable = "oc-live-durable";
        var messageId = Guid.NewGuid().ToString("D");
        var payload = Encoding.UTF8.GetBytes($"{{\"live\":\"roundtrip\",\"id\":\"{messageId}\"}}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = cts.Token;

        try
        {
            output.WriteLine($"Creating memory stream '{stream}' for subject '{subject}'.");
            var created = await js.CreateStreamAsync(
                new StreamConfig(stream, new[] { subject }) { Storage = StreamConfigStorage.Memory },
                cancellationToken);
            Assert.Equal(stream, created.Info.Config.Name);

            output.WriteLine($"Publishing 1 message with Nats-Msg-Id {messageId}.");
            var ack = await js.PublishAsync(
                subject,
                payload,
                NatsRawSerializer<byte[]>.Default,
                new NatsJSPubOpts { MsgId = messageId },
                headers: null,
                cancellationToken: cancellationToken);
            Assert.Null(ack.Error);
            Assert.Equal(stream, ack.Stream);
            Assert.False(ack.Duplicate);
            Assert.True(ack.Seq > 0, $"Expected stream sequence > 0, got {ack.Seq}.");
            output.WriteLine($"Publish acked: stream={ack.Stream} seq={ack.Seq} duplicate={ack.Duplicate}.");

            var consumer = await js.CreateOrUpdateConsumerAsync(
                stream,
                new ConsumerConfig(durable) { FilterSubject = subject, AckPolicy = ConsumerConfigAckPolicy.Explicit },
                cancellationToken);
            output.WriteLine($"Pull consumer '{durable}' ready; fetching 1 message.");

            INatsJSMsg<byte[]>? received = null;
            await foreach (var msg in consumer.FetchAsync(
                new NatsJSFetchOpts { MaxMsgs = 1, Expires = TimeSpan.FromSeconds(10) },
                NatsRawSerializer<byte[]>.Default,
                cancellationToken).ConfigureAwait(false))
            {
                received = msg;
                break;
            }

            var got = received ?? throw new Xunit.Sdk.XunitException("Expected 1 message from pull fetch, got none.");
            Assert.Equal(payload, got.Data);
            var metadata = got.Metadata ?? throw new Xunit.Sdk.XunitException("Expected JetStream metadata on fetched message.");
            Assert.Equal(stream, metadata.Stream);
            Assert.Equal(durable, metadata.Consumer);
            output.WriteLine(
                $"Received seq={metadata.Sequence} stream={metadata.Stream} " +
                $"consumer={metadata.Consumer} delivered={metadata.NumDelivered}.");

            await got.AckAsync(cancellationToken: cancellationToken);
            output.WriteLine("First ack completed.");
            try
            {
                await got.AckAsync(cancellationToken: cancellationToken);
                output.WriteLine("Second ack completed without error (idempotent).");
            }
            catch (Exception secondAck)
            {
                output.WriteLine($"Second ack raised {secondAck.GetType().Name} (tolerated as double-ack safe).");
            }
        }
        finally
        {
            try
            {
                await js.DeleteStreamAsync(stream, CancellationToken.None);
                output.WriteLine($"Deleted stream '{stream}'.");
            }
            catch (Exception cleanup)
            {
                output.WriteLine($"Stream cleanup for '{stream}' raised {cleanup.GetType().Name}: {cleanup.Message}");
            }
        }
    }
}
