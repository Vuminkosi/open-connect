using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.Persistence.EFCore.Messaging;

namespace OpenConnect.Samples.Blazor.Models;

public sealed class SqliteTodoStore(IServiceScopeFactory scopes, bool acceptanceData) : ITodoStore, ITenantTodoStore
{
    private const string LocalScope = "local";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private readonly InMemoryTodoStore _seed = acceptanceData
        ? InMemoryTodoStore.CreateAcceptanceStore()
        : new InMemoryTodoStore();

    public Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct = default) => GetAllCoreAsync(LocalScope, ct);
    public Task<IReadOnlyList<TodoItem>> GetAllAsync(TenantId tenantId, CancellationToken ct = default) =>
        GetAllCoreAsync(TenantScope(tenantId), ct);
    public Task<TodoItem?> GetByIdAsync(Guid id, CancellationToken ct = default) => GetByIdCoreAsync(LocalScope, id, ct);
    public Task<TodoItem?> GetByIdAsync(TenantId tenantId, Guid id, CancellationToken ct = default) =>
        GetByIdCoreAsync(TenantScope(tenantId), id, ct);
    public Task<TodoItem> AddAsync(string title, string? description = null, TodoPriority priority = TodoPriority.Medium,
        DateTime? dueDate = null, CancellationToken ct = default) => AddCoreAsync(LocalScope, null, title, description, priority, dueDate, null, null, null, ct);
    public Task<TodoItem> AddAsync(TenantId tenantId, string title, string? description = null, TodoPriority priority = TodoPriority.Medium,
        DateTime? dueDate = null, CancellationToken ct = default) => AddCoreAsync(TenantScope(tenantId), tenantId.Value.ToString(), title, description, priority, dueDate, null, null, null, ct);
    public Task<bool> UpdateStatusAsync(Guid id, bool isCompleted, CancellationToken ct = default) =>
        UpdateStatusCoreAsync(LocalScope, null, id, isCompleted, ct);
    public Task<bool> UpdateStatusAsync(TenantId tenantId, Guid id, bool isCompleted, CancellationToken ct = default) =>
        UpdateStatusCoreAsync(TenantScope(tenantId), tenantId, id, isCompleted, ct);
    public Task<TodoEditResult> EditAsync(Guid id, long expectedRevision, TodoEdit edit, CancellationToken ct = default) =>
        EditCoreAsync(LocalScope, id, expectedRevision, edit, ct);
    public Task<TodoDeleteResult> DeleteIfUnchangedAsync(Guid id, long expectedRevision, CancellationToken ct = default) =>
        DeleteIfUnchangedCoreAsync(LocalScope, null, id, expectedRevision, ct);
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => DeleteCoreAsync(LocalScope, null, id, ct);
    public Task<bool> DeleteAsync(TenantId tenantId, Guid id, CancellationToken ct = default) =>
        DeleteCoreAsync(TenantScope(tenantId), tenantId, id, ct);

    /// <summary>Additive helper for creating an operational task with tenant, assignee, and escalation policy.</summary>
    public Task<TodoItem> AddOperationalTaskAsync(
        TenantId tenantId,
        string title,
        string? description,
        TodoPriority priority,
        DateTime? dueDateUtc,
        PrincipalId? assignedPrincipalId,
        string? escalationPolicy,
        string actorId,
        CancellationToken ct = default) =>
        AddCoreAsync(
            TenantScope(tenantId),
            tenantId.Value.ToString(),
            title,
            description,
            priority,
            dueDateUtc,
            assignedPrincipalId?.Value.ToString(),
            escalationPolicy,
            actorId,
            ct);

    /// <summary>Additive helper to update rich operational workflow status, append audit trail, and cancel pending follow-ups when completed/cancelled.</summary>
    public async Task<TodoItem?> UpdateWorkflowStatusAsync(
        TenantId tenantId,
        Guid id,
        TodoStatus status,
        string actorId,
        string? note = null,
        CancellationToken ct = default)
    {
        var scope = TenantScope(tenantId);
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var row = await db.Set<PersistedTodoItem>().SingleOrDefaultAsync(item => item.Scope == scope && item.Id == id, ct);
        if (row is null)
            return null;

        var previous = row.Status;
        row.Status = status;
        row.IsCompleted = status == TodoStatus.Completed;
        row.CompletedAtUtc = status == TodoStatus.Completed ? (row.CompletedAtUtc ?? DateTime.UtcNow) : null;
        row.Revision++;
        row.AuditHistoryJson = AppendAudit(row.AuditHistoryJson, new TodoAuditEntry(
            "status_changed",
            actorId,
            string.IsNullOrWhiteSpace(note) ? $"Status changed from {previous} to {status}." : $"Status changed from {previous} to {status}: {note}",
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(ct);

        if (status is TodoStatus.Completed or TodoStatus.Cancelled)
            await CancelPendingFollowUpsForTaskAsync(db, tenantId, id, ct);

        return ToItem(row);
    }

    /// <summary>Additive helper to reassign a task, append audit trail, and cancel pending follow-ups for the prior assignee.</summary>
    public async Task<TodoItem?> AssignTaskAsync(
        TenantId tenantId,
        Guid id,
        PrincipalId newAssignee,
        string actorId,
        string? reason = null,
        CancellationToken ct = default)
    {
        var scope = TenantScope(tenantId);
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var row = await db.Set<PersistedTodoItem>().SingleOrDefaultAsync(item => item.Scope == scope && item.Id == id, ct);
        if (row is null)
            return null;

        var previousAssignee = row.AssignedPrincipalId;
        var nextAssigneeText = newAssignee.Value.ToString();
        row.AssignedPrincipalId = nextAssigneeText;
        row.Revision++;
        row.AuditHistoryJson = AppendAudit(row.AuditHistoryJson, new TodoAuditEntry(
            "task_reassigned",
            actorId,
            string.IsNullOrWhiteSpace(reason)
                ? $"Assigned to {nextAssigneeText} (previous: {previousAssignee ?? "unassigned"})."
                : $"Assigned to {nextAssigneeText} (previous: {previousAssignee ?? "unassigned"}): {reason}",
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(ct);

        if (!string.Equals(previousAssignee, nextAssigneeText, StringComparison.Ordinal))
            await CancelPendingFollowUpsForTaskAsync(db, tenantId, id, ct);

        return ToItem(row);
    }

    /// <summary>Additive helper to record an approval request and transition task to AwaitingApproval.</summary>
    public async Task<TodoItem?> RequestApprovalAsync(
        TenantId tenantId,
        Guid id,
        string requestedById,
        PrincipalId approverPrincipalId,
        string reason,
        CancellationToken ct = default)
    {
        var scope = TenantScope(tenantId);
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var row = await db.Set<PersistedTodoItem>().SingleOrDefaultAsync(item => item.Scope == scope && item.Id == id, ct);
        if (row is null)
            return null;

        var approval = new TodoApprovalEntry(
            Ulid.NewUlid().ToString(),
            requestedById,
            approverPrincipalId.Value.ToString(),
            "Pending",
            reason,
            DateTimeOffset.UtcNow);
        row.ApprovalsJson = AppendJsonList(row.ApprovalsJson, approval);
        row.Status = TodoStatus.AwaitingApproval;
        row.IsCompleted = false;
        row.Revision++;
        row.AuditHistoryJson = AppendAudit(row.AuditHistoryJson, new TodoAuditEntry(
            "approval_requested",
            requestedById,
            $"Approval requested from {approverPrincipalId.Value}: {reason}",
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(ct);
        return ToItem(row);
    }

    /// <summary>Additive helper to record an operational outcome/comment and audit entry.</summary>
    public async Task<TodoItem?> RecordOutcomeAsync(
        TenantId tenantId,
        Guid id,
        string actorId,
        string actorDisplayName,
        string outcomeSummary,
        TodoStatus? newStatus = null,
        CancellationToken ct = default)
    {
        var scope = TenantScope(tenantId);
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var row = await db.Set<PersistedTodoItem>().SingleOrDefaultAsync(item => item.Scope == scope && item.Id == id, ct);
        if (row is null)
            return null;

        row.CommentsJson = AppendJsonList(row.CommentsJson, new TodoCommentEntry(
            actorId,
            actorDisplayName,
            outcomeSummary,
            DateTimeOffset.UtcNow));
        if (newStatus.HasValue)
        {
            row.Status = newStatus.Value;
            row.IsCompleted = newStatus.Value == TodoStatus.Completed;
            row.CompletedAtUtc = newStatus.Value == TodoStatus.Completed ? (row.CompletedAtUtc ?? DateTime.UtcNow) : null;
        }
        row.Revision++;
        row.AuditHistoryJson = AppendAudit(row.AuditHistoryJson, new TodoAuditEntry(
            "outcome_recorded",
            actorId,
            outcomeSummary,
            DateTimeOffset.UtcNow));

        await db.SaveChangesAsync(ct);

        if (newStatus is TodoStatus.Completed or TodoStatus.Cancelled)
            await CancelPendingFollowUpsForTaskAsync(db, tenantId, id, ct);

        return ToItem(row);
    }

    /// <summary>Returns open/in-progress/blocked overdue tasks across all tenant scopes for deadline monitoring.</summary>
    public async Task<IReadOnlyList<TodoItem>> GetOverdueOperationalTasksAsync(DateTime utcNow, CancellationToken ct = default)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        var rows = await db.Set<PersistedTodoItem>()
            .AsNoTracking()
            .Where(item => !item.IsCompleted
                && item.Status != TodoStatus.Completed
                && item.Status != TodoStatus.Cancelled
                && item.DueDateUtc != null
                && item.DueDateUtc <= utcNow
                && item.TenantId != null
                && item.AssignedPrincipalId != null)
            .OrderBy(item => item.DueDateUtc)
            .ToListAsync(ct);
        return rows.Select(ToItem).ToArray();
    }

    /// <summary>Returns all tasks across all scopes for development diagnostics inspection.</summary>
    public async Task<IReadOnlyList<TodoItem>> GetAllAcrossTenantsForDiagnosticsAsync(CancellationToken ct = default)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        var rows = await db.Set<PersistedTodoItem>()
            .AsNoTracking()
            .OrderByDescending(item => item.Priority)
            .ThenByDescending(item => item.CreatedAtUtc)
            .ToListAsync(ct);
        return rows.Select(ToItem).ToArray();
    }

    private async Task<IReadOnlyList<TodoItem>> GetAllCoreAsync(string scope, CancellationToken ct)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var rows = await db.Set<PersistedTodoItem>().AsNoTracking().Where(item => item.Scope == scope)
            .OrderByDescending(item => item.Priority).ThenBy(item => item.CreatedAtUtc).ToListAsync(ct);
        return rows.Select(ToItem).ToArray();
    }

    private async Task<TodoItem?> GetByIdCoreAsync(string scope, Guid id, CancellationToken ct)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var row = await db.Set<PersistedTodoItem>().AsNoTracking().SingleOrDefaultAsync(item => item.Scope == scope && item.Id == id, ct);
        return row is null ? null : ToItem(row);
    }

    private async Task<TodoItem> AddCoreAsync(
        string scope,
        string? tenantId,
        string title,
        string? description,
        TodoPriority priority,
        DateTime? dueDate,
        string? assignedPrincipalId,
        string? escalationPolicy,
        string? actorId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (title.Length > 200)
            throw new ArgumentOutOfRangeException(nameof(title));
        if (!Enum.IsDefined(priority))
            throw new ArgumentOutOfRangeException(nameof(priority));
        if (dueDate is { Kind: DateTimeKind.Unspecified })
            throw new ArgumentException("A due date must include a timezone.", nameof(dueDate));
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var now = DateTime.UtcNow;
        var row = new PersistedTodoItem
        {
            Scope = scope,
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Priority = priority,
            DueDateUtc = dueDate is null ? null : AsUtc(dueDate.Value),
            CreatedAtUtc = now,
            TenantId = tenantId,
            AssignedPrincipalId = assignedPrincipalId,
            Status = TodoStatus.Open,
            EscalationPolicy = escalationPolicy,
            AuditHistoryJson = AppendAudit(null, new TodoAuditEntry(
                "task_created",
                actorId ?? "system",
                $"Created task '{title}' with priority {priority}.",
                DateTimeOffset.UtcNow))
        };
        db.Set<PersistedTodoItem>().Add(row);
        await db.SaveChangesAsync(ct);
        return ToItem(row);
    }

    private async Task<bool> UpdateStatusCoreAsync(string scope, TenantId? tenantId, Guid id, bool completed, CancellationToken ct)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var completedAt = DateTime.UtcNow;
        var nextStatus = completed ? TodoStatus.Completed : TodoStatus.Open;
        var updated = await db.Set<PersistedTodoItem>()
            .Where(item => item.Scope == scope && item.Id == id && item.Revision < long.MaxValue)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsCompleted, completed)
                .SetProperty(item => item.Status, nextStatus)
                .SetProperty(item => item.CompletedAtUtc, item => completed
                    ? item.IsCompleted ? item.CompletedAtUtc : completedAt
                    : null)
                .SetProperty(item => item.Revision, item => item.Revision + 1), ct) == 1;

        if (updated && completed && tenantId.HasValue)
            await CancelPendingFollowUpsForTaskAsync(db, tenantId.Value, id, ct);

        return updated;
    }

    private async Task<TodoEditResult> EditCoreAsync(string scope, Guid id, long expectedRevision, TodoEdit edit, CancellationToken ct)
    {
        InMemoryTodoStore.ValidateEdit(edit, expectedRevision);
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var title = edit.Title.Trim();
        var description = edit.Description?.Trim();
        var dueDate = edit.DueDateUtc?.ToUniversalTime();
        var updated = await db.Set<PersistedTodoItem>()
            .Where(item => item.Scope == scope && item.Id == id && item.Revision == expectedRevision && item.Revision < long.MaxValue)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Title, title)
                .SetProperty(item => item.Description, description)
                .SetProperty(item => item.Priority, edit.Priority)
                .SetProperty(item => item.DueDateUtc, dueDate)
                .SetProperty(item => item.Revision, item => item.Revision + 1), ct);
        if (updated == 1)
            return TodoEditResult.Updated;
        return await db.Set<PersistedTodoItem>().AsNoTracking().AnyAsync(item => item.Scope == scope && item.Id == id, ct)
            ? TodoEditResult.Conflict : TodoEditResult.NotFound;
    }

    private async Task<bool> DeleteCoreAsync(string scope, TenantId? tenantId, Guid id, CancellationToken ct)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var row = await db.Set<PersistedTodoItem>().SingleOrDefaultAsync(item => item.Scope == scope && item.Id == id, ct);
        if (row is null)
            return false;
        var resolvedTenant = tenantId ?? TryParseTenant(row.TenantId);
        db.Remove(row);
        await db.SaveChangesAsync(ct);

        if (resolvedTenant.HasValue)
            await CancelPendingFollowUpsForTaskAsync(db, resolvedTenant.Value, id, ct);

        return true;
    }

    private async Task<TodoDeleteResult> DeleteIfUnchangedCoreAsync(string scope, TenantId? tenantId, Guid id, long expectedRevision, CancellationToken ct)
    {
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var deleted = await db.Set<PersistedTodoItem>()
            .Where(item => item.Scope == scope && item.Id == id && item.Revision == expectedRevision)
            .ExecuteDeleteAsync(ct);
        if (deleted == 1)
        {
            if (tenantId.HasValue)
                await CancelPendingFollowUpsForTaskAsync(db, tenantId.Value, id, ct);
            return TodoDeleteResult.Deleted;
        }
        return await db.Set<PersistedTodoItem>().AsNoTracking().AnyAsync(item => item.Scope == scope && item.Id == id, ct)
            ? TodoDeleteResult.Conflict : TodoDeleteResult.NotFound;
    }

    /// <summary>Cancels any active/scheduled/waiting follow-ups tied to a Todo task (matched by causal event / idempotency prefix).</summary>
    public static async Task<int> CancelPendingFollowUpsForTaskAsync(BlazorDbContext db, TenantId tenantId, Guid todoId, CancellationToken ct)
    {
        var tenantText = tenantId.Value.ToString();
        var taskMarker = $"todo:{todoId:N}";
        var terminalStates = new[]
        {
            FollowUpState.Completed.ToString(),
            FollowUpState.Cancelled.ToString(),
            FollowUpState.Expired.ToString(),
            FollowUpState.ResponseReceived.ToString()
        };

        var activeFollowUps = await db.Set<MessagingFollowUpEntity>()
            .Where(f => f.TenantId == tenantText
                && !terminalStates.Contains(f.State)
                && (f.IdempotencyKey.Contains(taskMarker) || f.CausalEventId.Contains(taskMarker)))
            .ToListAsync(ct);

        if (activeFollowUps.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        foreach (var followUp in activeFollowUps)
        {
            followUp.State = FollowUpState.Cancelled.ToString();
            followUp.CompletedAtUtc = now;
            followUp.LeaseFence++;
        }

        await db.SaveChangesAsync(ct);
        return activeFollowUps.Count;
    }

    private async Task EnsureSeedAsync(BlazorDbContext db, string scope, CancellationToken ct)
    {
        if (await db.Set<PersistedTodoScope>().AsNoTracking().AnyAsync(item => item.Scope == scope, ct))
            return;
        await _seedGate.WaitAsync(ct);
        try
        {
            if (await db.Set<PersistedTodoScope>().AsNoTracking().AnyAsync(item => item.Scope == scope, ct))
                return;
            var seed = await _seed.GetAllAsync(ct);
            var tenantText = scope.StartsWith("tenant:", StringComparison.Ordinal) ? scope["tenant:".Length..] : null;
            var record = new PersistedTodoScope
            {
                Scope = scope,
                Items = seed.Select(item => new PersistedTodoItem
                {
                    Scope = scope,
                    Id = item.Id,
                    Title = item.Title,
                    Description = item.Description,
                    IsCompleted = item.IsCompleted,
                    Priority = item.Priority,
                    CreatedAtUtc = item.CreatedAtUtc,
                    DueDateUtc = item.DueDateUtc,
                    CompletedAtUtc = item.CompletedAtUtc,
                    TenantId = tenantText ?? item.TenantId,
                    AssignedPrincipalId = item.AssignedPrincipalId,
                    Status = item.IsCompleted ? TodoStatus.Completed : item.Status,
                    EscalationPolicy = item.EscalationPolicy,
                    CommentsJson = item.CommentsJson,
                    ApprovalsJson = item.ApprovalsJson,
                    AuditHistoryJson = item.AuditHistoryJson
                }).ToList()
            };
            db.Set<PersistedTodoScope>().Add(record);
            try
            { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                if (!await db.Set<PersistedTodoScope>().AsNoTracking().AnyAsync(item => item.Scope == scope, ct))
                    throw;
            }
        }
        finally
        {
            _seedGate.Release();
        }
    }

    private static string TenantScope(TenantId tenantId)
    {
        if (tenantId.Value == default)
            throw new ArgumentException("Tenant is required.", nameof(tenantId));
        return "tenant:" + tenantId.Value;
    }

    private static TenantId? TryParseTenant(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        try { return new TenantId(Ulid.Parse(raw)); }
        catch (FormatException) { return null; }
    }

    private static string AppendAudit(string? existingJson, TodoAuditEntry entry) =>
        AppendJsonList(existingJson, entry);

    private static string AppendJsonList<T>(string? existingJson, T entry)
    {
        List<T> items;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            items = [];
        }
        else
        {
            try { items = JsonSerializer.Deserialize<List<T>>(existingJson, JsonOptions) ?? []; }
            catch (JsonException) { items = []; }
        }
        items.Add(entry);
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    private static TodoItem ToItem(PersistedTodoItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Description = item.Description,
        IsCompleted = item.IsCompleted,
        Priority = item.Priority,
        CreatedAtUtc = AsUtc(item.CreatedAtUtc),
        DueDateUtc = item.DueDateUtc is null ? null : AsUtc(item.DueDateUtc.Value),
        CompletedAtUtc = item.CompletedAtUtc is null ? null : AsUtc(item.CompletedAtUtc.Value),
        Revision = item.Revision,
        TenantId = item.TenantId,
        AssignedPrincipalId = item.AssignedPrincipalId,
        Status = item.IsCompleted ? TodoStatus.Completed : item.Status,
        EscalationPolicy = item.EscalationPolicy,
        CommentsJson = item.CommentsJson,
        ApprovalsJson = item.ApprovalsJson,
        AuditHistoryJson = item.AuditHistoryJson
    };

    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
