using System.Net;
using System.Text.Json;
using OpenConnect.QdrantVactor;
using Xunit;

namespace OpenConnect.QdrantVactor.Tests;

public sealed class VectorStoreRestTests
{
    [Fact]
    public async Task UpsertUsesQdrantPutAndDeterministicUuidWithApiKeyHeader()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var store = Create(handler);
        var point = new VectorPoint(new string('a', 64), "tenant-a", "agent-a", "safe", new float[] { 0.25f, -0.5f }, 7);

        await store.UpsertAsync(point, default);
        await store.UpsertAsync(point, default);

        Assert.Equal(2, handler.Requests.Count);
        var first = handler.Requests[0];
        Assert.Equal(HttpMethod.Put, first.Method);
        Assert.Equal("http://qdrant.test:6333/collections/memory/points?wait=true", first.Uri);
        Assert.True(first.HasApiKey);
        using var one = JsonDocument.Parse(first.Body!);
        using var two = JsonDocument.Parse(handler.Requests[1].Body!);
        var id = one.RootElement.GetProperty("points")[0].GetProperty("id").GetGuid();
        Assert.Equal(id, two.RootElement.GetProperty("points")[0].GetProperty("id").GetGuid());
        Assert.Equal(2, one.RootElement.GetProperty("points")[0].GetProperty("vector").GetArrayLength());
        Assert.Equal("tenant-a", one.RootElement.GetProperty("points")[0].GetProperty("payload").GetProperty("tenant").GetString());
    }

    [Fact]
    public async Task UpsertRoundTripsPointMetadata()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var createdAt = DateTimeOffset.Parse("2025-01-02T03:04:05+00:00");
        var point = new VectorPoint("point-id", "tenant-a", "agent-a", "safe", new float[] { 1, 0 }, 7,
            "event-1", "session-1", "Sanitized", createdAt);

        await Create(handler).UpsertAsync(point, default);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!);
        var payload = body.RootElement.GetProperty("points")[0].GetProperty("payload");
        Assert.Equal("event-1", payload.GetProperty("event_id").GetString());
        Assert.Equal("session-1", payload.GetProperty("session_id").GetString());
        Assert.Equal("Sanitized", payload.GetProperty("privacy_class").GetString());
        Assert.Equal(createdAt, payload.GetProperty("created_at").GetDateTimeOffset());
    }

    [Fact]
    public async Task SearchPostsTenantAgentFilterAndRejectsCrossScopeResults()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"result":[{"id":"11111111-1111-4111-8111-111111111111","score":0.9,"vector":[1,0],"payload":{"tenant":"tenant-a","agent":"agent-a","content":"match","revision":4}},{"id":"22222222-2222-4222-8222-222222222222","score":0.8,"vector":[0,1],"payload":{"tenant":"tenant-b","agent":"agent-a","content":"wrong","revision":4}}]}""")
        });
        var results = await Create(handler).SearchAsync("tenant-a", "agent-a", new float[] { 1, 0 }, 5, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/collections/memory/points/search", request.Uri);
        Assert.True(request.HasApiKey);
        using var body = JsonDocument.Parse(request.Body!);
        var must = body.RootElement.GetProperty("filter").GetProperty("must");
        Assert.Equal("tenant", must[0].GetProperty("key").GetString());
        Assert.Equal("tenant-a", must[0].GetProperty("match").GetProperty("value").GetString());
        Assert.Equal("agent-a", must[1].GetProperty("match").GetProperty("value").GetString());
        Assert.Equal("match", Assert.Single(results).Content);
    }

    [Fact]
    public async Task SearchMapsQdrantScoreAndMetadata()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"result":[{"id":"11111111-1111-4111-8111-111111111111","score":0.75,"vector":[1,0],"payload":{"tenant":"tenant-a","agent":"agent-a","content":"match","revision":4,"event_id":"event-1","session_id":"session-1","privacy_class":"Sanitized","created_at":"2025-01-02T03:04:05+00:00"}}]}""")
        });

        var point = Assert.Single(await Create(handler).SearchAsync("tenant-a", "agent-a", new float[] { 1, 0 }, 5, default));

        Assert.Equal("event-1", point.EventId);
        Assert.Equal("session-1", point.SessionId);
        Assert.Equal("Sanitized", point.PrivacyClass);
        Assert.Equal(DateTimeOffset.Parse("2025-01-02T03:04:05+00:00"), point.CreatedAt);
        Assert.Equal(0.75, point.Score);
    }

    [Fact]
    public async Task DeleteUsesTenantAgentAndPointIdFilter()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Create(handler).DeleteAsync("tenant-a", "agent-a", "11111111-1111-4111-8111-111111111111", default);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/collections/memory/points/delete?wait=true", request.Uri);
        Assert.True(request.HasApiKey);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(3, body.RootElement.GetProperty("filter").GetProperty("must").GetArrayLength());
    }

    private static QdrantVectorStoreRest Create(RecordingHandler handler) =>
        new(new HttpClient(handler), new Uri("http://qdrant.test:6333"), "not-asserted-secret", "memory", 2);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body, request.Headers.Contains("api-key")));
            return response(request);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string? Body, bool HasApiKey);
}
