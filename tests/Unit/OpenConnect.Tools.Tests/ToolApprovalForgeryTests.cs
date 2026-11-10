using System.Reflection;
using System.Text.Json;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Tools.Tests;

// AUTH-016c + TOOLS-009/010: forged, replayed, expired, and missing approvals
// must be denied with zero side effects. Exercises the real
// ToolAuthorizationService (exact-argument-digest + identity match +
// FixedTimeEquals + one-time TryAdd + expiry). No production changes.
public sealed class ToolApprovalForgeryTests
{
    private static readonly AgentId Agent = new("agent");
    private static readonly ToolId Tool = new("lookup");
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly TaskId Task = new(Ulid.NewUlid());

    // Permissive schema so forged arguments reach ConsumeApproval
    // (a strict additionalProperties:false schema would reject them at validation).
    private const string PermissiveSchema = """{"type":"object"}""";

    [Fact]
    public void ForgedArguments_Denied_NoSideEffects()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var auth = new ToolAuthorizationService(new AllowPolicy(), clock);
        var request = BaseRequest();
        using var approvedArgs = JsonDocument.Parse("""{"q":"hello"}""");
        using var forgedArgs = JsonDocument.Parse("""{"q":"goodbye"}""");
        var approval = auth.CreateApproval(request, approvedArgs.RootElement, TimeSpan.FromMinutes(1));

