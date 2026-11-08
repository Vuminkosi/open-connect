using OpenConnect.Navigator.Events;
using Xunit;

namespace OpenConnect.Navigator.Tests.Events;

public sealed class CommittedEventProjectorTests
{
    [Fact]
    public async Task Validates_and_projects_repeated_events_with_independent_safe_copies()
    {
        var projector = new CommittedEventProjector();
        var input = SafeEvent();

        var projected = await projector.ProjectAsync(input);
        var duplicate = await projector.ProjectAsync(input);

        Assert.NotNull(projected);
        Assert.Equal(input.EventId, projected.EventId);
        Assert.NotNull(duplicate);
        Assert.NotSame(projected.Metadata, duplicate.Metadata);
        Assert.NotSame(input.Metadata, projected.Metadata);
    }

    [Fact]
    public async Task Ignores_optional_subscriber_failure()
    {
        var projector = new CommittedEventProjector(new ThrowingSubscriber());

        var result = await projector.ProjectAsync(SafeEvent());

        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("rawText")]
    [InlineData("screenshotUrl")]
    [InlineData("transcript")]
    public async Task Rejects_unsafe_metadata(string key)
    {
        var value = SafeEvent() with { Metadata = new Dictionary<string, string> { [key] = "sensitive" } };
        var projector = new CommittedEventProjector();

        await Assert.ThrowsAsync<ArgumentException>(() => projector.ProjectAsync(value));
    }

    [Theory]
    [InlineData("please reset password for account")]
    [InlineData("Bearer abcdef")]
    [InlineData("api_key=abc")]
    public async Task Rejects_secret_patterns_in_safe_metadata_values(string unsafeValue)
    {
        var value = SafeEvent() with { Metadata = new Dictionary<string, string> { ["state"] = unsafeValue } };

        await Assert.ThrowsAsync<ArgumentException>(() => new CommittedEventProjector().ProjectAsync(value));
    }

    [Fact]
    public async Task Rejects_image_blob_in_safe_metadata_value()
    {
        var image = "image/png," + new string('A', 1200);
        var value = SafeEvent() with { Metadata = new Dictionary<string, string> { ["state"] = image } };

        await Assert.ThrowsAsync<ArgumentException>(() => new CommittedEventProjector().ProjectAsync(value));
    }

    [Fact]
    public async Task Rejects_overlong_metadata_value()
    {
        var value = SafeEvent() with { Metadata = new Dictionary<string, string> { ["state"] = new string('a', 2001) } };

        await Assert.ThrowsAsync<ArgumentException>(() => new CommittedEventProjector().ProjectAsync(value));
    }

    [Fact]
    public async Task Allows_short_safe_metadata_value()
    {
        var value = SafeEvent() with { Metadata = new Dictionary<string, string> { ["state"] = "ready" } };

        var result = await new CommittedEventProjector().ProjectAsync(value);

        Assert.Equal("ready", result!.Metadata["state"]);
    }

    private static NavigatorEvent SafeEvent() => new(
        "evt-1", "navigator.session.started", "session-1", "task-1", 1, "corr-1", DateTimeOffset.UnixEpoch,
        new Dictionary<string, string> { ["state"] = "started" });

    private sealed class ThrowingSubscriber : IQdrantProjectionSubscriber
    {
        public Task OnCommittedAsync(CommittedNavigatorEvent value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("subscriber unavailable");
    }
}
