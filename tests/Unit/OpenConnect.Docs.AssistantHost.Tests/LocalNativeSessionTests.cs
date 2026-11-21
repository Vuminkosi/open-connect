using System.Collections.Immutable;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Agents.Execution;
using OpenConnect.Docs.AssistantHost.Sessions;
using Xunit;

namespace OpenConnect.Docs.AssistantHost.Tests;

public sealed class LocalNativeSessionTests
{
    [Fact]
    public async Task StoreUsesExactIdentityAndCompareAndSwap()
    {
        var store = new LocalNativeSessionStore();
        var binding = Binding();
        var other = new NativeAgentSessionBinding(binding.SessionId, binding.TenantId, new PrincipalId(Ulid.NewUlid()),
            "documentation", "v1", "maf", "v1", "provider");
        var first = new NativeAgentSessionRecord(binding, 1, [1, 2, 3]);
        Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(first, 0));
        Assert.Null(await store.GetAsync(other));
        Assert.Equal(NativeAgentSessionSaveOutcome.Conflict, await store.SaveAsync(first, 0));
        var next = new NativeAgentSessionRecord(binding, 2, [4, 5]);
        Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(next, 1));
        Assert.Equal(2, (await store.GetAsync(binding))!.Version);
    }

    [Fact]
    public async Task GateRejectsMismatchedScopeAndSerializesTurns()
    {
        var gate = new LocalNativeTurnGate();
        var binding = Binding();
        using var context = Context(binding);
        using var other = context with { PrincipalOrWorkloadScope = Ulid.NewUlid().ToString() };
        Assert.Null(await gate.TryAcquireAsync(binding, other));
        var first = await gate.TryAcquireAsync(binding, context);
        Assert.NotNull(first);
        Assert.Null(await gate.TryAcquireAsync(binding, context));
        await first.DisposeAsync();
        await first.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ValidateOwnershipAsync());
        var second = await gate.TryAcquireAsync(binding, context);
        Assert.NotNull(second);
        await second.DisposeAsync();
    }

    [Fact]
    public async Task StoreRejectsNewSessionsAtCapacityWithoutEvictingExistingState()
    {
        var store = new LocalNativeSessionStore();
        var first = Binding();
        Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(new(first, 1, [1]), 0));
        for (var i = 1; i < 2048; i++)
            Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(new(Binding(), 1, [1]), 0));
        Assert.Equal(NativeAgentSessionSaveOutcome.Conflict, await store.SaveAsync(new(Binding(), 1, [1]), 0));
        Assert.Equal(NativeAgentSessionSaveOutcome.Saved, await store.SaveAsync(new(first, 2, [2]), 1));
        Assert.Equal(2, (await store.GetAsync(first))!.Version);
    }

    private static NativeAgentSessionBinding Binding() => new(new SessionId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid()),
        new PrincipalId(Ulid.NewUlid()), "documentation", "v1", "maf", "v1", "provider");

    private static AgentExecutionContext Context(NativeAgentSessionBinding binding) => new()
    {
        TenantScope = binding.TenantId.Value.ToString(), PrincipalOrWorkloadScope = binding.PrincipalId.Value.ToString(),
        IdentityKind = AgentIdentityMode.ActiveUser, SessionId = binding.SessionId, TaskId = new TaskId(Ulid.NewUlid()),
        AgentExecutionId = new ExecutionId(Ulid.NewUlid()), Depth = 0, CatalogRevision = 1, PolicyRevision = 1,
        AuthRevision = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(1), RemainingBudgets = ImmutableDictionary<string, long>.Empty,
        CancellationToken = CancellationToken.None
    };
}
