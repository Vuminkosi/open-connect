using OpenConnect.Components.Contracts;
using OpenConnect.LiveUI.Blazor;
using Xunit;

namespace OpenConnect.LiveUI.Blazor.Tests;

public sealed class LiveUISpeechPlaybackFenceTests
{
    [Fact]
    public void ShouldClearHighlights_IgnoresCompletionFromAnOlderPlaybackEpoch()
    {
        var fence = new LiveUISpeechPlaybackFence();

        Assert.False(fence.ShouldClearHighlights(Event(2, ComposerSpeechPlaybackStage.Started)));
        Assert.False(fence.ShouldClearHighlights(Event(1, ComposerSpeechPlaybackStage.ResponseCompleted)));
        Assert.False(fence.ShouldClearHighlights(Event(2, ComposerSpeechPlaybackStage.Progress)));
        Assert.True(fence.ShouldClearHighlights(Event(2, ComposerSpeechPlaybackStage.ResponseCompleted)));
        Assert.False(fence.ShouldClearHighlights(Event(2, ComposerSpeechPlaybackStage.Started)));
    }

    [Fact]
    public void ShouldClearHighlights_InterruptionFencesLateEventsAndAllowsANewerEpoch()
    {
        var fence = new LiveUISpeechPlaybackFence();

        Assert.False(fence.ShouldClearHighlights(Event(5, ComposerSpeechPlaybackStage.Started)));
        Assert.True(fence.ShouldClearHighlights(Event(6, ComposerSpeechPlaybackStage.Interrupted)));
        Assert.False(fence.ShouldClearHighlights(Event(5, ComposerSpeechPlaybackStage.SegmentCompleted)));
        Assert.False(fence.ShouldClearHighlights(Event(6, ComposerSpeechPlaybackStage.Started)));
        Assert.False(fence.ShouldClearHighlights(Event(7, ComposerSpeechPlaybackStage.Started)));
        Assert.True(fence.ShouldClearHighlights(Event(7, ComposerSpeechPlaybackStage.ResponseCompleted)));
    }

    [Fact]
    public void ShouldClearHighlights_ClearsWhenSpeechCompletesWithoutAStartedEvent()
    {
        var fence = new LiveUISpeechPlaybackFence();

        Assert.True(fence.ShouldClearHighlights(Event(0, ComposerSpeechPlaybackStage.ResponseCompleted)));
        Assert.False(fence.ShouldClearHighlights(Event(-1, ComposerSpeechPlaybackStage.Interrupted)));
    }

    private static ComposerSpeechPlaybackEvent Event(long epoch, ComposerSpeechPlaybackStage stage) =>
        new(epoch, 0, 12, stage);
}
