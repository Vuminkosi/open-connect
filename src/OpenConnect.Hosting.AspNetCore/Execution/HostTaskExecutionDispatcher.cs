using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Hosting.AspNetCore;

namespace OpenConnect.Hosting.AspNetCore.Execution;

internal sealed class HostTaskExecutionDispatcher : IHostTaskExecutionDispatcher
{
    private readonly ExecutionQueue _queue;
    private readonly InMemoryTaskStore _store;
    private readonly ITaskLifecycleStore _lifecycle;
    private readonly InMemorySessionStore _sessions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OpenConnectTaskExecutionOptions _options;
    private readonly ConcurrentDictionary<string, (string Fingerprint, ExecutionWork Work)> _keys = new(StringComparer.Ordinal);
    private readonly ExecutionRuntimeState _runtime;
    private readonly object _admissionLock = new();

    public HostTaskExecutionDispatcher(ExecutionQueue queue, InMemoryTaskStore store, ITaskLifecycleStore lifecycle, InMemorySessionStore sessions, IServiceScopeFactory scopeFactory, ExecutionRuntimeState runtime, OpenConnectTaskExecutionOptions options)
    { _queue = queue; _store = store; _lifecycle = lifecycle; _sessions = sessions; _scopeFactory = scopeFactory; _runtime = runtime; _options = options; }

    public Task<AdmissionResult> AcceptAsync(TaskRequest request, string key, string fingerprint, TenantId tenant, PrincipalId principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scopedKey = $"{tenant.Value}:{principal.Value}:{key}";
        lock (_admissionLock)
        {
            if (_keys.TryGetValue(scopedKey, out var existing))
                return Task.FromResult(existing.Fingerprint == fingerprint ? new AdmissionResult(ExecutionAdmissionStatus.Accepted, _store.Find(existing.Work.Record.Id.Value) ?? existing.Work.Record) : new AdmissionResult(ExecutionAdmissionStatus.Conflict, null));
            if (_keys.Count >= _options.MaxRetainedTasks)
                return Task.FromResult(new AdmissionResult(ExecutionAdmissionStatus.Unavailable, null));
            // Confirm that mandatory dependencies resolve without invoking authorization or execution.
            using (var scope = _scopeFactory.CreateScope())
                if (scope.ServiceProvider.GetService<IHostTaskExecutor>() is null || scope.ServiceProvider.GetService<IHostTaskExecutionAuthorizer>() is null)
                    return Task.FromResult(new AdmissionResult(ExecutionAdmissionStatus.Unavailable, null));

            cancellationToken.ThrowIfCancellationRequested();
            var createdSession = request.SessionId is null ? _sessions.Create(tenant, principal) : null;
            ExecutionWork? work = null;
            TaskRecord? record = null;
            try
            {
                var session = createdSession?.Id ?? new SessionId(Ulid.Parse(request.SessionId!));
                record = new TaskRecord(new TaskId(Ulid.NewUlid()), tenant, principal, AgentRunState.Accepted);
                var input = new HostTaskExecution(record.Id, tenant, principal, session, request.Message ?? string.Empty, key);
                work = new ExecutionWork(input, record, new CancellationTokenSource());
                _store.Put(record);
                _keys[scopedKey] = (fingerprint, work);
                if (!_queue.Channel.Writer.TryWrite(work))
                {
                    RollbackAdmission(scopedKey, record, work, createdSession);
                    return Task.FromResult(new AdmissionResult(ExecutionAdmissionStatus.Unavailable, null));
                }
                return Task.FromResult(new AdmissionResult(ExecutionAdmissionStatus.Accepted, record));
            }
            catch
            {
                if (record is not null && work is not null) RollbackAdmission(scopedKey, record, work, createdSession);
                else if (createdSession is not null) _sessions.Remove(createdSession.Id.Value);
                throw;
            }
        }
    }

    private void RollbackAdmission(string scopedKey, TaskRecord record, ExecutionWork work, SessionRecord? createdSession)
    {
        _keys.TryRemove(scopedKey, out _);
        _store.TryRemove(record.Id.Value);
        if (createdSession is not null) _sessions.Remove(createdSession.Id.Value);
        work.Cancellation.Dispose();
    }

    public async Task<TaskRecord?> CancelAsync(Ulid taskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var record = _store.Find(taskId);
        if (record is null) return null;
        var work = _runtime.Active.GetValueOrDefault(taskId);
        if (record.State == AgentRunState.Accepted)
        {
            // Queued cancellation is terminal immediately; the worker will observe cancellation and skip execution.
            if (work is null && _keys.Values.Select(x => x.Work).FirstOrDefault(x => x.Record.Id.Value == taskId) is { } queued)
            {
                if (!await WorkCancellation.For(queued).CancelAsync(queued))
                    return await _store.GetAsync(taskId, cancellationToken);
                return await _lifecycle.TransitionAsync(record.Id, record.TenantId, record.PrincipalId, AgentRunState.Accepted, AgentRunState.Cancelled, cancellationToken)
                    ?? await _store.GetAsync(taskId, cancellationToken);
            }
        }
        if (work is not null) await WorkCancellation.For(work).CancelAsync(work);
        return await _store.GetAsync(taskId, cancellationToken);
    }
}

