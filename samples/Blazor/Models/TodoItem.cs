using System.ComponentModel.DataAnnotations;
using OpenConnect.Abstractions;

namespace OpenConnect.Samples.Blazor.Models;

public enum TodoPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Urgent = 3
}

public sealed class TodoItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [StringLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsCompleted { get; set; }

    public TodoPriority Priority { get; set; } = TodoPriority.Medium;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? DueDateUtc { get; set; }

    public long Revision { get; set; }
}

public sealed record TodoEdit(string Title, string? Description, TodoPriority Priority, DateTime? DueDateUtc);

public enum TodoEditResult { Updated, Conflict, NotFound }
public enum TodoDeleteResult { Deleted, Conflict, NotFound }

public interface ITodoStore
{
    Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct = default);
    Task<TodoItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<TodoItem> AddAsync(string title, string? description = null, TodoPriority priority = TodoPriority.Medium, DateTime? dueDate = null, CancellationToken ct = default);
    Task<bool> UpdateStatusAsync(Guid id, bool isCompleted, CancellationToken ct = default);
    Task<TodoEditResult> EditAsync(Guid id, long expectedRevision, TodoEdit edit, CancellationToken ct = default);
    Task<TodoDeleteResult> DeleteIfUnchangedAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class InMemoryTodoStore : ITodoStore, ITenantTodoStore
{
    public InMemoryTodoStore()
    {
        _tenantSeed = _items.Select(Clone).ToArray();
    }

    private InMemoryTodoStore(IEnumerable<TodoItem> initialItems)
    {
        _items.Clear();
        _items.AddRange(initialItems);
        _tenantSeed = _items.Select(Clone).ToArray();
    }

    public static InMemoryTodoStore CreateAcceptanceStore() =>
        new(
        [
            new TodoItem
            {
                Id = Guid.Parse("a1000000-0000-4000-8000-000000000001"),
                Title = "Review the quarterly report",
                Description = "Confirm the completed figures for the quarterly report.",
                Priority = TodoPriority.High,
                IsCompleted = true,
                CreatedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
            },
            new TodoItem
            {
                Id = Guid.Parse("a1000000-0000-4000-8000-000000000002"),
                Title = "Draft the annual report",
                Description = "Prepare the annual report outline.",
                Priority = TodoPriority.Medium,
                IsCompleted = true,
                CreatedAtUtc = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc)
            },
            new TodoItem
            {
                Id = Guid.Parse("a1000000-0000-4000-8000-000000000003"),
                Title = "Gather feedback for report",
                Description = "Collect the remaining feedback before the report is finalized.",
                Priority = TodoPriority.Low,
                IsCompleted = false,
                CreatedAtUtc = new DateTime(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc)
            },
            new TodoItem
            {
                Id = Guid.Parse("a1000000-0000-4000-8000-000000000004"),
                Title = "Prepare a launch checklist",
                Description = "Confirm the release tasks for the upcoming launch.",
                Priority = TodoPriority.Urgent,
                IsCompleted = false,
                CreatedAtUtc = new DateTime(2026, 1, 4, 12, 0, 0, DateTimeKind.Utc)
            }
        ]);

    private readonly List<TodoItem> _items = new()
    {
        new TodoItem
        {
            Title = "Deploy OpenConnect Gateway with Silero VAD",
            Description = "Ensure hybrid client and server acoustic endpointing is active.",
            Priority = TodoPriority.Urgent,
            IsCompleted = true
        },
        new TodoItem
        {
            Title = "Verify privacy firewall egress tokenization",
            Description = "Ensure credit card and PII canary tokens are encrypted in vault before external model egress.",
            Priority = TodoPriority.High,
            IsCompleted = false
        },
        new TodoItem
        {
            Title = "Review MAF Harness execution plans",
            Description = "Validate native todo tracking and 2-edge delegation constraints.",
            Priority = TodoPriority.Medium,
            IsCompleted = false
        }
    };

    private readonly object _lock = new();

    public Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<TodoItem>>(_items.OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAtUtc).Select(Clone).ToList());
        }
    }

    public Task<TodoItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            return Task.FromResult(item is null ? null : Clone(item));
        }
    }

    public Task<TodoItem> AddAsync(string title, string? description = null, TodoPriority priority = TodoPriority.Medium, DateTime? dueDate = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var item = new TodoItem
            {
                Title = title,
                Description = description,
                Priority = priority,
                DueDateUtc = dueDate
            };
            _items.Add(item);
            return Task.FromResult(Clone(item));
        }
    }

    public Task<bool> UpdateStatusAsync(Guid id, bool isCompleted, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null || item.Revision == long.MaxValue)
                return Task.FromResult(false);
            item.IsCompleted = isCompleted;
            item.Revision++;
            return Task.FromResult(true);
        }
    }

    public Task<TodoEditResult> EditAsync(Guid id, long expectedRevision, TodoEdit edit, CancellationToken ct = default)
    {
        ValidateEdit(edit, expectedRevision);
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return Task.FromResult(TodoEditResult.NotFound);
            if (item.Revision != expectedRevision || item.Revision == long.MaxValue)
                return Task.FromResult(TodoEditResult.Conflict);
            item.Title = edit.Title.Trim();
            item.Description = edit.Description?.Trim();
            item.Priority = edit.Priority;
            item.DueDateUtc = edit.DueDateUtc?.ToUniversalTime();
            item.Revision++;
            return Task.FromResult(TodoEditResult.Updated);
        }
    }

    internal static void ValidateEdit(TodoEdit edit, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (string.IsNullOrWhiteSpace(edit.Title) || edit.Title.Trim().Length > 200)
            throw new ArgumentException("A title of 1-200 characters is required.", nameof(edit));
        if (!Enum.IsDefined(edit.Priority))
            throw new ArgumentOutOfRangeException(nameof(edit));
        if (edit.DueDateUtc is { Kind: DateTimeKind.Unspecified })
            throw new ArgumentException("A due date must include a timezone.", nameof(edit));
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_items.RemoveAll(i => i.Id == id) > 0);
        }
    }

    public Task<TodoDeleteResult> DeleteIfUnchangedAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item is null)
                return Task.FromResult(TodoDeleteResult.NotFound);
            if (item.Revision != expectedRevision)
                return Task.FromResult(TodoDeleteResult.Conflict);
            _items.Remove(item);
            return Task.FromResult(TodoDeleteResult.Deleted);
        }
    }

    private readonly Dictionary<string, List<TodoItem>> _tenantItems = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<TodoItem> _tenantSeed;

    private List<TodoItem> ForTenant(TenantId tenantId)
    {
        var key = tenantId.Value.ToString();
        if (_tenantItems.TryGetValue(key, out var existing))
            return existing;
        var seeded = _tenantSeed.Select(Clone).ToList();
        _tenantItems[key] = seeded;
        return seeded;
    }

    public Task<IReadOnlyList<TodoItem>> GetAllAsync(TenantId tenantId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<TodoItem>>(ForTenant(tenantId).OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAtUtc).Select(Clone).ToList());
    }

    public Task<TodoItem> AddAsync(TenantId tenantId, string title, string? description = null, TodoPriority priority = TodoPriority.Medium, DateTime? dueDate = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var item = new TodoItem { Title = title, Description = description, Priority = priority, DueDateUtc = dueDate };
            ForTenant(tenantId).Add(item);
            return Task.FromResult(Clone(item));
        }
    }

    public Task<bool> UpdateStatusAsync(TenantId tenantId, Guid id, bool isCompleted, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var item = ForTenant(tenantId).FirstOrDefault(i => i.Id == id);
            if (item is null || item.Revision == long.MaxValue)
                return Task.FromResult(false);
            item.IsCompleted = isCompleted;
            item.Revision++;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(TenantId tenantId, Guid id, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(ForTenant(tenantId).RemoveAll(i => i.Id == id) > 0);
    }

    private static TodoItem Clone(TodoItem item) => new() { Id = item.Id, Title = item.Title, Description = item.Description, IsCompleted = item.IsCompleted, Priority = item.Priority, CreatedAtUtc = item.CreatedAtUtc, DueDateUtc = item.DueDateUtc, Revision = item.Revision };
}

public interface ITenantTodoStore
{
    Task<IReadOnlyList<TodoItem>> GetAllAsync(TenantId tenantId, CancellationToken ct = default);
    Task<TodoItem> AddAsync(TenantId tenantId, string title, string? description = null, TodoPriority priority = TodoPriority.Medium, DateTime? dueDate = null, CancellationToken ct = default);
    Task<bool> UpdateStatusAsync(TenantId tenantId, Guid id, bool isCompleted, CancellationToken ct = default);
    Task<bool> DeleteAsync(TenantId tenantId, Guid id, CancellationToken ct = default);
}
