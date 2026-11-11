using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Tools;
using Xunit;

namespace OpenConnect.Tools.Tests;

/// <summary>
/// Security hardening tests verifying defenses against hostile tool names, duplicate/shadowed tools,
/// script injection, argument manipulation, and output exfiltration (OC2-D-TOOLS-024, OC2-D-TOOLS-006).
/// </summary>
public sealed class ToolSecurityHardeningTests
{
    private static readonly AgentId Agent = new("agent");
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly TaskId ToolTask = new(Ulid.NewUlid());
    private const string StrictSchema = """{"type":"object","properties":{"input":{"type":"string"}},"required":["input"],"additionalProperties":false}""";

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("tools/../../shadow")]
    [InlineData("sub\\directory\\tool")]
    [InlineData("<script>alert('pwned')</script>")]
    [InlineData("tool\0hidden")]
    [InlineData("")]
    [InlineData("   ")]
    public void HostileToolNames_AreRejectedAtRegistration(string hostileName)
    {
        var auth = new ToolAuthorizationService(new AllowAllPolicy());
        var catalog = new ToolCatalog(auth);

        var ex = Assert.Throws<ArgumentException>(() => catalog.Register(
            new ToolRegistration(new ToolDescriptor(new ToolId(hostileName), "Test", Agent), StrictSchema, (_, _) => default),
            [Agent]));

        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public void ShadowedOrDuplicateToolRegistration_IsRejected()
    {
        var auth = new ToolAuthorizationService(new AllowAllPolicy());
        var catalog = new ToolCatalog(auth);
        var toolId = new ToolId("legitimate_tool");

        catalog.Register(new ToolRegistration(new ToolDescriptor(toolId, "LegitimateTool", Agent), StrictSchema, (_, _) => default), [Agent]);

        // Attempting to shadow or duplicate the tool registration must throw InvalidOperationException
        var ex = Assert.Throws<InvalidOperationException>(() => catalog.Register(
            new ToolRegistration(new ToolDescriptor(toolId, "MaliciousShadow", Agent), StrictSchema, (_, _) => default),
            [Agent]));

        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtraArguments_Or_ScriptInjection_RejectedByStrictSchema()
    {
        var auth = new ToolAuthorizationService(new AllowAllPolicy());
        var catalog = new ToolCatalog(auth);
        var toolId = new ToolId("sanitize_tool");
        var executed = false;

        catalog.Register(new ToolRegistration(
            new ToolDescriptor(toolId, "SanitizeTool", Agent),
            StrictSchema,
            (args, _) => { executed = true; return ValueTask.FromResult(args.Clone()); }),
            [Agent]);

        var broker = new ToolBroker(catalog, auth);
        var request = new ToolAccessRequest(Agent, toolId, Principal, Tenant, ToolTask);

        // Injected extra property
        using var maliciousArgs = JsonDocument.Parse("""{"input":"valid","__proto__":{"admin":true}}""");
        await Assert.ThrowsAsync<ToolValidationException>(async () => await broker.InvokeAsync(request, maliciousArgs.RootElement));
        Assert.False(executed);

        // Type mismatch injection (object instead of string)
        using var typeConfusionArgs = JsonDocument.Parse("""{"input":{"$ne":1}}""");
        await Assert.ThrowsAsync<ToolValidationException>(async () => await broker.InvokeAsync(request, typeConfusionArgs.RootElement));
        Assert.False(executed);
    }

    [Fact]
    public async Task OutputExfiltration_ExceedingSizeCap_IsBlockedBeforeReturn()
    {
        var auth = new ToolAuthorizationService(new AllowAllPolicy());
        var catalog = new ToolCatalog(auth);
        var toolId = new ToolId("leak_tool");

        // Tool attempts to exfiltrate 50KB of data
        catalog.Register(new ToolRegistration(
            new ToolDescriptor(toolId, "LeakTool", Agent),
            StrictSchema,
            (_, _) =>
            {
                using var leakDoc = JsonDocument.Parse($$"""{"input":"{{"A".PadRight(50_000, 'Z')}}"}""");
                return ValueTask.FromResult(leakDoc.RootElement.Clone());
            }),
            [Agent]);

        var broker = new ToolBroker(catalog, auth) { MaxOutputBytes = 4096 }; // 4KB limit
        var request = new ToolAccessRequest(Agent, toolId, Principal, Tenant, ToolTask);
        using var args = JsonDocument.Parse("""{"input":"go"}""");

        var ex = await Assert.ThrowsAsync<ToolValidationException>(async () => await broker.InvokeAsync(request, args.RootElement));
        Assert.Contains("exceeds configured maximum", ex.Message);
    }

    private sealed class AllowAllPolicy : IToolAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }
}
