using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Services.KnowledgeDemo;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationKnowledgeWireTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task VisitorIndexesOnceAndSearchesWithItsOwnAcl(bool cloud, bool wrongSource, bool retryStatus)
    {
        var key = Enumerable.Repeat((byte)0xab, 32).ToArray();
        var indexed = new Dictionary<Guid, (string Tenant, string Principal, string Document)>();
        var calls = new List<string>();
        var failStatusOnce = retryStatus;
        var fixture = WebApplication.CreateSlimBuilder();
        fixture.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = fixture.Build();
        async Task<IResult> Handle(HttpContext context)
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            var body = buffer.ToArray();
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var isCloud = context.Request.Path == "/cloud/v1/knowledge/embeddings";
            if (isCloud)
            {
                Assert.True(cloud);
                Assert.Equal("Bearer fixture-cloud-token", context.Request.Headers.Authorization.ToString());
                Assert.False(context.Request.Headers.ContainsKey("X-OpenConnect-Signature"));
                calls.Add("cloud.embed");
                return Results.Json(new
                {
                    schemaVersion = "openconnect.cloud.knowledge-embed.result/v1",
                    requestId = root.GetProperty("requestId").GetString(),
                    traceId = new string('a', 32),
                    result = Embedding()
                });
            }
            Assert.Equal("Bearer fixture-token", context.Request.Headers.Authorization.ToString());
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(key, body)).ToLowerInvariant(),
                context.Request.Headers["X-OpenConnect-Signature"].ToString());
            var kind = root.GetProperty("kind").GetString()!;
            var task = root.GetProperty("taskId").GetString();
            var tenant = root.GetProperty("tenantId").GetString()!;
            var payload = root.GetProperty("payload");
            calls.Add(kind);
            object result;
            switch (kind)
            {
                case "knowledge.index":
                    var job = payload.GetProperty("jobId").GetGuid();
                    var principal = payload.GetProperty("principals")[0].GetString()!;
                    var document = payload.GetProperty("documentId").GetString()!;
                    Assert.Equal("docs-" + principal, document);
                    Assert.Equal(0, payload.GetProperty("roles").GetArrayLength());
                    Assert.True(payload.GetProperty("documentExpiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
                    var created = indexed.TryAdd(job, (tenant, principal, document));
                    Assert.Equal((tenant, principal, document), indexed[job]);
                    result = new { job_id = job, created };
                    break;
                case "knowledge.index.status":
                    var stored = indexed[payload.GetProperty("jobId").GetGuid()];
                    Assert.Equal(stored.Tenant, tenant);
                    Assert.Equal(stored.Principal, payload.GetProperty("principalId").GetString());
                    Assert.Equal(stored.Document, payload.GetProperty("documentId").GetString());
                    if (failStatusOnce)
                    { failStatusOnce = false; return Results.StatusCode(503); }
                    result = new { state = "completed", attempts = 1, result_generation = 1, retry_at = (string?)null };
                    break;
                case "knowledge.embed":
                    Assert.False(cloud);
                    result = Embedding();
                    break;
                case "knowledge.search":
                    var searchPrincipal = payload.GetProperty("principalId").GetString();
                    var match = Assert.Single(indexed.Values, item => item.Tenant == tenant && item.Principal == searchPrincipal);
                    Assert.Equal(new string('c', 64), payload.GetProperty("hybrid").GetProperty("embeddingSpaceId").GetString());
                    result = new
                    {
                        model_id = "fixture",
                        fingerprint = new string('b', 64),
                        score_type = "raw_logit",
                        warnings = Array.Empty<string>(),
                        hits = new[] { new { rerank_score = 1.0, hit = new { document_id = wrongSource ? "other-document" : match.Document, source_revision = "v1", chunk_id = new string('b', 64),
                            page_id = "page", page_index = 0, start = 0, end = 4, text = "Mars", score = 0.5 } } }
                    };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected fixture operation.");
            }
            return Results.Json(new { schemaVersion = "openconnect.v1", taskId = task, kind, result });
        }
        app.MapPost("/internal/v1/execute", (Delegate)Handle);
        app.MapPost("/cloud/v1/knowledge/embeddings", (Delegate)Handle);
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var host = WebApplication.CreateBuilder();
            host.Environment.EnvironmentName = Environments.Development;
            host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenConnect:KnowledgeDemo:Enabled"] = "true",
                ["OpenConnect:KnowledgeDemo:IndexOrigin"] = address,
                ["OpenConnect:KnowledgeDemo:SearchOrigin"] = address,
                ["OpenConnect:KnowledgeDemo:EmbeddingOrigin"] = address,
                ["OpenConnect:KnowledgeDemo:AllowInsecureHttp"] = "true",
                ["OpenConnect:KnowledgeDemo:UseCloudEmbedding"] = cloud.ToString(),
                ["OpenConnect:KnowledgeDemo:CloudBearerToken"] = cloud ? "fixture-cloud-token" : null,
                ["OpenConnect:KnowledgeDemo:ObjectId"] = new string('a', 64),
                ["OpenConnect:KnowledgeDemo:ObjectSha256"] = new string('b', 64),
                ["OpenConnect:KnowledgeDemo:ObjectSize"] = "42",
                ["OpenConnect:KnowledgeDemo:ObjectVersion"] = "v1",
                ["OpenConnect:KnowledgeDemo:EmbeddingSpaceId"] = new string('c', 64),
                ["OPENCONNECT_WORKER_TOKEN"] = "fixture-token",
                ["OPENCONNECT_WORKER_SIGNING_KEY"] = Convert.ToHexString(key)
            });
            host.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
            host.Services.AddScoped<DocumentationGatewayVisitorIdentity>();
            host.Services.AddDocumentationKnowledgeDemo(host.Configuration, host.Environment);
            using var provider = host.Services.BuildServiceProvider();
            using var first = provider.CreateScope();
            using var second = provider.CreateScope();
            var firstDemo = first.ServiceProvider.GetRequiredService<IDocumentationKnowledgeDemo>();
            if (wrongSource)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => firstDemo.SearchAsync("planet", CancellationToken.None));
                Assert.Single(indexed);
                return;
            }
            if (retryStatus)
                await Assert.ThrowsAsync<HttpRequestException>(() => firstDemo.SearchAsync("planet", CancellationToken.None));
            using var firstResult = await firstDemo.SearchAsync("planet", CancellationToken.None);
            using var repeatResult = await firstDemo.SearchAsync("planet again", CancellationToken.None);
            using var secondResult = await second.ServiceProvider.GetRequiredService<IDocumentationKnowledgeDemo>().SearchAsync("planet", CancellationToken.None);
            Assert.Equal("Mars", firstResult.RootElement.GetProperty("result").GetProperty("hits")[0].GetProperty("hit").GetProperty("text").GetString());
            Assert.Equal(2, indexed.Count);
            Assert.Equal(2, indexed.Values.Select(item => item.Principal).Distinct().Count());
            Assert.Equal(retryStatus ? 3 : 2, calls.Count(kind => kind == "knowledge.index"));
            Assert.Equal(3, calls.Count(kind => kind == "knowledge.search"));
        }
        finally { await app.StopAsync(); CryptographicOperations.ZeroMemory(key); }
    }

    private static object Embedding() => new
    {
        model_id = "fixture",
        fingerprint = new string('b', 64),
        dimension = 2,
        space_id = new string('c', 64),
        items = new[] { new { vector = new[] { 0.6, 0.8 }, truncated = false, norm = 1 } }
    };
}
