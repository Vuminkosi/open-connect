using Microsoft.Extensions.Options;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.Samples.Blazor;
using Xunit;

namespace OpenConnect.Blazor.Tests;

public sealed class TodoLiveUIActionAuthorizerTests
{
    [Fact]
    public async Task AllowsOnlyTheCompiledCompletedTabForStatusFiltering()
    {
        var allowed = await AuthorizeAsync(
            Target("route.root.components-todolistcomponent-razor-tab-completed", LiveUITargetKind.Tab, "Completed"),
            LiveUIActionType.SelectTab);
        var sameLabelDifferentId = await AuthorizeAsync(
            Target("another-page.tab-completed", LiveUITargetKind.Tab, "Completed"),
            LiveUIActionType.SelectTab);

        Assert.True(allowed.Allowed);
        Assert.Equal("todo_status_filter", allowed.Code);
        Assert.False(sameLabelDifferentId.Allowed);
    }

    [Fact]
    public async Task AllowsOnlyTheCompiledSearchInputAndEnforcesValueLimit()
    {
        var allowed = await AuthorizeAsync(
            Target("route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description", LiveUITargetKind.Input, "Search tasks by title or description..."),
            LiveUIActionType.SetValue,
            new LiveUIActionParameters(Value: "report"));
        var sameLabelDifferentId = await AuthorizeAsync(
            Target("other.search", LiveUITargetKind.Input, "Search tasks by title or description..."),
            LiveUIActionType.SetValue,
            new LiveUIActionParameters(Value: "report"));
        var excessiveValue = await AuthorizeAsync(
            Target("route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description", LiveUITargetKind.Input, "Search tasks by title or description..."),
            LiveUIActionType.SetValue,
            new LiveUIActionParameters(Value: new string('x', 2_001)));

        Assert.True(allowed.Allowed);
        Assert.False(sameLabelDifferentId.Allowed);
        Assert.False(excessiveValue.Allowed);
    }

    [Fact]
    public async Task AllowsDueDateAndPriorityFilterSelectionsWithValidatedValues()
    {
        var dueTarget = Target(
            "route.root.components-todolistcomponent-razor-select-due-date-filter",
            LiveUITargetKind.Select,
            "Due date filter");
        var priorityTarget = Target(
            "route.root.components-todolistcomponent-razor-select-priority-filter",
            LiveUITargetKind.Select,
            "Priority filter");

        foreach (var window in new[] { "this-week", "tomorrow", "today", "overdue", "all", "Due this week" })
        {
            var decision = await AuthorizeAsync(dueTarget, LiveUIActionType.Select, new LiveUIActionParameters(Value: window));
            Assert.True(decision.Allowed, $"Expected due-date value '{window}' to be allowed.");
            Assert.Equal("todo_due_date_filter", decision.Code);
        }

        foreach (var priority in new[] { "High", "high", "Urgent", "Medium", "Low", "all" })
        {
            var decision = await AuthorizeAsync(priorityTarget, LiveUIActionType.Select, new LiveUIActionParameters(Value: priority));
            Assert.True(decision.Allowed, $"Expected priority value '{priority}' to be allowed.");
            Assert.Equal("todo_priority_filter", decision.Code);
        }

        var invalidDue = await AuthorizeAsync(dueTarget, LiveUIActionType.Select, new LiveUIActionParameters(Value: "next-year"));
        var invalidPriority = await AuthorizeAsync(priorityTarget, LiveUIActionType.Select, new LiveUIActionParameters(Value: "critical"));
        var spoofedDue = await AuthorizeAsync(
            Target("other.due-filter", LiveUITargetKind.Select, "Due date filter"),
            LiveUIActionType.Select,
            new LiveUIActionParameters(Value: "this-week"));

        Assert.False(invalidDue.Allowed);
        Assert.False(invalidPriority.Allowed);
        Assert.False(spoofedDue.Allowed);
    }

    [Fact]
    public async Task DeniesTaskCreationAndUnrecognizedTargets()
    {
        var addTask = await AuthorizeAsync(
            Target("route.root.components-todoeditorcomponent-razor-button-add-task", LiveUITargetKind.Button, "Add Task"),
            LiveUIActionType.Click);
        var unrecognizedTaskList = await AuthorizeAsync(
            Target("other.task-list", LiveUITargetKind.List, "Task list"),
            LiveUIActionType.Inspect);

        Assert.False(addTask.Allowed);
        Assert.False(unrecognizedTaskList.Allowed);
    }

