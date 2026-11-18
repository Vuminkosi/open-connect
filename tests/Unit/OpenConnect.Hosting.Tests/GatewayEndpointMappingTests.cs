using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Agents.Templates;
using OpenConnect.Core;
using Xunit;

namespace OpenConnect.Hosting.AspNetCore.Tests;

public class GatewayEndpointMappingTests
{
    [Fact]
    public void REG_AT_030_EndpointMappingIdempotencySafeAgainstDuplicateCalls()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        services.AddLogging();
        services.AddOpenConnect();

        using var sp = services.BuildServiceProvider();
        var builder = new DefaultEndpointRouteBuilder(sp);

        // Call MapOpenConnect first
        builder.MapOpenConnect();

        // Call sub-feature mappings explicitly; must be idempotent and not throw duplicate route collisions
        builder.MapOpenConnectVoice();
        builder.MapOpenConnectNavigator();
        builder.MapOpenConnectMcp();
        builder.MapOpenConnect();

        Assert.NotEmpty(builder.DataSources);
    }

    [Fact]
    public void REG_AT_008_012_LeadAgentSingleRegistrationInvariant()
    {
        var services = new ServiceCollection();
        services.AddOpenConnectLeadAgent("lead-agent-primary", b =>
        {
            b.Name = "Primary Lead";
            b.ExecutionProfile = AgentExecutionProfile.Direct;
            b.EnablePlanning = true;
            b.EnableTodoTracking = true;
        });

        // Attempting to register a second lead agent throws
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddOpenConnectLeadAgent("lead-agent-secondary", b => b.Name = "Secondary Lead");
        });

        Assert.Contains("Only one lead agent may be registered", ex.Message);
    }

    private sealed class DefaultEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() =>
            throw new NotImplementedException();
    }
}
