using System.Net;
using System.Net.Http.Headers;
using OpenConnect.Docs.Services.Voice;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationVoiceAvailabilityTests
{
    [Fact]
    public async Task UsesServerSideVisitorCredentialForHostReadiness()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"available\":true}");
        using var service = Create(handler);
        Assert.True(await service.IsAvailableAsync());
        Assert.Equal("https://localhost:8098/internal/docs/voice/model-ready", handler.Uri);
        Assert.Equal("Bearer visitor-token", handler.Authorization);
    }

    [Theory]
    [InlineData(200, "{\"available\":false}")]
    [InlineData(200, "bad json")]
    [InlineData(503, "{\"available\":true}")]
    public async Task MissingOrInvalidReadinessFailsClosed(int status, string body)
    {
        using var service = Create(new StubHandler((HttpStatusCode)status, body));
        Assert.False(await service.IsAvailableAsync());
    }

    [Fact]
    public async Task OversizedResponseFailsClosed()
    {
        using var service = Create(new StubHandler(HttpStatusCode.OK, new string('x', 1025)));
        Assert.False(await service.IsAvailableAsync());
    }

    private static DocumentationVoiceAvailability Create(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://localhost:8098/openconnect/v1/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "visitor-token");
        return new DocumentationVoiceAvailability(client);
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
