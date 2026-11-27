using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Docs.Components.KnowledgeDemo;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Services.KnowledgeDemo;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationKnowledgeDemoTests
{
    [Fact]
    public void ExampleIsOptInAndRequiresDevelopmentVisitorIdentity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Services.AddDocumentationKnowledgeDemo(builder.Configuration, builder.Environment);
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IDocumentationKnowledgeDemo));
        Configure(builder);
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationKnowledgeDemo(builder.Configuration, builder.Environment));
        AddVisitor(builder);
        builder.Environment.EnvironmentName = Environments.Production;
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationKnowledgeDemo(builder.Configuration, builder.Environment));
    }

    [Fact]
    public async Task CapacityDenialOccursBeforeWorkerDispatch()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        Configure(builder);
        AddVisitor(builder);
        builder.Services.AddDocumentationKnowledgeDemo(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var capacity = provider.GetRequiredService<DocumentationKnowledgeCapacity>();
        await capacity.Slots.WaitAsync();
        await capacity.Slots.WaitAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IDocumentationKnowledgeDemo>()
                .SearchAsync("synthetic question", CancellationToken.None));
        }
        finally { capacity.Slots.Release(2); }
    }

    [Theory]
    [InlineData("http://worker.example", false)]
    [InlineData("https://user:password@worker.example", false)]
    public void InvalidWorkerTransportIsRejected(string origin, bool allowHttp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        Configure(builder);
        AddVisitor(builder);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:KnowledgeDemo:IndexOrigin"] = origin,
            ["OpenConnect:KnowledgeDemo:AllowInsecureHttp"] = allowHttp.ToString()
        });
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationKnowledgeDemo(builder.Configuration, builder.Environment));
    }

    [Fact]
    public void CloudEmbeddingRequiresExplicitCredential()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        Configure(builder);
        AddVisitor(builder);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:KnowledgeDemo:UseCloudEmbedding"] = "true" });
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationKnowledgeDemo(builder.Configuration, builder.Environment));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:KnowledgeDemo:CloudBearerToken"] = "fixture-cloud-token" });
        builder.Services.AddDocumentationKnowledgeDemo(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        Assert.DoesNotContain("fixture-cloud-token", provider.GetRequiredService<DocumentationKnowledgeConfiguration>().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PageRequiresAQueryAndDoesNotExposeWorkerErrors()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IDocumentationKnowledgeDemo>(new FailingDemo());
        var component = context.Render<DocumentationKnowledgeExample>();
        Assert.True(component.Find("button").HasAttribute("disabled"));
        component.Find("input").Input("synthetic question");
        Assert.False(component.Find("button").HasAttribute("disabled"));
        component.Find("button").Click();
        component.WaitForAssertion(() => Assert.Contains("could not be completed", component.Markup));
        Assert.DoesNotContain("private backend detail", component.Markup);
    }

    [Fact]
    public void ChangingQuestionClearsPriorPassagesAndEscapesTheirText()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IDocumentationKnowledgeDemo>(new SuccessfulDemo());
        var component = context.Render<DocumentationKnowledgeExample>();
        component.Find("input").Input("first question");
        component.Find("button").Click();
        component.WaitForAssertion(() => Assert.Contains("Search complete", component.Markup));
        Assert.Contains("&lt;script&gt;", component.Markup);
        Assert.DoesNotContain("<script>", component.Markup);
        Assert.Contains("Relevant passages", component.Markup);

        component.Find("input").Input("different question");
        Assert.DoesNotContain("Relevant passages", component.Markup);
        Assert.DoesNotContain("Search complete", component.Markup);
        Assert.DoesNotContain("&lt;script&gt;", component.Markup);
    }

    private sealed class SuccessfulDemo : IDocumentationKnowledgeDemo
    {
        public Task<JsonDocument> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(JsonDocument.Parse("""
                {"result":{"warnings":[],"hits":[{"hit":{"text":"<script>alert(1)</script>","document_id":"demo","source_revision":"v1","page_index":0,"start":0,"end":25}}]}}
                """));
    }

    private static void AddVisitor(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
        builder.Services.AddScoped<DocumentationGatewayVisitorIdentity>();
    }

    private static void Configure(WebApplicationBuilder builder) => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OpenConnect:KnowledgeDemo:Enabled"] = "true",
        ["OpenConnect:KnowledgeDemo:IndexOrigin"] = "https://worker.example",
        ["OpenConnect:KnowledgeDemo:SearchOrigin"] = "https://worker.example",
        ["OpenConnect:KnowledgeDemo:EmbeddingOrigin"] = "https://worker.example",
        ["OpenConnect:KnowledgeDemo:ObjectId"] = new string('a', 64),
        ["OpenConnect:KnowledgeDemo:ObjectSha256"] = new string('b', 64),
        ["OpenConnect:KnowledgeDemo:ObjectSize"] = "42",
        ["OpenConnect:KnowledgeDemo:ObjectVersion"] = "v1",
        ["OpenConnect:KnowledgeDemo:EmbeddingSpaceId"] = new string('c', 64),
        ["OPENCONNECT_WORKER_TOKEN"] = "fixture-token",
        ["OPENCONNECT_WORKER_SIGNING_KEY"] = new string('a', 64)
    });

    private sealed class FailingDemo : IDocumentationKnowledgeDemo
    {
        public Task<JsonDocument> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromException<JsonDocument>(new InvalidOperationException("private backend detail"));
    }
}
