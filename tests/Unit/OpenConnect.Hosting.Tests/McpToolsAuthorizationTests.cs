using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.AspNetCore.Tests;

public sealed class McpToolsAuthorizationTests
{
    private static readonly ToolDescriptor Public = new(new ToolId("public_query"), "PublicQuery", new AgentId("default"));
    private static readonly ToolDescriptor Admin = new(new ToolId("admin_reset"), "AdminReset", new AgentId("default"));

    [Fact]
    public async Task Tools_AreFilteredPerTenantAndPrincipal()
    {
        var admin = Ulid.NewUlid();
        var user = Ulid.NewUlid();
        var tenantA = Ulid.NewUlid();
        var tenantB = Ulid.NewUlid();
        var discovery = new PolicyDiscovery(admin, tenantA);
        var services = new ServiceCollection().AddLogging().AddSingleton<IToolDiscovery>(discovery).BuildServiceProvider();

        var adminTools = await InvokeAsync(services, tenantA, admin);
        var userTools = await InvokeAsync(services, tenantA, user);
        var otherTenant = await InvokeAsync(services, tenantB, admin);

        Assert.Equal(["admin_reset", "public_query"], adminTools);
        Assert.Equal(["public_query"], userTools);
        Assert.Empty(otherTenant);
    }

    [Fact]
    public async Task Tools_AgentQueryIsPassedToDiscovery_AndInvalidAgentRejected()
    {
        var discovery = new PolicyDiscovery(Ulid.NewUlid(), Ulid.NewUlid());
        var services = new ServiceCollection().AddLogging().AddSingleton<IToolDiscovery>(discovery).BuildServiceProvider();
        await InvokeAsync(services, Ulid.NewUlid(), Ulid.NewUlid(), "worker");
        Assert.Equal(new AgentId("worker"), discovery.LastAgent);

        var context = Context(services, Ulid.NewUlid(), Ulid.NewUlid());
        var result = await EndpointRouteBuilderExtensions.HandleListMcpToolsAsync(context, new string('a', 129), CancellationToken.None);
        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task Tools_MissingIdentityClaims_AreDenied()
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<IToolDiscovery>(new PolicyDiscovery(Ulid.NewUlid(), Ulid.NewUlid())).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(new ClaimsIdentity("test")) };
        context.Response.Body = new MemoryStream();

        var result = await EndpointRouteBuilderExtensions.HandleListMcpToolsAsync(context, null, CancellationToken.None);
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Tools_WithoutPolicyDiscovery_FailClosed()
    {
        var registry = new CoreToolRegistry();
        registry.RegisterDescriptor(Public);
        var services = new ServiceCollection().AddLogging().AddSingleton<IToolCatalog>(registry).BuildServiceProvider();

        Assert.Empty(await InvokeAsync(services, Ulid.NewUlid(), Ulid.NewUlid()));
        Assert.Empty(registry.GetAuthorized(new AgentId("default"), new PrincipalId(Ulid.NewUlid()), new TenantId(Ulid.NewUlid())));
        Assert.Single(registry.GetForAgent(new AgentId("default")));
    }

    private static DefaultHttpContext Context(IServiceProvider services, Ulid tenant, Ulid principal)
    {
        var identity = new ClaimsIdentity([new Claim("tenant_id", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, principal.ToString())], "test");
        return new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(identity), Response = { Body = new MemoryStream() } };
    }

    private static async Task<string[]> InvokeAsync(IServiceProvider services, Ulid tenant, Ulid principal, string? agent = null)
    {
        var context = Context(services, tenant, principal);
        var result = await EndpointRouteBuilderExtensions.HandleListMcpToolsAsync(context, agent, CancellationToken.None);
        await result.ExecuteAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetProperty("value").GetString()!).Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class PolicyDiscovery(Ulid adminPrincipal, Ulid tenant) : IToolDiscovery
    {
        public AgentId LastAgent { get; private set; }

        public ValueTask<IReadOnlyList<ToolDescriptor>> DiscoverAsync(AgentId agent, PrincipalId principal, TenantId tenantId, TaskId task, CancellationToken cancellationToken = default)
        {
            LastAgent = agent;
            if (tenantId.Value != tenant) return new([]);
            return new(principal.Value == adminPrincipal ? [Public, Admin] : [Public]);
        }
    }
}
