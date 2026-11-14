using OpenConnect.LiveUI;

namespace OpenConnect.Samples.Blazor;

/// <summary>Allows only reversible Todo page filtering and visual guidance in the acceptance sample.</summary>
public sealed class TodoLiveUIActionAuthorizer : ILiveUIActionAuthorizer
{
    private const string SearchLabel = "Search tasks by title or description...";

    public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(
        LiveUIExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Route.Path != "/") return Deny();

        if (context.Action.Type == LiveUIActionType.ClearHighlight)
            return Allow("todo_visual_clear");

        if (context.Action.Type == LiveUIActionType.Navigate &&
            context.Action.Parameters.DestinationRouteId == context.Route.Id && context.Route.Path == "/")
            return Allow("todo_home_navigation");

        if (context.Target is null)
            return context.Action.Type == LiveUIActionType.Inspect ? Allow("todo_page_inspection") : Deny();

        var target = context.Target;
        var decision = context.Action.Type switch
        {
            LiveUIActionType.SelectTab when target.Kind == LiveUITargetKind.Tab &&
                target.Label is "All" or "Active" or "Completed" => Allow("todo_status_filter"),
            LiveUIActionType.SetValue when target.Kind == LiveUITargetKind.Input && target.Label == SearchLabel &&
                context.Action.Parameters.Value is { Length: <= 2_000 } => Allow("todo_search_filter"),
            LiveUIActionType.Focus when target.Kind == LiveUITargetKind.Input && target.Label == SearchLabel => Allow("todo_search_focus"),
            LiveUIActionType.Inspect when (target.Kind is LiveUITargetKind.Region or LiveUITargetKind.List) &&
                target.Label is "todo-list" or "Task list" => Allow("todo_list_inspection"),
            LiveUIActionType.Highlight or LiveUIActionType.Scroll when (target.Kind is LiveUITargetKind.Region or LiveUITargetKind.List) &&
                target.Label is "todo-list" or "Task list" or "todo-progress" => Allow("todo_visual_guidance"),
            LiveUIActionType.WaitForState when target.Kind == LiveUITargetKind.Tab &&
                context.Action.Parameters.StateName == "selected" && target.Label is "All" or "Active" or "Completed" => Allow("todo_filter_verification"),
            _ => Deny()
        };

        return decision;
    }

    private static ValueTask<LiveUIAuthorizationDecision> Allow(string code) =>
        ValueTask.FromResult(new LiveUIAuthorizationDecision(true, code));

    private static ValueTask<LiveUIAuthorizationDecision> Deny() =>
        ValueTask.FromResult(new LiveUIAuthorizationDecision(false, "todo_action_not_allowed"));
}
