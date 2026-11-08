using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenConnect.Abstractions;
using OpenConnect.Hosting.AspNetCore;
using Xunit;

namespace OpenConnect.Hosting.Tests;

/// <summary>
/// Covers the UUID canonical event route (contracts EVENT-V1-PUBLIC + EVENT-CURSOR-V1
/// + SESSION-UUID-MAPPING resolver surface): canonical envelopes, legacy ULID
/// preservation, cursor aliases, ownership concealment, and explicit 422 handling
/// for legacy records that lack genuine UUID/time identity.
/// </summary>
public sealed class SessionEventCanonicalTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string Principal = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
    private const string OtherPrincipal = "01ARZ3NDEKTSV4RRFFQ69G5FAX";

    [Fact]
    public async Task Uuid_session_returns_canonical_envelopes_with_contract_field_names()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");
        var firstId = Guid.Parse("123e4567-e89b-42d3-a456-426614174010");
        var secondId = Guid.Parse("123e4567-e89b-42d3-a456-426614174011");
        await store.AppendEventAsync(session.Id.Value, "task.accepted",
            CanonicalData(firstId, "2026-01-01T00:00:00Z", """{"taskId":"task-1"}"""));
        await store.AppendEventAsync(session.Id.Value, "task.completed",
            CanonicalData(secondId, "2026-01-02T00:00:00Z", """{"taskId":"task-1"}""", "corr-7"));

        var response = await Invoke(store, sessionUuid.ToString("D"), string.Empty, Map(sessionUuid, session.Id.Value));

        Assert.Equal(200, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        Assert.Equal(2, json.RootElement.GetArrayLength());

        var first = json.RootElement[0];
        Assert.Equal("openconnect.v1", first.GetProperty("schemaVersion").GetString());
        Assert.Equal(firstId.ToString("D"), first.GetProperty("eventId").GetString());
        Assert.Equal("task.accepted", first.GetProperty("eventType").GetString());
        Assert.Equal(sessionUuid.ToString("D"), first.GetProperty("sessionId").GetString());
        Assert.Equal(1, first.GetProperty("sequence").GetInt64());
        Assert.Equal("2026-01-01T00:00:00Z", first.GetProperty("occurredAt").GetString());
        Assert.Equal("task-1", first.GetProperty("payload").GetProperty("taskId").GetString());
        Assert.False(first.TryGetProperty("correlationId", out _));

        var second = json.RootElement[1];
        Assert.Equal("corr-7", second.GetProperty("correlationId").GetString());
        Assert.Equal(2, second.GetProperty("sequence").GetInt64());
        Assert.False(second.TryGetProperty("type", out _));
        Assert.False(second.TryGetProperty("data", out _));
    }

    [Fact]
    public async Task Ulid_session_response_remains_legacy_untouched()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");
        await store.AppendEventAsync(session.Id.Value, "task.accepted",
            CanonicalData(Guid.Parse("123e4567-e89b-42d3-a456-426614174010"), "2026-01-01T00:00:00Z", """{"taskId":"task-1"}"""));

        var response = await Invoke(store, session.Id.Value.ToString(), string.Empty, Map(sessionUuid, session.Id.Value));

        Assert.Equal(200, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        var item = json.RootElement[0];
        // Legacy EventRecord response is untouched: SessionId serializes as an
        // object (identifier wrapper) and Data as a JSON string, never as the
        // canonical eventId/eventType/payload envelope.
        Assert.Equal(JsonValueKind.Object, item.GetProperty("sessionId").ValueKind);
        Assert.Equal("task.accepted", item.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, item.GetProperty("data").ValueKind);
        Assert.False(item.TryGetProperty("eventId", out _));
        Assert.False(item.TryGetProperty("eventType", out _));
        Assert.False(item.TryGetProperty("payload", out _));
    }

    [Fact]
    public async Task Uuid_cursor_aliases_replay_exclusively_after_cursor()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");
        var resolver = Map(sessionUuid, session.Id.Value);
        for (var i = 1; i <= 3; i++)
            await store.AppendEventAsync(session.Id.Value, "task.accepted",
                CanonicalData(Guid.Parse($"123e4567-e89b-42d3-a456-4266141740{i:D2}"), "2026-01-01T00:00:00Z", """{"n":1}"""));

        foreach (var (query, expected) in new[] { ("", new long[] { 1, 2, 3 }), ("?afterSequence=1", new long[] { 2, 3 }), ("?after=2", new long[] { 3 }) })
        {
            var response = await Invoke(store, sessionUuid.ToString("D"), query, resolver);
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
    public async Task Uuid_invalid_or_ambiguous_cursor_returns_safe_400(string query)
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");

        var response = await Invoke(store, sessionUuid.ToString("D"), query, Map(sessionUuid, session.Id.Value));

        Assert.Equal(400, response.Status);
        Assert.Contains("invalid_request", response.Body);
    }

    [Fact]
    public async Task Uuid_unknown_mapping_is_concealed_as_404()
    {
        var store = new InMemorySessionStore();
        store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));

        var response = await Invoke(store, Guid.Parse("123e4567-e89b-42d3-a456-426614174099").ToString("D"), string.Empty, new InMemorySessionUuidResolver());

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task Uuid_cross_tenant_session_is_concealed_as_404()
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");

        var response = await Invoke(store, sessionUuid.ToString("D"), string.Empty, Map(sessionUuid, session.Id.Value), OtherPrincipal);

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task Uuid_missing_resolver_is_concealed_as_404()
    {
        var store = new InMemorySessionStore();
        store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));

        var response = await Invoke(store, Guid.Parse("123e4567-e89b-42d3-a456-426614174001").ToString("D"), string.Empty, resolver: null);

        Assert.Equal(404, response.Status);
    }

    [Theory]
    [InlineData("task.accepted", "1")]
    [InlineData("task.accepted", """{"taskId":"task-1"}""")]
    [InlineData("task.accepted", """{"occurredAt":"2026-01-01T00:00:00Z","taskId":"task-1"}""")]
    public async Task Uuid_legacy_record_without_genuine_identity_returns_422_never_fabricates(string type, string data)
    {
        var store = new InMemorySessionStore();
        var session = store.Create(new TenantId(Ulid.Parse(Tenant)), new PrincipalId(Ulid.Parse(Principal)));
        var sessionUuid = Guid.Parse("123e4567-e89b-42d3-a456-426614174001");
        await store.AppendEventAsync(session.Id.Value, type, data);

        var response = await Invoke(store, sessionUuid.ToString("D"), string.Empty, Map(sessionUuid, session.Id.Value));

        Assert.Equal(422, response.Status);
        Assert.Contains("legacy-event-without-canonical-identity", response.Body);
        Assert.DoesNotContain("eventId", response.Body);
    }

    private static string CanonicalData(Guid eventId, string occurredAt, string payloadJson, string? correlationId = null)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        Assert.Equal(JsonValueKind.Object, payload.RootElement.ValueKind);
        var builder = new StringBuilder("{");
        builder.Append($"\"eventId\":\"{eventId:D}\",\"occurredAt\":\"{occurredAt}\",");
        if (correlationId is not null) builder.Append($"\"correlationId\":\"{correlationId}\",");
        foreach (var property in payload.RootElement.EnumerateObject())
            builder.Append($"\"{property.Name}\":{property.Value.GetRawText()},");
        builder.Length--;
        builder.Append('}');
        return builder.ToString();
    }

    private static InMemorySessionUuidResolver Map(Guid sessionUuid, Ulid sessionUlid)
    {
        var resolver = new InMemorySessionUuidResolver();
        resolver.Register(sessionUuid, sessionUlid);
        return resolver;
    }

    private static async Task<(int Status, string Body)> Invoke(
        InMemorySessionStore store,
        string id,
        string query,
        ISessionUuidResolver? resolver,
        string principal = Principal)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (resolver is not null) services.AddSingleton(resolver);
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
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }
}
