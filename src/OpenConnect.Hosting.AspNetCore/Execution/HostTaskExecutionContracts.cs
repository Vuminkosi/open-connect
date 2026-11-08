using OpenConnect.Abstractions;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Authenticated input passed from the HTTP host to a native execution adapter.</summary>
public sealed record HostTaskExecution(TaskId TaskId, TenantId TenantId, PrincipalId PrincipalId, SessionId SessionId, string Message, string IdempotencyKey);

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
