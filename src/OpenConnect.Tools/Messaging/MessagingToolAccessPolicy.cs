using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.Tools.Messaging;

/// <summary>
/// Wraps an inner <see cref="IToolAccessPolicy"/> to enforce host enablement and per-agent
/// <see cref="MessagingPermission"/> grants for the 11 canonical messaging tools (default deny).
/// </summary>
public sealed class MessagingToolAccessPolicy : IToolAccessPolicy
{
    private readonly IToolAccessPolicy _inner;
    private readonly MessagingToolPolicyOptions _options;

    public MessagingToolAccessPolicy(IToolAccessPolicy inner, MessagingToolPolicyOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public static MessagingPermission GetRequiredPermission(string toolId) => toolId switch
    {
        MessagingToolIds.ResolveRecipient => MessagingPermission.Discover,
        MessagingToolIds.Send => MessagingPermission.Send,
        MessagingToolIds.RequestResponse => MessagingPermission.Send,
        MessagingToolIds.GetStatus => MessagingPermission.Receive,
        MessagingToolIds.GetHistory => MessagingPermission.Receive,
        MessagingToolIds.ScheduleFollowUp => MessagingPermission.FollowUp,
        MessagingToolIds.CancelFollowUp => MessagingPermission.FollowUp,
        MessagingToolIds.SendVoiceNote => MessagingPermission.Attachment,
        MessagingToolIds.StartCall => MessagingPermission.Call,
        MessagingToolIds.RequestApproval => MessagingPermission.Approval,
        MessagingToolIds.Escalate => MessagingPermission.Send,
        _ => throw new ArgumentException($"Unknown messaging tool '{toolId}'.", nameof(toolId))
    };

    public async ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!MessagingToolIds.IsMessagingTool(request.ToolId))
            return await _inner.IsAllowedAsync(request, cancellationToken).ConfigureAwait(false);

        if (!_options.Enabled)
            return false;

        var required = GetRequiredPermission(request.ToolId.Value);
        if (!_options.HasPermission(request.AgentId, required))
            return false;

        return await _inner.IsAllowedAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
