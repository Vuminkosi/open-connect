using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// This is test infrastructure, not a worker or an alternate Todo implementation.
// Fixed exclusive listeners: a collision must fail, never reuse another app/database.
const string providerOrigin = "http://127.0.0.1:5188";
const string appOrigin = "http://127.0.0.1:5187";
const string tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const string workerToken = "synthetic-worker-not-a-real-credential";
const string todoToken = "synthetic-todo-not-a-real-credential";
const string sessionAckType = "session-ack";
var selfTest = args.Contains("--self-test");
var title = "check weighbridge calibration tomorrow " + Guid.NewGuid().ToString("N")[..8];
var raw = $"Create a toto called \"{title}\"";
var normalized = $"Create a Todo called \"{title}\"";
// Only ephemeral local guard entropy required by the existing app guard; no account credentials.
var guard = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
var state = new Evidence();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.Configuration.Sources.Clear();
builder.Configuration.AddInMemoryCollection();
builder.WebHost.UseUrls(providerOrigin);
var app = builder.Build();
app.UseWebSockets();
app.Use(async (context, next) =>
{
    if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress!) ||
        context.Request.Host.Value != "127.0.0.1:5188" ||
        (context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin != appOrigin))
    {
        context.Response.StatusCode = 403;
        return;
    }
    await next(context);
});
app.MapGet("/_fixture/config", () => new { app = appOrigin, provider = providerOrigin, title, raw, normalized, guard, todoToken, tenant });
app.MapGet("/_fixture/evidence", () => state);
using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
app.MapGet("/_fixture/ready", async () =>
{
    if (selfTest) return Results.Ok();
    try
    {
        using var response = await http.GetAsync(appOrigin);
        return response.IsSuccessStatusCode ? Results.Ok() : Results.StatusCode(503);
    }
    catch (HttpRequestException) { return Results.StatusCode(503); }
    catch (TaskCanceledException) { return Results.StatusCode(503); }
});
app.Map("/internal/v1/realtime", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest || context.Request.Headers.Authorization != "Bearer " + workerToken)
    {
        context.Response.StatusCode = 403;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[64 * 1024];
    var sent = false;
    using var control = new MemoryStream();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        // The gateway waits for a worker acknowledgement before relaying PCM.
        // The real worker forwards the provider's opaque first frame; this fixture owns its marker.
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = sessionAckType }), WebSocketMessageType.Text, true, deadline.Token);
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                if (result.Count == 0) continue;
                Interlocked.Add(ref state.PcmBytes, result.Count);
                if (!sent)
                {
                    sent = true;
                    state.FinalAt = Stopwatch.GetTimestamp();
                    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "transcript", text = raw, final = true }));
                    if (selfTest)
                    {
                        // Exercise the fixture self-test receiver, not the sample's production relay.
                        await socket.SendAsync(payload.AsMemory(0, 7), WebSocketMessageType.Text, false, deadline.Token);
                        await socket.SendAsync(payload.AsMemory(7), WebSocketMessageType.Text, true, deadline.Token);
                    }
                    else
                    {
                        await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
                    }
                }
            }
            else if (result.MessageType == WebSocketMessageType.Text)
            {
                if (control.Length + result.Count > 4096)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "control limit", deadline.Token);
                    return;
                }
                control.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                using var message = JsonDocument.Parse(control.ToArray());
                control.SetLength(0);
                var endStream = message.RootElement.ValueKind == JsonValueKind.Object &&
                    message.RootElement.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "endStream";
                if (!endStream) continue;
                // Keep the provider alive after the final to prove early admission separately from close fallback.
                await Task.Delay(200, deadline.Token);
                break;
            }
        }
        state.ClosedAt = Stopwatch.GetTimestamp();
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "synthetic stream ended", deadline.Token);
    }
    catch (JsonException)
    {
        state.Errors.Enqueue("invalid control JSON");
        await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "invalid control", CancellationToken.None);
    }
    catch (OperationCanceledException) { state.Errors.Enqueue("transcription timeout"); }
    catch (WebSocketException) { state.Errors.Enqueue("unexpected transcription disconnect"); }
});
app.MapPost("/v1/chat/completions", async (HttpContext context) =>
{
    if (context.Request.Headers.Authorization != "Bearer synthetic-local-key") return Results.StatusCode(403);
    using var document = await JsonDocument.ParseAsync(context.Request.Body);
    var root = document.RootElement;
    var messages = root.GetProperty("messages").EnumerateArray().ToArray();
    var users = messages.Where(m => m.GetProperty("role").GetString() == "user")
        .Select(m => m.GetProperty("content").Clone()).ToArray();
    state.ModelRequests.Enqueue(new { at = Stopwatch.GetTimestamp(), user = users });
    // Accept only this run's exact normalized request, never a canned success for arbitrary input.
    if (!users.Any(u => u.ValueKind == JsonValueKind.String && u.GetString() == normalized))
    {
        state.Errors.Enqueue("missing exact normalized user text");
        return Results.StatusCode(422);
    }
    var toolResults = messages.Where(m => m.GetProperty("role").GetString() == "tool").ToArray();
    object message;
    string finish;
    if (toolResults.Length == 0)
    {
        var name = root.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString())
                .SingleOrDefault(n => n is "todo.create" or "todo_create") : null;
        if (name is null)
        {
            state.Errors.Enqueue("creation tool was not authorized");
            return Results.StatusCode(422);
        }
        message = new { role = "assistant", content = (string?)null, tool_calls = new[] {
            new { id = "synthetic-create", type = "function", function = new { name,
                arguments = JsonSerializer.Serialize(new { title, description = (string?)null, priority = "Medium", dueDateUtc = (string?)null }) } } } };
        finish = "tool_calls";
    }
    else
    {
        state.ToolResults.Enqueue(toolResults.Select(t => t.Clone()).ToArray());
        message = new { role = "assistant", content = "Created the synthetic Todo." };
        finish = "stop";
    }
    return Results.Json(new { id = "synthetic-local", @object = "chat.completion", created = 0, model = "synthetic-local",
        choices = new[] { new { index = 0, message, finish_reason = finish } }, usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 } });
});
app.MapPost("/internal/v1/execute", () => Results.Json(new {
    schemaVersion = "openconnect.v1", result = new { voiceId = "af_heart", format = "pcm", sampleRateHz = 24000, channels = 1, audioBase64 = "AAAAAA==" }
}));
app.MapFallback((HttpContext context) =>
{
    state.Errors.Enqueue("denied route " + context.Request.Path);
    return Results.StatusCode(403);
});
await app.StartAsync();
if (selfTest)
{
    try
    {
        using var unknown = await http.GetAsync(providerOrigin + "/unknown");
        if (unknown.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Unknown HTTP route was not denied");
        using var unauthorized = await http.GetAsync(providerOrigin + "/internal/v1/realtime");
        if (unauthorized.StatusCode != HttpStatusCode.Forbidden) throw new Exception("Unauthorized WS route was not denied");
        using var ws = new ClientWebSocket();
        ws.Options.Proxy = null;
        ws.Options.SetRequestHeader("Authorization", "Bearer " + workerToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(new Uri(providerOrigin.Replace("http:", "ws:") + "/internal/v1/realtime"), timeout.Token);
        using var acknowledgement = await ReceiveFixtureTextAsync(ws, timeout.Token);
        if (acknowledgement.RootElement.GetProperty("type").GetString() != sessionAckType || state.ReceivedPcmBytes != 0)
            throw new Exception("Missing initial acknowledgement before PCM");
        // Neither a substring nor a non-string type is an endStream control.
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"not-endStream\",\"text\":\"endStream\"}"), WebSocketMessageType.Text, true, timeout.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":42}"), WebSocketMessageType.Text, true, timeout.Token);
        await ws.SendAsync(new byte[] { 0, 1, 2, 3 }, WebSocketMessageType.Binary, true, timeout.Token);
        using var transcript = await ReceiveFixtureTextAsync(ws, timeout.Token);
        if (transcript.RootElement.GetProperty("text").GetString() != raw) throw new Exception("Wrong raw transcript");
        using var request = new HttpRequestMessage(HttpMethod.Post, providerOrigin + "/v1/chat/completions");
        request.Headers.Authorization = new("Bearer", "synthetic-local-key");
        request.Content = JsonContent.Create(new { messages = new[] { new { role = "user", content = normalized } }, tools = new[] { new { function = new { name = "todo.create" } } } });
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var completion = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (completion.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString() != "tool_calls") throw new Exception("Missing tool call");
        foreach (var (text, tool, bearer, expected) in new[] {
            (raw, "todo.create", "synthetic-local-key", HttpStatusCode.UnprocessableEntity),
            (normalized, "todo.list", "synthetic-local-key", HttpStatusCode.UnprocessableEntity),
            (normalized, "todo.create", "invalid", HttpStatusCode.Forbidden) })
        {
            using var negative = new HttpRequestMessage(HttpMethod.Post, providerOrigin + "/v1/chat/completions");
            negative.Headers.Authorization = new("Bearer", bearer);
            negative.Content = JsonContent.Create(new { messages = new[] { new { role = "user", content = text } }, tools = new[] { new { function = new { name = tool } } } });
            using var denied = await http.SendAsync(negative, timeout.Token);
            if (denied.StatusCode != expected) throw new Exception("Invalid inference request was not rejected");
        }
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"end"), WebSocketMessageType.Text, false, timeout.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("Stream\"}"), WebSocketMessageType.Text, true, timeout.Token);
        var bytes = new byte[7];
        var result = await ws.ReceiveAsync(bytes.AsMemory(), timeout.Token);
        if (result.MessageType != WebSocketMessageType.Close) throw new Exception("Fragmented endStream did not close");
        if (state.ClosedAt <= state.FinalAt || state.PcmBytes != 4) throw new Exception("Incorrect transport evidence");
        using var oversized = new ClientWebSocket();
        oversized.Options.Proxy = null;
        oversized.Options.SetRequestHeader("Authorization", "Bearer " + workerToken);
        await oversized.ConnectAsync(new Uri(providerOrigin.Replace("http:", "ws:") + "/internal/v1/realtime"), timeout.Token);
        using var oversizedAcknowledgement = await ReceiveFixtureTextAsync(oversized, timeout.Token);
        if (oversizedAcknowledgement.RootElement.GetProperty("type").GetString() != sessionAckType)
            throw new Exception("Missing initial acknowledgement for oversized control test");
        await oversized.SendAsync(new byte[2048], WebSocketMessageType.Text, false, timeout.Token);
        await oversized.SendAsync(new byte[2049], WebSocketMessageType.Text, true, timeout.Token);
        var rejected = await oversized.ReceiveAsync(bytes.AsMemory(), timeout.Token);
        if (rejected.MessageType != WebSocketMessageType.Close || oversized.CloseStatus != WebSocketCloseStatus.MessageTooBig)
            throw new Exception("Fragmented oversized control was not rejected");
        Console.WriteLine("PASS synthetic HTTP/WS fixture self-test; NOT browser/app persistence verification");
    }
    finally { await app.StopAsync(); }
    return;
}
var sampleIndex = Array.IndexOf(args, "--sample");
if (sampleIndex < 0 || sampleIndex + 1 >= args.Length) throw new ArgumentException("--sample <absolute built sample dll> is required");
var sample = Path.GetFullPath(args[sampleIndex + 1]);
if (!File.Exists(sample)) throw new FileNotFoundException("Build the real sample before running the smoke", sample);
var rootIndex = Array.IndexOf(args, "--content-root");
if (rootIndex < 0 || rootIndex + 1 >= args.Length) throw new ArgumentException("--content-root <sample source directory> is required");
var contentRoot = Path.GetFullPath(args[rootIndex + 1]);
var temp = Directory.CreateTempSubdirectory("openconnect-voice-smoke-");
try
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add(sample);
    start.ArgumentList.Add("--contentRoot");
    start.ArgumentList.Add(contentRoot);
    // Do not inherit provider configuration, credentials, proxy settings, ASPNETCORE_*, or DOTNET_STARTUP_HOOKS.
    start.Environment.Clear();
    start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
    start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(Environment.ProcessPath!);
    start.Environment["HOME"] = temp.FullName;
    var settings = new Dictionary<string, string> {
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1",
        ["ASPNETCORE_ENVIRONMENT"] = "Development", ["ASPNETCORE_URLS"] = appOrigin,
        ["OPENCONNECT_BLAZOR_SQLITE_PATH"] = Path.Combine(temp.FullName, "todo.db"),
        ["OPENCONNECT_BLAZOR_DATA_PROTECTION_KEYS_PATH"] = Path.Combine(temp.FullName, "keys"),
        ["OPENCONNECT_BLAZOR_ACCEPTANCE_DATA"] = "true",
        ["OpenConnect__Gateway__Mode"] = "SameHostGateway", ["OpenConnect__Gateway__SameHostBaseUrl"] = appOrigin,
        ["OpenConnect__ApiUrl"] = providerOrigin + "/v1", ["OpenConnect__ApiKey"] = "synthetic-local-key",
        ["OpenConnect__ReasoningModel"] = "synthetic-local", ["Workers__Kokoro__Endpoint"] = providerOrigin,
        ["Workers__Transcription__Endpoint"] = providerOrigin, ["OPENCONNECT_WORKER_TOKEN"] = workerToken,
        ["TODO_SERVICE_URL"] = appOrigin, ["TODO_INTERNAL_TOKEN"] = todoToken, ["TODO_INTERNAL_TENANT"] = tenant,
        ["TODO_ALLOW_MUTATIONS"] = "true", ["TODO_MUTATION_APPROVAL"] = "approved",
        ["OpenConnect__LocalAcceptance__Enabled"] = "true", ["OpenConnect__LocalAcceptance__BrowserOrigin"] = appOrigin,
        ["OpenConnect__LocalAcceptance__AllowedHosts__0"] = "127.0.0.1:5187", ["OpenConnect__LocalAcceptance__ServiceBearer"] = guard,
        ["OpenConnect__LiveUI__Inference__Worker__Endpoint"] = providerOrigin, ["GATEWAY_URL"] = appOrigin
    };
    foreach (var (key, value) in settings) start.Environment[key] = value;
    // These are the ONLY explicitly configured provider/service destinations, all literal loopback.
    foreach (var (key, value) in settings.Where(s => s.Key.EndsWith("Endpoint", StringComparison.Ordinal) || s.Key.EndsWith("ApiUrl", StringComparison.Ordinal) || s.Key.EndsWith("URL", StringComparison.Ordinal)))
        if (new Uri(value).Host != "127.0.0.1") throw new InvalidOperationException("Non-loopback destination: " + key);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Sample did not start");
    try
    {
        var stopped = app.WaitForShutdownAsync();
        var exited = child.WaitForExitAsync();
        if (await Task.WhenAny(stopped, exited) == exited) throw new InvalidOperationException("Sample exited before fixture shutdown");
    }
    finally
    {
        if (!child.HasExited) child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
        await app.StopAsync();
    }
}
finally { temp.Delete(recursive: true); }

static async Task<JsonDocument> ReceiveFixtureTextAsync(ClientWebSocket socket, CancellationToken token)
{
    // A tiny receive buffer forces multi-receive assembly even if frames are coalesced.
    var bytes = new byte[7];
    using var assembled = new MemoryStream();
    ValueWebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(bytes.AsMemory(), token);
        if (result.MessageType != WebSocketMessageType.Text || assembled.Length + result.Count > 4096)
            throw new Exception("Invalid or oversized fixture message");
        assembled.Write(bytes, 0, result.Count);
    } while (!result.EndOfMessage);
    return JsonDocument.Parse(assembled.ToArray());
}

internal sealed class Evidence
{
    public long PcmBytes;
    public long FinalAt { get; set; }
    public long ClosedAt { get; set; }
    public long ReceivedPcmBytes => Interlocked.Read(ref PcmBytes);
    public ConcurrentQueue<object> ModelRequests { get; } = new();
    public ConcurrentQueue<JsonElement[]> ToolResults { get; } = new();
    public ConcurrentQueue<string> Errors { get; } = new();
}
