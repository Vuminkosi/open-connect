using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows.Declarative.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// MCP live round-trip lab (architect MCP spike, slice AG-IMPL-007).
/// Proves a minimal live <see cref="DefaultMcpToolHandler.InvokeToolAsync"/> round trip against a
/// local in-process MCP server over real loopback HTTP (no external network), plus a fail-closed
/// probe for an unreachable server. No mocks: both ends use the real SDK stack
/// (ModelContextProtocol.Core 2.2.0, transitively referenced).
/// </summary>
public sealed class McpLiveRoundTripLabTests
{
    private static readonly TimeSpan HappyPathTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan UnreachableClientTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task InvokeToolAsync_returns_deterministic_add_result_over_loopback_http()
    {
        using var cts = new CancellationTokenSource(HappyPathTimeout);
        await using var lab = await InProcMcpLab.StartAsync("add", cts.Token);

        await using var handler = new DefaultMcpToolHandler((_, _) => Task.FromResult<HttpClient?>(lab.HttpClient));
        var result = await handler.InvokeToolAsync(
            lab.ServerUrl,
            "lab",
            "add",
            new Dictionary<string, object?> { ["a"] = 40, ["b"] = 2 },
            new Dictionary<string, string>(),
            null,
            cts.Token);

        Assert.NotNull(result);
        Assert.NotNull(result.Outputs);
        var text = Assert.Single(result.Outputs);
        var textContent = Assert.IsType<TextContent>(text);
        Assert.Equal("42", textContent.Text.Trim());
    }

    [Fact]
    public async Task InvokeToolAsync_unreachable_server_throws_without_hanging()
    {
        // Fail-closed: no listener on this loopback port, so the handler must surface a
        // transport failure promptly instead of returning success or hanging.
        int closedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var started = DateTimeOffset.UtcNow;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var httpClient = new HttpClient { Timeout = UnreachableClientTimeout };
        await using var handler = new DefaultMcpToolHandler((_, _) => Task.FromResult<HttpClient?>(httpClient));

        var exception = await Record.ExceptionAsync(() => handler.InvokeToolAsync(
            $"http://127.0.0.1:{closedPort}/mcp",
            "lab",
            "add",
            new Dictionary<string, object?> { ["a"] = 1, ["b"] = 1 },
            new Dictionary<string, string>(),
            null,
            cts.Token));

        Assert.NotNull(exception);
        Assert.True(
            DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(12),
            $"Unreachable-server probe took too long; fail-closed must be prompt. Exception: {exception.GetType().FullName}");
    }

    /// <summary>
    /// Minimal in-process MCP lab: a real <see cref="McpServer"/> exposing one deterministic tool,
    /// fronted by a raw <see cref="TcpListener"/> loopback HTTP bridge driving the real
    /// <see cref="StreamableHttpServerTransport"/>. No ASP.NET Core hosting package is referenced,
    /// so the stateless streamable-HTTP envelope (headers, framing) is hand-rolled while every
    /// protocol byte is produced/consumed by the real SDK on both ends.
    /// </summary>
    private sealed class InProcMcpLab : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly StreamableHttpServerTransport _transport;
        private readonly McpServer _server;
        private readonly CancellationTokenSource _cts = new();
        private Task _acceptTask = Task.CompletedTask;
        private int _disposed;

        public string ServerUrl { get; }

        public HttpClient HttpClient { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

        private InProcMcpLab(
            TcpListener listener,
            StreamableHttpServerTransport transport,
            McpServer server,
            string serverUrl)
        {
            _listener = listener;
            _transport = transport;
            _server = server;
            ServerUrl = serverUrl;
        }

        public static async Task<InProcMcpLab> StartAsync(string toolName, CancellationToken ct)
        {
            var transport = new StreamableHttpServerTransport(NullLoggerFactory.Instance)
            {
                Stateless = true,
            };

            var tools = new McpServerPrimitiveCollection<McpServerTool>();
            tools.Add(McpServerTool.Create(
                (Func<int, int, int>)((a, b) => a + b),
                new McpServerToolCreateOptions { Name = toolName, Description = "Adds two integers." }));

            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "mcp-live-roundtrip-lab", Version = "1.0.0" },
                Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
                ToolCollection = tools,
            };

