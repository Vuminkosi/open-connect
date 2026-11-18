using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Serialization;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class LiveUIConnectionEndpointTests
{
    private static readonly TenantId Tenant = new(Ulid.NewUlid());
    private static readonly PrincipalId Principal = new(Ulid.NewUlid());

    [Fact]
    public async Task Broker_binds_request_to_the_only_registered_connection_and_filters_it_from_others()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(Tenant, Principal);
        using var selected = store.RegisterBoundConnection(session)!;
        using var cancellation = new CancellationTokenSource();
        var requestTask = store.RequestAsync(session.Id, NewRequest(), "action", TimeSpan.FromSeconds(2), cancellation.Token);

        var record = await WaitForRequestAsync(store, session.Id.Value);
        var request = JsonSerializer.Deserialize<ClientInteractionRequest>(record.Data, OpenConnectHttpJson.CreateOptions())!;
        using var other = store.RegisterBoundConnection(session)!;

        Assert.Equal(selected.ConnectionId, request.ConnectionId);
        Assert.True(store.IsInteractionEventForConnection(record, selected.ConnectionId));
        Assert.False(store.IsInteractionEventForConnection(record, other.ConnectionId));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
    }

    [Fact]
    public async Task Broker_fails_closed_without_exactly_one_bound_connection()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(Tenant, Principal);
        Assert.Null(await store.RequestAsync(session.Id, NewRequest(), "action", TimeSpan.FromSeconds(1)));

        using var first = store.RegisterBoundConnection(session)!;
        using var second = store.RegisterBoundConnection(session)!;
        Assert.Null(await store.RequestAsync(session.Id, NewRequest(), "action", TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain(store.GetEvents(session.Id.Value, 0),
            item => item.Type == ClientInteractionEventTypes.Request);
    }

    [Fact]
    public async Task A_task_bound_to_a_disconnected_client_is_not_reassigned_to_a_replacement()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(Tenant, Principal);
        var original = store.RegisterBoundConnection(session)!;
        var originalId = original.ConnectionId;
        original.Dispose();
        using var replacement = store.RegisterBoundConnection(session)!;

        var response = await store.RequestAsync(session.Id, NewRequest() with { ConnectionId = originalId },
            "action", TimeSpan.FromSeconds(1));

        Assert.Null(response);
        Assert.DoesNotContain(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Request);
    }

    [Fact]
    public async Task Response_requires_active_matching_connection_and_accepts_only_once()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var store = new InMemorySessionStore();
        var session = store.Create(Tenant, Principal);
        var connection = store.RegisterBoundConnection(session)!;
        var requestTask = store.RequestAsync(session.Id, NewRequest(), "action", TimeSpan.FromSeconds(2));
        var requestRecord = await WaitForRequestAsync(store, session.Id.Value);
        var request = JsonSerializer.Deserialize<ClientInteractionRequest>(requestRecord.Data, OpenConnectHttpJson.CreateOptions())!;
        var response = new ClientInteractionResponse(request.CorrelationId, "action", "{\"ok\":true}")
        { ConnectionId = connection.ConnectionId };

        var wrongConnection = await PostAsync(session, response with { ConnectionId = "stale-connection" }, store, services);
        Assert.Equal(StatusCodes.Status409Conflict, wrongConnection);

        var accepted = await PostAsync(session, response, store, services);
        var duplicate = await PostAsync(session, response, store, services);
        Assert.Equal(StatusCodes.Status200OK, accepted);
        Assert.Equal(StatusCodes.Status409Conflict, duplicate);

        connection.Dispose();
        var lateResponse = await PostAsync(session,
            response with { PayloadJson = "{\"late\":true}" }, store, services);
        Assert.Equal(StatusCodes.Status409Conflict, lateResponse);
        Assert.Single(store.GetEvents(session.Id.Value, 0), item => item.Type == ClientInteractionEventTypes.Response);
        Assert.NotNull(await requestTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static ClientInteractionRequest NewRequest() =>
        new(Guid.NewGuid().ToString("D"), "execute", "target", "click");

    private static async Task<EventRecord> WaitForRequestAsync(InMemorySessionStore store, Ulid sessionId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var found = store.GetEvents(sessionId, 0).FirstOrDefault(item => item.Type == ClientInteractionEventTypes.Request);
            if (found is not null)
                return found;
            await Task.Delay(5);
        }
        throw new TimeoutException("Broker did not append its request event.");
    }

    private static async Task<int> PostAsync(SessionRecord session, ClientInteractionResponse response,
        InMemorySessionStore store, IServiceProvider services)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant_id", Tenant.Value.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Principal.Value.ToString())
            ], "test"))
        };
        var result = await EndpointRouteBuilderExtensions.HandlePostClientInteractionResponseAsync(
            session.Id.Value.ToString(), response, context, store);
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }
}
