namespace OpenConnect.LiveUI.Blazor;

internal sealed class LiveUIInteractionReplayFence
{
    private const int MaximumSeenInteractions = 4096;
    private readonly object _gate = new();
    private readonly HashSet<(string Session, Guid Correlation)> _seen = [];

    public bool TryBegin(string sessionId, Guid correlationId)
    {
        lock (_gate)
        {
            return _seen.Count < MaximumSeenInteractions && _seen.Add((sessionId, correlationId));
        }
    }
}
