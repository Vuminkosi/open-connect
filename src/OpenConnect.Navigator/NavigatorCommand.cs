namespace OpenConnect.Navigator;

/// <summary>A requested action; callers must validate its grant before execution.</summary>
public sealed record NavigatorCommand(
    string CommandId,
    string NavigatorSessionId,
    long Sequence,
    string IdempotencyKey,
    long ExpectedVersion,
    NavigatorActionType Action,
    string TargetId,
    long TargetRevision,
    object? Args);
