namespace OpenConnect.Navigator;

/// <summary>Final or indeterminate result of a command intent.</summary>
public sealed record NavigatorReceipt(string Intent, NavigatorActionType Action, NavigatorCommandOutcome Outcome, string? Postcondition);

/// <summary>Outcome classification for safely reporting command execution.</summary>
public enum NavigatorCommandOutcome { Completed, Denied, Uncertain, Failed }
