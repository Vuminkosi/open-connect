using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;
using OpenConnect.IMessaging;
using Xunit;

namespace OpenConnect.Core.Tests;

public sealed class WorkerEventConsumerTests
{
    private static MessagingEventEnvelope Envelope(string eventId = "evt-1", TenantId? tenant = null) => new(
        1, eventId, MessagingEventTypes.MessageCommitted, tenant ?? new TenantId(Ulid.NewUlid()),
        new ConversationId(Ulid.NewUlid()), 1, "cursor-1", DateTimeOffset.UtcNow, "{}");

    [Fact]
    public async Task Ac1_HandlerThrows_EventIsRedeliverableAndHandlerRunsAgain()
    {
        var calls = 0;
        var consumer = new WorkerEventConsumer((_, _) =>
        {
            if (++calls == 1)
                throw new InvalidOperationException("boom");
            return ValueTask.CompletedTask;
        });
        var env = Envelope();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await consumer.DispatchAsync(env));
        Assert.True(await consumer.DispatchAsync(env));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Ac1_HandlerCancelled_EventIsRedeliverable()
    {
        var calls = 0;
        var consumer = new WorkerEventConsumer((_, ct) =>
        {
            if (++calls == 1)
                ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        });
        var env = Envelope();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await consumer.DispatchAsync(env, cts.Token));
        Assert.True(await consumer.DispatchAsync(env));
        Assert.Equal(1, calls);

        // Handler-originated cancellation with a live dispatch token also releases the key.
        var calls2 = 0;
        var consumer2 = new WorkerEventConsumer((_, _) =>
        {
            if (++calls2 == 1)
                throw new OperationCanceledException();
            return ValueTask.CompletedTask;
        });
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await consumer2.DispatchAsync(env));
        Assert.True(await consumer2.DispatchAsync(env));
        Assert.Equal(2, calls2);
    }

    [Fact]
    public async Task Ac1_FailureDoesNotMarkProcessed_ThenSuccessDeduplicates()
    {
        var calls = 0;
        var consumer = new WorkerEventConsumer((_, _) =>
        {
            if (++calls == 1)
                throw new InvalidOperationException("boom");
            return ValueTask.CompletedTask;
        });
        var env = Envelope();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await consumer.DispatchAsync(env));
        Assert.True(await consumer.DispatchAsync(env));
        Assert.False(await consumer.DispatchAsync(env));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Ac2_ConcurrentDuplicate_DoesNotReturnFalseUntilFirstSucceeds()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var consumer = new WorkerEventConsumer(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await gate.Task;
        });
        var env = Envelope();

        var first = consumer.DispatchAsync(env).AsTask();
        await started.Task;
        var second = consumer.DispatchAsync(env).AsTask();

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        gate.SetResult();
        Assert.True(await first);
        Assert.False(await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Ac2_ConcurrentDuplicate_WhenFirstFails_WaiterRunsHandlerAndReturnsTrue()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var consumer = new WorkerEventConsumer(async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await gate.Task;
                throw new InvalidOperationException("boom");
            }
        });
        var env = Envelope();

        var first = consumer.DispatchAsync(env).AsTask();
        await started.Task;
        var second = consumer.DispatchAsync(env).AsTask();
        Assert.False(second.IsCompleted);

        gate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first);
        Assert.True(await second);
        Assert.Equal(2, calls);
        Assert.False(await consumer.DispatchAsync(env));
    }

    [Fact]
    public async Task WaiterCancellation_ThrowsWithoutAffectingFirstAttempt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var consumer = new WorkerEventConsumer(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await gate.Task;
        });
        var env = Envelope();
        using var cts = new CancellationTokenSource();

        var first = consumer.DispatchAsync(env).AsTask();
        await started.Task;
        var waiter = consumer.DispatchAsync(env, cts.Token).AsTask();
        Assert.False(waiter.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        Assert.False(first.IsCompleted);

        gate.SetResult();
        Assert.True(await first);
        Assert.False(await consumer.DispatchAsync(env));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Ac4_Eviction_OldestCompletedEntryIsReprocessed()
    {
        var calls = new Dictionary<string, int>();
        var consumer = new WorkerEventConsumer((e, _) =>
        {
            calls[e.EventId] = calls.GetValueOrDefault(e.EventId) + 1;
            return ValueTask.CompletedTask;
        }, maxDedupEntries: 2);
        var tenant = new TenantId(Ulid.NewUlid());

        Assert.True(await consumer.DispatchAsync(Envelope("a", tenant)));
        Assert.True(await consumer.DispatchAsync(Envelope("b", tenant)));
        Assert.True(await consumer.DispatchAsync(Envelope("c", tenant))); // evicts "a"

        Assert.False(await consumer.DispatchAsync(Envelope("b", tenant)));
        Assert.False(await consumer.DispatchAsync(Envelope("c", tenant)));
        Assert.True(await consumer.DispatchAsync(Envelope("a", tenant)));
        Assert.Equal(2, calls["a"]);
        Assert.Equal(1, calls["b"]);
    }

    [Fact]
    public async Task Ac4_Eviction_DoesNotEvictInFlightEntries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCalls = 0;
        var consumer = new WorkerEventConsumer(async (e, _) =>
        {
            if (e.EventId == "slow")
            {
                Interlocked.Increment(ref slowCalls);
                started.TrySetResult();
                await gate.Task;
            }
        }, maxDedupEntries: 1);
        var tenant = new TenantId(Ulid.NewUlid());

        var slow = consumer.DispatchAsync(Envelope("slow", tenant)).AsTask();
        await started.Task;

        Assert.True(await consumer.DispatchAsync(Envelope("x", tenant)));
        Assert.True(await consumer.DispatchAsync(Envelope("y", tenant)));

        var duplicate = consumer.DispatchAsync(Envelope("slow", tenant)).AsTask();
        Assert.False(duplicate.IsCompleted);

        gate.SetResult();
        Assert.True(await slow);
        Assert.False(await duplicate);
        Assert.Equal(1, slowCalls);
    }

    [Fact]
    public async Task SameEventIdForDifferentTenants_IsProcessedForBoth()
    {
        var calls = 0;
        var consumer = new WorkerEventConsumer((_, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });

        Assert.True(await consumer.DispatchAsync(Envelope("same")));
        Assert.True(await consumer.DispatchAsync(Envelope("same")));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Ac5_NullHandler_IsNoOpThatReportsTrueOnce()
    {
        var consumer = new WorkerEventConsumer();
        var env = Envelope();

        Assert.True(await consumer.DispatchAsync(env));
        Assert.False(await consumer.DispatchAsync(env));
    }

    [Fact]
    public async Task InvalidEnvelope_ThrowsMessagingDomainException()
    {
        var consumer = new WorkerEventConsumer();
        var valid = Envelope();

        var badVersion = valid with { SchemaVersion = 2 };
        var ex1 = await Assert.ThrowsAsync<MessagingDomainException>(async () => await consumer.DispatchAsync(badVersion));
        Assert.Equal(ErrorCodes.InvalidRequest, ex1.ErrorCode);

        var badId = valid with { EventId = " " };
        var ex2 = await Assert.ThrowsAsync<MessagingDomainException>(async () => await consumer.DispatchAsync(badId));
        Assert.Equal(ErrorCodes.InvalidRequest, ex2.ErrorCode);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await consumer.DispatchAsync(null!));
    }
}
