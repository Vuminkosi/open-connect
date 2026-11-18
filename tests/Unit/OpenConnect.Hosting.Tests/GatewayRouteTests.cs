using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Core;
using OpenConnect.Core.Capabilities;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.Privacy;
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
        await using var host = await CreateAppAsync();
        var app = host.App;
        var created = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCreateSession(Context(app), new SessionRequest(), app.Services.GetRequiredService<InMemorySessionStore>()), app.Services);
        AssertNo501(created);
        Assert.Equal(201, created.StatusCode);
        var id = created.Location.Split('/').Last();
        var read = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleGetSession(id, Context(app), app.Services.GetRequiredService<InMemorySessionStore>()), app.Services);
        AssertNo501(read);
        Assert.Equal(200, read.StatusCode);
    }

    [Fact]
    public async Task MiniComposer_admission_binds_execution_to_its_registered_connection()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var sessions = app.Services.GetRequiredService<InMemorySessionStore>();
        var session = sessions.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = sessions.RegisterBoundConnection(session)!;
        var context = Context(app);
        context.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("N");
        var request = new TaskRequest(Guid.NewGuid().ToString("N"), "Show the tasks", session.Id.Value.ToString())
        {
            ClientConnectionId = "another-connection"
        };
        var rejected = await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCreateTaskAsync(
            context, request, app.Services.GetRequiredService<InMemoryTaskStore>(), CancellationToken.None, HostTaskOrigin.MiniComposer);
        Assert.Equal(409, (await ExecuteAsync(rejected, app.Services)).StatusCode);
        var result = await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCreateTaskAsync(
            context, request with { ClientConnectionId = connection.ConnectionId },
            app.Services.GetRequiredService<InMemoryTaskStore>(), CancellationToken.None, HostTaskOrigin.MiniComposer);
        Assert.Equal(202, (await ExecuteAsync(result, app.Services)).StatusCode);
        var executor = app.Services.GetRequiredService<TestExecutor>();
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HostTaskOrigin.MiniComposer, executor.Input!.Origin);
        Assert.Equal(connection.ConnectionId, executor.Input.ClientConnectionId);
    }

    [Fact]
    public async Task Session_events_after_position_returns_empty_success()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
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
        await using var host = await CreateAppAsync(services => services.AddSingleton(journal));
        var app = host.App;
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
    public async Task Client_interaction_response_is_tenant_scoped_and_rejects_replay()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var store = app.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var response = new ClientInteractionResponse(Guid.NewGuid().ToString("D"), "action",
            "{\"outcome\":\"Completed\",\"postconditionVerified\":true}")
        { ConnectionId = connection.ConnectionId };
        var pending = store.RequestAsync(session.Id, new ClientInteractionRequest(response.CorrelationId, "execute", "todo-list", "Highlight"), "action", TimeSpan.FromSeconds(2));

        var first = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, Context(app), store), app.Services);
        var duplicate = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, Context(app), store), app.Services);

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(409, duplicate.StatusCode);
        Assert.NotNull(await pending);
        var stored = Assert.Single(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Response);
        Assert.Equal(ClientInteractionEventTypes.Response, stored.Type);
        Assert.Contains(response.CorrelationId, stored.Data);

        var foreign = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response with { CorrelationId = Guid.NewGuid().ToString("D") },
            Context(app, "01ARZ3NDEKTSV4RRFFQ69G5FAX"), store), app.Services);
        Assert.Equal(404, foreign.StatusCode);
        Assert.Single(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Fact]
    public async Task Client_interaction_response_rejects_malformed_payload()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var store = app.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));

        var result = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), new ClientInteractionResponse(Guid.NewGuid().ToString("D"), "page", "[]"),
            Context(app), store), app.Services);

        Assert.Equal(400, result.StatusCode);
        Assert.Empty(store.GetEvents(session.Id.Value, 0));
    }

    [Fact]
    public async Task Client_interaction_response_requires_a_matching_pending_request()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var store = app.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;

        var result = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), new ClientInteractionResponse(Guid.NewGuid().ToString("D"), "page", "{}") { ConnectionId = connection.ConnectionId },
            Context(app), store), app.Services);

        Assert.Equal(409, result.StatusCode);
        Assert.Empty(store.GetEvents(session.Id.Value, 0));
    }

    [Fact]
    public async Task Client_page_content_is_checked_before_it_is_journaled()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var store = app.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var correlationId = Guid.NewGuid().ToString("D");
        var pending = store.RequestAsync(session.Id, new ClientInteractionRequest(correlationId, "get_current_page"),
            "page", TimeSpan.FromSeconds(5));
        await WaitForRequestAsync(store, session.Id.Value);
        var response = new ClientInteractionResponse(correlationId, "page", "{\"label\":\"person\\u0040example.org\"}")
        { ConnectionId = connection.ConnectionId };

        var blocked = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, Context(app), store), app.Services);
        Assert.Equal(403, blocked.StatusCode);
        Assert.DoesNotContain(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Response);

        var safe = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response with { PayloadJson = "{\"label\":\"Tasks\"}" }, Context(app), store), app.Services);
        Assert.Equal(200, safe.StatusCode);
        Assert.NotNull(await pending);
    }

    [Fact]
    public async Task Client_page_content_is_not_journaled_when_privacy_gate_is_missing()
    {
        await using var host = await CreateAppAsync(services => services.RemoveAll<IEgressGate>());
        var app = host.App;
        var store = app.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var response = new ClientInteractionResponse(Guid.NewGuid().ToString("D"), "page", "{\"label\":\"Tasks\"}")
        { ConnectionId = connection.ConnectionId };

        var result = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, Context(app), store), app.Services);

        Assert.Equal(503, result.StatusCode);
        Assert.DoesNotContain(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task Client_page_screen_rejects_invalid_or_non_object_snapshots(string payload)
    {
        await using var host = await CreateAppAsync();
        var store = host.App.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var gate = host.App.Services.GetRequiredService<IEgressGate>();

        var result = await ClientPageEgressScreen.EvaluateAsync(payload, session, gate, CancellationToken.None);

        Assert.Equal(ClientPageScreenResult.Blocked, result);
    }

    [Theory]
    [InlineData("{\"label\":\"Tasks\"}", true)]
    [InlineData("{\"label\":\"person\\u0040example.org\"}", false)]
    [InlineData("[]", false)]
    public async Task Screened_page_broker_checks_even_directly_journaled_responses(string payload, bool allowed)
    {
        await using var host = await CreateAppAsync();
        var store = host.App.Services.GetRequiredService<InMemorySessionStore>();
        var broker = Assert.IsAssignableFrom<IScreenedClientPageBroker>(host.App.Services.GetRequiredService<IClientInteractionBroker>());
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "get_current_page");
        var pending = broker.RequestScreenedPageAsync(session.Id, request, TimeSpan.FromSeconds(2));
        await WaitForRequestAsync(store, session.Id.Value);
        await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Response,
            System.Text.Json.JsonSerializer.Serialize(new ClientInteractionResponse(request.CorrelationId, "page", payload)));

        var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        if (allowed)
            Assert.Equal(payload, response!.PayloadJson);
        else
            Assert.Null(response);
    }

    [Fact]
    public async Task Screened_page_broker_fails_closed_without_a_gate()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var broker = new ScreenedClientInteractionBroker(store, null);
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "get_current_page");

        var response = await broker.RequestScreenedPageAsync(session.Id, request, TimeSpan.FromSeconds(2));

        Assert.Null(response);
        Assert.Empty(store.GetEvents(session.Id.Value, 0));
    }

    [Theory]
    [InlineData("navigate")]
    [InlineData("inspect")]
    [InlineData("await_state")]
    [InlineData("highlight")]
    [InlineData("clear_highlight")]
    public async Task Client_interaction_response_accepts_correlated_liveui_operations(string operation)
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var store = app.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var correlation = Guid.NewGuid().ToString("D");
        var pending = store.RequestAsync(session.Id, new ClientInteractionRequest(correlation, operation), "action", TimeSpan.FromSeconds(2));

        var result = await ExecuteAsync(
            await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
                session.Id.Value.ToString(), new ClientInteractionResponse(correlation, "action", "{}") { ConnectionId = connection.ConnectionId }, Context(app), store),
            app.Services);

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(await pending);
        Assert.Single(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Fact]
    public async Task Client_interaction_broker_waits_for_matching_correlation_and_result_kind()
    {
        await using var host = await CreateAppAsync();
        var store = host.App.Services.GetRequiredService<InMemorySessionStore>();
        var broker = host.App.Services.GetRequiredService<IClientInteractionBroker>();
        Assert.IsAssignableFrom<IScreenedClientPageBroker>(broker);
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "execute", "todo-list", "Highlight");
        var pending = broker.RequestAsync(session.Id, request, "action", TimeSpan.FromSeconds(2));

        await WaitForRequestAsync(store, session.Id.Value);
        await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Response,
            System.Text.Json.JsonSerializer.Serialize(new ClientInteractionResponse(Guid.NewGuid().ToString("D"), "action", "{}")));
        await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Response,
            System.Text.Json.JsonSerializer.Serialize(new ClientInteractionResponse(request.CorrelationId, "page", "{}")));
        await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Response,
            System.Text.Json.JsonSerializer.Serialize(new ClientInteractionResponse(request.CorrelationId, "action", "{\"outcome\":\"Completed\"}")));

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(result);
        Assert.Equal(request.CorrelationId, result.CorrelationId);
        Assert.Equal("action", result.ResultKind);
        Assert.Contains("Completed", result.PayloadJson);
    }

    [Theory]
    [InlineData("get_current_page", "page")]
    [InlineData("infer_plan", "plan")]
    [InlineData("execute", "action")]
    [InlineData("navigate", "action")]
    [InlineData("inspect", "action")]
    [InlineData("await_state", "action")]
    [InlineData("highlight", "action")]
    [InlineData("clear_highlight", "action")]
    public async Task Client_interaction_broker_accepts_each_connected_liveui_operation(
        string operation, string resultKind)
    {
        await using var host = await CreateAppAsync();
        var store = host.App.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), operation);
        var pending = store.RequestAsync(session.Id, request, resultKind, TimeSpan.FromSeconds(2));

        await WaitForRequestAsync(store, session.Id.Value);
        await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Response,
            System.Text.Json.JsonSerializer.Serialize(new ClientInteractionResponse(request.CorrelationId, resultKind, "{}")));

        var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(response);
        Assert.Equal(request.CorrelationId, response.CorrelationId);
        Assert.Equal(resultKind, response.ResultKind);
    }

    [Fact]
    public async Task Client_interaction_broker_publishes_correlation_when_caller_cancels()
    {
        await using var host = await CreateAppAsync();
        var store = host.App.Services.GetRequiredService<InMemorySessionStore>();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var request = new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "highlight", "todo.list");
        using var cancellation = new CancellationTokenSource();
        var pending = store.RequestAsync(session.Id, request, "action", TimeSpan.FromSeconds(10), cancellation.Token);
        await WaitForRequestAsync(store, session.Id.Value);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var canceled = Assert.Single(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Cancellation);
        var eventPayload = System.Text.Json.JsonSerializer.Deserialize<ClientInteractionCancellation>(
            canceled.Data, OpenConnectHttpJson.CreateOptions());
        Assert.Equal(request.CorrelationId, eventPayload!.CorrelationId);
    }

    [Fact]
    public async Task Components_returns_registered_agent_descriptors()
    {
        var registry = new AgentDefinitionRegistry();
        registry.Register(new AgentDefinition(new AgentId("lead"), "Lead"));
        await using var host = await CreateAppAsync(services => services.AddSingleton<IAgentDefinitionRegistry>(registry));
        var app = host.App;
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
        await foreach (var item in journal.ReplayAsync(sessionId, 0))
            replayed.Add(item);
        Assert.Equal(new long[] { 1, 2 }, replayed.Select(item => item.Sequence));
    }

    [Fact]
    public async Task Cancel_owned_task_changes_state_to_cancelled()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
        var store = app.Services.GetRequiredService<InMemoryTaskStore>();
        var taskId = await CreateTaskIdAsync(app);
        var executor = app.Services.GetRequiredService<TestExecutor>();
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Response result;
        try
        {
            result = await ExecuteAsync(await OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleCancelTaskAsync(taskId, Context(app), store, CancellationToken.None), app.Services);
            AssertNo501(result);
            Assert.Equal(200, result.StatusCode);
            using var body = System.Text.Json.JsonDocument.Parse(result.Body);
            Assert.Equal("running", body.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            executor.ReleaseAfterCancellation.TrySetResult();
        }
        await executor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(store, taskId, AgentRunState.Cancelled);
    }

    [Fact]
    public async Task Cancel_task_owned_by_another_principal_returns_not_found()
    {
        await using var host = await CreateAppAsync();
        var app = host.App;
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
        await using var host = await CreateAppAsync(services => services.AddSingleton<ICapabilityRegistry>(registry));
        var app = host.App;
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
        await using var host = await CreateAppAsync();
        var app = host.App;
        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleReady(app.Services), app.Services);
        AssertNo501(result);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_unavailable_when_stores_are_missing()
    {
        await using var host = await CreateAppAsync(registerStores: false);
        var app = host.App;
        var result = await ExecuteAsync(OpenConnect.Hosting.AspNetCore.EndpointRouteBuilderExtensions.HandleReady(app.Services), app.Services);
        AssertNo501(result);
        Assert.Equal(503, result.StatusCode);
    }

    private static async Task<TestHost> CreateAppAsync(Action<IServiceCollection>? configure = null, bool registerStores = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (registerStores)
        {
            builder.Services.AddOpenConnectTaskExecution();
            builder.Services.AddSingleton<IHostTaskExecutionAuthorizer, TestAuthorizer>();
            builder.Services.AddSingleton<TestExecutor>();
            builder.Services.AddSingleton<IHostTaskExecutor>(services => services.GetRequiredService<TestExecutor>());
        }
        configure?.Invoke(builder.Services);
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
        try
        { await app.StopAsync(stopTimeout.Token); }
        catch (Exception) { /* Preserve the startup exception. */ }
        try
        { await app.DisposeAsync(); }
        catch (Exception) { /* Preserve the startup exception. */ }
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

    private static async Task WaitForRequestAsync(InMemorySessionStore store, Ulid sessionId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (store.GetEvents(sessionId, 0).Any(item => item.Type == ClientInteractionEventTypes.Request))
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException("The broker did not append its client interaction request.");
    }

    private static async Task<Response> ExecuteAsync(IResult result, IServiceProvider? services = null)
    {
        var context = new DefaultHttpContext { RequestServices = services ?? new ServiceCollection().BuildServiceProvider(), Response = { Body = new MemoryStream() } };
        await result.ExecuteAsync(context);
        return new Response(context.Response.StatusCode, ((MemoryStream)context.Response.Body).ToArray(), context.Response.Headers.Location.ToString());
    }

    private static void AssertNo501(Response response) => Assert.NotEqual(501, response.StatusCode);
    private sealed record Response(int StatusCode, byte[] Body, string Location = "");

    private sealed class TestAuthorizer : IHostTaskExecutionAuthorizer
    {
        public Task AuthorizeAsync(HostTaskExecution task, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static async Task WaitForStateAsync(InMemoryTaskStore store, string taskId, AgentRunState expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (store.Find(Ulid.Parse(taskId))?.State != expected && DateTime.UtcNow < deadline)
            await Task.Yield();
        Assert.Equal(expected, store.Find(Ulid.Parse(taskId))?.State);
    }

    private sealed class TestExecutor : IHostTaskExecutor
    {
        public HostTaskExecution? Input { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAfterCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(HostTaskExecution task, CancellationToken cancellationToken)
        {
            Input = task;
            Started.TrySetResult();
            try
            { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await ReleaseAfterCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
                throw;
            }
        }
    }

    private sealed class TestHost(WebApplication app) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public async ValueTask DisposeAsync()
        {
            App.Services.GetService<TestExecutor>()?.ReleaseAfterCancellation.TrySetResult();
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            { await App.StopAsync(stopTimeout.Token); }
            finally { await App.DisposeAsync(); }
        }
    }
}
