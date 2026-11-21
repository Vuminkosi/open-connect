using Microsoft.Extensions.Logging;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class VoiceRealtimeLoggingTests
{
    [Theory]
    [InlineData("private@example.test")]
    [InlineData("error")]
    [InlineData("speechComplete")]
    [InlineData(null)]
    public void ProviderEventsNeverLogUntrustedEventContent(string? eventType)
    {
        var logger = new RecordingLogger();
        VoiceRealtimeWebSocketHandler.LogProviderEvent(logger, eventType);
        Assert.Single(logger.Messages);
        Assert.DoesNotContain("private@example.test", logger.Messages[0]);
        Assert.DoesNotContain("provider error code", logger.Messages[0], StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
