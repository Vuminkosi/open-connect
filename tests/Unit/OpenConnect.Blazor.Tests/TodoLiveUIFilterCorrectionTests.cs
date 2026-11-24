using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using OpenConnect.Samples.Blazor;
using Xunit;

namespace OpenConnect.Blazor.Tests;

/// <summary>
/// LV-20: Verifies that authorized LiveUI Todo filter actions (due-date window + priority correction)
/// produce actual UI state changes with verified receipts through <see cref="TodoLiveUIActionAuthorizer"/>,
/// <see cref="LiveUIOrchestrator"/>, and <see cref="LiveUIAgentTools"/>.
/// </summary>
public sealed class TodoLiveUIFilterCorrectionTests : OpenConnectBunitContext
{
    private const string ActiveTabId = "route.root.components-todolistcomponent-razor-tab-active";
    private const string DueDateFilterId = "route.root.components-todolistcomponent-razor-select-due-date-filter";
    private const string PriorityFilterId = "route.root.components-todolistcomponent-razor-select-priority-filter";
    private const string TodoRegionId = "route.root.components-todolistcomponent-razor-region-todo-list";

    [Fact]
    public async Task LiveUI_TodoFilterAndPriorityCorrection_UpdatesRenderedTasksWithVerifiedReceipts()
    {
        var todayLocal = DateTime.Today;
        DateTime LocalDateToUtc(int offsetDays) =>
            DateTime.SpecifyKind(todayLocal.AddDays(offsetDays), DateTimeKind.Local).ToUniversalTime();

        var seededTasks = new TodoTaskRecord[]
        {
            new("Prepare Q4 executive brief", "High", false, LocalDateToUtc(2)),
            new("Organize sprint retro notes", "Low", false, LocalDateToUtc(3)),
            new("Submit expense receipts", "Medium", false, LocalDateToUtc(1)),
            new("Completed weekly audit", "High", true, LocalDateToUtc(2)),
            new("Plan next quarter offsite", "High", false, LocalDateToUtc(25)),
            new("Renew SSL certificate", "High", false, LocalDateToUtc(-2))
        };

        var cut = Render<TodoFilterHarness>(parameters => parameters.Add(p => p.Items, seededTasks));
        Assert.Contains("Prepare Q4 executive brief", cut.Markup);
        Assert.Contains("Completed weekly audit", cut.Markup);

        // Build real LiveUI manifest + TodoLiveUIActionAuthorizer + LiveUIOrchestrator + LiveUIAgentTools
        var activeTab = new LiveUITarget(
            ActiveTabId,
            LiveUITargetKind.Tab,
            "Active",
            new HashSet<LiveUICapability> { LiveUICapability.Activate, LiveUICapability.Filter, LiveUICapability.WaitForState });
        var dueDateFilter = new LiveUITarget(
            DueDateFilterId,
            LiveUITargetKind.Select,
            "Due date filter",
            new HashSet<LiveUICapability> { LiveUICapability.Focus, LiveUICapability.Highlight, LiveUICapability.Select });
        var priorityFilter = new LiveUITarget(
            PriorityFilterId,
            LiveUITargetKind.Select,
            "Priority filter",
            new HashSet<LiveUICapability> { LiveUICapability.Focus, LiveUICapability.Highlight, LiveUICapability.Select });
        var todoRegion = new LiveUITarget(
            TodoRegionId,
            LiveUITargetKind.Region,
            "todo-list",
            new HashSet<LiveUICapability> { LiveUICapability.Highlight, LiveUICapability.Inspect, LiveUICapability.WaitForState });

        var route = new LiveUIRoute("route.root", "/", "Todo", [activeTab, dueDateFilter, priorityFilter, todoRegion]);
        var unsignedManifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-lv20", string.Empty, [route]);
        var manifest = unsignedManifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(unsignedManifest) };

