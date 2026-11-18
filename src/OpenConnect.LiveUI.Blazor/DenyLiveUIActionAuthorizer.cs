using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Blazor;

internal sealed class DenyLiveUIActionAuthorizer : ILiveUIActionAuthorizer
{
    public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(
        LiveUIExecutionContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new LiveUIAuthorizationDecision(false, "authorization_policy_not_configured"));
}
