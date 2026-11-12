using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Client;

/// <summary>Optional realtime event transport implemented by the HTTP/WebSocket client.</summary>
public interface IRealtimeOpenConnectClient
{
    IAsyncEnumerable<EventRecord> SubscribeEventsWebSocketAsync(
        SessionId sessionId,
        long afterSequence = 0,
        CancellationToken cancellationToken = default);
}

public partial class OpenConnectClient : IRealtimeOpenConnectClient
{
    public async IAsyncEnumerable<EventRecord> SubscribeEventsWebSocketAsync(
        SessionId sessionId,
        long afterSequence = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));

        var baseAddress = _httpClient.BaseAddress
            ?? throw new InvalidOperationException("The OpenConnect client requires an HTTP base address for realtime events.");
        var builder = new UriBuilder(baseAddress)
        {
            Scheme = baseAddress.Scheme switch
            {
                "https" => "wss",
                "http" => "ws",
                _ => baseAddress.Scheme
            },
            Path = baseAddress.AbsolutePath.TrimEnd('/') + $"/sessions/{sessionId.Value}/events",
            Query = $"after={afterSequence.ToString(CultureInfo.InvariantCulture)}"
        };

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("openconnect.v1");
        foreach (var header in _options?.WebSocketHeaders ?? new Dictionary<string, string>())
        {
            socket.Options.SetRequestHeader(header.Key, header.Value);
        }
        foreach (var header in _httpClient.DefaultRequestHeaders)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            socket.Options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
        }

        await socket.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) yield break;
            if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage) continue;

            var record = JsonSerializer.Deserialize<EventRecord>(buffer.AsSpan(0, result.Count), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (record is not null)
                yield return record;
        }
    }
}
