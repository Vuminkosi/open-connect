using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class TaskIdempotencyTests
{
    [Fact]
    public async Task Same_key_and_payload_returns_same_task()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var first = await PostTaskAsync(app, "same");
        var second = await PostTaskAsync(app, "same");

        Assert.Equal(StatusCodes.Status202Accepted, first.StatusCode);
        Assert.Equal(StatusCodes.Status202Accepted, second.StatusCode);
        Assert.Equal(first.Location, second.Location);
    }

    [Fact]
    public async Task Same_key_with_different_payload_returns_problem_details_conflict()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        await PostTaskAsync(app, "first");
        var conflict = await PostTaskAsync(app, "different");

        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("application/problem+json", conflict.ContentType);
        var body = Encoding.UTF8.GetString(conflict.Body);
        Assert.Contains("\"status\":409", body);
        Assert.Contains("idempotency-conflict", body);
    }

    [Fact]
    public async Task Tenant_is_taken_from_claim_not_request_body()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var response = await PostTaskAsync(app, "tenant-check", "00000000000000000000000000");

        Assert.Equal(StatusCodes.Status202Accepted, response.StatusCode);
        Assert.DoesNotContain("00000000000000000000000000", Encoding.UTF8.GetString(response.Body));
    }

    private static async Task<TestHost> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenConnectTaskExecution();
        builder.Services.AddSingleton<IHostTaskExecutionAuthorizer, TestAuthorizer>();
        builder.Services.AddSingleton<IHostTaskExecutor, TestExecutor>();
        var app = builder.Build();
        app.MapOpenConnect();
        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StartAsync(startupTimeout.Token);
            return new TestHost(app);
        }
        catch
        {
            await CleanupAfterStartFailureAsync(app);
            throw;
        }
    }

    private static async Task CleanupAfterStartFailureAsync(WebApplication app)
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(stopTimeout.Token); }
        catch (Exception) { /* Preserve the startup exception. */ }
        try { await app.DisposeAsync(); }
        catch (Exception) { /* Preserve the startup exception. */ }
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
        var result = await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCreateTaskAsync(context, new TaskRequest(clientTaskId), app.Services.GetRequiredService<InMemoryTaskStore>(), context.RequestAborted);
        await result.ExecuteAsync(context);
        return new Response(context.Response.StatusCode, context.Response.Headers.Location.ToString(), context.Response.ContentType, responseBody.ToArray());
    }

    private sealed record Response(int StatusCode, string Location, string? ContentType, byte[] Body);

    private sealed class TestHost(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public async ValueTask DisposeAsync()
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await App.StopAsync(stopTimeout.Token); }
            finally { await App.DisposeAsync(); }
        }
    }

    private sealed class TestAuthorizer : IHostTaskExecutionAuthorizer
    {
        public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestExecutor : IHostTaskExecutor
    {
        public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        }
    }
}
