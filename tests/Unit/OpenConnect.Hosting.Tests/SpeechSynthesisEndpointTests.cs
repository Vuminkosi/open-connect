using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class SpeechSynthesisEndpointTests
{
    private const string ValidRequest = """
        {"text":"Hello from LiveUI.","voiceId":"af_heart","locale":"en-US","format":"pcm","speechEpoch":7}
        """;

    [Fact]
    public async Task Synthesize_requires_authentication()
    {
        using var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Worker must not be called."));
        await using var host = await SpeechHost.StartAsync(handler, configureWorker: true);
        using var anonymousClient = new HttpClient { BaseAddress = host.Client.BaseAddress };

        using var response = await anonymousClient.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(ValidRequest));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Synthesize_returns_typed_worker_result_and_forwards_authenticated_contract()
    {
        using var handler = new RecordingHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                schemaVersion = "openconnect.v1",
                result = new { voiceId = "af_heart", format = "pcm", sampleRateHz = 24000, channels = 1, audioBase64 = "AQID", speechEpoch = 7 }
            })
        }));
        await using var host = await SpeechHost.StartAsync(handler, configureWorker: true);

        using var response = await host.Client.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(ValidRequest));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("af_heart", body.GetProperty("voiceId").GetString());
        Assert.Equal("pcm", body.GetProperty("format").GetString());
        Assert.Equal(24000, body.GetProperty("sampleRateHz").GetInt32());
        Assert.Equal(1, body.GetProperty("channels").GetInt32());
        Assert.Equal("AQID", body.GetProperty("audioBase64").GetString());
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("http://worker.test/internal/v1/execute", handler.LastRequestUri);
        Assert.Equal("Bearer test-worker-token", handler.LastAuthorization);
        Assert.Equal("speech.synthesize", handler.LastRequestBody!.Value.GetProperty("kind").GetString());
        Assert.Equal("Hello from LiveUI.", handler.LastRequestBody.Value.GetProperty("payload").GetProperty("text").GetString());
        Assert.Equal(7, handler.LastRequestBody.Value.GetProperty("payload").GetProperty("speechEpoch").GetInt32());
    }

    [Fact]
    public async Task Synthesize_returns_service_unavailable_when_worker_is_not_configured()
    {
        using var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Worker must not be called."));
        await using var host = await SpeechHost.StartAsync(handler, configureWorker: false);

        using var response = await host.Client.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(ValidRequest));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("{\"text\":\" \"}")]
    [InlineData("{\"text\":\"Hello\",\"voiceId\":\"unknown\"}")]
    [InlineData("{\"text\":\"Hello\",\"format\":\"mp3\"}")]
    public async Task Synthesize_rejects_unsupported_request_before_calling_worker(string requestJson)
    {
        using var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("Worker must not be called."));
        await using var host = await SpeechHost.StartAsync(handler, configureWorker: true);

        using var response = await host.Client.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(requestJson));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Synthesize_maps_worker_failure_to_problem_response()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await using var host = await SpeechHost.StartAsync(handler, configureWorker: true);

        using var response = await host.Client.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(ValidRequest));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Kokoro speech synthesis failed.", problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"result\":null}")]
    public async Task Synthesize_maps_malformed_worker_json_to_bad_gateway(string workerJson)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(workerJson)
        }));
        await using var host = await SpeechHost.StartAsync(handler, configureWorker: true);

        using var response = await host.Client.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(ValidRequest));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Kokoro returned an invalid synthesis response.", problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(401, 502)]
    [InlineData(403, 502)]
    [InlineData(400, 502)]
    [InlineData(404, 502)]
    [InlineData(429, 502)]
    [InlineData(500, 502)]
    [InlineData(503, 503)]
    [InlineData(504, 504)]
    [InlineData(408, 504)]
    public async Task Worker_status_is_a_gateway_failure_not_caller_authentication(int upstream, int expected)
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)upstream)
        { Content = new StringContent("sensitive-worker-detail test-worker-token") }));
        await using var host = await SpeechHost.StartAsync(handler, true);
        using var response = await host.Client.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(ValidRequest));
        await AssertGenericProblem(response, expected);
        AssertSafeLogs(host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_transport_or_timeout_returns_generic_problem(bool timeout)
    {
        using var handler = new RecordingHandler((_, _) => throw (timeout
            ? new TaskCanceledException("sensitive-worker-detail test-worker-token")
            : new HttpRequestException("sensitive-worker-detail test-worker-token")));
        await using var host = await SpeechHost.StartAsync(handler, true);
        using var response = await host.Client.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(ValidRequest));
        await AssertGenericProblem(response, timeout ? 504 : 502);
        AssertSafeLogs(host);
    }

    [Fact]
    public async Task Worker_content_read_failure_returns_generic_bad_gateway()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new FailedContent() }));
        await using var host = await SpeechHost.StartAsync(handler, true);
        using var response = await host.Client.PostAsync("/openconnect/v1/speech/synthesize", JsonBody(ValidRequest));
        await AssertGenericProblem(response, 502);
        AssertSafeLogs(host);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_propagates_without_problem_or_error_log(bool wrapped)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new RecordingHandler((_, _) =>
        {
            cancellation.Cancel();
            if (wrapped)
                throw new HttpRequestException("sensitive-worker-detail", new OperationCanceledException(cancellation.Token));
            throw new OperationCanceledException("sensitive-worker-detail", cancellation.Token);
        });
        await using var host = await SpeechHost.StartAsync(handler, true);
        // Invoke the actual handler with an explicit caller token: an HTTP disconnect
        // alone would hide an incorrectly generated 5xx response from the test client.
        var method = typeof(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions).GetMethod("HandleSpeechSynthesisAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var request = Activator.CreateInstance(method.GetParameters()[0].ParameterType,
            ["Hello from LiveUI.", "af_heart", "en-US", "pcm", 7L, null]);
        var args = method.GetParameters().Select(p => p.ParameterType == typeof(CancellationToken) ? (object)cancellation.Token
            : p.Position == 0 ? request! : host.Services.GetRequiredService(p.ParameterType)).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (Task<IResult>)method.Invoke(null, args)!);
        Assert.DoesNotContain(host.Logs.Entries, x => x.Level >= LogLevel.Error);
        AssertSafeLogs(host, requireDiagnostic: false);
    }

    private static async Task AssertGenericProblem(HttpResponseMessage response, int expected)
    {
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal(expected, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("detail", out _));
        Assert.DoesNotContain("sensitive-worker-detail", body);
        Assert.DoesNotContain("test-worker-token", body);
        Assert.DoesNotContain("Hello from LiveUI.", body);
    }

    private static void AssertSafeLogs(SpeechHost host, bool requireDiagnostic = true)
    {
        var entries = host.Logs.Entries.Where(x => x.Category == "OpenConnect.Speech").ToArray();
        if (requireDiagnostic)
            Assert.Contains(entries, x => x.Level == LogLevel.Warning && x.Text.StartsWith("Speech worker", StringComparison.Ordinal));
        foreach (var entry in entries)
        {
            Assert.DoesNotContain("sensitive-worker-detail", entry.Text);
            Assert.DoesNotContain("test-worker-token", entry.Text);
            Assert.DoesNotContain("Hello from LiveUI.", entry.Text);
            Assert.Null(entry.Exception);
        }
    }

    private sealed class FailedContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("sensitive-worker-detail test-worker-token");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class CaptureLogs : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Category, LogLevel Level, string Text, Exception? Exception)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(("OpenConnect.Speech", logLevel, formatter(state, exception), exception));
        public void Dispose() { }
        private sealed class CategoryLogger(CaptureLogs owner, string category) : ILogger
        {
            public bool IsEnabled(LogLevel logLevel) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((category, logLevel, formatter(state, exception), exception));
        }
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastAuthorization { get; private set; }
        public JsonElement? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            return await send(request, cancellationToken);
        }
    }

    private sealed class SpeechHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public HttpClient Client { get; }
        public IServiceProvider Services => _app.Services;
        public CaptureLogs Logs { get; }

        private SpeechHost(WebApplication app, HttpClient client, CaptureLogs logs)
        {
            _app = app;
            Logs = logs;
            Client = client;
        }

        public static async Task<SpeechHost> StartAsync(RecordingHandler handler, bool configureWorker)
        {
            var builder = WebApplication.CreateBuilder();
            var logs = new CaptureLogs();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton<ILogger>(logs);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddAuthentication("SpeechTest")
                .AddScheme<AuthenticationSchemeOptions, SpeechTestAuthenticationHandler>("SpeechTest", _ => { });
            builder.Services.AddAuthorization();
            builder.Services.AddOpenConnectTaskExecution();
            builder.Services.AddHttpClient("openconnect-kokoro")
                .ConfigurePrimaryHttpMessageHandler(() => handler);
            if (configureWorker)
            {
                builder.Configuration["Workers:Kokoro:Endpoint"] = "http://worker.test/";
                builder.Configuration["OPENCONNECT_WORKER_TOKEN"] = "test-worker-token";
            }

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapOpenConnect();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
                .Addresses.Single();
            var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("SpeechTest");
            return new SpeechHost(app, client, logs);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed class SpeechTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!string.Equals(Request.Headers.Authorization, "SpeechTest", StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "speech-test")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
