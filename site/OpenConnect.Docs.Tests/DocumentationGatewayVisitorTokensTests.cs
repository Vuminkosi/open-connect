using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Client;
using OpenConnect.Docs.Services;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationGatewayVisitorTokensTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    [Fact]
    public void VisitorsHaveDistinctAuthenticatedPrincipalsWithinTheConfiguredTenant()
    {
        var tokens = new DocumentationGatewayVisitorTokens(Tenant, Key);
        var first = tokens.Issue();
        var second = tokens.Issue();
        Assert.True(tokens.TryValidate(first, out var firstTenant, out var firstPrincipal));
        Assert.True(tokens.TryValidate(second, out var secondTenant, out var secondPrincipal));
        Assert.Equal(Tenant, firstTenant);
        Assert.Equal(firstTenant, secondTenant);
        Assert.NotEqual(firstPrincipal, secondPrincipal);
    }

    [Fact]
    public void ExpiredTamperedAndOtherTenantTokensAreRejected()
    {
        var clock = new TestClock();
        var tokens = new DocumentationGatewayVisitorTokens(Tenant, Key, clock);
        var token = tokens.Issue();
        Assert.False(tokens.TryValidate(token[..^1] + (token[^1] == '0' ? '1' : '0'), out _, out _));
        var pieces = token.Split('.');
        pieces[2] = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
        Assert.False(tokens.TryValidate(string.Join('.', pieces), out _, out _));
        Assert.False(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAW", Key, clock).TryValidate(token, out _, out _));
        Assert.False(new DocumentationGatewayVisitorTokens(Tenant, Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), clock).TryValidate(token, out _, out _));
        clock.Now = clock.Now.AddHours(12);
        Assert.False(tokens.TryValidate(token, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AQ==")]
    public void InvalidSigningKeysFailClosed(string key) =>
        Assert.Throws<ArgumentException>(() => new DocumentationGatewayVisitorTokens(Tenant, key));

    [Theory]
    [InlineData("v1.01ARZ3NDEKTSV4RRFFQ69G5FAV.!!!!!!!!!!!!!!!!!!!!!!!!!!.1.0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("v1.01ARZ3NDEKTSV4RRFFQ69G5FAV.01ARZ3NDEKTSV4RRFFQ69G5FAV.999999999999999999999999.0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("v1.01ARZ3NDEKTSV4RRFFQ69G5FAV.01ARZ3NDEKTSV4RRFFQ69G5FAV.1.zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void MalformedTokensAreRejectedWithoutThrowing(string token) =>
        Assert.False(new DocumentationGatewayVisitorTokens(Tenant, Key).TryValidate(token, out _, out _));

    [Fact]
    public void AssistantCreatesOneVisitorIdentityPerScope()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Assistant:Enabled"] = "true",
            ["OpenConnect:Assistant:GatewayUrl"] = "https://gateway.example/",
            ["OpenConnect:Assistant:VisitorTenant"] = Tenant,
            ["OpenConnect:Assistant:VisitorSigningKey"] = Key
        });
        builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var firstToken = first.ServiceProvider.GetRequiredService<DocumentationGatewayVisitorIdentity>().Token;
        Assert.Equal(firstToken, first.ServiceProvider.GetRequiredService<DocumentationGatewayVisitorIdentity>().Token);
        Assert.NotEqual(firstToken, second.ServiceProvider.GetRequiredService<DocumentationGatewayVisitorIdentity>().Token);
        Assert.True(provider.GetRequiredService<DocumentationGatewayVisitorTokens>().TryValidate(firstToken, out _, out _));
    }

    [Fact]
    public void VisitorIdentityCannotBeCombinedWithSharedBearer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Assistant:Enabled"] = "true",
            ["OpenConnect:Assistant:GatewayUrl"] = "https://gateway.example/",
            ["OpenConnect:Assistant:GatewayBearer"] = "shared",
            ["OpenConnect:Assistant:VisitorTenant"] = Tenant,
            ["OpenConnect:Assistant:VisitorSigningKey"] = Key
        });
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment));
    }

    [Fact]
    public async Task GatewayHttpRequestsCarryTheOwningVisitorIdentity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Assistant:Enabled"] = "true",
            ["OpenConnect:Assistant:GatewayUrl"] = "https://gateway.example/",
            ["OpenConnect:Assistant:VisitorTenant"] = Tenant,
            ["OpenConnect:Assistant:VisitorSigningKey"] = Key
        });
        var handler = new RecordingHandler();
        builder.Services.AddDocumentationAssistant(builder.Configuration, builder.Environment);
        builder.Services.AddHttpClient(nameof(IOpenConnectClient)).ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = builder.Services.BuildServiceProvider();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        await first.ServiceProvider.GetRequiredService<IOpenConnectClient>().GetCapabilitiesAsync();
        await second.ServiceProvider.GetRequiredService<IOpenConnectClient>().GetCapabilitiesAsync();
        Assert.Equal(new[] { first.ServiceProvider.GetRequiredService<DocumentationGatewayVisitorIdentity>().Token,
            second.ServiceProvider.GetRequiredService<DocumentationGatewayVisitorIdentity>().Token }, handler.Tokens);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string?> Tokens { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.Authorization?.Parameter);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("[]") });
        }
    }
}