internal sealed class HostTaskExecutionWorker : BackgroundService
{
    private readonly ExecutionQueue _queue;
    private readonly ITaskLifecycleStore _lifecycle;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OpenConnectTaskExecutionOptions _options;
    private readonly ILogger<HostTaskExecutionWorker> _logger;
    private readonly ExecutionRuntimeState _runtime;
    public HostTaskExecutionWorker(ExecutionQueue queue, ITaskLifecycleStore lifecycle, IServiceScopeFactory scopeFactory, OpenConnectTaskExecutionOptions options, ILogger<HostTaskExecutionWorker> logger, ExecutionRuntimeState runtime)
    { _queue = queue; _lifecycle = lifecycle; _scopeFactory = scopeFactory; _options = options; _logger = logger; _runtime = runtime; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, _options.WorkerConcurrency).Select(_ => ConsumeAsync(stoppingToken)).ToArray();
        await Task.WhenAll(workers);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Channel.Writer.TryComplete();
        while (_queue.Channel.Reader.TryRead(out var pending))
        {
            await WorkCancellation.For(pending).CancelAsync(pending);
            await TransitionAsync(pending, AgentRunState.Accepted, AgentRunState.Cancelled, CancellationToken.None);
            await WorkCancellation.For(pending).DisposeAsync(pending);
        }
        await base.StopAsync(cancellationToken);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var work in _queue.Channel.Reader.ReadAllAsync(stoppingToken))
            {
                if (work.Cancellation.IsCancellationRequested) { await TransitionAsync(work, AgentRunState.Accepted, AgentRunState.Cancelled, stoppingToken); await WorkCancellation.For(work).DisposeAsync(work); continue; }
                _runtime.Active[work.Record.Id.Value] = work;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.Cancellation.Token);
                    using var scope = _scopeFactory.CreateScope();
                    var auth = scope.ServiceProvider.GetRequiredService<IHostTaskExecutionAuthorizer>();
                    var executor = scope.ServiceProvider.GetRequiredService<IHostTaskExecutor>();
                    await auth.AuthorizeAsync(work.Input, linked.Token);
                    if (work.Cancellation.IsCancellationRequested) { await TransitionAsync(work, AgentRunState.Accepted, AgentRunState.Cancelled, stoppingToken); continue; }
                    var running = await _lifecycle.TransitionAsync(work.Record.Id, work.Record.TenantId, work.Record.PrincipalId, AgentRunState.Accepted, AgentRunState.Running, stoppingToken);
                    if (running is null || running.State != AgentRunState.Running || work.Cancellation.IsCancellationRequested)
                    {
                        if (running?.State == AgentRunState.Running) await TransitionAsync(work, AgentRunState.Running, AgentRunState.Cancelled, stoppingToken);
                        continue;
                    }
                    await executor.ExecuteAsync(work.Input, linked.Token);
                    await TransitionAsync(work, AgentRunState.Running, work.Cancellation.IsCancellationRequested ? AgentRunState.Cancelled : AgentRunState.Completed, stoppingToken);
                }
                catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested || stoppingToken.IsCancellationRequested)
                {
                    if (await TransitionAsync(work, AgentRunState.Running, AgentRunState.Cancelled, CancellationToken.None) is null)
                        await TransitionAsync(work, AgentRunState.Accepted, AgentRunState.Cancelled, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning("Host task execution failed for task {TaskId}; failure type {FailureType}.", work.Record.Id.Value, exception.GetType().Name);
                    if (await TransitionAsync(work, AgentRunState.Accepted, AgentRunState.Failed, CancellationToken.None) is null)
                        await TransitionAsync(work, AgentRunState.Running, AgentRunState.Failed, CancellationToken.None);
                }
                finally
                {
                    _runtime.Active.TryRemove(work.Record.Id.Value, out _);
                    await WorkCancellation.For(work).DisposeAsync(work);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private Task<TaskRecord?> TransitionAsync(ExecutionWork work, AgentRunState expected, AgentRunState state, CancellationToken cancellationToken)
    {
        return _lifecycle.TransitionAsync(work.Record.Id, work.Record.TenantId, work.Record.PrincipalId, expected, state, cancellationToken);
    }
}

/// <summary>Coordinates cancellation calls with disposal without holding a lock while callbacks run.</summary>
internal static class WorkCancellation
{
    private static readonly ConditionalWeakTable<ExecutionWork, Lifetime> Lifetimes = new();
    public static Lifetime For(ExecutionWork work) => Lifetimes.GetValue(work, static _ => new Lifetime());

    internal sealed class Lifetime
    {
        private readonly object _gate = new();
        private int _cancellationsInFlight;
        private bool _disposeRequested;
        private TaskCompletionSource? _drained;
        private Task? _disposeTask;

        public async Task<bool> CancelAsync(ExecutionWork work)
        {
            await Task.Yield();
            lock (_gate)
            {
                if (_disposeRequested) return false;
                _cancellationsInFlight++;
            }
            try
            {
                work.Cancellation.Cancel();
                return true;
            }
            finally
            {
                lock (_gate)
                {
                    _cancellationsInFlight--;
                    if (_cancellationsInFlight == 0) _drained?.TrySetResult();
                }
            }
        }

        public Task DisposeAsync(ExecutionWork work)
        {
            lock (_gate)
            {
                if (_disposeTask is not null) return _disposeTask;
                _disposeRequested = true;
                var drained = _cancellationsInFlight == 0
                    ? Task.CompletedTask
                    : (_drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                _disposeTask = DisposeAfterCancellationsAsync(work.Cancellation, drained);
                return _disposeTask;
            }
        }

        private static async Task DisposeAfterCancellationsAsync(CancellationTokenSource cancellation, Task drained)
        {
            await Task.Yield();
            await drained.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
