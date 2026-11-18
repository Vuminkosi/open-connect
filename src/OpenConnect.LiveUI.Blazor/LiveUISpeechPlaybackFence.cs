using OpenConnect.Components.Contracts;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Rejects stale composer playback events before they can clear current LiveUI highlights.</summary>
public sealed class LiveUISpeechPlaybackFence
{
    private readonly object _gate = new();
    private long _currentEpoch;
    private bool _terminal;

    /// <summary>Returns true when the event belongs to the current playback and should clear highlights.</summary>
    public bool ShouldClearHighlights(ComposerSpeechPlaybackEvent playbackEvent)
    {
        ArgumentNullException.ThrowIfNull(playbackEvent);
        if (playbackEvent.PlaybackEpoch < 0)
            return false;

        lock (_gate)
        {
            switch (playbackEvent.Stage)
            {
                case ComposerSpeechPlaybackStage.Started:
                    if (playbackEvent.PlaybackEpoch == 0 || playbackEvent.PlaybackEpoch < _currentEpoch ||
                        playbackEvent.PlaybackEpoch == _currentEpoch && _terminal)
                        return false;

                    if (playbackEvent.PlaybackEpoch > _currentEpoch)
                    {
                        _currentEpoch = playbackEvent.PlaybackEpoch;
                        _terminal = false;
                    }
                    return false;

                case ComposerSpeechPlaybackStage.ResponseCompleted:
                case ComposerSpeechPlaybackStage.Interrupted:
                    if (playbackEvent.PlaybackEpoch < _currentEpoch)
                        return false;
                    _currentEpoch = playbackEvent.PlaybackEpoch;
                    _terminal = true;
                    return true;

                case ComposerSpeechPlaybackStage.Progress:
                case ComposerSpeechPlaybackStage.SegmentCompleted:
                default:
                    return false;
            }
        }
    }
}
