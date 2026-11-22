using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenConnect.Abstractions;
using OpenConnect.Docs.AssistantHost.Speech;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Docs.AssistantHost.Tests;

public sealed class SpeechPrivacyMiddlewareTests
{
    [Fact]
    public async Task SanitizesTextBeforeCallingSpeechEndpointAndRestoresBody()
    {
        var context = Request("{\"text\":\"private\",\"voiceId\":\"af_heart\"}");
        var original = context.Request.Body;
        string? forwarded = null;
        var middleware = new SpeechPrivacyMiddleware(async http =>
        {
            using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
            forwarded = await reader.ReadToEndAsync();
        });
        var gate = new FakeGate(value => value == "private" ? "[redacted]" : value);
        await middleware.InvokeAsync(context, gate);
        using var document = JsonDocument.Parse(forwarded!);
        Assert.Equal("[redacted]", document.RootElement.GetProperty("text").GetString());
        Assert.DoesNotContain("private", forwarded!);
        Assert.Equal(EgressDestination.Worker, gate.LastContext!.Destination);
        Assert.Same(original, context.Request.Body);
    }

    [Theory]
    [InlineData("{\"text\":\"one\",\"TEXT\":\"two\"}", 400)]
    [InlineData("{\"text\":\"ok\",\"unknown\":\"private\"}", 400)]
    [InlineData("{\"text\":\"\"}", 400)]
    public async Task InvalidBodiesNeverReachWorker(string body, int expected)
    {
        var context = Request(body);
        var called = false;
        var middleware = new SpeechPrivacyMiddleware(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, new FakeGate(value => value));
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task DeniedOrChangedMetadataNeverReachesWorker()
    {
        var context = Request("{\"text\":\"hello\",\"locale\":\"private\"}");
        var called = false;
        var middleware = new SpeechPrivacyMiddleware(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, new FakeGate(value => value == "private" ? "[redacted]" : value));
        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task MissingDetectorFailsClosed()
    {
        var context = Request("{\"text\":\"hello\"}");
        var called = false;
        var middleware = new SpeechPrivacyMiddleware(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, new FakeGate(_ => null));
        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task TrailingSlashCannotBypassSpeechScreening()
    {
        var context = Request("{\"text\":\"hello\"}");
        context.Request.Path = "/openconnect/v1/speech/synthesize/";
        var called = false;
        var middleware = new SpeechPrivacyMiddleware(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context, new FakeGate(_ => null));
        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(called);
    }

    [Fact]
    public async Task OversizedBodyAndDetectorFailureFailClosed()
    {
        var oversized = Request("{\"text\":\"" + new string('a', 17000) + "\"}");
        var called = false;
        var middleware = new SpeechPrivacyMiddleware(_ => { called = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(oversized, new FakeGate(value => value));
        Assert.Equal(413, oversized.Response.StatusCode);
        var failure = Request("{\"text\":\"hello\"}");
        await middleware.InvokeAsync(failure, new FakeGate(_ => throw new IOException("private")));
        Assert.Equal(503, failure.Response.StatusCode);
        Assert.False(called);
    }

    private static DefaultHttpContext Request(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/openconnect/v1/speech/synthesize";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", Ulid.NewUlid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, Ulid.NewUlid().ToString())], "test"));
        return context;
    }

    private sealed class FakeGate(Func<string, string?> sanitize) : IEgressGate
    {
        public EgressContext? LastContext { get; private set; }
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            var result = sanitize(content);
            return ValueTask.FromResult(result is null
                ? new EgressDecision(false, null, [], SecurityPolicyAction.Block, "blocked")
                : new EgressDecision(true, result, [], result == content ? SecurityPolicyAction.Allow : SecurityPolicyAction.Mask, "test"));
        }
    }
}
