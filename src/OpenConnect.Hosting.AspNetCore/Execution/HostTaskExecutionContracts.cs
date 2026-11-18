using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Authenticated input passed from the HTTP host to a native execution adapter.</summary>
public enum HostTaskOrigin
{
    NonInteractive = 0,
    MiniComposer = 1
}

public sealed record HostTaskExecution(TaskId TaskId, TenantId TenantId, PrincipalId PrincipalId, SessionId SessionId, string Message, string IdempotencyKey)
{
    /// <summary>Trusted host-selected submission channel; never populated from request data.</summary>
    public HostTaskOrigin Origin { get; init; } = HostTaskOrigin.NonInteractive;
    /// <summary>Server-issued connection identity bound to an interactive task, when present.</summary>
    public string? ClientConnectionId { get; init; }
}

/// <summary>Executes one accepted task. Implementations must honor cancellation and recheck revocation.</summary>
public interface IHostTaskExecutor
{
    /// <summary>Runs the task until completion or cancellation.</summary>
    Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken);
}

/// <summary>Authorizes a task immediately before its execution begins.</summary>
public interface IHostTaskExecutionAuthorizer
{
    /// <summary>Throws when execution is not authorized.</summary>
    Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken);
}