    [Fact]
    public async Task Agent_tools_hide_and_deny_add_task_through_the_real_authorization_pipeline()
    {
        var addTask = Target(
            "route.root.components-todoeditorcomponent-razor-button-add-task",
            LiveUITargetKind.Button,
            "Add Task") with
        {
            Capabilities = new HashSet<LiveUICapability>
            {
                LiveUICapability.Activate,
                LiveUICapability.Highlight
            }
        };
        var completedTab = Target(
            "route.root.components-todolistcomponent-razor-tab-completed",
            LiveUITargetKind.Tab,
            "Completed") with
        {
            Capabilities = new HashSet<LiveUICapability>
            {
                LiveUICapability.Activate,
                LiveUICapability.Filter,
                LiveUICapability.WaitForState
            }
        };
        var search = Target("route.root.components-todolistcomponent-razor-input-search-tasks-by-title-or-description",
            LiveUITargetKind.Input, "Search tasks by title or description...") with
        {
            Capabilities = new HashSet<LiveUICapability> { LiveUICapability.SetValue, LiveUICapability.Focus }
        };
        var route = new LiveUIRoute("route.root", "/", "Todo", [addTask, completedTab, search]);
        var unsignedManifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-1", string.Empty, [route]);
        var manifest = unsignedManifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(unsignedManifest) };
        var runtime = new RecordingRuntime();
        var authorizer = new TodoLiveUIActionAuthorizer();
        var orchestrator = new LiveUIOrchestrator(
            new FixedManifestProvider(manifest), authorizer, runtime,
            Options.Create(new LiveUIOptions()), new LiveUIManifestValidator());
        var tools = new LiveUIAgentTools(
            new FixedAgentContextAccessor(new LiveUIAgentContext("trusted-session", route.Id)),
            new FixedManifestProvider(manifest), authorizer, new LiveUIManifestValidator(), orchestrator,
            Options.Create(new LiveUIOptions()));

        var page = await tools.GetCurrentPageAsync();
        var deniedReceipt = await tools.ExecuteAsync(addTask.Id, "Click");

        Assert.NotNull(page);
        Assert.DoesNotContain(page.Targets, target => target.TargetId == addTask.Id);
        Assert.Contains(page.Targets, target => target.TargetId == completedTab.Id &&
            target.Capabilities.Contains(LiveUICapability.Activate));
        Assert.Contains(page.Targets, target => target.TargetId == search.Id &&
            target.Capabilities.Contains(LiveUICapability.SetValue));
        Assert.Equal(LiveUIActionOutcome.Denied, deniedReceipt.Outcome);
        Assert.Equal("todo_action_not_allowed", deniedReceipt.Code);
        Assert.False(deniedReceipt.PostconditionVerified);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task ThrowsWhenAuthorizationIsCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TodoLiveUIActionAuthorizer().AuthorizeAsync(Context(
                Target("route.root.components-todolistcomponent-razor-tab-completed", LiveUITargetKind.Tab, "Completed"),
                LiveUIActionType.SelectTab), cancellation.Token).AsTask());
    }

    private static Task<LiveUIAuthorizationDecision> AuthorizeAsync(
        LiveUITarget target,
        LiveUIActionType actionType,
        LiveUIActionParameters? parameters = null) =>
        new TodoLiveUIActionAuthorizer().AuthorizeAsync(Context(target, actionType, parameters)).AsTask();

    private static LiveUIExecutionContext Context(
        LiveUITarget? target,
        LiveUIActionType actionType,
        LiveUIActionParameters? parameters = null)
    {
        var route = new LiveUIRoute("route.root", "/", "Todo", target is null ? [] : [target]);
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-1", "hash", [route]);
        var action = new LiveUIAction("action-1", LiveUISchema.CurrentVersion, "session-1", "hash",
            route.Id, target?.Id, actionType, parameters ?? new LiveUIActionParameters(), TimeSpan.FromSeconds(5));
        return new LiveUIExecutionContext(manifest, route, target, action);
    }

    private static LiveUITarget Target(string id, LiveUITargetKind kind, string label) =>
        new(id, kind, label, new HashSet<LiveUICapability>());

    private sealed class FixedManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class FixedAgentContextAccessor(LiveUIAgentContext context) : ILiveUIAgentContextAccessor
    {
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(context);
    }

    private sealed class RecordingRuntime : ILiveUIRuntime
    {
        public int Calls { get; private set; }

        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(
            LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return ValueTask.FromResult(new LiveUIRuntimeResult(
                LiveUIActionOutcome.Completed, "verified", true, "verified"));
        }
    }
}
