using System.Net;
using System.Reflection;
using System.Text;
using OpenConnect.LiveUI.Inference;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class CloudLiveUIHttpTransportTests
{
    [Theory]
    [InlineData("http://cloud.example/")]
    [InlineData("https://user:pass@cloud.example/")]
    [InlineData("https://cloud.example/subpath")]
    [InlineData("https://cloud.example/?target=other")]
    public void RejectsUnsafeOrigins(string address) =>
        Assert.Throws<ArgumentException>(() => new CloudLiveUIHttpTransport(new Uri(address), "credential"));

    [Theory]
    [InlineData("")]
    [InlineData("bad credential")]
    [InlineData("one,two")]
    public void RejectsInvalidCredentials(string credential) =>
        Assert.Throws<ArgumentException>(() => new CloudLiveUIHttpTransport(new Uri("https://cloud.example/"), credential));

    [Fact]
    public async Task SendsOnlyToFixedCloudRoutesWithServerCredential()
    {
        using var transport = WithHandler(new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/health/ready", request.RequestUri!.PathAndQuery);
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"ready\",\"capabilities\":[\"inference.liveui\"]}") };
            }
            Assert.Equal("/cloud/v1/liveui/plan", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("credential", request.Headers.Authorization.Parameter);
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("{}", await request.Content.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"result\":true}") };
        }));

        Assert.True(await transport.IsReadyAsync());
        var response = await transport.SendAsync(Encoding.UTF8.GetBytes("{}"));
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("{\"result\":true}", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task RejectsRedirectsAndOversizedOrEncodedResponses()
    {
        using var redirect = WithHandler(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect))));
        Assert.Equal(307, (await redirect.SendAsync(Encoding.UTF8.GetBytes("{}"))).StatusCode);
        using var large = WithHandler(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent(new byte[262145]) })));
        Assert.Equal(503, (await large.SendAsync(Encoding.UTF8.GetBytes("{}"))).StatusCode);
        using var encoded = WithHandler(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        }));
        Assert.Equal(503, (await encoded.SendAsync(Encoding.UTF8.GetBytes("{}"))).StatusCode);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsReadinessFailure()
    {
        using var transport = WithHandler(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("unreachable");
        }));
        using var cancellation = new CancellationTokenSource();
        var pending = transport.IsReadyAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static CloudLiveUIHttpTransport WithHandler(HttpMessageHandler handler) =>
        (CloudLiveUIHttpTransport)Activator.CreateInstance(typeof(CloudLiveUIHttpTransport), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [new Uri("https://cloud.example/"), "credential", handler], culture: null)!;

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handle;
        public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) => _handle = (request, _) => handle(request);
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) => _handle = handle;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handle(request, cancellationToken);
    }
}
