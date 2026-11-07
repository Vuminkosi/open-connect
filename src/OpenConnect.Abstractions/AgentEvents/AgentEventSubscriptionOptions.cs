namespace OpenConnect.Abstractions;

/// <summary>Options identifying a named consumer and the event types it subscribes to.</summary>
public sealed record AgentEventSubscriptionOptions(string ConsumerName, string[] EventTypes);
