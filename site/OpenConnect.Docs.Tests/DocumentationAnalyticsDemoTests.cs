using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Docs.Components.AnalyticsDemo;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Services.AnalyticsDemo;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationAnalyticsDemoTests
{
    [DocumentationAnalyticsLiveFact]
    public async Task AnonymousVisitorCanAnalyzeSyntheticDataThroughWorker()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:AnalyticsDemo:Enabled"] = "true",
            ["OpenConnect:AnalyticsDemo:Origin"] = Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_URL"),
            ["OpenConnect:AnalyticsDemo:AllowInsecureHttp"] = "true",
            ["OPENCONNECT_WORKER_TOKEN"] = Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_TOKEN"),
            ["OPENCONNECT_WORKER_SIGNING_KEY"] = Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_KEY")
        });
        builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
        builder.Services.AddScoped<DocumentationGatewayVisitorIdentity>();
        builder.Services.AddDocumentationAnalyticsDemo(builder.Configuration, builder.Environment);
        using var services = builder.Services.BuildServiceProvider();
        using var scope = services.CreateScope();
        using var result = await scope.ServiceProvider.GetRequiredService<IDocumentationAnalyticsDemo>().AnalyzeAsync(CancellationToken.None);
        Assert.Equal(6, result.RootElement.GetProperty("result").GetProperty("sample_size").GetInt32());
    }

    [Fact]
    public void DemoIsOptInAndRequiresDevelopmentVisitorIdentity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Services.AddDocumentationAnalyticsDemo(builder.Configuration, builder.Environment);
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IDocumentationAnalyticsDemo));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["OpenConnect:AnalyticsDemo:Enabled"] = "true" });
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationAnalyticsDemo(builder.Configuration, builder.Environment));
        builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
        builder.Environment.EnvironmentName = Environments.Production;
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationAnalyticsDemo(builder.Configuration, builder.Environment));
    }

    [Fact]
    public void PageReportsFailureWithoutBackendDetails()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IDocumentationAnalyticsDemo>(new FakeDemo(false));
        var component = context.Render<DocumentationAnalyticsExample>();
        component.Find("button").Click();
        component.WaitForAssertion(() => Assert.Contains("could not be completed", component.Markup));
        Assert.DoesNotContain("private backend detail", component.Markup);
    }

    [Fact]
    public void PageDisplaysValidatedResult()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IDocumentationAnalyticsDemo>(new FakeDemo(true));
        var component = context.Render<DocumentationAnalyticsExample>();
        component.Find("button").Click();
        component.WaitForAssertion(() => Assert.Contains("Analysis complete", component.Markup));
        Assert.Contains("Samples: 6", component.Markup);
        Assert.Contains("Anomalies found: 1", component.Markup);
    }

    private sealed class FakeDemo(bool succeeds) : IDocumentationAnalyticsDemo
    {
        public Task<JsonDocument> AnalyzeAsync(CancellationToken cancellationToken) => succeeds
            ? Task.FromResult(JsonDocument.Parse("""{"result":{"sample_size":6,"median":3.5,"anomalies":[{}],"algorithm_version":"robust-mad-v1"}}"""))
            : Task.FromException<JsonDocument>(new InvalidOperationException("private backend detail"));
    }
}

public sealed class DocumentationAnalyticsLiveFactAttribute : FactAttribute
{
    public DocumentationAnalyticsLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_URL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_TOKEN")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCONNECT_ANALYTICS_TEST_KEY")))
            Skip = "Set the disposable analytics worker endpoint and credentials to run the live test.";
    }
}
