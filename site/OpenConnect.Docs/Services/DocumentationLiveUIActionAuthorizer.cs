using OpenConnect.LiveUI;

namespace OpenConnect.Docs.Services;

// Public documentation guidance only; Cloud administration remains user-operated.
public sealed class DocumentationLiveUIActionAuthorizer(DocumentationContentStore? content = null) : ILiveUIActionAuthorizer
{
    public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool PublicRoute(string? path) => path is "/" or "/docs" or "/api" ||
            (path is not null && content?.Documents.Any(document =>
                string.Equals(path, $"/docs/{document.Version}/{document.Slug}", StringComparison.Ordinal)) == true);
        if (!PublicRoute(context.Route.Path)) return Deny();
        if (context.Action.Type == LiveUIActionType.Navigate)
        {
            var destination = context.Manifest.Routes.SingleOrDefault(route => route.Id == context.Action.Parameters.DestinationRouteId);
            return PublicRoute(destination?.Path) ? Allow() : Deny();
        }
        if (context.Action.Type == LiveUIActionType.ClearHighlight) return Allow();
        if (context.Action.Type == LiveUIActionType.Inspect && context.Target is null) return Allow();
        if (context.Route.Path == "/docs" && context.Target is { Id: "documentation-show-guides", Kind: LiveUITargetKind.Button })
            return context.Action.Type is LiveUIActionType.Inspect or LiveUIActionType.Click ? Allow() : Deny();
        if (context.Route.Path == "/docs" && context.Target is { Id: "documentation-search", Kind: LiveUITargetKind.Input })
            return context.Action.Type switch
            {
                LiveUIActionType.Inspect or LiveUIActionType.Focus => Allow(),
                LiveUIActionType.SetValue when context.Action.Parameters.Value is { Length: <= 200 } => Allow(),
                _ => Deny()
            };
        return context.Target is { Kind: LiveUITargetKind.Heading or LiveUITargetKind.Region or LiveUITargetKind.Link } &&
            context.Action.Type is LiveUIActionType.Inspect or LiveUIActionType.Highlight or LiveUIActionType.Scroll ? Allow() : Deny();
    }

    private static ValueTask<LiveUIAuthorizationDecision> Allow() => ValueTask.FromResult(new LiveUIAuthorizationDecision(true, "documentation_guidance"));
    private static ValueTask<LiveUIAuthorizationDecision> Deny() => ValueTask.FromResult(new LiveUIAuthorizationDecision(false, "documentation_action_not_allowed"));
}
