using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore.Outbox;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class NavigatorCommitHookTests
{
    [Fact]
    public async Task AfterCommitOrdersEventsAndProjectsEachSafeEvent()
    {
        var sink = new RecordingWriter();
        var hook = new NavigatorCommitHook(new NavigatorOutboxWriter(sink));
        var late = Event("late", 2);
        var early = Event("early", 1);

        await hook.AfterCommitAsync("session-1", [late, early]);

        Assert.Equal(new[] { "early", "late" }, sink.Values);
    }

    [Fact]
    public async Task AfterCommitQuarantinesEventsFromAnotherSession()
    {
        var hook = new NavigatorCommitHook(new NavigatorOutboxWriter(new RecordingWriter()));
        await hook.AfterCommitAsync("session-1", [Event("foreign", 1, sessionId: "session-2")]);
        Assert.Equal(new[] { "foreign" }, hook.QuarantinedEventIds);
        Assert.True(hook.TryDequeueQuarantined(out var recovered));
        Assert.Equal("foreign", recovered);
        Assert.False(hook.TryDequeueQuarantined(out _));
    }

    [Fact]
    public async Task AfterCommitProjectsDuplicateEventIdOnlyOnce()
    {
        var sink = new RecordingWriter();
        var hook = new NavigatorCommitHook(new NavigatorOutboxWriter(sink));
        var value = Event("same", 1);

        await hook.AfterCommitAsync("session-1", [value, value with { Sequence = 2 }]);

        Assert.Single(sink.Values);
        Assert.Equal(new[] { "same" }, hook.DedupSkippedEventIds);
        Assert.Empty(hook.QuarantinedEventIds);
    }

    [Fact]
    public async Task AfterCommitQuarantinesUnsafeEventAndContinuesWithSafeEvents()
    {
        var sink = new RecordingWriter();
        var hook = new NavigatorCommitHook(new NavigatorOutboxWriter(sink));

        await hook.AfterCommitAsync("session-1", [Event("unsafe", 1, metadataValue: "<script>"), Event("safe", 2)]);

        Assert.Equal(new[] { "safe" }, sink.Values);
        Assert.Equal(new[] { "unsafe" }, hook.QuarantinedEventIds);
    }

    private static NavigatorEvent Event(string id, long sequence, string? unsafeKey = null, string sessionId = "session-1", string metadataValue = "redacted") =>
        new(id, "command.completed", sessionId, "task-1", sequence, null, DateTimeOffset.UtcNow,
            unsafeKey is null && metadataValue == "redacted" ? new Dictionary<string, string>() : new Dictionary<string, string> { [unsafeKey ?? "safe-key"] = metadataValue });

    private sealed class RecordingWriter : INavigatorOutboxWriter
    {
        public List<string> Values { get; } = [];

        public ValueTask WriteAsync(string topic, string messageId, string payload, CancellationToken cancellationToken = default)
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            Values.Add(document.RootElement.GetProperty("payloadRef").GetString()!.Split('/')[2]);
            return ValueTask.CompletedTask;
        }
    }
}