        var authorizer = new TodoLiveUIActionAuthorizer();
        var runtime = new BunitTodoLiveUIRuntime(cut);
        var orchestrator = new LiveUIOrchestrator(
            new FixedManifestProvider(manifest),
            authorizer,
            runtime,
            Options.Create(new LiveUIOptions()),
            new LiveUIManifestValidator());
        var tools = new LiveUIAgentTools(
            new FixedAgentContextAccessor(new LiveUIAgentContext("trusted-session", route.Id)),
            new FixedManifestProvider(manifest),
            authorizer,
            new LiveUIManifestValidator(),
            orchestrator,
            Options.Create(new LiveUIOptions()));

        // Verify both filter controls are surfaced in the authorized page snapshot
        var page = await tools.GetCurrentPageAsync();
        Assert.NotNull(page);
        Assert.Contains(page.Targets, t => t.TargetId == DueDateFilterId && t.Capabilities.Contains(LiveUICapability.Select));
        Assert.Contains(page.Targets, t => t.TargetId == PriorityFilterId && t.Capabilities.Contains(LiveUICapability.Select));

        // Turn 1 (§11 / §17 demo): "Show my outstanding tasks for this week."
        var activeReceipt = await tools.ExecuteAsync(ActiveTabId, "SelectTab");
        Assert.Equal(LiveUIActionOutcome.Completed, activeReceipt.Outcome);
        Assert.True(activeReceipt.PostconditionVerified);

        var thisWeekReceipt = await tools.ExecuteAsync(DueDateFilterId, "Select", value: "this-week");
        Assert.Equal(LiveUIActionOutcome.Completed, thisWeekReceipt.Outcome);
        Assert.True(thisWeekReceipt.PostconditionVerified);
        Assert.Equal("selection_verified", thisWeekReceipt.Code);
        Assert.Equal("this-week", cut.Find("#todo-due-filter").GetAttribute("value"));

        // Active tasks due this week (High, Low, Medium) are shown; completed, next-month, and overdue are excluded.
        Assert.Contains("Prepare Q4 executive brief", cut.Markup);
        Assert.Contains("Organize sprint retro notes", cut.Markup);
        Assert.Contains("Submit expense receipts", cut.Markup);
        Assert.DoesNotContain("Completed weekly audit", cut.Markup);
        Assert.DoesNotContain("Plan next quarter offsite", cut.Markup);
        Assert.DoesNotContain("Renew SSL certificate", cut.Markup);

        // Turn 2 (Interruption correction): "Actually, only high priority."
        var highPriorityReceipt = await tools.ExecuteAsync(PriorityFilterId, "Select", value: "High");
        Assert.Equal(LiveUIActionOutcome.Completed, highPriorityReceipt.Outcome);
        Assert.True(highPriorityReceipt.PostconditionVerified);
        Assert.Equal("selection_verified", highPriorityReceipt.Code);
        Assert.Equal("High", cut.Find("#todo-priority-filter").GetAttribute("value"));

        // Only the active High-priority task due this week remains visible.
        Assert.Contains("Prepare Q4 executive brief", cut.Markup);
        Assert.DoesNotContain("Organize sprint retro notes", cut.Markup);
        Assert.DoesNotContain("Submit expense receipts", cut.Markup);

        // Turn 3 (Follow-up): "Can you show me the tasks due tomorrow?"
        var resetPriorityReceipt = await tools.ExecuteAsync(PriorityFilterId, "Select", value: "all");
        var tomorrowReceipt = await tools.ExecuteAsync(DueDateFilterId, "Select", value: "tomorrow");
        Assert.Equal(LiveUIActionOutcome.Completed, resetPriorityReceipt.Outcome);
        Assert.True(resetPriorityReceipt.PostconditionVerified);
        Assert.Equal(LiveUIActionOutcome.Completed, tomorrowReceipt.Outcome);
        Assert.True(tomorrowReceipt.PostconditionVerified);
        Assert.Contains("Submit expense receipts", cut.Markup);
        Assert.DoesNotContain("Prepare Q4 executive brief", cut.Markup);

