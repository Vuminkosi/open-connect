using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Hosting.AspNetCore.Execution;

/// <summary>Opt-in registration for bounded, process-local HTTP task execution.</summary>
public static class HostTaskExecutionServiceCollectionExtensions
{
    /// <summary>Registers bounded in-memory execution. A native executor and authorizer must be registered separately.</summary>
    /// <remarks>Queue capacity and process-lifetime retained-task capacity are separate limits. No automatic eviction or durability is provided.</remarks>
    public static IServiceCollection AddOpenConnectTaskExecution(this IServiceCollection services, Action<OpenConnectTaskExecutionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new OpenConnectTaskExecutionOptions();
        configure?.Invoke(options);
        if (options.Capacity < 1 || options.WorkerConcurrency < 1 || options.WorkerConcurrency > 64 || options.MaxRetainedTasks < 1)
            throw new ArgumentOutOfRangeException(nameof(configure), "Capacity and retained task limit must be positive; worker concurrency must be between 1 and 64.");
        services.AddSingleton(options);
        services.AddSingleton<InMemoryTaskStore>();
        services.AddSingleton<ITaskLifecycleStore>(provider => provider.GetRequiredService<InMemoryTaskStore>());
        services.AddSingleton<InMemorySessionStore>();
        services.AddSingleton<ExecutionQueue>();
        services.AddSingleton<ExecutionRuntimeState>();
        services.AddSingleton<DurableToolExecutionTracker>();
        services.AddSingleton<IHostTaskExecutionDispatcher, HostTaskExecutionDispatcher>();
        services.AddHostedService<HostTaskExecutionWorker>();
        return services;
    }
}

internal sealed class ExecutionQueue
{
    public ExecutionQueue(OpenConnectTaskExecutionOptions options) => Channel = System.Threading.Channels.Channel.CreateBounded<ExecutionWork>(new BoundedChannelOptions(options.Capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = options.WorkerConcurrency == 1, SingleWriter = false });
    public Channel<ExecutionWork> Channel { get; }
}

internal sealed record ExecutionWork(HostTaskExecution Input, TaskRecord Record, CancellationTokenSource Cancellation);
internal sealed class ExecutionRuntimeState
{
    public System.Collections.Concurrent.ConcurrentDictionary<Ulid, ExecutionWork> Active { get; } = new();
}

internal interface IHostTaskExecutionDispatcher
{
    Task<AdmissionResult> AcceptAsync(TaskRequest request, string key, string fingerprint, TenantId tenant, PrincipalId principal, CancellationToken cancellationToken);
    Task<TaskRecord?> CancelAsync(Ulid taskId, CancellationToken cancellationToken);
}

internal sealed record AdmissionResult(ExecutionAdmissionStatus Status, TaskRecord? Task);
internal enum ExecutionAdmissionStatus { Accepted, Conflict, Unavailable }
