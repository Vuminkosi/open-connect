using System.Net;
using Microsoft.Extensions.Configuration;
using OpenConnect.Abstractions.Speech;
using OpenConnect.Docs.AssistantHost.Voice;
using Xunit;

namespace OpenConnect.Docs.AssistantHost.Tests;

public sealed class DocumentationVoiceReadinessTests
{
    [Fact]
    public async Task ChecksOnlyTheConfiguredLocalWorkerWithItsBearer()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"modelReady\":true}");
        var service = Create(handler);
        Assert.True(await service.IsAvailableAsync(CancellationToken.None));
        Assert.Equal("http://worker:8080/internal/v1/realtime/local/model-ready", handler.Uri);
        Assert.Equal("Bearer worker-token", handler.Authorization);
    }

    [Theory]
    [InlineData(503, "{\"detail\":\"model_unavailable\"}")]
    [InlineData(200, "{\"modelReady\":false}")]
    [InlineData(200, "not json")]
    public async Task UnavailableOrInvalidResponsesFailClosed(int status, string body)
    {
        var service = Create(new StubHandler((HttpStatusCode)status, body));
        Assert.False(await service.IsAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OversizedResponseFailsClosed()
    {
        var service = Create(new StubHandler(HttpStatusCode.OK, new string('x', 1025)));
        Assert.False(await service.IsAvailableAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("false", "http://worker:8080/", "worker-token")]
    [InlineData("true", "http://worker:8080/path", "worker-token")]
    [InlineData("true", "http://user:password@worker/", "worker-token")]
    [InlineData("true", "http://worker:8080/", "invalid token")]
    public async Task InvalidOrNonLocalConfigurationDoesNotDispatch(string localOnly, string endpoint, string token)
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"modelReady\":true}");
        Assert.False(await Create(handler, localOnly, endpoint, token).IsAvailableAsync(CancellationToken.None));
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task ReturnsTrueImmediatelyWhenSdkSpeechToTextIsConfiguredAndNotLocalOnly()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "{}");
        var service = Create(handler, localOnly: "false", realtimeSpeechToText: new StubRealtimeSpeechToTextClient(true));

        Assert.True(await service.IsAvailableAsync(CancellationToken.None));
        Assert.Null(handler.Uri);
    }

    [Fact]
    public async Task LocalOnlyStillChecksLocalWorkerEvenWhenSdkSpeechToTextIsConfigured()
    {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable, "{}");
        var service = Create(handler, localOnly: "true", realtimeSpeechToText: new StubRealtimeSpeechToTextClient(true));

        Assert.False(await service.IsAvailableAsync(CancellationToken.None));
        Assert.Equal("http://worker:8080/internal/v1/realtime/local/model-ready", handler.Uri);
    }

    private static DocumentationVoiceReadiness Create(StubHandler handler, string localOnly = "true",
        string endpoint = "http://worker:8080/", string token = "worker-token",
        IRealtimeSpeechToTextClient? realtimeSpeechToText = null) =>
        new(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workers:Transcription:RealtimeLocalOnly"] = localOnly,
            ["Workers:Transcription:Endpoint"] = endpoint,
            ["OPENCONNECT_WORKER_TOKEN"] = token
        }).Build(), realtimeSpeechToText);

    private sealed class StubRealtimeSpeechToTextClient(bool configured) : IRealtimeSpeechToTextClient
    {
        public bool IsConfigured => configured;

        public Task<RealtimeTranscriptionResult> TranscribePcmAsync(
            Stream pcm24kMono16Bit,
            CancellationToken cancellationToken = default,
            Func<RealtimePartialTranscript, CancellationToken, ValueTask>? onPartialTranscript = null,
            Func<RealtimeTranscriptTurn, CancellationToken, ValueTask>? onFinalTranscript = null)
            => Task.FromResult(new RealtimeTranscriptionResult(string.Empty, []));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