        // Unauthorized / invalid filter value is denied without mutating UI state.
        var deniedReceipt = await tools.ExecuteAsync(DueDateFilterId, "Select", value: "next-year");
        Assert.Equal(LiveUIActionOutcome.Denied, deniedReceipt.Outcome);
        Assert.False(deniedReceipt.PostconditionVerified);
        Assert.Equal("todo_action_not_allowed", deniedReceipt.Code);
        Assert.Contains("Submit expense receipts", cut.Markup);
    }

    private sealed record TodoTaskRecord(string Title, string Priority, bool IsCompleted, DateTime? DueDateUtc);

    /// <summary>
    /// Mirrors the filter controls and filtering logic of <c>samples/Blazor/Components/TodoListComponent.razor</c>
    /// so bUnit can verify real DOM state transitions across net8.0/net9.0/net10.0.
    /// </summary>
    private sealed class TodoFilterHarness : ComponentBase
    {
        private string _activeTab = "All";
        private string _priorityFilter = "all";
        private string _dueDateFilter = "all";

        [Parameter]
        public IReadOnlyList<TodoTaskRecord> Items { get; set; } = Array.Empty<TodoTaskRecord>();

        private IEnumerable<TodoTaskRecord> FilteredItems
        {
            get
            {
                var query = Items.AsEnumerable();
                if (_activeTab == "Active")
                    query = query.Where(i => !i.IsCompleted);
                else if (_activeTab == "Completed")
                    query = query.Where(i => i.IsCompleted);

                if (!string.Equals(_priorityFilter, "all", StringComparison.OrdinalIgnoreCase))
                    query = query.Where(i => string.Equals(i.Priority, _priorityFilter, StringComparison.OrdinalIgnoreCase));

                var normalizedDue = _dueDateFilter.Trim().ToLowerInvariant();
                if (normalizedDue != "all")
                {
                    var today = DateTime.Today;
                    query = normalizedDue switch
                    {
                        "today" => query.Where(i => i.DueDateUtc is { } due && due.ToLocalTime().Date == today),
                        "tomorrow" => query.Where(i => i.DueDateUtc is { } due && due.ToLocalTime().Date == today.AddDays(1)),
                        "this-week" => query.Where(i => i.DueDateUtc is { } due && due.ToLocalTime().Date >= today && due.ToLocalTime().Date <= today.AddDays(7)),
                        "overdue" => query.Where(i => !i.IsCompleted && i.DueDateUtc is { } due && due.ToLocalTime().Date < today),
                        _ => query
                    };
                }

                return query;
            }
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "section");
            builder.AddAttribute(1, "data-oc-target", "todo-list");

            foreach (var tab in new[] { "All", "Active", "Completed" })
            {
                builder.OpenElement(2, "button");
                builder.AddAttribute(3, "type", "button");
                builder.AddAttribute(4, "role", "tab");
                builder.AddAttribute(5, "aria-selected", _activeTab == tab ? "true" : "false");
                builder.AddAttribute(6, "onclick", EventCallback.Factory.Create(this, () => _activeTab = tab));
                builder.AddContent(7, tab);
                builder.CloseElement();
            }

            builder.OpenElement(10, "select");
            builder.AddAttribute(11, "id", "todo-priority-filter");
            builder.AddAttribute(12, "aria-label", "Priority filter");
            builder.AddAttribute(13, "value", _priorityFilter);
            builder.AddAttribute(14, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => _priorityFilter = e.Value?.ToString() ?? "all"));
            foreach (var option in new[] { "all", "Low", "Medium", "High", "Urgent" })
            {
                builder.OpenElement(15, "option");
                builder.AddAttribute(16, "value", option);
                builder.AddContent(17, option);
                builder.CloseElement();
            }
            builder.CloseElement();

            builder.OpenElement(20, "select");
            builder.AddAttribute(21, "id", "todo-due-filter");
            builder.AddAttribute(22, "aria-label", "Due date filter");
            builder.AddAttribute(23, "value", _dueDateFilter);
            builder.AddAttribute(24, "onchange", EventCallback.Factory.Create<ChangeEventArgs>(this, e => _dueDateFilter = e.Value?.ToString() ?? "all"));
            foreach (var option in new[] { "all", "today", "tomorrow", "this-week", "overdue" })
            {
                builder.OpenElement(25, "option");
                builder.AddAttribute(26, "value", option);
                builder.AddContent(27, option);
                builder.CloseElement();
            }
            builder.CloseElement();

            builder.OpenElement(30, "ul");
            builder.AddAttribute(31, "class", "mud-list");
            foreach (var item in FilteredItems)
            {
                builder.OpenElement(32, "li");
                builder.AddContent(33, item.Title);
                builder.CloseElement();
            }
            builder.CloseElement();

            builder.CloseElement();
        }
    }

    private sealed class BunitTodoLiveUIRuntime(IRenderedComponent<TodoFilterHarness> cut) : ILiveUIRuntime
    {
        public async ValueTask<LiveUIRuntimeResult> ExecuteAsync(
            LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            if (context.Target is null)
                return new LiveUIRuntimeResult(LiveUIActionOutcome.MissingTarget, "missing_target", false);

            switch (context.Action.Type)
            {
                case LiveUIActionType.SelectTab when context.Target.Id == ActiveTabId:
                {
                    await cut.InvokeAsync(() =>
                    {
                        var tabs = cut.FindAll("button[role='tab']");
                        var activeTab = tabs.Single(t => t.TextContent.Trim().Equals("Active", StringComparison.Ordinal));
                        activeTab.Click();
                    });
                    var selected = cut.FindAll("button[role='tab']")
                        .Single(t => t.TextContent.Trim().Equals("Active", StringComparison.Ordinal))
                        .GetAttribute("aria-selected");
                    var verified = string.Equals(selected, "true", StringComparison.OrdinalIgnoreCase);
                    return new LiveUIRuntimeResult(
                        verified ? LiveUIActionOutcome.Completed : LiveUIActionOutcome.Failed,
                        verified ? "state_verified" : "state_mismatch",
                        verified,
                        "Active");
                }

                case LiveUIActionType.Select when context.Target.Id == DueDateFilterId:
                {
                    var value = context.Action.Parameters.Value ?? string.Empty;
                    await cut.InvokeAsync(() => cut.Find("#todo-due-filter").Change(value));
                    var observed = cut.Find("#todo-due-filter").GetAttribute("value");
                    var verified = string.Equals(observed, value, StringComparison.OrdinalIgnoreCase);
                    return new LiveUIRuntimeResult(
                        verified ? LiveUIActionOutcome.Completed : LiveUIActionOutcome.Failed,
                        verified ? "selection_verified" : "selection_mismatch",
                        verified,
                        observed);
                }

                case LiveUIActionType.Select when context.Target.Id == PriorityFilterId:
                {
                    var value = context.Action.Parameters.Value ?? string.Empty;
                    await cut.InvokeAsync(() => cut.Find("#todo-priority-filter").Change(value));
                    var observed = cut.Find("#todo-priority-filter").GetAttribute("value");
                    var verified = string.Equals(observed, value, StringComparison.OrdinalIgnoreCase);
                    return new LiveUIRuntimeResult(
                        verified ? LiveUIActionOutcome.Completed : LiveUIActionOutcome.Failed,
                        verified ? "selection_verified" : "selection_mismatch",
                        verified,
                        observed);
                }

                default:
                    return new LiveUIRuntimeResult(LiveUIActionOutcome.Failed, "unsupported_action", false);
            }
        }
    }

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
}