        var executions = 0;
        Assert.False(TryConsumeAndExecute(auth, approval, request, forgedArgs.RootElement, ref executions));
        Assert.Equal(0, executions);
    }

    [Fact]
    public void ForgedArguments_BrokerNeverExecutesTool()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var auth = new ToolAuthorizationService(new AllowPolicy(), clock);
        var (broker, invoked) = CreateBroker(auth, PermissiveSchema);
        var request = BaseRequest();
        using var approvedArgs = JsonDocument.Parse("""{"q":"hello"}""");
        using var forgedArgs = JsonDocument.Parse("""{"q":"goodbye"}""");
        var approval = auth.CreateApproval(request, approvedArgs.RootElement, TimeSpan.FromMinutes(1));

        var ex = Assert.Throws<ToolAuthorizationException>(() =>
            broker.InvokeAsync(request, forgedArgs.RootElement, approval).AsTask().GetAwaiter().GetResult());
        Assert.Equal(ErrorCodes.ApprovalRequired, ex.ErrorCode);
        Assert.False(invoked());
    }

    [Fact]
    public void WrongTenant_Denied_NoSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromMinutes(1));

        var executions = 0;
        var forged = request with { TenantId = new TenantId(Ulid.NewUlid()) };
        Assert.False(TryConsumeAndExecute(auth, approval, forged, args.RootElement, ref executions));
        Assert.Equal(0, executions);
    }

    [Fact]
    public void WrongSession_Denied_NoSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromMinutes(1));

        var executions = 0;
        var forged = request with { TaskId = new TaskId(Ulid.NewUlid()) };
        Assert.False(TryConsumeAndExecute(auth, approval, forged, args.RootElement, ref executions));
        Assert.Equal(0, executions);
    }

    [Fact]
    public void WrongTool_Denied_NoSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromMinutes(1));

        var executions = 0;
        var forged = request with { ToolId = new ToolId("other-tool") };
        Assert.False(TryConsumeAndExecute(auth, approval, forged, args.RootElement, ref executions));
        Assert.Equal(0, executions);
    }

    [Fact]
    public void WrongActor_Denied_NoSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromMinutes(1));

        var executions = 0;
        var forged = request with { PrincipalId = new PrincipalId(Ulid.NewUlid()) };
        Assert.False(TryConsumeAndExecute(auth, approval, forged, args.RootElement, ref executions));
        Assert.Equal(0, executions);
    }

    [Fact]
    public void ExpiredApproval_Denied_NoSideEffects()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var auth = new ToolAuthorizationService(new AllowPolicy(), clock);
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));

        var executions = 0;
        Assert.False(TryConsumeAndExecute(auth, approval, request, args.RootElement, ref executions));
        Assert.Equal(0, executions);
    }

    [Fact]
    public void ReplayAfterConsume_Denied_SecondHasNoSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromMinutes(1));

        var executions = 0;
        Assert.True(TryConsumeAndExecute(auth, approval, request, args.RootElement, ref executions));
        Assert.False(TryConsumeAndExecute(auth, approval, request, args.RootElement, ref executions));
        Assert.Equal(1, executions);
    }

    [Fact]
    public void TamperedArgumentsHash_Denied_NoSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromMinutes(1));
        var tampered = WithHash(approval, FlipLastHexChar(approval.ArgumentsHash));

        var executions = 0;
        Assert.False(TryConsumeAndExecute(auth, tampered, request, args.RootElement, ref executions));
        Assert.Equal(0, executions);
    }

    [Fact]
    public void MalformedArgumentsHash_DeniedWithoutThrow_NoSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");
        var approval = auth.CreateApproval(request, args.RootElement, TimeSpan.FromMinutes(1));
        var malformed = WithHash(approval, "ZZZ-not-hex");

        var executions = 0;
        var exception = Record.Exception(() =>
            TryConsumeAndExecute(auth, malformed, request, args.RootElement, ref executions));
        Assert.Null(exception);
        Assert.Equal(0, executions);
    }

    [Fact]
    public void MissingApproval_Denied_NoSideEffects()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");

        var executions = 0;
        Assert.False(TryConsumeAndExecute(auth, null, request, args.RootElement, ref executions));
        Assert.Equal(0, executions);
    }

    [Fact]
    public void MissingApproval_BrokerNeverExecutesTool()
    {
        var auth = new ToolAuthorizationService(new AllowPolicy());
        var (broker, invoked) = CreateBroker(auth, PermissiveSchema);
        var request = BaseRequest();
        using var args = JsonDocument.Parse("""{"q":"hello"}""");

        Assert.Throws<ToolAuthorizationException>(() =>
            broker.InvokeAsync(request, args.RootElement, approval: null).AsTask().GetAwaiter().GetResult());
        Assert.False(invoked());
    }

    private static ToolAccessRequest BaseRequest() => new(Agent, Tool, Principal, Tenant, Task);

    // Execution stub: increments only when the approval is accepted.
    // Denial must therefore leave the counter at zero (zero side effects).
    private static bool TryConsumeAndExecute(
        ToolAuthorizationService auth,
        ToolApproval? approval,
        ToolAccessRequest request,
        JsonElement args,
        ref int executions)
    {
        if (!auth.ConsumeApproval(approval, request, args))
            return false;
        executions++;
        return true;
    }

    private static (ToolBroker Broker, Func<bool> Invoked) CreateBroker(
        ToolAuthorizationService auth, string schema)
    {
        var catalog = new ToolCatalog(auth);
        var wasInvoked = false;
        catalog.Register(new ToolRegistration(new ToolDescriptor(Tool, "Lookup", Agent), schema,
            (args, _) => { wasInvoked = true; return ValueTask.FromResult(args.Clone()); },
            RequiresApproval: true), [Agent]);
        return (new ToolBroker(catalog, auth), () => wasInvoked);
    }

    private static ToolApproval WithHash(ToolApproval source, string argumentsHash)
    {
        var ctor = typeof(ToolApproval).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance,
            [typeof(string), typeof(AgentId), typeof(ToolId), typeof(PrincipalId),
                typeof(TenantId), typeof(TaskId), typeof(string), typeof(DateTimeOffset)]);
        Assert.NotNull(ctor);
        return (ToolApproval)ctor.Invoke(
            [source.Id, source.AgentId, source.ToolId, source.PrincipalId,
                source.TenantId, source.TaskId, argumentsHash, source.ExpiresAt]);
    }

    private static string FlipLastHexChar(string hash)
    {
        Assert.NotEmpty(hash);
        var last = hash[^1];
        var flipped = last == 'A' ? 'B' : 'A';
        return string.Concat(hash.AsSpan(0, hash.Length - 1), flipped.ToString());
    }

    private sealed class AllowPolicy : IToolAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
