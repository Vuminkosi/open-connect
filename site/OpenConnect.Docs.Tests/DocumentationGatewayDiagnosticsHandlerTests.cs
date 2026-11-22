using System.Net;
using Microsoft.Extensions.Logging;
using OpenConnect.Docs.Services;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationGatewayDiagnosticsHandlerTests
{
    [Fact]
    public async Task TransportFailureLogsCategoryWithoutExceptionDetails()
    {
        var logger = new RecordingLogger();
        var handler = new DocumentationGatewayDiagnosticsHandler(logger)
        {
            InnerHandler = new StubHandler(_ => throw new HttpRequestException(
                HttpRequestError.ConnectionError, "private-message-and-token"))
        };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://example.invalid/private-path"));

        Assert.Contains("ConnectionError", Assert.Single(logger.Messages));
        Assert.DoesNotContain("private", logger.Messages[0]);
    }

    [Fact]
    public async Task UnsuccessfulResponseLogsStatusWithoutRequestDetails()
    {
        var logger = new RecordingLogger();
        var handler = new DocumentationGatewayDiagnosticsHandler(logger)
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized))
        };
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync("https://example.invalid/private-path");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("401", Assert.Single(logger.Messages));
        Assert.DoesNotContain("private", logger.Messages[0]);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class RecordingLogger : ILogger<DocumentationGatewayDiagnosticsHandler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
