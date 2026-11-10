using System.ComponentModel.DataAnnotations;

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
}

public interface ITodoStore
{
    Task<IReadOnlyList<TodoItem>> GetAllAsync(CancellationToken ct = default);
    Task<TodoItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<TodoItem> AddAsync(string title, string? description = null, TodoPriority priority = TodoPriority.Medium, DateTime? dueDate = null, CancellationToken ct = default);
    Task<bool> UpdateStatusAsync(Guid id, bool isCompleted, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}

public sealed class InMemoryTodoStore : ITodoStore
{
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
            return Task.FromResult<IReadOnlyList<TodoItem>>(_items.OrderByDescending(t => t.Priority).ThenBy(t => t.CreatedAtUtc).ToList());
        }
    }

    public Task<TodoItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_items.FirstOrDefault(i => i.Id == id));
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
            return Task.FromResult(item);
        }
    }

    public Task<bool> UpdateStatusAsync(Guid id, bool isCompleted, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item == null) return Task.FromResult(false);
            item.IsCompleted = isCompleted;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_items.RemoveAll(i => i.Id == id) > 0);
        }
    }
}
