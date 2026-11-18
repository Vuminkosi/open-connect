using OpenConnect.LiveUI;

namespace OpenConnect.Samples.Blazor;

/// <summary>Allows only reversible Todo page filtering and visual guidance in the acceptance sample.</summary>
public sealed class TodoLiveUIActionAuthorizer : ILiveUIActionAuthorizer
{
    private const string SearchLabel = "Search tasks by title or description...";
    private const string AllTabId = "route.root.components-todolistcomponent-razor-tab-all";
    private const string ActiveTabId = "route.root.components-todolistcomponent-razor-tab-active";
    private const string CompletedTabId = "route.root.components-todolistcomponent-razor-tab-completed";
    private const string SearchInputId = "route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description";
    private const string TaskListId = "route.root.components-todolistcomponent-razor-list-task-list";
    private const string TodoRegionId = "route.root.components-todolistcomponent-razor-region-todo-list";
    private const string ProgressRegionId = "route.root.components-todoprogresscomponent-razor-region-todo-progress";

    public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(
        LiveUIExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Action.Type == LiveUIActionType.Navigate)
        {
            var destination = context.Manifest.Routes.SingleOrDefault(route =>
                route.Id == context.Action.Parameters.DestinationRouteId);
            return destination?.Path == "/" ? Allow("todo_home_navigation") : Deny();
        }

        if (context.Action.Type == LiveUIActionType.ClearHighlight)
            return context.Route.Path == "/" ? Allow("todo_visual_clear") : Deny();

        if (context.Route.Path != "/") return Deny();

        if (context.Target is null)
            return context.Action.Type == LiveUIActionType.Inspect ? Allow("todo_page_inspection") : Deny();

        var target = context.Target;
        var decision = context.Action.Type switch
        {
            LiveUIActionType.SelectTab when IsTab(target) &&
                target.Label is "All" or "Active" or "Completed" => Allow("todo_status_filter"),
            LiveUIActionType.SetValue when target.Id == SearchInputId && target.Kind == LiveUITargetKind.Input && target.Label == SearchLabel &&
                context.Action.Parameters.Value is { Length: <= 2_000 } => Allow("todo_search_filter"),
            LiveUIActionType.Focus when target.Id == SearchInputId && target.Kind == LiveUITargetKind.Input && target.Label == SearchLabel => Allow("todo_search_focus"),
            LiveUIActionType.Inspect when IsTaskList(target) => Allow("todo_list_inspection"),
            LiveUIActionType.Highlight or LiveUIActionType.Scroll when IsTaskList(target) || IsProgressRegion(target) => Allow("todo_visual_guidance"),
            LiveUIActionType.WaitForState when IsTab(target) &&
                context.Action.Parameters.StateName == "selected" && target.Label is "All" or "Active" or "Completed" => Allow("todo_filter_verification"),
            LiveUIActionType.WaitForState when IsTaskList(target) &&
                (context.Action.Parameters.StateName is "textContains" or "textNotContains") &&
                context.Action.Parameters.StateValue is { Length: <= 128 } => Allow("todo_search_verification"),
            _ => Deny()
        };

        return decision;
    }

    private static bool IsTab(LiveUITarget target) =>
        target.Kind == LiveUITargetKind.Tab && target.Id switch
        {
            AllTabId => target.Label == "All",
            ActiveTabId => target.Label == "Active",
            CompletedTabId => target.Label == "Completed",
            _ => false
        };

    private static bool IsTaskList(LiveUITarget target) =>
        (target.Id == TaskListId && target.Kind == LiveUITargetKind.List && target.Label == "Task list") ||
        (target.Id == TodoRegionId && target.Kind == LiveUITargetKind.Region && target.Label == "todo-list");

    private static bool IsProgressRegion(LiveUITarget target) =>
        target.Id == ProgressRegionId && target.Kind == LiveUITargetKind.Region && target.Label == "todo-progress";

    private static ValueTask<LiveUIAuthorizationDecision> Allow(string code) =>
        ValueTask.FromResult(new LiveUIAuthorizationDecision(true, code));

    private static ValueTask<LiveUIAuthorizationDecision> Deny() =>
        ValueTask.FromResult(new LiveUIAuthorizationDecision(false, "todo_action_not_allowed"));
}
