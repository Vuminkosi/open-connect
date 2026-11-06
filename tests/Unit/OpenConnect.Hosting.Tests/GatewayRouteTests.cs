using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Routing;
using OpenConnect.Abstractions;
using OpenConnect.Core.Capabilities;
using OpenConnect.Core;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class GatewayRouteTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private static readonly Type Routes = typeof(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions);

    [Fact]
    public async Task Create_session_and_scoped_read_succeed()
    {
        await using var app = CreateApp();
        var created = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCreateSession(Context(app), new SessionRequest(), app.Services.GetRequiredService<InMemorySessionStore>()), app.Services);
        AssertNo501(created);
        Assert.Equal(201, created.StatusCode);
        var id = created.Location.Split('/').Last();
        var read = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleGetSession(id, Context(app), app.Services.GetRequiredService<InMemorySessionStore>()), app.Services);
        AssertNo501(read);
        Assert.Equal(200, read.StatusCode);
    }

    [Fact]
    public async Task Session_events_after_position_returns_empty_success()
    {
        await using var app = CreateApp();
        var session = app.Services.GetRequiredService<InMemorySessionStore>().Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleGetSessionEvents(session.Id.Value.ToString(), 0, Context(app), app.Services.GetRequiredService<InMemorySessionStore>()), app.Services);
        AssertNo501(result);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("[]", Encoding.UTF8.GetString(result.Body));
    }

    [Fact]
    public async Task Session_events_replay_in_sequence_after_requested_position()
    {
        var journal = new InMemoryEventJournal();
        await using var app = CreateApp(services => services.AddSingleton(journal));
        var store = app.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        await store.AppendEventAsync(session.Id.Value, "started", "one");
        await store.AppendEventAsync(session.Id.Value, "updated", "two");

        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleGetSessionEvents(session.Id.Value.ToString(), 1, Context(app), store), app.Services);
        Assert.Equal(200, result.StatusCode);
        var json = System.Text.Json.JsonDocument.Parse(result.Body);
        Assert.Equal(1, json.RootElement.GetArrayLength());
        Assert.Equal(2, json.RootElement[0].GetProperty("sequence").GetInt64());
        Assert.Equal("updated", json.RootElement[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Components_returns_registered_agent_descriptors()
    {
        var registry = new AgentDefinitionRegistry();
        registry.Register(new AgentDefinition(new AgentId("lead"), "Lead"));
        await using var app = CreateApp(services => services.AddSingleton<IAgentDefinitionRegistry>(registry));
        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleComponents(app.Services), app.Services);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("Lead", Encoding.UTF8.GetString(result.Body));
    }

    [Fact]
    public async Task Journal_replay_returns_events_after_sequence_in_order()
    {
        var journal = new InMemoryEventJournal();
        var sessionId = new SessionId(Ulid.NewUlid());
        Assert.Equal(1, await journal.AppendAsync(new EventRecord(sessionId, 0, "first", "1")));
        Assert.Equal(2, await journal.AppendAsync(new EventRecord(sessionId, 0, "second", "2")));
        var replayed = new List<EventRecord>();
        await foreach (var item in journal.ReplayAsync(sessionId, 0)) replayed.Add(item);
        Assert.Equal(new long[] { 1, 2 }, replayed.Select(item => item.Sequence));
    }

    [Fact]
    public async Task Cancel_owned_task_changes_state_to_cancelled()
    {
        await using var app = CreateApp();
        var store = app.Services.GetRequiredService<InMemoryTaskStore>();
        var taskId = await CreateTaskIdAsync(app);
        var result = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCancelTaskAsync(taskId, Context(app), store, CancellationToken.None), app.Services);
        AssertNo501(result);
        Assert.Equal(200, result.StatusCode);
        using var body = System.Text.Json.JsonDocument.Parse(result.Body);
        Assert.Equal("cancelled", body.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Cancel_task_owned_by_another_principal_returns_not_found()
    {
        await using var app = CreateApp();
        var store = app.Services.GetRequiredService<InMemoryTaskStore>();
        var taskId = await CreateTaskIdAsync(app);
        var context = Context(app, "01ARZ3NDEKTSV4RRFFQ69G5FAX");
        var result = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCancelTaskAsync(taskId, context, store, CancellationToken.None), app.Services);
        AssertNo501(result);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task Capabilities_returns_registered_ready_models()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new CapabilityDescriptor(new CapabilityId("echo"), CapabilityKind.Model, DetectorState.Ready));
        registry.Register(new CapabilityDescriptor(new CapabilityId("streaming-test"), CapabilityKind.Model, DetectorState.Ready));
        await using var app = CreateApp(services => services.AddSingleton<ICapabilityRegistry>(registry));
        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCapabilities(app.Services), app.Services);
        AssertNo501(result);
        Assert.Equal(200, result.StatusCode);
        var body = Encoding.UTF8.GetString(result.Body);
        Assert.Contains("echo", body);
        Assert.Contains("streaming-test", body);
    }

    [Fact]
    public async Task Readiness_is_successful_when_both_stores_are_registered()
    {
        await using var app = CreateApp();
        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleReady(app.Services), app.Services);
        AssertNo501(result);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_unavailable_when_stores_are_missing()
    {
        await using var app = CreateApp(registerStores: false);
        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleReady(app.Services), app.Services);
        AssertNo501(result);
        Assert.Equal(503, result.StatusCode);
    }

    private static WebApplication CreateApp(Action<IServiceCollection>? configure = null, bool registerStores = true)
    {
        var builder = WebApplication.CreateBuilder();
        if (registerStores)
        {
            builder.Services.AddSingleton<InMemoryTaskStore>();
            builder.Services.AddSingleton<InMemorySessionStore>();
        }
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.MapOpenConnect();
        return app;
    }

    private static DefaultHttpContext Context(WebApplication app, string principal = Principal) => new()
    {
        RequestServices = app.Services,
        User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, principal)
        ], "test"))
    };

    private static async Task<string> CreateTaskIdAsync(WebApplication app)
    {
        var context = Context(app);
        context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("N");
        var result = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCreateTaskAsync(context, new TaskRequest(Guid.NewGuid().ToString("N")), app.Services.GetRequiredService<InMemoryTaskStore>(), CancellationToken.None), app.Services);
        AssertNo501(result);
        return result.Location.Split('/').Last();
    }

    private static async Task<Response> ExecuteAsync(IResult result, IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext { RequestServices = services ?? new ServiceCollection().BuildServiceProvider(), Response = { Body = new MemoryStream() } };
        await result.ExecuteAsync(context);
        return new Response(context.Response.StatusCode, ((MemoryStream)context.Response.Body).ToArray(), context.Response.Headers.Location.ToString());
    }

    private static void AssertNo501(Response response) => Assert.NotEqual(501, response.StatusCode);
    private sealed record Response(int StatusCode, byte[] Body, string Location = "");
}
