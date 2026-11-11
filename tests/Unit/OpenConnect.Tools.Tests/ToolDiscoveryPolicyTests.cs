using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Tools;
using Xunit;

namespace OpenConnect.Tools.Tests;

/// <summary>
/// Verifies bounded tool discovery (OC2-D-TOOLS-004) and policy-filtered tool descriptions (OC2-D-TOOLS-005).
/// </summary>
public sealed class ToolDiscoveryPolicyTests
{
    private static readonly AgentId LeadAgent = new("lead");
    private static readonly AgentId WorkerAgent = new("worker");
    private static readonly ToolId PublicTool = new("public_query");
    private static readonly ToolId AdminTool = new("admin_reset");
    private static readonly ToolId WorkerOnlyTool = new("worker_compute");

    private static readonly PrincipalId UserPrincipal = new(Ulid.NewUlid());
    private static readonly PrincipalId AdminPrincipal = new(Ulid.NewUlid());
    private static readonly TenantId TenantA = new(Ulid.NewUlid());
    private static readonly TenantId TenantB = new(Ulid.NewUlid());
    private static readonly TaskId Task1 = new(Ulid.NewUlid());

    private const string Schema = """{"type":"object"}""";

    [Fact]
    public async Task DiscoverAsync_Intersects_AgentAllowlist_And_PolicyGrants()
    {
        var auth = new ToolAuthorizationService(new ScopedPolicy());
        var catalog = new ToolCatalog(auth);

        catalog.Register(new ToolRegistration(new ToolDescriptor(PublicTool, "PublicQuery", LeadAgent), Schema, (_, _) => default), [LeadAgent, WorkerAgent]);
        catalog.Register(new ToolRegistration(new ToolDescriptor(AdminTool, "AdminReset", LeadAgent), Schema, (_, _) => default), [LeadAgent]);
        catalog.Register(new ToolRegistration(new ToolDescriptor(WorkerOnlyTool, "WorkerCompute", WorkerAgent), Schema, (_, _) => default), [WorkerAgent]);

        // Case 1: Lead agent with standard user in Tenant A -> only PublicTool (AdminTool requires AdminPrincipal)
        var discoveredLeadUser = await catalog.DiscoverAsync(LeadAgent, UserPrincipal, TenantA, Task1);
        Assert.Single(discoveredLeadUser);
        Assert.Equal(PublicTool, discoveredLeadUser[0].Id);

        // Case 2: Lead agent with admin in Tenant A -> PublicTool + AdminTool
        var discoveredLeadAdmin = await catalog.DiscoverAsync(LeadAgent, AdminPrincipal, TenantA, Task1);
        Assert.Equal(2, discoveredLeadAdmin.Count);
        Assert.Contains(discoveredLeadAdmin, t => t.Id == PublicTool);
        Assert.Contains(discoveredLeadAdmin, t => t.Id == AdminTool);

        // Case 3: Worker agent with standard user in Tenant A -> only tools owned by WorkerAgent and in its allowlist
        var discoveredWorkerUser = await catalog.DiscoverAsync(WorkerAgent, UserPrincipal, TenantA, Task1);
        Assert.Single(discoveredWorkerUser);
        Assert.Equal(WorkerOnlyTool, discoveredWorkerUser[0].Id);
        Assert.DoesNotContain(discoveredWorkerUser, t => t.Id == AdminTool); // Not allowlisted for WorkerAgent

        // Case 4: Cross-tenant isolation (Tenant B policy blocks all)
        var discoveredTenantB = await catalog.DiscoverAsync(LeadAgent, AdminPrincipal, TenantB, Task1);
        Assert.Empty(discoveredTenantB);
    }

    [Fact]
    public async Task DiscoverAsync_Redacts_Untrusted_Or_Denied_Tool_Descriptions()
    {
        var auth = new ToolAuthorizationService(new ScopedPolicy());
        var catalog = new ToolCatalog(auth);

        catalog.Register(new ToolRegistration(new ToolDescriptor(AdminTool, "SuperSecretInternalToolDescription", LeadAgent), Schema, (_, _) => default), [LeadAgent]);

        // Standard user cannot see the admin tool description in discovered list
        var userTools = await catalog.DiscoverAsync(LeadAgent, UserPrincipal, TenantA, Task1);
        Assert.Empty(userTools);
        Assert.DoesNotContain(userTools, t => t.Name.Contains("SuperSecret", StringComparison.Ordinal));

        // Legacy unfiltered GetAuthorized returns empty array fail-closed
        var legacy = catalog.GetAuthorized(LeadAgent, AdminPrincipal, TenantA);
        Assert.Empty(legacy);
    }

    private sealed class ScopedPolicy : IToolAccessPolicy
    {
        public ValueTask<bool> IsAllowedAsync(ToolAccessRequest request, CancellationToken cancellationToken)
        {
            // Tenant B is disabled
            if (request.TenantId == TenantB)
                return ValueTask.FromResult(false);

            // AdminTool only for AdminPrincipal
            if (request.ToolId == AdminTool && request.PrincipalId != AdminPrincipal)
                return ValueTask.FromResult(false);

            return ValueTask.FromResult(true);
        }
    }
}
