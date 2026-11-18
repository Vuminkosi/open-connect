using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Tools;
using Xunit;

namespace OpenConnect.Tools.Tests;

/// <summary>
/// Verifies the tool-budget reservation delegate choke point on <see cref="ToolBroker.InvokeAsync"/>.
/// The intended production adapter is <c>() => ledger.TryReserveToolCall()</c>, mirrored here by
/// <see cref="FakeBudgetLedger"/> (a minimal stand-in for the Agents-layer ledger, which Tools
/// must not reference directly).
/// </summary>
public sealed class ToolBudgetTests
{
    private static readonly AgentId Agent = new("agent");
    private static readonly ToolId Tool = new("lookup");
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly TaskId Task = new(Ulid.NewUlid());
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"],"additionalProperties":false}""";

    [Fact]
    public async Task ExhaustedDelegate_DeniesNPlusOnethCall_WithZeroSideEffects()
    {
        var (catalog, auth, invocations) = Create(requiresApproval: false);
        var broker = new ToolBroker(catalog, auth);
        var ledger = new FakeBudgetLedger(toolCallBudget: 1);
        var request = new ToolAccessRequest(Agent, Tool, Principal, Tenant, Task);
        using var args = JsonDocument.Parse("""{"q":"hello"}""");

        // Intended ledger adapter shape: () => ledger.TryReserveToolCall().
        await broker.InvokeAsync(request, args.RootElement, tryReserveToolCall: () => ledger.TryReserveToolCall());
        Assert.Equal(1, invocations());

        var ex = await Assert.ThrowsAsync<ToolAuthorizationException>(async () =>
            await broker.InvokeAsync(request, args.RootElement, tryReserveToolCall: () => ledger.TryReserveToolCall()));
        Assert.Equal(ErrorCodes.ToolNotAllowed, ex.ErrorCode);
        Assert.Equal(1, invocations());
    }

    [Fact]
    public async Task UnconstrainedDelegate_AllowsAndInvokesOnce()
    {
        var (catalog, auth, invocations) = Create(requiresApproval: false);
        var broker = new ToolBroker(catalog, auth);
        var request = new ToolAccessRequest(Agent, Tool, Principal, Tenant, Task);
        using var args = JsonDocument.Parse("""{"q":"hello"}""");

        var result = await broker.InvokeAsync(request, args.RootElement, tryReserveToolCall: () => true);

        Assert.Equal(1, invocations());
        Assert.Equal("hello", result.GetProperty("q").GetString());
    }

    [Fact]
    public async Task NullDelegate_PreservesExistingUnenforcedBehavior()
    {
        var (catalog, auth, invocations) = Create(requiresApproval: false);
        var broker = new ToolBroker(catalog, auth);
        var request = new ToolAccessRequest(Agent, Tool, Principal, Tenant, Task);
        using var args = JsonDocument.Parse("""{"q":"hello"}""");

        // No delegate passed: budget unenforced, invocation proceeds as before.
        await broker.InvokeAsync(request, args.RootElement);

        Assert.Equal(1, invocations());
    }

    [Fact]
    public async Task ApprovalDenied_DeniesBeforeConsultingDelegate()
    {
        var (catalog, auth, invocations) = Create(requiresApproval: true);
        var broker = new ToolBroker(catalog, auth);
        var request = new ToolAccessRequest(Agent, Tool, Principal, Tenant, Task);
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var delegateConsultations = 0;
        bool Reserve()
        {
            delegateConsultations++;
            return true;
        }

        var ex = await Assert.ThrowsAsync<ToolAuthorizationException>(async () =>
            await broker.InvokeAsync(request, args.RootElement, tryReserveToolCall: Reserve));

        Assert.Equal(ErrorCodes.ApprovalRequired, ex.ErrorCode);
        Assert.Equal(0, delegateConsultations);
        Assert.Equal(0, invocations());
    }

    private static (ToolCatalog Catalog, ToolAuthorizationService Authorization, Func<int> Invocations) Create(bool requiresApproval)
    {
        var authorization = new ToolAuthorizationService(new AllowPolicy(_ => true));
        var catalog = new ToolCatalog(authorization);
        var invocations = 0;
        catalog.Register(new ToolRegistration(new ToolDescriptor(Tool, "Lookup", Agent), Schema,
            (args, _) => { invocations++; return ValueTask.FromResult(args.Clone()); }, RequiresApproval: requiresApproval), [Agent]);
        return (catalog, authorization, () => invocations);
    }

    private sealed class AllowPolicy(Func<ToolAccessRequest, bool> allow) : IToolAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(allow(request));
    }

    /// <summary>Minimal stand-in for the Agents-layer budget ledger; production wiring passes
    /// <c>() => ledger.TryReserveToolCall()</c> as the reservation delegate.</summary>
    private sealed class FakeBudgetLedger(int toolCallBudget)
    {
        private int _remaining = toolCallBudget;
        public bool TryReserveToolCall()
        {
            if (_remaining <= 0) return false;
            _remaining--;
            return true;
        }
    }
}
