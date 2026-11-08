using System.Diagnostics;
using OpenConnect.Navigator.Diagnostics;
using Xunit;

namespace OpenConnect.Navigator.Tests.Diagnostics;

public sealed class NavigatorTelemetryTests
{
    [Fact]
    public void SafeTags_DropsCanaryContentAndUnknownDimensions()
    {
        const string email = "pii-canary@example.invalid";
        const string dom = "<div>dom-canary</div>";
        const string audio = "audio:audio-canary";
        const string secret = "Bearer secret-canary";
        var tags = NavigatorActivitySource.SafeTags([
            new("outcome", "success"), new("modelRevision", "r7"),
            new("sessionId", email), new("stepKind", dom),
            new("voiceId", audio), new("taskId", secret), new("prompt", "prompt-canary")
        ]);

        Assert.Equal(2, tags.Length);
        Assert.DoesNotContain(tags, item => item.Value?.ToString()?.Contains("canary", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(tags, item => item.Key == "prompt");
    }

    [Fact]
    public void Activity_EmitsOnlySafeAllowlistedMetadata()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NavigatorActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = NavigatorActivitySource.StartActivity("navigation.step", [
            new("stepKind", "navigate"), new("prompt", "prompt-canary"), new("actionType", "<button>secret-canary</button>")
        ]);

        Assert.NotNull(activity);
        Assert.Equal("navigate", activity!.GetTagItem("stepKind"));
        Assert.Null(activity.GetTagItem("prompt"));
        Assert.Null(activity.GetTagItem("actionType"));
    }

    [Fact]
    public void SafeTags_PreservesOnlyBoundedFiniteMeasurementValues()
    {
        var tags = NavigatorActivitySource.SafeTags([
            new("durationMs", 12L), new("count", 2.5d), new("count", 1_000_000_001L),
            new("durationMs", double.NaN), new("count", double.PositiveInfinity),
            new("durationMs", -1L), new("durationMs", "12")
        ]);

        Assert.Equal(2, tags.Length);
        Assert.Contains(tags, tag => tag.Key == "durationMs" && Equals(tag.Value, 12L));
        Assert.Contains(tags, tag => tag.Key == "count" && Equals(tag.Value, 2.5d));
    }

    [Fact]
    public void SafeMetricTags_ExcludesCorrelationIdentifiers()
    {
        var tags = NavigatorActivitySource.SafeMetricTags([
            new("outcome", "success"), new("kind", "navigate"), new("provider", "local"),
            new("sessionId", "session-123"), new("eventId", "event-123"), new("taskId", "task-123")
        ]);

        Assert.Equal(new[] { "outcome", "kind", "provider" }, tags.Select(tag => tag.Key));
    }
}
