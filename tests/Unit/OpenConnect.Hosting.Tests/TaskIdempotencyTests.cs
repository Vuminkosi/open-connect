using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class TaskIdempotencyTests
{
    [Fact]
    public async Task Same_key_and_payload_returns_same_task()
    {
        var app = CreateApp();
        var first = await PostTaskAsync(app, "same");
        var second = await PostTaskAsync(app, "same");

        Assert.Equal(StatusCodes.Status202Accepted, first.StatusCode);
        Assert.Equal(StatusCodes.Status202Accepted, second.StatusCode);
        Assert.Equal(first.Location, second.Location);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Same_key_with_different_payload_returns_problem_details_conflict()
    {
        var app = CreateApp();
        await PostTaskAsync(app, "first");
        var conflict = await PostTaskAsync(app, "different");

        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("application/problem+json", conflict.ContentType);
        var body = Encoding.UTF8.GetString(conflict.Body);
        Assert.Contains("\"status\":409", body);
        Assert.Contains("idempotency-conflict", body);
        await app.DisposeAsync();
    }

    [Fact]
    public async Task Tenant_is_taken_from_claim_not_request_body()
    {
        var app = CreateApp();
        var response = await PostTaskAsync(app, "tenant-check", "00000000000000000000000000");

        Assert.Equal(StatusCodes.Status202Accepted, response.StatusCode);
        Assert.DoesNotContain("00000000000000000000000000", Encoding.UTF8.GetString(response.Body));
        await app.DisposeAsync();
    }

    private static WebApplication CreateApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<InMemoryTaskStore>();
        var app = builder.Build();
        app.MapOpenConnect();
        return app;
    }

    private static async Task<Response> PostTaskAsync(WebApplication app, string clientTaskId, string? tenant = null)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = "POST";
        context.Request.Path = "/openconnect/v1/tasks";
        context.Request.Headers["Idempotency-Key"] = "test-key";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"clientTaskId\":\"{clientTaskId}\",\"tenantId\":\"{tenant ?? "attacker"}\"}}"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", "01ARZ3NDEKTSV4RRFFQ69G5FAV"),
            new Claim(ClaimTypes.NameIdentifier, "01ARZ3NDEKTSV4RRFFQ69G5FAW")
        ], "test"));
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var handler = typeof(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions).GetMethod("CreateTaskAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = await (Task<IResult>)handler.Invoke(null, [context, new TaskRequest(clientTaskId), app.Services.GetRequiredService<InMemoryTaskStore>(), context.RequestAborted])!;
        await result.ExecuteAsync(context);
        return new Response(context.Response.StatusCode, context.Response.Headers.Location.ToString(), context.Response.ContentType, responseBody.ToArray());
    }

    private sealed record Response(int StatusCode, string Location, string? ContentType, byte[] Body);
}
