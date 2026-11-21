using OpenConnect.LiveUI;

namespace OpenConnect.Docs.Services;

// Public documentation guidance only; Cloud administration remains user-operated.
public sealed class DocumentationLiveUIActionAuthorizer : ILiveUIActionAuthorizer
{
    public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        static bool PublicRoute(string? path) => path is "/" or "/docs" or "/api";
        if (!PublicRoute(context.Route.Path)) return Deny();
        if (context.Action.Type == LiveUIActionType.Navigate)
        {
            var destination = context.Manifest.Routes.SingleOrDefault(route => route.Id == context.Action.Parameters.DestinationRouteId);
            return PublicRoute(destination?.Path) ? Allow() : Deny();
        }
        if (context.Action.Type == LiveUIActionType.ClearHighlight) return Allow();
        if (context.Action.Type == LiveUIActionType.Inspect && context.Target is null) return Allow();
        return context.Target is { Kind: LiveUITargetKind.Heading or LiveUITargetKind.Region or LiveUITargetKind.Link } &&
            context.Action.Type is LiveUIActionType.Inspect or LiveUIActionType.Highlight or LiveUIActionType.Scroll ? Allow() : Deny();
    }

    private static ValueTask<LiveUIAuthorizationDecision> Allow() => ValueTask.FromResult(new LiveUIAuthorizationDecision(true, "documentation_guidance"));
    private static ValueTask<LiveUIAuthorizationDecision> Deny() => ValueTask.FromResult(new LiveUIAuthorizationDecision(false, "documentation_action_not_allowed"));
}
