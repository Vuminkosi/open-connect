using OpenConnect.Abstractions;
using OpenConnect.Samples.Blazor.Diagnostics;
using OpenConnect.Samples.Blazor.Models;

namespace OpenConnect.Samples.Blazor.Agents;

/// <summary>
/// Authorized operational Todo business tools invoked by operational agents and endpoints.
/// Enforces strict tenant isolation, principal authorization, audit trail recording, and
/// automatic cancellation of pending follow-ups on completion, deletion, or reassignment.
/// </summary>
public sealed class TodoAgentBusinessTools(
    SqliteTodoStore todoStore,
    TodoAcceptanceDiagnosticsService diagnostics)
{
    public const string ToolQueryTasks = "todo_query_tasks";
    public const string ToolUpdateStatus = "todo_update_status";
    public const string ToolAssignTask = "todo_assign_task";
    public const string ToolRequestApproval = "todo_request_approval";
    public const string ToolRecordOutcome = "todo_record_outcome";

    public static readonly IReadOnlyList<string> ToolNames =
    [
        ToolQueryTasks,
        ToolUpdateStatus,
        ToolAssignTask,
        ToolRequestApproval,
        ToolRecordOutcome
    ];

    public async Task<TodoToolResult<IReadOnlyList<TodoItem>>> QueryTasksAsync(
        TenantId tenantId,
        PrincipalId actorPrincipalId,
        TodoStatus? statusFilter = null,
        PrincipalId? assigneeFilter = null,
        bool overdueOnly = false,
        CancellationToken cancellationToken = default)
    {
        var actor = TodoDirectorySeed.FindPrincipal(tenantId, actorPrincipalId);
        if (actor is null)
            return Deny<IReadOnlyList<TodoItem>>(tenantId, ToolQueryTasks, "Principal is not authorized for this tenant.");

        var all = await todoStore.GetAllAsync(tenantId, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        IEnumerable<TodoItem> query = all;
        if (statusFilter.HasValue)
            query = query.Where(t => t.Status == statusFilter.Value);
        if (assigneeFilter.HasValue)
        {
            var targetText = assigneeFilter.Value.Value.ToString();
            query = query.Where(t => string.Equals(t.AssignedPrincipalId, targetText, StringComparison.Ordinal));
        }
        if (overdueOnly)
            query = query.Where(t => !t.IsCompleted && t.DueDateUtc.HasValue && t.DueDateUtc.Value <= now);

        return TodoToolResult<IReadOnlyList<TodoItem>>.Ok(query.ToArray());
    }

    public async Task<TodoToolResult<TodoItem>> UpdateStatusAsync(
        TenantId tenantId,
        PrincipalId actorPrincipalId,
        Guid todoId,
        TodoStatus newStatus,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var actor = TodoDirectorySeed.FindPrincipal(tenantId, actorPrincipalId);
        if (actor is null)
            return Deny<TodoItem>(tenantId, ToolUpdateStatus, "Principal is not authorized for this tenant.");

        var existing = await todoStore.GetByIdAsync(tenantId, todoId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return TodoToolResult<TodoItem>.Fail(ErrorCodes.TaskNotFound, "Task not found in tenant.");

        // Non-supervisors may only update tasks assigned to them or unassigned tasks
        var isSupervisor = string.Equals(actor.Role, "Supervisor", StringComparison.OrdinalIgnoreCase);
        var isAssignee = string.Equals(existing.AssignedPrincipalId, actor.PrincipalId.Value.ToString(), StringComparison.Ordinal);
        if (!isSupervisor && !string.IsNullOrWhiteSpace(existing.AssignedPrincipalId) && !isAssignee)
            return Deny<TodoItem>(tenantId, ToolUpdateStatus, "Employees may only update their own assigned tasks.");

        var updated = await todoStore.UpdateWorkflowStatusAsync(
            tenantId,
            todoId,
            newStatus,
            actor.Key,
            note,
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
            return TodoToolResult<TodoItem>.Fail(ErrorCodes.TaskNotFound, "Task not found in tenant.");

        diagnostics.RecordSecurityEvidence(
            category: "agent_tool",
            outcome: "allowed",
            summary: $"{ToolUpdateStatus}: {actor.Key} updated task {todoId} to {newStatus}.",
            tenantId: tenantId.Value.ToString());

        return TodoToolResult<TodoItem>.Ok(updated);
    }

    public async Task<TodoToolResult<TodoItem>> AssignTaskAsync(
        TenantId tenantId,
        PrincipalId actorPrincipalId,
        Guid todoId,
        PrincipalId newAssigneePrincipalId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var actor = TodoDirectorySeed.FindPrincipal(tenantId, actorPrincipalId);
        if (actor is null)
            return Deny<TodoItem>(tenantId, ToolAssignTask, "Principal is not authorized for this tenant.");

        if (!string.Equals(actor.Role, "Supervisor", StringComparison.OrdinalIgnoreCase))
            return Deny<TodoItem>(tenantId, ToolAssignTask, "Only supervisors may reassign tasks.");

        var targetAssignee = TodoDirectorySeed.FindPrincipal(tenantId, newAssigneePrincipalId);
        if (targetAssignee is null)
            return Deny<TodoItem>(tenantId, ToolAssignTask, "Cross-tenant or unknown assignee rejected.");

        var updated = await todoStore.AssignTaskAsync(
            tenantId,
            todoId,
            newAssigneePrincipalId,
            actor.Key,
            reason,
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
            return TodoToolResult<TodoItem>.Fail(ErrorCodes.TaskNotFound, "Task not found in tenant.");

        diagnostics.RecordSecurityEvidence(
            category: "agent_tool",
            outcome: "allowed",
            summary: $"{ToolAssignTask}: {actor.Key} assigned task {todoId} to {targetAssignee.Key} (prior follow-ups cancelled).",
            tenantId: tenantId.Value.ToString());

        return TodoToolResult<TodoItem>.Ok(updated);
    }

    public async Task<TodoToolResult<TodoItem>> RequestApprovalAsync(
        TenantId tenantId,
        PrincipalId actorPrincipalId,
        Guid todoId,
        PrincipalId approverPrincipalId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var actor = TodoDirectorySeed.FindPrincipal(tenantId, actorPrincipalId);
        if (actor is null)
            return Deny<TodoItem>(tenantId, ToolRequestApproval, "Principal is not authorized for this tenant.");

        var approver = TodoDirectorySeed.FindPrincipal(tenantId, approverPrincipalId);
        if (approver is null || !string.Equals(approver.Role, "Supervisor", StringComparison.OrdinalIgnoreCase))
            return Deny<TodoItem>(tenantId, ToolRequestApproval, "Approver must be a supervisor in the same tenant.");

        var updated = await todoStore.RequestApprovalAsync(
            tenantId,
            todoId,
            actor.Key,
            approverPrincipalId,
            string.IsNullOrWhiteSpace(reason) ? "Supervisor review requested." : reason.Trim(),
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
            return TodoToolResult<TodoItem>.Fail(ErrorCodes.TaskNotFound, "Task not found in tenant.");

        diagnostics.RecordSecurityEvidence(
            category: "agent_tool",
            outcome: "allowed",
            summary: $"{ToolRequestApproval}: {actor.Key} requested approval from {approver.Key} on task {todoId}.",
            tenantId: tenantId.Value.ToString());

        return TodoToolResult<TodoItem>.Ok(updated);
    }

    public async Task<TodoToolResult<TodoItem>> RecordOutcomeAsync(
        TenantId tenantId,
        PrincipalId actorPrincipalId,
        Guid todoId,
        string outcomeSummary,
        TodoStatus? newStatus = null,
        CancellationToken cancellationToken = default)
    {
        var actor = TodoDirectorySeed.FindPrincipal(tenantId, actorPrincipalId);
        if (actor is null)
            return Deny<TodoItem>(tenantId, ToolRecordOutcome, "Principal is not authorized for this tenant.");

        if (string.IsNullOrWhiteSpace(outcomeSummary))
            return TodoToolResult<TodoItem>.Fail(ErrorCodes.InvalidRequest, "Outcome summary is required.");

        var updated = await todoStore.RecordOutcomeAsync(
            tenantId,
            todoId,
            actor.Key,
            actor.DisplayName,
            outcomeSummary.Trim(),
            newStatus,
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
            return TodoToolResult<TodoItem>.Fail(ErrorCodes.TaskNotFound, "Task not found in tenant.");

        diagnostics.RecordSecurityEvidence(
            category: "agent_tool",
            outcome: "allowed",
            summary: $"{ToolRecordOutcome}: {actor.Key} recorded outcome on task {todoId}.",
            tenantId: tenantId.Value.ToString());

        return TodoToolResult<TodoItem>.Ok(updated);
    }

    private TodoToolResult<T> Deny<T>(TenantId tenantId, string toolName, string reason)
    {
        diagnostics.RecordSecurityEvidence(
            category: "agent_tool",
            outcome: "denied",
            summary: $"{toolName} denied: {reason}",
            tenantId: tenantId.Value.ToString());
        return TodoToolResult<T>.Fail(ErrorCodes.Unauthorized, reason);
    }
}

public sealed record TodoToolResult<T>(
    bool Succeeded,
    T? Value,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static TodoToolResult<T> Ok(T value) => new(true, value, null, null);
    public static TodoToolResult<T> Fail(string errorCode, string message) => new(false, default, errorCode, message);
}
