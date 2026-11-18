using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
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

        private SpeechHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public static async Task<SpeechHost> StartAsync(RecordingHandler handler, bool configureWorker)
        {
            var builder = WebApplication.CreateBuilder();
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
            return new SpeechHost(app, client);
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
