using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Navigator;
using OpenConnect.Navigator.AspNetCore;
using OpenConnect.Navigator.AspNetCore.Outbox;
using Xunit;

namespace OpenConnect.Navigator.AspNetCore.Tests.Outbox;

public sealed class EndpointAuthTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

    [Fact]
    public async Task EventsReturnsOnlyProjectedSafeDtoFields()
    {
        var store = new NavigatorMemoryStore();
        var session = CreateSession(store);
        var raw = new NavigatorEvent("event-1", "command.completed", session, "task-1", 1, null,
            DateTimeOffset.UtcNow, new Dictionary<string, string> { ["status"] = "complete" });
        Assert.True((bool)typeof(NavigatorMemoryStore).GetMethod("AppendEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [session, raw])!);

        var result = await Run(session, store, Context(Tenant, Principal));

        Assert.Equal(200, result.StatusCode);
        var dto = Assert.Single(result.Json.RootElement.EnumerateArray()).EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new[] { "eventId", "sessionId", "taskId", "sequence", "correlationId", "type", "occurredAt", "metadata" }.ToHashSet(StringComparer.Ordinal), dto);
        Assert.DoesNotContain("payload", result.Json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dom", result.Json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audio", result.Json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", result.Json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAX", Principal)]
    [InlineData(Tenant, "01ARZ3NDEKTSV4RRFFQ69G5FAY")]
    public async Task EventsDoesNotExposeSessionAcrossTenantOrPrincipal(string tenant, string principal)
    {
        var store = new NavigatorMemoryStore();
        var session = CreateSession(store);
        var result = await Run(session, store, Context(tenant, principal));
        Assert.Equal(404, result.StatusCode);
    }

    [Theory]
    [InlineData("bad/value")]
    public async Task EventsRejectsInvalidConsumerId(string consumerId)
    {
        var store = new NavigatorMemoryStore();
        var session = CreateSession(store);
        var result = await Run(session, store, Context(Tenant, Principal), consumerId);
        Assert.Equal(400, result.StatusCode);
    }

    private static string CreateSession(NavigatorMemoryStore store)
    {
        var entry = typeof(NavigatorMemoryStore).GetMethod("Create", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [Tenant, Principal, "client", "home"])!;
        return (string)entry.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(entry)!;
    }

    private static DefaultHttpContext Context(string tenant, string principal)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("tenant_id", tenant), new Claim(ClaimTypes.NameIdentifier, principal), new Claim("sub", principal)
        ], "test"));
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int StatusCode, JsonDocument Json)> Run(string id, NavigatorMemoryStore store, DefaultHttpContext context, string? consumerId = null)
    {
        var method = typeof(NavigatorEndpoints).GetMethod("Events", BindingFlags.NonPublic | BindingFlags.Static)!;
        var pending = (Task<IResult>)method.Invoke(null, [id, (long?)null, context, (INavigatorSessionStore)store, new NavigatorWsDeliveryAdapter(new NavigatorWsProjector()), consumerId])!;
        var result = await pending;
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        var body = context.Response.Body.Length == 0
            ? JsonDocument.Parse("null")
            : await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, body);
    }
}
