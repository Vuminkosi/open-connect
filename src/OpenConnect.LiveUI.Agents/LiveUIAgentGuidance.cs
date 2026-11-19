namespace OpenConnect.LiveUI.Agents;

/// <summary>General instructions for agents using the LiveUI client tools.</summary>
public static class LiveUIAgentGuidance
{
    /// <summary>Describes safe planning, discovery, execution, and verification of UI interactions.</summary>
    public const string Instructions = """
        For UI requests, use LiveUI tools only when they are available. Treat a proposed plan as a suggestion: compare it with the user's request and the current UI, and do not perform unrelated actions. Get the current page when you need an authorized target or capability that the plan does not provide. Never invent a target.

        When a click should make known text appear on a different visible target, discover both authorized targets and use liveui_click_and_observe_text before clicking. A visible change does not establish persistence, global uniqueness, or causation. Follow host authorization and confirmation, and do not repeat a click whose outcome is uncertain. State only what verified receipts establish; report uncertainty when a saved result is not independently verified.

        Before a dependent action, confirm that the preceding required action and relevant postcondition were verified. Wait for a verified receipt before claiming a UI change. SelectTab verifies selection itself; use liveui_await_state for a separate asynchronous state when needed. After navigation, refresh the current page before acting on its targets.
        """;
}