            var server = McpServer.Create(transport, options, NullLoggerFactory.Instance, NullServiceProvider.Instance);

            // Pump server-side dispatch (initialize handshake, tools/list, tools/call).
            var pump = server.RunAsync(CancellationToken.None);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var lab = new InProcMcpLab(
                listener,
                transport,
                server,
                $"http://127.0.0.1:{port}/mcp");

            lab.StartAcceptLoop();
            _ = pump.ContinueWith(
                t => { if (t.IsFaulted) Console.Error.WriteLine(t.Exception); },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            await Task.CompletedTask;
            return lab;
        }

        private void StartAcceptLoop()
        {
            _acceptTask = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        break;
                    }

                    _ = Task.Run(() => HandleConnectionAsync(client, _cts.Token));
                }
            });
        }

        private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                await HandleConnectionCoreAsync(client, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Lab-only bridge: surface the failure as a 500 so the client observes a
                // protocol-level error instead of a bare connection reset.
                try
                {
                    using var stream = client.GetStream();
                    var payload = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.GetType().Name}\"}}");
                    await WriteResponseAsync(stream, "500 Internal Server Error", "application/json", payload, CancellationToken.None);
                }
                catch
                {
                    // Best effort: the client already went away.
                }
            }
        }

        private async Task HandleConnectionCoreAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                string headers;
                try
                {
                    headers = await ReadHeadersAsync(stream, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
                }

                var headerLines = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (headerLines.Length == 0)
                {
                    return;
                }

                var requestLine = headerLines[0].Split(' ');
                if (requestLine.Length < 2)
                {
                    return;
                }

                var method = requestLine[0];
                var headerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < headerLines.Length; i++)
                {
                    var colon = headerLines[i].IndexOf(':');
                    if (colon > 0)
                    {
                        headerMap[headerLines[i].Substring(0, colon).Trim()] = headerLines[i].Substring(colon + 1).Trim();
                    }
                }

                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleGetAsync(stream, ct);
                    return;
                }

                if (string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, "202 Accepted", "application/json", Array.Empty<byte>(), ct);
                    return;
                }

                if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, "405 Method Not Allowed", "application/json", Array.Empty<byte>(), ct);
                    return;
                }

                var contentLength = 0;
                if (headerMap.TryGetValue("Content-Length", out var lengthValue))
                {
                    int.TryParse(lengthValue, out contentLength);
                }

                if (headerMap.TryGetValue("Expect", out var expect) &&
                    expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    await stream.WriteAsync(interim, ct);
                }

                var body = await ReadBodyAsync(stream, headerMap, ct);
                JsonRpcMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<JsonRpcMessage>(
                        body,
                        McpJsonUtilities.DefaultOptions);
                }
                catch (JsonException)
                {
                    var error = Encoding.UTF8.GetBytes("{\"error\":\"invalid JSON-RPC payload\"}");
                    await WriteResponseAsync(stream, "400 Bad Request", "application/json", error, ct);
                    return;
                }

                if (message is null)
                {
                    var error = Encoding.UTF8.GetBytes("{\"error\":\"empty JSON-RPC payload\"}");
                    await WriteResponseAsync(stream, "400 Bad Request", "application/json", error, ct);
                    return;
                }

                using var responseBuffer = new MemoryStream();
                var handled = await _transport.HandlePostRequestAsync(
                    message,
                    responseBuffer,
                    static _ => ValueTask.CompletedTask,
                    ct);

                if (!handled)
                {
                    await WriteResponseAsync(stream, "404 Not Found", "application/json", Array.Empty<byte>(), ct);
                    return;
                }

                var payload = responseBuffer.ToArray();
                var contentType = LooksLikeSse(payload) ? "text/event-stream" : "application/json";
                await WriteResponseAsync(stream, "200 OK", contentType, payload, ct);
            }
        }

        private async Task HandleGetAsync(NetworkStream stream, CancellationToken ct)
        {
            // Standalone SSE stream requested by HttpClientTransport (EnableStandaloneGetStream
            // defaults to true). Hold it open for the life of the lab; server-initiated traffic
            // is not needed for this lab.
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(header, ct);
                await _transport.HandleGetRequestAsync(stream, ct);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            HttpClient.Dispose();
            await _server.DisposeAsync().ConfigureAwait(false);
            await _transport.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }

        private static bool LooksLikeSse(byte[] payload)
        {
            var i = 0;
            while (i < payload.Length && (payload[i] == (byte)' ' || payload[i] == (byte)'\n' || payload[i] == (byte)'\r' || payload[i] == (byte)'\t'))
            {
                i++;
            }

            const string marker = "event:";
            if (payload.Length - i < marker.Length)
            {
                return false;
            }

            for (var j = 0; j < marker.Length; j++)
            {
                if (payload[i + j] != (byte)marker[j])
                {
                    return false;
                }
            }

            return true;
        }

        private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new List<byte>(512);
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one, ct);
                if (read == 0)
                {
                    break;
                }

                buffer.Add(one[0]);
                var n = buffer.Count;
                if (n >= 4 &&
                    buffer[n - 4] == (byte)'\r' && buffer[n - 3] == (byte)'\n' &&
                    buffer[n - 2] == (byte)'\r' && buffer[n - 1] == (byte)'\n')
                {
                    break;
                }

                if (n > 65536)
                {
                    throw new IOException("HTTP headers too large.");
                }
            }

            return Encoding.ASCII.GetString(buffer.ToArray());
        }

        private static async Task<byte[]> ReadBodyAsync(
            NetworkStream stream,
            Dictionary<string, string> headerMap,
            CancellationToken ct)
        {
            if (headerMap.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
                transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using var buffer = new MemoryStream();
                while (true)
                {
                    var sizeLine = await ReadLineAsync(stream, ct);
                    var separator = sizeLine.IndexOf(';');
                    var sizeText = (separator >= 0 ? sizeLine.Substring(0, separator) : sizeLine).Trim();
                    if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize))
                    {
                        throw new IOException($"Invalid chunk size: '{sizeText}'.");
                    }

                    if (chunkSize == 0)
                    {
                        // Consume optional trailers.
                        while (!string.IsNullOrEmpty(await ReadLineAsync(stream, ct)))
                        {
                        }

                        break;
                    }

                    var chunk = await ReadExactAsync(stream, chunkSize, ct);
                    buffer.Write(chunk, 0, chunk.Length);
                    // Consume the trailing CRLF after the chunk.
                    await ReadExactAsync(stream, 2, ct);
                }

                return buffer.ToArray();
            }

            var contentLength = 0;
            if (headerMap.TryGetValue("Content-Length", out var lengthValue))
            {
                int.TryParse(lengthValue, out contentLength);
            }

            return await ReadExactAsync(stream, contentLength, ct);
        }

        private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new List<byte>(64);
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one, ct);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of stream while reading a line.");
                }

                if (one[0] == (byte)'\n')
                {
                    break;
                }

                if (one[0] != (byte)'\r')
                {
                    buffer.Add(one[0]);
                }

                if (buffer.Count > 65536)
                {
                    throw new IOException("Line too large.");
                }
            }

            return Encoding.ASCII.GetString(buffer.ToArray());
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
        {
            var body = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(body.AsMemory(offset, count - offset), ct);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of request body.");
                }

                offset += read;
            }

            return body;
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            string status,
            string contentType,
            byte[] payload,
            CancellationToken ct)
        {
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, ct);
            if (payload.Length > 0)
            {
                await stream.WriteAsync(payload, ct);
            }

            await stream.FlushAsync(ct);
        }

        private sealed class NullServiceProvider : IServiceProvider
        {
            public static readonly NullServiceProvider Instance = new();

            public object? GetService(Type serviceType) => null;
        }
    }
}
