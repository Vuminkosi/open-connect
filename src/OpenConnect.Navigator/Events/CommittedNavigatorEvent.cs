namespace OpenConnect.Navigator.Events;

/// <summary>Transport-safe representation of a committed Navigator event.</summary>
public sealed record CommittedNavigatorEvent(
    string EventId,
    string SessionId,
    string? TaskId,
    long Sequence,
    string? CorrelationId,
    string Type,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string> Metadata);
