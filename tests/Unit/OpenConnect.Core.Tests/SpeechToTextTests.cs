using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Core.Speech;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class SpeechToTextTests
{
    [Fact]
    public void CollectorParsesBoundedPartialsAndRejectsInvalidFrames()
    {
        var collector = new RealtimeTranscriptCollector();

        Assert.True(collector.TryReadPartial(
            """{"type":"transcript","final":false,"turnId":2,"transcript":"  hello world  "}""",
            out var partial));
        Assert.NotNull(partial);
        Assert.Equal(2, partial!.TurnId);
        Assert.Equal("hello world", partial.Text);

        Assert.True(collector.TryReadPartial(
            """{"type":"transcript","final":false,"transcript":"no turn id"}""",
            out var withoutTurn));
        Assert.Null(withoutTurn!.TurnId);
        Assert.Equal("no turn id", withoutTurn.Text);

        Assert.False(collector.TryReadPartial(
            """{"type":"transcript","final":true,"transcript":"already final"}""",
            out _));
        Assert.False(collector.TryReadPartial(
            $$"""{"type":"transcript","final":false,"transcript":"{{new string('a', 8001)}}"}""",
            out _));
        Assert.False(collector.TryReadPartial(
            """{"type":"transcript","final":false,"turnId":-1,"transcript":"bad turn"}""",
            out _));
        Assert.False(collector.TryReadPartial("not-json", out _));
        Assert.False(collector.TryReadPartial("[]", out _));
    }

    [Fact]
    public void CollectorHandlesSpeechCompleteAndFinalTranscriptAndDeduplicatesTurnId()
    {
        var collector = new RealtimeTranscriptCollector();

        Assert.True(collector.TryAppend(
            """{"type":"speechComplete","turnId":2,"transcript":"second turn"}""",
            out var second));
        Assert.Equal(2, second!.TurnId);
        Assert.Equal("second turn", second.Text);

        // Duplicate turnId is rejected across both event shapes
        Assert.False(collector.TryAppend("""{"type":"speechComplete","turnId":2,"transcript":"duplicate"}"""));
        Assert.False(collector.TryAppend("""{"type":"transcript","final":true,"turnId":2,"transcript":"duplicate"}"""));

        Assert.True(collector.TryAppend(
            """{"type":"transcript","final":true,"turnId":1,"transcript":"first turn"}""",
            out var first));
        Assert.Equal(1, first!.TurnId);
        Assert.Equal("first turn", first.Text);

        // PUSH_TO_TALK without turnId
        Assert.True(collector.TryAppend(
            """{"type":"transcript","final":true,"transcript":"tail turn"}""",
            out var tail));
        Assert.Null(tail!.TurnId);

        // Ignored non-final / malformed
        Assert.False(collector.TryAppend("""{"type":"transcript","final":false,"transcript":"partial"}"""));
        Assert.False(collector.TryAppend("""{"type":"speechComplete","transcript":"missing turnId"}"""));
        Assert.False(collector.TryAppend("{bad"));

        Assert.Equal(["first turn", "second turn", "tail turn"], collector.Transcripts);
    }

    [Theory]
    [InlineData("wss://asr.example.test/ws", "secret", true)]
    [InlineData("ws://127.0.0.1:8080/ws", "secret", true)]
    [InlineData("ws://localhost:8080/ws", "secret", true)]
    [InlineData("ws://asr.example.test/ws", "secret", false)]
    [InlineData("wss://user:pass@asr.example.test/ws", "secret", false)]
    [InlineData("wss://asr.example.test/ws", "", false)]
    [InlineData("https://asr.example.test/ws", "secret", false)]
    public void RealtimeRouteValidationEnforcesSchemeLoopbackAndKey(string url, string apiKey, bool expected)
    {
        var options = new SpeechToTextOptions { RealtimeWebSocketUrl = url, ApiKey = apiKey };
        var client = new WebSocketRealtimeSpeechToTextClient(Options.Create(options));
        Assert.Equal(expected, options.HasRealtimeRoute);
        Assert.Equal(expected, client.IsConfigured);
    }

    [Fact]
    public void RealtimeBuildHandshakeSerializesExpectedFieldsAndThrowsWhenUnconfigured()
    {
        var unconfigured = new WebSocketRealtimeSpeechToTextClient(Options.Create(new SpeechToTextOptions()));
        Assert.Throws<InvalidOperationException>(() => unconfigured.BuildHandshake());

        var client = new WebSocketRealtimeSpeechToTextClient(Options.Create(new SpeechToTextOptions
        {
            RealtimeWebSocketUrl = "wss://asr.example.test/v1/realtime",
            ApiKey = "test-secret-key",
            Model = "muse-voice-transcribe-1.0",
            Mode = "ENDPOINTING",
            AudioEncoding = "PCM_24KHZ"
        }));

        using var doc = JsonDocument.Parse(client.BuildHandshake());
        var root = doc.RootElement;
        Assert.Equal("Bearer test-secret-key", root.GetProperty("authorization").GetProperty("accessToken").GetString());
        Assert.Equal("PCM_24KHZ", root.GetProperty("audioEncoding").GetString());
        Assert.Equal("muse-voice-transcribe-1.0", root.GetProperty("model").GetString());
        Assert.Equal("ENDPOINTING", root.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task RealtimeClientCompletesLoopbackWebSocketSession()
    {
        string? receivedHandshake = null;
        int receivedBinaryBytes = 0;
        bool receivedEndStream = false;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            using var ws = await context.WebSockets.AcceptWebSocketAsync();
            var buf = new byte[16384];
            var hs = await ws.ReceiveAsync(buf, context.RequestAborted);
            receivedHandshake = Encoding.UTF8.GetString(buf, 0, hs.Count);

            await ws.SendAsync(
                Encoding.UTF8.GetBytes("""{"sessionId":"sess-42"}"""),
                WebSocketMessageType.Text,
                true,
                context.RequestAborted);

            while (ws.State == WebSocketState.Open)
            {
                var frame = await ws.ReceiveAsync(buf, context.RequestAborted);
                if (frame.MessageType == WebSocketMessageType.Binary)
                {
                    receivedBinaryBytes += frame.Count;
                }
                else if (frame.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(buf, 0, frame.Count);
                    if (text.Contains("endStream", StringComparison.Ordinal))
                    {
                        receivedEndStream = true;
                        await ws.SendAsync(
                            Encoding.UTF8.GetBytes("""{"type":"transcript","final":false,"turnId":0,"transcript":"hello"}"""),
                            WebSocketMessageType.Text,
                            true,
                            context.RequestAborted);
                        await ws.SendAsync(
                            Encoding.UTF8.GetBytes("""{"type":"speechComplete","turnId":0,"transcript":"hello openconnect"}"""),
                            WebSocketMessageType.Text,
                            true,
                            context.RequestAborted);
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", context.RequestAborted);
                        break;
                    }
                }
                else if (frame.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUrl = address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws";

        var client = new WebSocketRealtimeSpeechToTextClient(Options.Create(new SpeechToTextOptions
        {
            RealtimeWebSocketUrl = wsUrl,
            ApiKey = "loopback-api-key"
        }));

        var partials = new List<RealtimePartialTranscript>();
        var finals = new List<RealtimeTranscriptTurn>();
        using var pcm = new MemoryStream(new byte[480]);

        var result = await client.TranscribePcmAsync(
            pcm,
            CancellationToken.None,
            onPartialTranscript: (p, _) => { partials.Add(p); return ValueTask.CompletedTask; },
            onFinalTranscript: (f, _) => { finals.Add(f); return ValueTask.CompletedTask; });

        await app.StopAsync();

        Assert.NotNull(receivedHandshake);
        Assert.Contains("Bearer loopback-api-key", receivedHandshake, StringComparison.Ordinal);
        Assert.Equal(480, receivedBinaryBytes);
        Assert.True(receivedEndStream);
        Assert.Equal("sess-42", result.SessionId);
        Assert.Single(partials);
        Assert.Equal("hello", partials[0].Text);
        Assert.Single(finals);
        Assert.Equal("hello openconnect", finals[0].Text);
        Assert.Equal(["hello openconnect"], result.FinalTranscripts);
    }

    [Fact]
    public async Task UploadClientSendsMultipartRequestAndParsesResult()
    {
        var handler = new RecordingHttpHandler(HttpStatusCode.OK, """
            {
              "sessionId": "upload-1",
              "transcript": "hello world",
              "audioDurationMs": 1250,
              "turns": [
                { "turnId": 0, "startMs": 0, "endMs": 1250, "transcript": "hello world", "speaker": "spk_0" }
              ]
            }
            """);

        using var http = new HttpClient(handler);
        var client = new MultipartSpeechToTextUploadClient(http, Options.Create(new SpeechToTextOptions
        {
            UploadUrl = "https://asr.example.test/v1/transcribe",
            ApiKey = "upload-secret",
            Model = "muse-voice-transcribe-1.0",
            Mode = "DIARIZATION",
            Language = "en"
        }));

        Assert.True(client.IsConfigured);

        using var wav = new MemoryStream([1, 2, 3, 4]);
        var result = await client.TranscribeAsync(wav, "clip.wav", "audio/wav", CancellationToken.None);

        Assert.Equal("https://asr.example.test/v1/transcribe", handler.RequestUri);
        Assert.Equal("Bearer upload-secret", handler.Authorization);
        Assert.Contains("name=request", handler.BodyPreview, StringComparison.Ordinal);
        Assert.Contains("\"audioEncoding\":\"WAV\"", handler.BodyPreview, StringComparison.Ordinal);
        Assert.Contains("name=audio; filename=clip.wav", handler.BodyPreview, StringComparison.Ordinal);
        Assert.Equal("upload-1", result.SessionId);
        Assert.Equal("hello world", result.Transcript);
        Assert.Equal(1250, result.AudioDurationMs);
        var turn = Assert.Single(result.Turns);
        Assert.Equal(0, turn.TurnId);
        Assert.Equal("spk_0", turn.Speaker);
        Assert.Equal("hello world", turn.Transcript);
    }

    [Fact]
    public async Task UploadClientEnforcesMaxAudioBytesAndNeverLeaksApiKey()
    {
        var handler = new RecordingHttpHandler(HttpStatusCode.Unauthorized, "{}");
        using var http = new HttpClient(handler);
        var secret = "super-secret-stt-key-999";
        var client = new MultipartSpeechToTextUploadClient(http, Options.Create(new SpeechToTextOptions
        {
            UploadUrl = "https://asr.example.test/v1/transcribe",
            ApiKey = secret,
            MaxAudioBytes = 16
        }));

        // Seekable stream exceeding cap is rejected before sending
        using var oversized = new MemoryStream(new byte[17]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TranscribeAsync(oversized, "big.wav", "audio/wav", CancellationToken.None));
        Assert.Null(handler.RequestUri);

        // Non-seekable stream exceeding cap is rejected client-side
        using var nonSeekable = new NonSeekableMemoryStream(new byte[17]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TranscribeAsync(nonSeekable, "stream.wav", "audio/wav", CancellationToken.None));

        // HTTP failure must never expose API key
        using var small = new MemoryStream(new byte[4]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TranscribeAsync(small, "ok.wav", "audio/wav", CancellationToken.None));
        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigurationBindingSupportsPrimaryAndLegacyFallbackKeys()
    {
        var legacyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenConnect:ASR_WSS"] = "wss://legacy.example.test/realtime",
                ["OpenConnect:ASR_Transcription:URL"] = "https://legacy.example.test/upload",
                ["OpenConnect:ASR_MODEL"] = "legacy-model",
                ["OpenConnect:ASRMode"] = "PUSH_TO_TALK",
                ["OpenConnect:Asr:Language"] = "fr",
                ["OpenConnect:ApiKey"] = "legacy-key"
            })
            .Build();

        using var legacyServices = new ServiceCollection()
            .AddOpenConnectSpeechToText(legacyConfig.AsEnumerable())
            .BuildServiceProvider();

        var legacyOptions = legacyServices.GetRequiredService<IOptions<SpeechToTextOptions>>().Value;
        Assert.Equal("wss://legacy.example.test/realtime", legacyOptions.RealtimeWebSocketUrl);
        Assert.Equal("https://legacy.example.test/upload", legacyOptions.UploadUrl);
        Assert.Equal("legacy-model", legacyOptions.Model);
        Assert.Equal("PUSH_TO_TALK", legacyOptions.Mode);
        Assert.Equal("fr", legacyOptions.Language);
        Assert.Equal("legacy-key", legacyOptions.ApiKey);
        Assert.True(legacyServices.GetRequiredService<IRealtimeSpeechToTextClient>().IsConfigured);
        Assert.True(legacyServices.GetRequiredService<ISpeechToTextUploadClient>().IsConfigured);

        // Primary keys override legacy fallbacks
        var primaryConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenConnect:SpeechToText:RealtimeWebSocketUrl"] = "wss://primary.example.test/realtime",
                ["OpenConnect:SpeechToText:UploadUrl"] = "https://primary.example.test/upload",
                ["OpenConnect:SpeechToText:Model"] = "primary-model",
                ["OpenConnect:SpeechToText:Mode"] = "DIARIZATION",
                ["OpenConnect:SpeechToText:Language"] = "de",
                ["OpenConnect:SpeechToText:ApiKey"] = "primary-key",
                ["OpenConnect:ASR_WSS"] = "wss://legacy.example.test/realtime",
                ["OpenConnect:MetaApiKey"] = "legacy-meta-key"
            })
            .Build();

        using var primaryServices = new ServiceCollection()
            .AddOpenConnectSpeechToText(primaryConfig.AsEnumerable())
            .BuildServiceProvider();

        var primaryOptions = primaryServices.GetRequiredService<IOptions<SpeechToTextOptions>>().Value;
        Assert.Equal("wss://primary.example.test/realtime", primaryOptions.RealtimeWebSocketUrl);
        Assert.Equal("https://primary.example.test/upload", primaryOptions.UploadUrl);
        Assert.Equal("primary-model", primaryOptions.Model);
        Assert.Equal("DIARIZATION", primaryOptions.Mode);
        Assert.Equal("de", primaryOptions.Language);
        Assert.Equal("primary-key", primaryOptions.ApiKey);
    }

    private sealed class RecordingHttpHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string BodyPreview { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            BodyPreview = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class NonSeekableMemoryStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
