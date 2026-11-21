using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Services.DocumentDemo;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationDocumentWireTests
{
    [Fact]
    public async Task VisitorDocumentWorkflowUsesSignedWorkerRequestsAndReturnsSourceCitation()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"oc-doc-{Guid.NewGuid():N}.sock");
        var key = Enumerable.Repeat((byte)0xab, 32).ToArray();
        var requests = new List<(string Kind, string Tenant)>();
        var fixture = WebApplication.CreateSlimBuilder();
        fixture.WebHost.ConfigureKestrel(options =>
        {
            options.ListenUnixSocket(socket);
            options.Listen(IPAddress.Loopback, 0);
        });
        await using var app = fixture.Build();
        app.MapPost("/internal/v1/{operation}", async (HttpContext context, string operation) =>
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes);
            var body = bytes.ToArray();
            Assert.Equal("Bearer fixture-token", context.Request.Headers.Authorization.ToString());
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(key, body)).ToLowerInvariant(),
                context.Request.Headers["X-OpenConnect-Signature"].ToString());
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var taskId = root.GetProperty("taskId").GetString();
            var tenant = root.GetProperty("tenantId").GetString()!;
            requests.Add((root.GetProperty("kind").GetString()!, tenant));
            if (operation == "execute")
            {
                const string text = "Reference: ABC";
                return Results.Json(new { schemaVersion = "openconnect.v1", taskId, kind = "documents.ocr", result = new
                {
                    pages = new[] { new { text, width = 10, height = 10, model_id = "fixture", model_fingerprint = new string('a', 64), language = "eng",
                        spans = new[] { new { text, start = 0, end = text.Length, box = new[] { 0.0, 0.0, 1.0, 1.0 }, confidence = .9 } } } }
                } });
            }
            Assert.Equal("extract", operation);
            Assert.Equal("Reference: ABC", root.GetProperty("payload").GetProperty("document").GetString());
            return Results.Json(new { schemaVersion = "openconnect.v1", taskId, kind = "documents.extract", result = new
            {
                fields = new { reference = new { value = "ABC", start = 11, end = 14 } },
                warnings = new[] { "source_support_not_semantic_validation" }, schema_valid = true, confidence_method = "not_estimated"
            } });
        });
        try
        {
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Single(value => value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            var host = WebApplication.CreateBuilder();
            host.Environment.EnvironmentName = Environments.Development;
            host.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenConnect:DocumentDemo:Enabled"] = "true",
                ["OpenConnect:DocumentDemo:DocumentSocket"] = socket,
                ["OpenConnect:DocumentDemo:ExtractionOrigin"] = address,
                ["OpenConnect:DocumentDemo:AllowInsecureHttp"] = "true",
                ["OPENCONNECT_WORKER_TOKEN"] = "fixture-token",
                ["OPENCONNECT_WORKER_SIGNING_KEY"] = Convert.ToHexString(key)
            });
            host.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
            host.Services.AddScoped<DocumentationGatewayVisitorIdentity>();
            host.Services.AddDocumentationDocumentDemo(host.Configuration, host.Environment);
            using var provider = host.Services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            using var input = new MemoryStream([1, 2, 3]);
            using var result = await scope.ServiceProvider.GetRequiredService<IDocumentationDocumentDemo>().ProcessAsync(input, CancellationToken.None);
            Assert.Equal("ABC", result.Extraction.RootElement.GetProperty("result").GetProperty("fields").GetProperty("reference").GetProperty("value").GetString());
            Assert.Equal(0, result.Citations["reference"]!.PageIndex);
            Assert.Equal(11, result.Citations["reference"]!.Start);
            Assert.Equal(new[] { "documents.ocr", "documents.extract" }, requests.Select(request => request.Kind));
            Assert.All(requests, request => Assert.Equal("01ARZ3NDEKTSV4RRFFQ69G5FAV", request.Tenant));
        }
        finally
        {
            await app.StopAsync();
            if (File.Exists(socket)) File.Delete(socket);
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
