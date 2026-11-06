using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Tools;
using Xunit;

namespace OpenConnect.Tools.Tests;

public sealed class ToolBrokerTests
{
    private static readonly AgentId Agent = new("agent");
    private static readonly ToolId Tool = new("lookup");
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly TaskId Task = new(Ulid.NewUlid());
    private const string Schema = """{"type":"object","properties":{"q":{"type":"string"}},"required":["q"],"additionalProperties":false}""";

    [Fact]
    public async Task Discovery_filters_agent_allowlist_and_current_policy()
    {
        var policy = new AllowPolicy(request => request.TenantId == Tenant);
        var (catalog, auth, _) = Create(policy);
        Assert.Single(await catalog.DiscoverAsync(Agent, Principal, Tenant, Task));
        Assert.Empty(await catalog.DiscoverAsync(Agent, Principal, new TenantId(Ulid.NewUlid()), Task));
    }

    [Fact]
    public async Task Invocation_reauthorizes_and_denies_cross_tenant()
    {
        var (catalog, auth, invoked) = Create(new AllowPolicy(request => request.TenantId == Tenant));
        var broker = new ToolBroker(catalog, auth);
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await broker.InvokeAsync(
            new(Agent, Tool, Principal, new TenantId(Ulid.NewUlid()), Task), args.RootElement));
        Assert.False(invoked());
    }

    [Fact]
    public async Task Expired_or_replayed_approval_is_rejected()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var auth = new ToolAuthorizationService(new AllowPolicy(_ => true), clock);
        var (catalog, _, _) = Create(new AllowPolicy(_ => true), auth);
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var request = new ToolAccessRequest(Agent, Tool, Principal, Tenant, Task);
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<ToolAuthorizationException>(async () => await new ToolBroker(catalog, auth).InvokeAsync(request, args.RootElement, approval));
    }

    [Fact]
    public async Task Approval_binds_canonical_arguments_and_rejects_prompt_escalation_as_extra_arguments()
    {
        var (catalog, auth, invoked) = Create(new AllowPolicy(_ => true));
        var broker = new ToolBroker(catalog, auth);
        var request = new ToolAccessRequest(Agent, Tool, Principal, Tenant, Task);
        using var approvedArgs = JsonDocument.Parse("""{"q":"hello"}""");
        using var reorderedArgs = JsonDocument.Parse("""{"q":"hello","system_override":true}""");
        var approval = auth.CreateApproval(request, approvedArgs.RootElement, TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<ToolValidationException>(async () => await broker.InvokeAsync(request, reorderedArgs.RootElement, approval));
        Assert.False(invoked());
    }

    private static (ToolCatalog Catalog, ToolAuthorizationService Authorization, Func<bool> Invoked) Create(
        IToolAccessPolicy policy, ToolAuthorizationService? authorization = null)
    {
        authorization ??= new ToolAuthorizationService(policy);
        var catalog = new ToolCatalog(authorization);
        var wasInvoked = false;
        catalog.Register(new ToolRegistration(new ToolDescriptor(Tool, "Lookup", Agent), Schema,
            (args, _) => { wasInvoked = true; return ValueTask.FromResult(args.Clone()); }, RequiresApproval: true), [Agent]);
        return (catalog, authorization, () => wasInvoked);
    }

    private sealed class AllowPolicy(Func<ToolAccessRequest, bool> allow) : IToolAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(allow(request));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
