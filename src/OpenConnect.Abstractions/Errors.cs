namespace OpenConnect.Abstractions;

/// <summary>Stable error codes shared across OpenConnect components.</summary>
public static class ErrorCodes
{
    public const string TaskNotFound = "task_not_found";
    public const string SessionNotFound = "session_not_found";
    public const string Unauthorized = "unauthorized";
    public const string ToolNotAllowed = "tool_not_allowed";
    public const string ApprovalRequired = "approval_required";
    public const string CapabilityUnavailable = "capability_unavailable";
    public const string PrivacyBlocked = "privacy_blocked";
    public const string InvalidRequest = "invalid_request";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
}
