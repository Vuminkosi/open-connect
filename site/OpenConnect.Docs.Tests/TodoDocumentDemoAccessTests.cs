using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Components.Authorization;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Samples.Blazor;
using OpenConnect.Samples.Blazor.Components.Pages;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class TodoDocumentDemoAccessTests
{
    private static TodoGatewayOptions Options() => new() { Mode = TodoGatewayMode.SameHostGateway };

    [Fact]
    public void Requires_explicit_development_and_same_host_configuration()
    {
        Assert.Throws<InvalidOperationException>(() => new TodoDocumentDemoAccess(new Environment("Production"), Options(), true));
        Assert.Throws<InvalidOperationException>(() => new TodoDocumentDemoAccess(new Environment("Development"), Options(), false));
        Assert.Throws<InvalidOperationException>(() => new TodoDocumentDemoAccess(new Environment("Development"), new TodoGatewayOptions(), true));
    }

    [Fact]
    public void Requires_the_configured_authenticated_identity_and_scope()
    {
        var options = Options();
        var access = new TodoDocumentDemoAccess(new Environment("Development"), options, true);
        ClaimsPrincipal Principal(string scheme, string tenant, string principal) => new(new ClaimsIdentity(
            [new Claim("tenant_id", tenant), new Claim(ClaimTypes.NameIdentifier, principal)], scheme));
        var user = Principal(SameHostDevelopmentAuthenticationHandler.SchemeName, options.TenantId, options.PrincipalId);
        var context = access.CreateContext(user);
        Assert.True(access.Authorizes(context));
        Assert.False(access.Authorizes(context with { Destination = EgressDestination.ExternalModel }));
        Assert.False(access.Authorizes(context with { Session = new SessionId(default) }));
        Assert.Throws<UnauthorizedAccessException>(() => access.CreateContext(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Throws<UnauthorizedAccessException>(() => access.CreateContext(Principal("other", options.TenantId, options.PrincipalId)));
        Assert.Throws<UnauthorizedAccessException>(() => access.CreateContext(Principal(SameHostDevelopmentAuthenticationHandler.SchemeName,
            Ulid.NewUlid().ToString(), options.PrincipalId)));
        Assert.Throws<UnauthorizedAccessException>(() => access.CreateContext(Principal(SameHostDevelopmentAuthenticationHandler.SchemeName,
            options.TenantId, Ulid.NewUlid().ToString())));
    }

    [Fact]
    public void Document_page_does_not_offer_upload_when_disabled()
    {
        using var context = new BunitContext();
        var component = context.Render<DocumentExample>();
        Assert.Contains("This example is not enabled", component.Markup);
        Assert.Empty(component.FindAll("input[type=file]"));
    }

    [Fact]
    public void Enabled_document_page_offers_an_explicit_bounded_upload()
    {
        using var context = new BunitContext();
        var options = Options();
        context.Services.AddSingleton(new TodoDocumentDemoAccess(new Environment("Development"), options, true));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<IPrivacyFirewall>(new TestFirewall());
        context.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()));
        context.Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(options));
        context.Services.AddScoped<TodoDocumentDemo>();
        var component = context.Render<DocumentExample>();
        Assert.Contains("Use non-sensitive sample documents", component.Markup);
        Assert.Single(component.FindAll("input[type=file]"));
        Assert.Contains("10 MiB", component.Markup);
        Assert.True(component.Find("button").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Runner_rejects_unauthenticated_users_before_opening_workers()
    {
        var access = new TodoDocumentDemoAccess(new Environment("Development"), Options(), true);
        var demo = new TodoDocumentDemo(access, new ConfigurationBuilder().Build(), new TestFirewall(),
            Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => demo.ProcessAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public void CapabilityMonitorRequiresExplicitDevelopmentIdentityAndProtectedEndpoints()
    {
        var settings = new Dictionary<string, string?>
        {
            ["OPENCONNECT_WORKER_TOKEN"] = "worker-token",
            ["OpenConnect:CapabilityMonitor:KnowledgeEndpoint"] = "https://knowledge.example.test",
            ["OpenConnect:CapabilityMonitor:InferenceEndpoint"] = "https://inference.example.test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Equal(2, TodoCapabilityMonitorConfiguration.Create(configuration, new Environment("Development"), Options()).Count);
        Assert.Throws<InvalidOperationException>(() => TodoCapabilityMonitorConfiguration.Create(configuration, new Environment("Production"), Options()));
        settings["OpenConnect:CapabilityMonitor:KnowledgeEndpoint"] = "http://knowledge.example.test";
        configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Throws<ArgumentException>(() => TodoCapabilityMonitorConfiguration.Create(configuration, new Environment("Development"), Options()));
        settings["OpenConnect:CapabilityMonitor:AllowInsecureHttp"] = "true";
        configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        Assert.Equal(2, TodoCapabilityMonitorConfiguration.Create(configuration, new Environment("Development"), Options()).Count);
    }

    [Fact]
    public void Knowledge_page_does_not_offer_search_when_disabled()
    {
        using var context = new BunitContext();
        var component = context.Render<KnowledgeExample>();
        Assert.Contains("This example is not enabled", component.Markup);
        Assert.Empty(component.FindAll("input"));
    }

    [Fact]
    public void Enabled_knowledge_page_offers_search_without_submitting_until_entered()
    {
        using var context = new BunitContext();
        var options = Options();
        context.Services.AddSingleton(new TodoDocumentDemoAccess(new Environment("Development"), options, true));
        context.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        context.Services.AddSingleton<IEgressGate>(new TestGate());
        context.Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(options));
        context.Services.AddScoped<TodoKnowledgeDemo>();
        var component = context.Render<KnowledgeExample>();
        Assert.Single(component.FindAll("input"));
        Assert.True(component.Find("button").HasAttribute("disabled"));
        component.Find("input").Input("Where is the source?");
        Assert.False(component.Find("button").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Knowledge_runner_rejects_unauthenticated_users_before_opening_workers()
    {
        var access = new TodoDocumentDemoAccess(new Environment("Development"), Options(), true);
        var demo = new TodoKnowledgeDemo(access, new ConfigurationBuilder().Build(), new TestGate());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => demo.SearchAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), "query", CancellationToken.None));
    }

    private sealed class TestGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class TestFirewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }

    private sealed class TestAuthentication(TodoGatewayOptions options) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", options.TenantId),
                new Claim(ClaimTypes.NameIdentifier, options.PrincipalId)], SameHostDevelopmentAuthenticationHandler.SchemeName))));
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "OpenConnect";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
