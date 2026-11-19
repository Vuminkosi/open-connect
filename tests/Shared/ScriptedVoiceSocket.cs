using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace OpenConnect.Testing;

// In-memory frames only: no socket, provider, audio, or network service is opened.
internal sealed class ScriptedVoiceSocket : WebSocket
{
    private readonly Channel<string?> _frames = Channel.CreateUnbounded<string?>();
    private readonly SemaphoreSlim _sent = new(0);
    private bool _failOnEnd;
    private WebSocketState _state = WebSocketState.Open;
    public ConcurrentQueue<string> Sent { get; } = new();
    public void Enqueue(string json) => _frames.Writer.TryWrite(json);
    public void Fail() { _failOnEnd = true; End(); }
    public void End() => _frames.Writer.TryWrite(null);
    public async Task WaitForSendAsync()
    {
        if (!await _sent.WaitAsync(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The relay did not forward the expected frame.");
    }
    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose() { _state = WebSocketState.Closed; _sent.Dispose(); }
    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    { _state = WebSocketState.Closed; return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var frame = await _frames.Reader.ReadAsync(cancellationToken);
        if (frame is null)
        {
            if (_failOnEnd)
                throw new WebSocketException("Synthetic provider close.");
            return new(0, WebSocketMessageType.Close, true);
        }
        var bytes = Encoding.UTF8.GetBytes(frame);
        bytes.CopyTo(buffer.AsSpan());
        return new(bytes.Length, WebSocketMessageType.Text, true);
    }
    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Enqueue(Encoding.UTF8.GetString(buffer));
        _sent.Release();
        return Task.CompletedTask;
    }
}
