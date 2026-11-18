using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Hosting.AspNetCore;
using OpenConnect.Hosting.AspNetCore.Execution;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class LiveUIClientInteractionRoundTripTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string OtherTenant = "01ARZ3NDEKTSV4RRFFQ69G5FAX";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string OtherPrincipal = "01ARZ3NDEKTSV4RRFFQ69G5FAY";

    [Theory]
    [InlineData(false, 1_500)]
    [InlineData(true, 0)]
    public async Task Maf_tool_waits_for_host_authorized_browser_action_and_verified_receipt(
        bool synchronizeWithSpeech, int expectedHighlightDuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenConnectTaskExecution();
        using var serviceProvider = services.BuildServiceProvider();
        var sessions = serviceProvider.GetRequiredService<InMemorySessionStore>();
        var session = sessions.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = sessions.RegisterBoundConnection(session)!;

        var manifest = CreateManifest();
        var manifestProvider = new TestManifestProvider(manifest);
        var authorizer = new AllowAuthorizer();
        var runtime = new RecordingRuntime();
        var options = Options.Create(new LiveUIOptions
        {
            Narration = new LiveUINarrationOptions
            {
                Enabled = synchronizeWithSpeech,
                SynchronizeWithSpeech = synchronizeWithSpeech
            }
        });
        var validator = new LiveUIManifestValidator();
        var orchestrator = new LiveUIOrchestrator(manifestProvider, authorizer, runtime, options, validator);
        var hostTools = new LiveUIAgentTools(new TestContextAccessor(new LiveUIAgentContext("blazor-circuit", "todo.home")),
            manifestProvider, authorizer, validator, orchestrator, options);
        var clientTools = LiveUIClientAgentToolFactory.CreateTools(
            serviceProvider.GetRequiredService<IClientInteractionBroker>(), session.Id, TimeSpan.FromSeconds(2));
        var execute = Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(
            clientTools.Single(tool => tool.Name == "liveui_execute"));

        var toolCall = execute.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
        {
            ["targetId"] = "todo.completed",
            ["action"] = "SelectTab"
        })).AsTask();
        var request = await WaitForRequestAsync(sessions, session.Id.Value, "execute");
        Assert.Equal("execute", request.Operation);
        Assert.Equal("todo.completed", request.TargetId);
        Assert.Equal("SelectTab", request.Action);

        var browserReceipt = await hostTools.ExecuteAsync(request.TargetId!, request.Action!, request.Value);
        Assert.Equal(LiveUIActionOutcome.Completed, browserReceipt.Outcome);
        Assert.True(browserReceipt.PostconditionVerified);
        Assert.Equal(LiveUIActionType.SelectTab, runtime.LastContext!.Action.Type);

        var response = new ClientInteractionResponse(request.CorrelationId, "action",
            JsonSerializer.Serialize(browserReceipt, OpenConnectHttpJson.CreateOptions())) { ConnectionId = connection.ConnectionId };
        var httpContext = AuthenticatedContext(serviceProvider);
        var endpointResult = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, httpContext, sessions);
        var httpResponse = await ExecuteAsync(endpointResult, serviceProvider);
        Assert.Equal(StatusCodes.Status200OK, httpResponse.StatusCode);

        var modelResult = Assert.IsType<JsonElement>(await toolCall.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Completed", modelResult.GetProperty("outcome").GetString());
        Assert.True(modelResult.GetProperty("postconditionVerified").GetBoolean());

        var highlight = Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(
            clientTools.Single(tool => tool.Name == "liveui_highlight"));
        var highlightToolCall = highlight.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
        {
            ["targetId"] = "todo.completed",
            ["durationMilliseconds"] = 1_500,
            ["speechStartCharacter"] = 24,
            ["speechEndCharacter"] = 40
        })).AsTask();
        var highlightRequest = await WaitForRequestAsync(sessions, session.Id.Value, "highlight");
        Assert.Equal(1_500, highlightRequest.HighlightDurationMilliseconds);
        Assert.Equal(24, highlightRequest.SpeechStartCharacter);
        Assert.Equal(40, highlightRequest.SpeechEndCharacter);

        var highlightReceipt = await hostTools.HighlightAsync(
            highlightRequest.TargetId!, durationMilliseconds: highlightRequest.HighlightDurationMilliseconds);
        Assert.Equal(LiveUIActionOutcome.Completed, highlightReceipt.Outcome);
        Assert.Equal(LiveUIActionType.Highlight, runtime.LastContext!.Action.Type);
        Assert.Equal(expectedHighlightDuration, runtime.LastContext.Action.Parameters.HighlightDurationMilliseconds);

        var highlightResponse = new ClientInteractionResponse(highlightRequest.CorrelationId, "action",
            JsonSerializer.Serialize(highlightReceipt, OpenConnectHttpJson.CreateOptions())) { ConnectionId = connection.ConnectionId };
        var highlightEndpointResult = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), highlightResponse, AuthenticatedContext(serviceProvider), sessions);
        var highlightHttpResponse = await ExecuteAsync(highlightEndpointResult, serviceProvider);
        Assert.Equal(StatusCodes.Status200OK, highlightHttpResponse.StatusCode);

        var highlightModelResult = Assert.IsType<JsonElement>(await highlightToolCall.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Completed", highlightModelResult.GetProperty("outcome").GetString());
        Assert.True(highlightModelResult.GetProperty("postconditionVerified").GetBoolean());
    }

    [Fact]
    public async Task Every_liveui_action_is_bound_to_the_authenticated_session_tenant_and_principal()
    {
        var sessions = new InMemorySessionStore();
        var ownerSession = sessions.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var foreignTenantSession = sessions.Create(new TenantId(Ulid.Parse(OtherTenant)), new PrincipalId(Ulid.Parse(Principal)));
        var foreignPrincipalSession = sessions.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(OtherPrincipal)));
        var unsignedManifest = CreateManifest();
        var fullCapabilityRoute = unsignedManifest.Routes[0] with
        {
            Targets = unsignedManifest.Routes[0].Targets.Select(target => target with
            {
                Capabilities = Enum.GetValues<LiveUICapability>().ToHashSet()
            }).ToArray()
        };
        var unhashedManifest = unsignedManifest with { Routes = [fullCapabilityRoute], ContentHash = string.Empty };
        var manifest = unhashedManifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(unhashedManifest) };
        var options = Options.Create(new LiveUIOptions());
        var validator = new LiveUIManifestValidator();

        var ownedManifestProvider = new TestManifestProvider(manifest);
        var discoveryOwnedAuthorizer = new SessionScopeAuthorizer(sessions, AuthenticatedUser(Tenant, Principal));
        var discoveryOwnedOrchestrator = new LiveUIOrchestrator(
            ownedManifestProvider, discoveryOwnedAuthorizer, new RecordingRuntime(), options, validator);
        using var ownedTools = new LiveUIAgentTools(
            new TestContextAccessor(new LiveUIAgentContext(ownerSession.Id.Value.ToString(), "todo.home")),
            ownedManifestProvider, discoveryOwnedAuthorizer, validator, discoveryOwnedOrchestrator, options);
        var ownedPage = await ownedTools.GetCurrentPageAsync();
        Assert.NotNull(ownedPage);
        Assert.NotEmpty(ownedPage.Targets);
        Assert.Contains(ownedPage.NavigableRoutes, route => route.RouteId == "todo.home");

        var foreignManifestProvider = new TestManifestProvider(manifest);
        var discoveryForeignAuthorizer = new SessionScopeAuthorizer(sessions, AuthenticatedUser(Tenant, Principal));
        var discoveryForeignOrchestrator = new LiveUIOrchestrator(
            foreignManifestProvider, discoveryForeignAuthorizer, new RecordingRuntime(), options, validator);
        using var foreignTools = new LiveUIAgentTools(
            new TestContextAccessor(new LiveUIAgentContext(foreignTenantSession.Id.Value.ToString(), "todo.home")),
            foreignManifestProvider, discoveryForeignAuthorizer, validator, discoveryForeignOrchestrator, options);
        var foreignPage = await foreignTools.GetCurrentPageAsync();
        Assert.NotNull(foreignPage);
        Assert.Empty(foreignPage.Targets);
        Assert.Empty(foreignPage.NavigableRoutes);

        foreach (var actionType in Enum.GetValues<LiveUIActionType>())
        {
            var parameters = actionType switch
            {
                LiveUIActionType.Navigate => new LiveUIActionParameters(DestinationRouteId: "todo.home"),
                LiveUIActionType.SetValue or LiveUIActionType.Select => new LiveUIActionParameters(Value: "value"),
                LiveUIActionType.WaitForState => new LiveUIActionParameters(StateName: "visible", StateValue: "true"),
                _ => new LiveUIActionParameters()
            };
            var targetId = actionType is LiveUIActionType.Navigate or LiveUIActionType.ClearHighlight
                ? null
                : "todo.completed";

            var ownedRuntime = new RecordingRuntime();
            var ownedAuthorizer = new SessionScopeAuthorizer(sessions, AuthenticatedUser(Tenant, Principal));
            var ownedOrchestrator = new LiveUIOrchestrator(
                new TestManifestProvider(manifest), ownedAuthorizer, ownedRuntime, options, validator);
            var ownedAction = new LiveUIAction(Guid.NewGuid().ToString("N"), LiveUISchema.CurrentVersion,
                ownerSession.Id.Value.ToString(), manifest.ContentHash, "todo.home", targetId, actionType, parameters,
                TimeSpan.FromSeconds(5));

            var ownedReceipt = await ownedOrchestrator.ExecuteAsync(ownedAction);

            Assert.Equal(LiveUIActionOutcome.Completed, ownedReceipt.Outcome);
            Assert.Equal(1, ownedRuntime.Calls);

            foreach (var foreignSession in new[] { foreignTenantSession, foreignPrincipalSession })
            {
                var foreignRuntime = new RecordingRuntime();
                var foreignAuthorizer = new SessionScopeAuthorizer(sessions, AuthenticatedUser(Tenant, Principal));
                var foreignOrchestrator = new LiveUIOrchestrator(
                    new TestManifestProvider(manifest), foreignAuthorizer, foreignRuntime, options, validator);
                var foreignAction = ownedAction with
                {
                    ActionId = Guid.NewGuid().ToString("N"),
                    SessionId = foreignSession.Id.Value.ToString()
                };

                var foreignReceipt = await foreignOrchestrator.ExecuteAsync(foreignAction);

                Assert.Equal(LiveUIActionOutcome.Denied, foreignReceipt.Outcome);
                Assert.Equal("session_scope_mismatch", foreignReceipt.Code);
                Assert.Equal(0, foreignRuntime.Calls);
            }
        }
    }

    [Fact]
    public async Task Client_interaction_response_rejects_a_different_principal()
    {
        using var serviceProvider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var correlationId = Guid.NewGuid().ToString("D");
        using var pending = StartPendingRequest(store, session, correlationId);
        var response = VerifiedResponse(correlationId) with { ConnectionId = pending.ConnectionId };

        var result = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, AuthenticatedContext(serviceProvider, Tenant, OtherPrincipal), store);
        var httpResponse = await ExecuteAsync(result, serviceProvider);

        Assert.Equal(StatusCodes.Status404NotFound, httpResponse.StatusCode);
        Assert.DoesNotContain(store.GetEvents(session.Id.Value, 0),
            item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Fact]
    public async Task Client_interaction_response_rejects_a_different_tenant()
    {
        using var serviceProvider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var correlationId = Guid.NewGuid().ToString("D");
        using var pending = StartPendingRequest(store, session, correlationId);

        var result = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), VerifiedResponse(correlationId) with { ConnectionId = pending.ConnectionId },
            AuthenticatedContext(serviceProvider, OtherTenant, Principal), store);
        var httpResponse = await ExecuteAsync(result, serviceProvider);

        Assert.Equal(StatusCodes.Status404NotFound, httpResponse.StatusCode);
        Assert.DoesNotContain(store.GetEvents(session.Id.Value, 0),
            item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Fact]
    public async Task Client_interaction_response_cannot_use_another_sessions_pending_correlation()
    {
        using var serviceProvider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var store = new InMemorySessionStore();
        var ownerSession = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var otherSession = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var correlationId = Guid.NewGuid().ToString("D");
        using var pending = StartPendingRequest(store, ownerSession, correlationId);

        var result = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            otherSession.Id.Value.ToString(), VerifiedResponse(correlationId) with { ConnectionId = pending.ConnectionId }, AuthenticatedContext(serviceProvider), store);
        var httpResponse = await ExecuteAsync(result, serviceProvider);

        Assert.Equal(StatusCodes.Status409Conflict, httpResponse.StatusCode);
        Assert.DoesNotContain(store.GetEvents(otherSession.Id.Value, 0),
            item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Fact]
    public async Task Client_interaction_response_is_rejected_after_correlated_cancellation()
    {
        using var serviceProvider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var correlationId = Guid.NewGuid().ToString("D");
        using var pending = StartPendingRequest(store, session, correlationId);
        Assert.True(store.TryExpirePendingInteraction(session.Id, correlationId));
        await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Cancellation,
            JsonSerializer.Serialize(new ClientInteractionCancellation(correlationId), OpenConnectHttpJson.CreateOptions()));

        var result = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), VerifiedResponse(correlationId) with { ConnectionId = pending.ConnectionId }, AuthenticatedContext(serviceProvider), store);
        var httpResponse = await ExecuteAsync(result, serviceProvider);

        Assert.Equal(StatusCodes.Status409Conflict, httpResponse.StatusCode);
        Assert.DoesNotContain(store.GetEvents(session.Id.Value, 0),
            item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Fact]
    public async Task Client_interaction_response_rejects_a_conflicting_replay()
    {
        using var serviceProvider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var correlationId = Guid.NewGuid().ToString("D");
        using var pending = StartPendingRequest(store, session, correlationId);
        var response = VerifiedResponse(correlationId) with { ConnectionId = pending.ConnectionId };

        var first = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, AuthenticatedContext(serviceProvider), store);
        var conflict = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response with { PayloadJson = "{\"outcome\":\"Failed\"}" },
            AuthenticatedContext(serviceProvider), store);

        Assert.Equal(StatusCodes.Status200OK, (await ExecuteAsync(first, serviceProvider)).StatusCode);
        Assert.Equal(StatusCodes.Status409Conflict, (await ExecuteAsync(conflict, serviceProvider)).StatusCode);
        Assert.Single(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Fact]
    public async Task Concurrent_client_interaction_responses_are_idempotent_per_correlation()
    {
        using var serviceProvider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var correlationId = Guid.NewGuid().ToString("D");
        using var pending = StartPendingRequest(store, session, correlationId);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int submissions = 32;
        var ready = new CountdownEvent(submissions);

        var posts = Enumerable.Range(0, submissions).Select(index => Task.Run(async () =>
        {
            ready.Signal();
            await start.Task;
            var response = VerifiedResponse(correlationId) with
            {
                PayloadJson = JsonSerializer.Serialize(new { outcome = "Completed", receipt = index }),
                ConnectionId = pending.ConnectionId
            };
            var result = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
                session.Id.Value.ToString(), response, AuthenticatedContext(serviceProvider), store);
            return (response, Result: await ExecuteAsync(result, serviceProvider));
        })).ToArray();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Response submissions did not reach the concurrency barrier.");
        start.SetResult();
        var results = await Task.WhenAll(posts);

        Assert.Single(results, item => item.Result.StatusCode == StatusCodes.Status200OK);
        Assert.Equal(submissions - 1, results.Count(item => item.Result.StatusCode == StatusCodes.Status409Conflict));
        Assert.Single(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Response);
    }

    [Fact]
    public async Task Broker_ignores_wrong_correlation_and_response_in_another_session()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var other = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var correlation = Guid.NewGuid().ToString("D");
        var pending = store.RequestAsync(new SessionId(session.Id.Value),
            new ClientInteractionRequest(correlation, "execute", "todo.completed", "SelectTab"),
            "action", TimeSpan.FromSeconds(2));
        await WaitForRequestAsync(store, session.Id.Value, "execute");

        var wrongSessionResponse = VerifiedResponse(correlation) with
        {
            PayloadJson = "{\"outcome\":\"WrongSession\"}"
        };
        await store.AppendEventAsync(other.Id.Value, ClientInteractionEventTypes.Response,
            JsonSerializer.Serialize(wrongSessionResponse, OpenConnectHttpJson.CreateOptions()));
        var wrongCorrelationResponse = VerifiedResponse(Guid.NewGuid().ToString("D")) with
        {
            PayloadJson = "{\"outcome\":\"WrongCorrelation\"}"
        };
        await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Response,
            JsonSerializer.Serialize(wrongCorrelationResponse, OpenConnectHttpJson.CreateOptions()));

        var expectedResponse = VerifiedResponse(correlation) with
        {
            PayloadJson = "{\"outcome\":\"CorrectResponse\",\"receipt\":\"expected\"}"
        };
        await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Response,
            JsonSerializer.Serialize(expectedResponse, OpenConnectHttpJson.CreateOptions()));
        var actualResponse = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(actualResponse);
        Assert.Equal(expectedResponse.CorrelationId, actualResponse.CorrelationId);
        Assert.Equal(expectedResponse.ResultKind, actualResponse.ResultKind);
        Assert.Equal(expectedResponse.PayloadJson, actualResponse.PayloadJson);
        Assert.DoesNotContain("WrongSession", actualResponse.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("WrongCorrelation", actualResponse.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_broker_requests_receive_only_their_correlated_responses()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var correlations = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        Assert.Equal(2, correlations.Distinct(StringComparer.Ordinal).Count());
        var requests = correlations.Select(correlation => store.RequestAsync(new SessionId(session.Id.Value),
            new ClientInteractionRequest(correlation, "execute", "todo.completed", "SelectTab"),
            "action", TimeSpan.FromSeconds(2))).ToArray();
        await WaitForRequestsAsync(store, session.Id.Value, correlations);

        foreach (var correlation in correlations.Reverse())
            await store.AppendEventAsync(session.Id.Value, ClientInteractionEventTypes.Response,
                JsonSerializer.Serialize(VerifiedResponse(correlation), OpenConnectHttpJson.CreateOptions()));

        var responses = await Task.WhenAll(requests.Select(request => request.WaitAsync(TimeSpan.FromSeconds(2))));
        Assert.Equal(correlations, responses.Select(response => response!.CorrelationId));
    }

    [Fact]
    public async Task Cancelled_broker_request_does_not_accept_a_late_response()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var correlation = Guid.NewGuid().ToString("D");
        using var cancellation = new CancellationTokenSource();
        var pending = store.RequestAsync(new SessionId(session.Id.Value),
            new ClientInteractionRequest(correlation, "execute", "todo.completed", "SelectTab"),
            "action", TimeSpan.FromSeconds(2), cancellation.Token);
        await WaitForRequestAsync(store, session.Id.Value, "execute");
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var lateResponse = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), VerifiedResponse(correlation) with { ConnectionId = connection.ConnectionId }, AuthenticatedContext(services), store);
        Assert.Equal(StatusCodes.Status409Conflict,
            (await ExecuteAsync(lateResponse, services)).StatusCode);
        Assert.DoesNotContain(store.GetEvents(session.Id.Value, 0),
            item => item.Type == ClientInteractionEventTypes.Response);
        Assert.Contains(store.GetEvents(session.Id.Value, 0), item =>
            item.Type == ClientInteractionEventTypes.Cancellation &&
            JsonSerializer.Deserialize<ClientInteractionCancellation>(item.Data, OpenConnectHttpJson.CreateOptions())!
                .CorrelationId == correlation);
    }

    [Fact]
    public async Task Disconnect_releases_pending_interaction_and_reconnect_cannot_answer_it()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var original = store.RegisterBoundConnection(session)!;
        var correlation = Guid.NewGuid().ToString("D");
        using var cancellation = new CancellationTokenSource();
        var pending = store.RequestAsync(session.Id,
            new ClientInteractionRequest(correlation, "execute", "todo.completed", "SelectTab")
                { ConnectionId = original.ConnectionId },
            "action", TimeSpan.FromSeconds(10), cancellation.Token);
        try
        {
            await WaitForRequestAsync(store, session.Id.Value, "execute");
            original.Dispose();
            using var replacement = store.RegisterBoundConnection(session)!;
            using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
            var response = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
                session.Id.Value.ToString(), VerifiedResponse(correlation) with { ConnectionId = replacement.ConnectionId },
                AuthenticatedContext(services), store);
            Assert.Equal(StatusCodes.Status409Conflict, (await ExecuteAsync(response, services)).StatusCode);

            Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            var requestEvent = Assert.Single(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Request);
            Assert.False(store.IsInteractionEventForConnection(requestEvent, replacement.ConnectionId));
        }
        finally
        {
            cancellation.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
            original.Dispose();
        }
    }

    [Fact]
    public async Task Accepted_response_survives_a_subsequent_disconnect()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var correlation = Guid.NewGuid().ToString("D");
        var pending = store.RequestAsync(session.Id,
            new ClientInteractionRequest(correlation, "execute", "todo.completed", "SelectTab")
                { ConnectionId = connection.ConnectionId }, "action", TimeSpan.FromSeconds(5));
        await WaitForRequestAsync(store, session.Id.Value, "execute");
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var response = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), VerifiedResponse(correlation) with { ConnectionId = connection.ConnectionId },
            AuthenticatedContext(services), store);
        Assert.Equal(StatusCodes.Status200OK, (await ExecuteAsync(response, services)).StatusCode);

        connection.Dispose();

        Assert.Equal(correlation, (await pending.WaitAsync(TimeSpan.FromSeconds(2)))!.CorrelationId);
    }

    [Fact]
    public async Task Agent_tool_reports_uncertain_outcome_when_client_disconnects_after_dispatch()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        using var connection = store.RegisterBoundConnection(session)!;
        var tools = LiveUIClientAgentToolFactory.CreateTools(store, session.Id, TimeSpan.FromSeconds(10));
        var execute = Assert.IsAssignableFrom<Microsoft.Extensions.AI.AIFunction>(
            tools.Single(tool => tool.Name == "liveui_execute"));
        var pending = execute.InvokeAsync(new Microsoft.Extensions.AI.AIFunctionArguments(new Dictionary<string, object?>
        { ["targetId"] = "todo.completed", ["action"] = "SelectTab" })).AsTask();
        await WaitForRequestAsync(store, session.Id.Value, "execute");

        connection.Dispose();

        var result = Assert.IsType<JsonElement>(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("Uncertain", result.GetProperty("outcome").GetString());
        Assert.False(result.GetProperty("postconditionVerified").GetBoolean());
        Assert.False(result.GetProperty("retrySafe").GetBoolean());
    }

    private static PendingRequest StartPendingRequest(InMemorySessionStore store, SessionRecord session, string correlationId)
    {
        var connection = store.RegisterBoundConnection(session)!;
        var cancellation = new CancellationTokenSource();
        var task = store.RequestAsync(session.Id,
            new ClientInteractionRequest(correlationId, "execute", "todo.completed", "SelectTab"),
            "action", TimeSpan.FromSeconds(10), cancellation.Token);
        return new PendingRequest(connection, cancellation, task);
    }

    private sealed class PendingRequest(InMemoryEventJournal.BoundConnectionRegistration connection,
        CancellationTokenSource cancellation, Task<ClientInteractionResponse?> task) : IDisposable
    {
        public string ConnectionId => connection.ConnectionId;
        public void Dispose()
        {
            cancellation.Cancel();
            try { task.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            cancellation.Dispose();
            connection.Dispose();
        }
    }

    private static ClientInteractionResponse VerifiedResponse(string correlationId) =>
        new(correlationId, "action", "{\"outcome\":\"Completed\",\"postconditionVerified\":true}");

    private static bool TryParseUlid(string value, out Ulid id)
    {
        try
        {
            id = Ulid.Parse(value);
            return id != default && string.Equals(id.ToString(), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            id = default;
            return false;
        }
    }

    private static LiveUIManifest CreateManifest()
    {
        var manifest = new LiveUIManifest(LiveUISchema.CurrentVersion, "build-1", string.Empty,
        [
            new LiveUIRoute("todo.home", "/", "Todo",
            [
                new LiveUITarget("todo.completed", LiveUITargetKind.Tab, "Completed",
                    new HashSet<LiveUICapability> { LiveUICapability.Activate, LiveUICapability.Filter, LiveUICapability.Highlight })
            ])
        ]);
        return manifest with { ContentHash = LiveUIManifestHasher.ComputeContentHash(manifest) };
    }

    private static async Task<ClientInteractionRequest> WaitForRequestAsync(
        InMemorySessionStore sessions, Ulid sessionId, string operation)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var request = sessions.GetEvents(sessionId, 0)
                .Where(item => item.Type == ClientInteractionEventTypes.Request)
                .Select(item => JsonSerializer.Deserialize<ClientInteractionRequest>(item.Data, OpenConnectHttpJson.CreateOptions()))
                .FirstOrDefault(item => item?.Operation == operation);
            if (request is not null) return request;
            await Task.Delay(10);
        }
        throw new TimeoutException("The LiveUI MAF tool did not append a client request.");
    }

    private static async Task WaitForRequestsAsync(
        InMemorySessionStore sessions, Ulid sessionId, IReadOnlyCollection<string> correlations)
    {
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            var registered = sessions.GetEvents(sessionId, 0)
                .Where(item => item.Type == ClientInteractionEventTypes.Request)
                .Select(item => JsonSerializer.Deserialize<ClientInteractionRequest>(
                    item.Data, OpenConnectHttpJson.CreateOptions()))
                .Where(item => item is not null)
                .Select(item => item!.CorrelationId)
                .ToHashSet(StringComparer.Ordinal);
            if (correlations.All(registered.Contains)) return;
            await Task.Yield();
        }

        throw new TimeoutException("Both correlated client requests were not registered in the session event stream.");
    }

    private static DefaultHttpContext AuthenticatedContext(
        IServiceProvider services,
        string tenant = Tenant,
        string principal = Principal) => new()
    {
        RequestServices = services,
        User = AuthenticatedUser(tenant, principal)
    };

    private static ClaimsPrincipal AuthenticatedUser(string tenant, string principal) =>
        new(new ClaimsIdentity(
        [
            new Claim("tenant_id", tenant),
            new Claim(ClaimTypes.NameIdentifier, principal)
        ], "test"));

    private static async Task<(int StatusCode, byte[] Body)> ExecuteAsync(IResult result, IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services, Response = { Body = new MemoryStream() } };
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, ((MemoryStream)context.Response.Body).ToArray());
    }

    private sealed class TestManifestProvider(LiveUIManifest manifest) : ILiveUIManifestProvider
    {
        public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIManifest?>(manifest);
    }

    private sealed class TestContextAccessor(LiveUIAgentContext context) : ILiveUIAgentContextAccessor
    {
        public ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<LiveUIAgentContext?>(context);
    }

    private sealed class AllowAuthorizer : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveUIAuthorizationDecision(true, "test_allowed"));
    }

    private sealed class SessionScopeAuthorizer(InMemorySessionStore sessions, ClaimsPrincipal user)
        : ILiveUIActionAuthorizer
    {
        public ValueTask<LiveUIAuthorizationDecision> AuthorizeAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = TryParseUlid(context.Action.SessionId, out var sessionId) ? sessions.Get(sessionId) : null;
            var tenant = user.FindFirstValue("tenant_id");
            var principal = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            var allowed = session is not null && tenant is not null && principal is not null &&
                TryParseUlid(tenant, out var tenantId) && TryParseUlid(principal, out var principalId) &&
                session.TenantId.Value == tenantId &&
                session.PrincipalId.Value == principalId;
            return ValueTask.FromResult(new LiveUIAuthorizationDecision(allowed,
                allowed ? "session_scope_allowed" : "session_scope_mismatch"));
        }
    }

    private sealed class RecordingRuntime : ILiveUIRuntime
    {
        public LiveUIExecutionContext? LastContext { get; private set; }
        public int Calls { get; private set; }

        public ValueTask<LiveUIRuntimeResult> ExecuteAsync(LiveUIExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastContext = context;
            return ValueTask.FromResult(new LiveUIRuntimeResult(LiveUIActionOutcome.Completed, "selected", true, "selected"));
        }
    }
}
