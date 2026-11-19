using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;

namespace OpenConnect.Samples.Blazor.Models;

public sealed class SqliteTodoStore(IServiceScopeFactory scopes, bool acceptanceData) : ITodoStore, ITenantTodoStore
{
    private const string LocalScope = "local";
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private readonly InMemoryTodoStore _seed = acceptanceData
        ? InMemoryTodoStore.CreateAcceptanceStore()
        : new InMemoryTodoStore();

    public Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct = default) => GetAllCoreAsync(LocalScope, ct);
    public Task<IReadOnlyList<TodoItem>> GetAllAsync(TenantId tenantId, CancellationToken ct = default) =>
        GetAllCoreAsync(TenantScope(tenantId), ct);
    public Task<TodoItem?> GetByIdAsync(Guid id, CancellationToken ct = default) => GetByIdCoreAsync(LocalScope, id, ct);
    public Task<TodoItem> AddAsync(string title, string? description = null, TodoPriority priority = TodoPriority.Medium,
        DateTime? dueDate = null, CancellationToken ct = default) => AddCoreAsync(LocalScope, title, description, priority, dueDate, ct);
    public Task<TodoItem> AddAsync(TenantId tenantId, string title, string? description = null, TodoPriority priority = TodoPriority.Medium,
        DateTime? dueDate = null, CancellationToken ct = default) => AddCoreAsync(TenantScope(tenantId), title, description, priority, dueDate, ct);
    public Task<bool> UpdateStatusAsync(Guid id, bool isCompleted, CancellationToken ct = default) =>
        UpdateStatusCoreAsync(LocalScope, id, isCompleted, ct);
    public Task<bool> UpdateStatusAsync(TenantId tenantId, Guid id, bool isCompleted, CancellationToken ct = default) =>
        UpdateStatusCoreAsync(TenantScope(tenantId), id, isCompleted, ct);
    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => DeleteCoreAsync(LocalScope, id, ct);
    public Task<bool> DeleteAsync(TenantId tenantId, Guid id, CancellationToken ct = default) =>
        DeleteCoreAsync(TenantScope(tenantId), id, ct);

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

    private async Task<TodoItem> AddCoreAsync(string scope, string title, string? description, TodoPriority priority, DateTime? dueDate, CancellationToken ct)
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
        var row = new PersistedTodoItem
        {
            Scope = scope,
            Id = Guid.NewGuid(),
            Title = title,
            Description = description,
            Priority = priority,
            DueDateUtc = dueDate is null ? null : AsUtc(dueDate.Value),
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Set<PersistedTodoItem>().Add(row);
        await db.SaveChangesAsync(ct);
        return ToItem(row);
    }

    private async Task<bool> UpdateStatusCoreAsync(string scope, Guid id, bool completed, CancellationToken ct)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var row = await db.Set<PersistedTodoItem>().SingleOrDefaultAsync(item => item.Scope == scope && item.Id == id, ct);
        if (row is null)
            return false;
        row.IsCompleted = completed;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> DeleteCoreAsync(string scope, Guid id, CancellationToken ct)
    {
        await using var serviceScope = scopes.CreateAsyncScope();
        var db = serviceScope.ServiceProvider.GetRequiredService<BlazorDbContext>();
        await EnsureSeedAsync(db, scope, ct);
        var row = await db.Set<PersistedTodoItem>().SingleOrDefaultAsync(item => item.Scope == scope && item.Id == id, ct);
        if (row is null)
            return false;
        db.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
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
                    DueDateUtc = item.DueDateUtc
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

    private static TodoItem ToItem(PersistedTodoItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Description = item.Description,
        IsCompleted = item.IsCompleted,
        Priority = item.Priority,
        CreatedAtUtc = AsUtc(item.CreatedAtUtc),
        DueDateUtc = item.DueDateUtc is null ? null : AsUtc(item.DueDateUtc.Value)
    };

    private static DateTime AsUtc(DateTime value) => value.Kind == DateTimeKind.Local
        ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
