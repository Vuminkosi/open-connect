using System.Security.Claims;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class AuthenticatedTaskExecutionTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

    [Fact]
    public async Task Missing_executor_or_authorizer_fails_closed()
    {
        var app = Build(registerExecutor: false);
        var response = await Submit(app, "key", "message");
        Assert.Equal(503, response.Status);
        Assert.Empty(app.Services.GetRequiredService<InMemoryTaskStore>().All());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Identical_retry_is_not_dispatched_twice_and_changed_body_conflicts()
    {
        var executor = new ControlledExecutor();
        var app = Build(executor);
        var one = await Submit(app, "key", "same");
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var two = await Submit(app, "key", "same");
        var conflict = await Submit(app, "key", "different");
        Assert.Equal(202, one.Status);
        Assert.Equal(one.Location, two.Location);
        Assert.Equal(409, conflict.Status);
        Assert.Equal(1, executor.Calls);
        executor.Release.TrySetResult();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Worker_uses_claim_identity_and_executor_failure_is_not_success()
    {
        var executor = new ControlledExecutor { Failure = new InvalidOperationException("private detail") };
        var app = Build(executor);
        var tenant = new TenantId(Ulid.Parse(Tenant));
        var principal = new PrincipalId(Ulid.Parse(Principal));
        var session = app.Services.GetRequiredService<InMemorySessionStore>().Create(tenant, principal);
        var response = await Submit(app, "key", "message", session.Id.Value.ToString());
        Assert.Equal(202, response.Status);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        executor.Release.TrySetResult();
        await WaitForState(app, response.Location, AgentRunState.Failed);
        Assert.Equal(tenant, executor.Last!.TenantId);
        Assert.Equal(principal, executor.Last.PrincipalId);
        Assert.Equal(session.Id, executor.Last.SessionId);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Capacity_rejection_does_not_create_task()
    {
        var executor = new ControlledExecutor();
        var app = Build(executor, capacity: 1, workers: 1, maxRetainedTasks: 3);
        var first = await Submit(app, "one", "message");
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The sole worker is busy; fill the sole queue slot, then reject the next request.
        var second = await Submit(app, "two", "message");
        Assert.Equal(202, second.Status);
        Assert.Equal(503, (await Submit(app, "three", "message")).Status);
        Assert.Equal(2, app.Services.GetRequiredService<InMemorySessionStore>().Count);
        executor.Release.TrySetResult();
        await WaitForState(app, second.Location, AgentRunState.Completed);
        Assert.Equal(202, (await Submit(app, "three", "message")).Status);
        Assert.Equal(3, app.Services.GetRequiredService<InMemoryTaskStore>().All().Count);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Retained_limit_rejects_new_tasks_but_preserves_retry_and_conflict_after_completion()
    {
        var executor = new ControlledExecutor();
        executor.Release.TrySetResult();
        var app = Build(executor, maxRetainedTasks: 1);
        var first = await Submit(app, "retained-one", "original");
        await WaitForState(app, first.Location, AgentRunState.Completed);

        var retry = await Submit(app, "retained-one", "original");
        var divergent = await Submit(app, "retained-one", "changed");
        var newTask = await Submit(app, "retained-two", "message");

        Assert.Equal(202, retry.Status);
        Assert.Equal(first.Location, retry.Location);
        Assert.Equal(409, divergent.Status);
        Assert.Equal(503, newTask.Status);
        Assert.Equal(1, executor.Calls);
        Assert.Single(app.Services.GetRequiredService<InMemoryTaskStore>().All());
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_identical_admissions_reserve_one_retained_task()
    {
        var executor = new ControlledExecutor();
        var app = Build(executor, maxRetainedTasks: 1);
        var submissions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Submit(app, "parallel-key", "same")));

        Assert.All(submissions, response => Assert.Equal(202, response.Status));
        Assert.All(submissions, response => Assert.Equal(submissions[0].Location, response.Location));
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, executor.Calls);
        Assert.Single(app.Services.GetRequiredService<InMemoryTaskStore>().All());

        executor.Release.TrySetResult();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Cancellation_racing_executor_completion_returns_consistent_terminal_state()
    {
        var executor = new ControlledExecutor { BlockCancellationCallback = true };
        var terminalTransition = new TaskCompletionSource<TaskRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = Build(executor, lifecycleObserver: terminalTransition);
        try
        {
            var accepted = await Submit(app, "cancel-race", "message");
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var id = Ulid.Parse(accepted.Location.Split('/').Last());
            var dispatcher = app.Services.GetRequiredService<IHostTaskExecutionDispatcher>();

            // Cancel runs on its own task because its synchronous CTS callbacks intentionally block.
            var cancelTask = Task.Run(() => dispatcher.CancelAsync(id, CancellationToken.None));
            await executor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            executor.Release.TrySetResult(); // Executor completes while CTS.Cancel is still inside the held callback.
            Assert.Equal(AgentRunState.Cancelled, (await terminalTransition.Task.WaitAsync(TimeSpan.FromSeconds(5))).State);

            // The public host stop waits for worker cleanup; release the callback before awaiting either task.
            var stopTask = Task.Run(async () => await app.StopAsync());
            Assert.False(stopTask.IsCompleted);
            executor.ReleaseCancellationCallback.TrySetResult();
            var cancelResult = await cancelTask.WaitAsync(TimeSpan.FromSeconds(5));
            await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
            var finalState = app.Services.GetRequiredService<InMemoryTaskStore>().Find(id)!.State;
            Assert.Equal(AgentRunState.Cancelled, finalState);
            Assert.Equal(finalState, cancelResult?.State);
        }
        finally
        {
            executor.Release.TrySetResult();
            executor.ReleaseCancellationCallback.TrySetResult();
            try { await app.DisposeAsync(); }
            finally { executor.DisposeCancellationRegistration(); }
        }
    }

    private static WebApplication Build(ControlledExecutor? executor = null, bool registerExecutor = true, int capacity = 4, int workers = 1, int maxRetainedTasks = 10_000, TaskCompletionSource<TaskRecord>? lifecycleObserver = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddOpenConnectTaskExecution(o => { o.Capacity = capacity; o.WorkerConcurrency = workers; o.MaxRetainedTasks = maxRetainedTasks; });
        if (lifecycleObserver is not null)
            builder.Services.AddSingleton<ITaskLifecycleStore>(services => new ObservingLifecycleStore(services.GetRequiredService<InMemoryTaskStore>(), lifecycleObserver));
        builder.Services.AddSingleton<IHostTaskExecutionAuthorizer, AllowAuthorizer>();
        if (registerExecutor) builder.Services.AddSingleton<IHostTaskExecutor>(executor ?? new ControlledExecutor());
        var app = builder.Build();
        app.MapOpenConnect();
        app.StartAsync().GetAwaiter().GetResult();
        return app;
    }

    private static async Task<(int Status, string Location)> Submit(WebApplication app, string key, string message, string? sessionId = null)
    {
        var body = $$"""{"clientTaskId":"{{key}}","message":"{{message}}","sessionId":{{(sessionId is null ? "null" : $"\"{sessionId}\"")}},"tenantId":"attacker-supplied-tenant"}""";
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Headers["Idempotency-Key"] = key;
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, Principal)], "test"));
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        var result = await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCreateTaskAsync(context, new TaskRequest(key, message, sessionId), app.Services.GetRequiredService<InMemoryTaskStore>(), CancellationToken.None);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.Headers.Location.ToString());
    }

    private static async Task WaitForState(WebApplication app, string location, AgentRunState expected)
    {
        var id = Ulid.Parse(location.Split('/').Last());
        for (var i = 0; i < 100; i++)
        {
            if (app.Services.GetRequiredService<InMemoryTaskStore>().Find(id)?.State == expected) return;
            await Task.Delay(10);
        }
        Assert.Equal(expected, app.Services.GetRequiredService<InMemoryTaskStore>().Find(id)?.State);
    }

    private sealed class AllowAuthorizer : IHostTaskExecutionAuthorizer
    { public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask; }

    private sealed class ObservingLifecycleStore(ITaskLifecycleStore inner, TaskCompletionSource<TaskRecord> terminalTransition) : ITaskLifecycleStore
    {
        public async Task<TaskRecord?> TransitionAsync(TaskId taskId, TenantId tenantId, PrincipalId principalId, AgentRunState expectedState, AgentRunState newState, CancellationToken cancellationToken = default)
        {
            var result = await inner.TransitionAsync(taskId, tenantId, principalId, expectedState, newState, cancellationToken);
            if (result?.State is AgentRunState.Cancelled or AgentRunState.Completed or AgentRunState.Failed)
                terminalTransition.TrySetResult(result);
            return result;
        }
    }

    private sealed class ControlledExecutor : IHostTaskExecutor
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Exception? Failure;
        public bool BlockCancellationCallback;
        public readonly TaskCompletionSource ReleaseCancellationCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _cancellationRegistration;
        public HostTaskExecution? Last;
        public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
        {
            Last = task;
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            if (BlockCancellationCallback)
                _cancellationRegistration = cancellationToken.Register(() =>
                {
                    CancellationObserved.TrySetResult();
                    ReleaseCancellationCallback.Task.GetAwaiter().GetResult();
                });
            await (BlockCancellationCallback ? Release.Task : Release.Task.WaitAsync(cancellationToken));
            if (Failure is not null) throw Failure;
        }

        public void DisposeCancellationRegistration() => _cancellationRegistration.Dispose();
    }
}
