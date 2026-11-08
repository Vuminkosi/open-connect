using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class SessionEventCursorTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";

    [Fact]
    public async Task Both_cursor_spellings_default_and_replay_exclusively_after_cursor()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        await store.AppendEventAsync(session.Id.Value, "one", "1");
        await store.AppendEventAsync(session.Id.Value, "two", "2");
        await store.AppendEventAsync(session.Id.Value, "three", "3");

        foreach (var (query, expected) in new[] { ("", new long[] { 1, 2, 3 }), ("?afterSequence=1", new long[] { 2, 3 }), ("?after=2", new long[] { 3 }) })
        {
            var response = await Invoke(store, session.Id.Value.ToString(), query);
            Assert.Equal(200, response.Status);
            using var json = JsonDocument.Parse(response.Body);
            Assert.Equal(expected, json.RootElement.EnumerateArray().Select(item => item.GetProperty("sequence").GetInt64()));
        }
    }

    [Theory]
    [InlineData("?afterSequence=-1")]
    [InlineData("?after=bad")]
    [InlineData("?afterSequence=9223372036854775808")]
    [InlineData("?after=1&after=2")]
    [InlineData("?afterSequence=1&after=1")]
    [InlineData("?afterSequence=1&afterSequence=1")]
    public async Task Invalid_or_ambiguous_cursor_returns_safe_400(string query)
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));

        var response = await Invoke(store, session.Id.Value.ToString(), query);

        Assert.Equal(400, response.Status);
        Assert.Contains("invalid_request", response.Body);
    }

    [Fact]
    public async Task Session_owned_by_another_principal_remains_concealed()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var response = await Invoke(store, session.Id.Value.ToString(), "?afterSequence=0", "01ARZ3NDEKTSV4RRFFQ69G5FAX");
        Assert.Equal(404, response.Status);
    }

    private static async Task<(int Status, string Body)> Invoke(InMemorySessionStore store, string id, string query, string principal = Principal)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("tenant_id", Tenant), new Claim(ClaimTypes.NameIdentifier, principal)
            ], "test")),
            Response = { Body = new MemoryStream() }
        };
        context.Request.QueryString = new QueryString(query);
        var result = EndpointRouteBuilderExtensions.HandleGetSessionEventsFromRequest(id, context, store);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }
}
