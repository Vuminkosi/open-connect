using OpenConnect.LiveUI;

namespace OpenConnect.Docs.Services;

// Public documentation and read-only Cloud overview guidance; Cloud administration remains strictly user-operated.
public sealed class DocumentationLiveUIActionAuthorizer(DocumentationContentStore? content = null) : ILiveUIActionAuthorizer
{
    public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool PublicRoute(string? path) => path is "/" or "/docs" or "/api" ||
            (path is not null && content?.Documents.Any(document =>
                string.Equals(path, $"/docs/{document.Version}/{document.Slug}", StringComparison.Ordinal)) == true);
        static bool CloudGuidanceRoute(string? path) => string.Equals(path, "/cloud", StringComparison.Ordinal);
        bool GuidanceRoute(string? path) => PublicRoute(path) || CloudGuidanceRoute(path);
        if (!GuidanceRoute(context.Route.Path)) return Deny();
        if (context.Action.Type == LiveUIActionType.Navigate)
        {
            var destination = context.Manifest.Routes.SingleOrDefault(route => route.Id == context.Action.Parameters.DestinationRouteId);
            return GuidanceRoute(destination?.Path) ? Allow() : Deny();
        }
        if (context.Action.Type == LiveUIActionType.ClearHighlight) return Allow();
        if (context.Action.Type == LiveUIActionType.Inspect && context.Target is null) return Allow();
        if (CloudGuidanceRoute(context.Route.Path))
        {
            return context.Target is { Kind: LiveUITargetKind.Heading or LiveUITargetKind.Region or LiveUITargetKind.Link } &&
                context.Action.Type is LiveUIActionType.Inspect or LiveUIActionType.Highlight or LiveUIActionType.Scroll
                ? Allow()
                : Deny();
        }
        if (context.Route.Path == "/docs" && context.Target is { Id: "documentation-show-guides", Kind: LiveUITargetKind.Button })
            return context.Action.Type is LiveUIActionType.Inspect or LiveUIActionType.Click ? Allow() : Deny();
        if (context.Route.Path == "/docs/v1/privacy" && context.Target is { Id: "documentation-privacy-demo-run", Kind: LiveUITargetKind.Button })
            return context.Action.Type is LiveUIActionType.Inspect or LiveUIActionType.Click ? Allow() : Deny();
        if (context.Route.Path == "/docs/v1/documents-and-knowledge" && context.Target is { Id: "documentation-document-analyze", Kind: LiveUITargetKind.Button })
            return context.Action.Type is LiveUIActionType.Inspect or LiveUIActionType.Click ? Allow() : Deny();
        if (context.Route.Path == "/docs/v1/documents-and-knowledge" && context.Target is { Id: "documentation-knowledge-search", Kind: LiveUITargetKind.Button })
            return context.Action.Type is LiveUIActionType.Inspect or LiveUIActionType.Click ? Allow() : Deny();
        if (context.Route.Path == "/docs/v1/documents-and-knowledge" && context.Target is { Id: "documentation-knowledge-query", Kind: LiveUITargetKind.Input })
            return context.Action.Type switch
            {
                LiveUIActionType.Inspect or LiveUIActionType.Focus => Allow(),
                LiveUIActionType.SetValue when context.Action.Parameters.Value is { Length: <= 2000 } => Allow(),
                _ => Deny()
            };
        if (context.Route.Path == "/docs/v1/privacy" && context.Target is { Id: "documentation-privacy-demo-example", Kind: LiveUITargetKind.Select })
            return context.Action.Type switch
            {
                LiveUIActionType.Inspect or LiveUIActionType.Focus => Allow(),
                // LiveUI probes capabilities without a value; the orchestrator rejects value-less execution.
                LiveUIActionType.Select when context.Action.Parameters.Value is null or "email" or "phone" or "card" or "secret" or "ordinary" => Allow(),
                _ => Deny()
            };
        if ((context.Route.Path == "/docs" && context.Target is { Id: "documentation-search", Kind: LiveUITargetKind.Input }) ||
            (context.Route.Path == "/api" && context.Target is { Id: "documentation-api-search", Kind: LiveUITargetKind.Input }))
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
