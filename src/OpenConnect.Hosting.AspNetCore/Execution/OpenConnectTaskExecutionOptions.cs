namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Bounds process-local HTTP task execution resources.</summary>
/// <remarks>Retained tasks and idempotency keys are not evicted. Reaching the retained-task limit rejects new unique submissions until restart; restart loses all in-memory task, queue, and deduplication state.</remarks>
public sealed class OpenConnectTaskExecutionOptions
{
    /// <summary>Maximum accepted tasks waiting in memory, excluding active workers.</summary>
    public int Capacity { get; set; } = 100;
    /// <summary>Maximum number of concurrently executing tasks.</summary>
    public int WorkerConcurrency { get; set; } = 1;
    /// <summary>Maximum number of tasks and idempotency reservations retained for the process lifetime.</summary>
    public int MaxRetainedTasks { get; set; } = 10_000;
}
