using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class KnowledgeImageClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()),
        new SessionId(Ulid.NewUlid()), EgressDestination.Worker);

    [Fact]
    public async Task AuthorizedImageAndTextFlowThroughVisionToSignedIndex()
    {
        var context = Context();
        var visionCalls = 0;
        var visionHandler = new Handler(async request =>
        {
            visionCalls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var payload = json.RootElement.GetProperty("payload");
            if (visionCalls == 1)
                Assert.Equal("image", payload.GetProperty("modality").GetString());
            else
                Assert.Equal("safe", payload.GetProperty("text").GetString());
            return Reply(new
            {
                schemaVersion = "openconnect.v1",
                taskId = json.RootElement.GetProperty("taskId").GetString(),
                kind = "vision.embed",
                result = new
                {
                    vector = Enumerable.Range(0, 512).Select(i => i == 0 ? 1.0 : 0.0),
                    model_id = "clip",
                    fingerprint = new string('a', 64),
                    space_id = new string('b', 64),
                    dimension = 512
                }
            });
        });
        var indexHandler = new Handler(async request =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(),
                request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var kind = root.GetProperty("kind").GetString();
            var payload = root.GetProperty("payload");
            Assert.Equal(context.Tenant.Value.ToString(), root.GetProperty("tenantId").GetString());
            Assert.Equal(new string('b', 64), payload.GetProperty("embeddingSpaceId").GetString());
            Assert.Equal(512, payload.GetProperty("vector").GetArrayLength());
            object result = kind == "knowledge.image.upsert" ? new { stored = true } : new
            {
                hits = new[]
                { new { document_id = "doc", image_id = "img", image_revision = "v1", score = 0.9 } }
            };
            if (kind == "knowledge.image.search")
                Assert.Equal(context.Principal.Value.ToString(), payload.GetProperty("principalId").GetString());
            return Reply(new { schemaVersion = "openconnect.v1", taskId = root.GetProperty("taskId").GetString(), kind, result });
        });
        using var visionHttp = new HttpClient(visionHandler);
        using var indexHttp = new HttpClient(indexHandler);
        using var vision = Vision(visionHttp);
        using var client = new KnowledgeImageClient(indexHttp, new PassGate(), new Uri("https://worker.test"), "token", Key, vision,
            (_, submission, _, _) => ValueTask.FromResult(submission.DocumentId == "doc"),
            (_, app, collection, roles, _) => ValueTask.FromResult(app == "app" && collection == "collection" && roles.Count == 0));
        await client.UpsertAsync(context, Submission(), new byte[] { 1, 2 });
        using var result = await client.SearchAsync(context, "app", "collection", [], "private");
        Assert.Equal("img", result.RootElement.GetProperty("result").GetProperty("hits")[0].GetProperty("image_id").GetString());
        Assert.Equal(2, visionCalls);
        using var outputDenied = new KnowledgeImageClient(indexHttp, new OutputDenyGate(), new Uri("https://worker.test"), "token", Key, vision,
            (_, _, _, _) => ValueTask.FromResult(true), (_, _, _, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => outputDenied.SearchAsync(context, "app", "collection", [], "private"));
    }

    [Fact]
    public async Task DeniedScopeNeverCallsEitherWorker()
    {
        var visionHandler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var indexHandler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var visionHttp = new HttpClient(visionHandler);
        using var indexHttp = new HttpClient(indexHandler);
        using var vision = Vision(visionHttp);
        using var client = new KnowledgeImageClient(indexHttp, new PassGate(), new Uri("https://worker.test"), "token", Key, vision,
            (_, _, _, _) => ValueTask.FromResult(false), (_, _, _, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.UpsertAsync(Context(), Submission(), new byte[] { 1 }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.SearchAsync(Context(), "app", "collection", [], "query"));
        Assert.Equal(0, visionHandler.Calls);
        Assert.Equal(0, indexHandler.Calls);
    }

    [Fact]
    public async Task PrivacyDenialStopsImageMetadataAndClearsTemporaryBytes()
    {
        var visionHandler = new Handler(_ => throw new InvalidOperationException("must not send"));
        var indexHandler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var visionHttp = new HttpClient(visionHandler);
        using var indexHttp = new HttpClient(indexHandler);
        using var vision = Vision(visionHttp);
        ReadOnlyMemory<byte> captured = default;
        using var client = new KnowledgeImageClient(indexHttp, new DenyGate(), new Uri("https://worker.test"), "token", Key, vision,
            (_, _, bytes, _) => { captured = bytes; return ValueTask.FromResult(true); },
            (_, _, _, _, _) => ValueTask.FromResult(true));

        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.UpsertAsync(Context(), Submission(), new byte[] { 1, 2 }));
        Assert.Equal(new byte[] { 0, 0 }, captured.ToArray());
        Assert.Equal(0, visionHandler.Calls);
        Assert.Equal(0, indexHandler.Calls);
    }

    [Fact]
    public async Task SearchSnapshotsAuthorizedRolesAndRejectsDuplicateHits()
    {
        var roles = new List<string> { "reader" };
        var visionHandler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(new
            {
                schemaVersion = "openconnect.v1",
                taskId = json.RootElement.GetProperty("taskId").GetString(),
                kind = "vision.embed",
                result = new
                {
                    vector = Enumerable.Range(0, 512).Select(i => i == 0 ? 1.0 : 0.0),
                    model_id = "clip",
                    fingerprint = new string('a', 64),
                    space_id = new string('b', 64),
                    dimension = 512
                }
            });
        });
        var indexHandler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal("reader", json.RootElement.GetProperty("payload").GetProperty("roleIds")[0].GetString());
            var hit = new { document_id = "doc", image_id = "img", image_revision = "v1", score = 0.9 };
            return Reply(new
            {
                schemaVersion = "openconnect.v1",
                taskId = json.RootElement.GetProperty("taskId").GetString(),
                kind = "knowledge.image.search",
                result = new { hits = new[] { hit, hit } }
            });
        });
        using var visionHttp = new HttpClient(visionHandler);
        using var indexHttp = new HttpClient(indexHandler);
        using var vision = Vision(visionHttp);
        using var client = new KnowledgeImageClient(indexHttp, new PassGate(), new Uri("https://worker.test"), "token", Key, vision,
            (_, _, _, _) => ValueTask.FromResult(true), (_, _, _, authorizedRoles, _) =>
            {
                Assert.Equal("reader", authorizedRoles[0]);
                roles[0] = "administrator";
                return ValueTask.FromResult(true);
            });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SearchAsync(Context(), "app", "collection", roles, "query"));
    }

    [Fact]
    public async Task ObjectAccessIsAuthorizedAndIntegrityCheckedBeforeWorkers()
    {
        byte[] content = [1, 2, 3];
        var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var objectId = new string('c', 64);
        var reference = new KnowledgeImageObjectReference(objectId, digest, content.Length, "version-1");
        var context = Context();
        var authorized = false;
        var visionHandler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal(content, Convert.FromBase64String(json.RootElement.GetProperty("payload").GetProperty("contentBase64").GetString()!));
            return Reply(new
            {
                schemaVersion = "openconnect.v1",
                taskId = json.RootElement.GetProperty("taskId").GetString(),
                kind = "vision.embed",
                result = new
                {
                    vector = Enumerable.Range(0, 512).Select(i => i == 0 ? 1.0 : 0.0),
                    model_id = "clip",
                    fingerprint = new string('a', 64),
                    space_id = new string('b', 64),
                    dimension = 512
                }
            });
        });
        var indexHandler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(new
            {
                schemaVersion = "openconnect.v1",
                taskId = json.RootElement.GetProperty("taskId").GetString(),
                kind = "knowledge.image.upsert",
                result = new { stored = true }
            });
        });
        using var visionHttp = new HttpClient(visionHandler);
        using var indexHttp = new HttpClient(indexHandler);
        using var vision = Vision(visionHttp);
        using var client = new KnowledgeImageClient(indexHttp, new PassGate(), new Uri("https://worker.test"), "token", Key, vision,
            (_, _, _, _) => ValueTask.FromResult(true), (_, _, _, _, _) => ValueTask.FromResult(true));
        await client.UpsertObjectAsync(context, Submission(), reference, (scope, _, seen, _) =>
        {
            Assert.Equal(context, scope);
            Assert.Same(reference, seen);
            authorized = true;
            return ValueTask.FromResult(true);
        }, (key, version, _) =>
        {
            Assert.True(authorized);
            var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(context.Tenant.Value.ToString()))).ToLowerInvariant();
            Assert.Equal($"v1/tenants/{tenantHash}/documents/{objectId}", key);
            Assert.Equal("version-1", version);
            return Task.FromResult<Stream>(new MemoryStream(content));
        });
        Assert.Equal(1, visionHandler.Calls);
        Assert.Equal(1, indexHandler.Calls);

        var badStream = new TrackingStream(new byte[] { 9, 9, 9 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UpsertObjectAsync(context, Submission(), reference,
            (_, _, _, _) => ValueTask.FromResult(true), (_, _, _) => Task.FromResult<Stream>(badStream)));
        Assert.True(badStream.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UpsertObjectAsync(context, Submission(), reference,
            (_, _, _, _) => ValueTask.FromResult(true), (_, _, _) => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3, 4 }))));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.UpsertObjectAsync(context, Submission(), reference,
            (_, _, _, _) => ValueTask.FromResult(false), (_, _, _) => throw new InvalidOperationException("must not open")));
        Assert.Equal(1, visionHandler.Calls);
        Assert.Equal(1, indexHandler.Calls);
    }

    private static KnowledgeImageSubmission Submission() => new("app", "collection", "doc", "source-v1", 1, "img", "v1");

    [Fact]
    public void NullVersionCannotRepresentAnImmutableObject()
    {
        Assert.Throws<ArgumentException>(() => new KnowledgeImageObjectReference(new string('a', 64), new string('b', 64), 1, "null"));
    }

    private static VisionEmbeddingClient Vision(HttpClient http) => new(http, "token", Key, new Gate(), new Firewall(),
        Options.Create(new PrivacyOptions()), (_, _, _) => ValueTask.FromResult(true), (_, _, _) => ValueTask.FromResult(true));
    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
    private sealed class Gate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, context.Destination == EgressDestination.Output ? content : "safe", Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "test"));
    }

    private sealed class DenyGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(false, null, [], SecurityPolicyAction.Block, "test"));
    }
    private sealed class PassGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }
    private sealed class OutputDenyGate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(context.Destination == EgressDestination.Output
                ? new EgressDecision(false, null, [], SecurityPolicyAction.Block, "test")
                : new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }
    private sealed class Firewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request);
        }
    }

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }
        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return base.DisposeAsync();
        }
    }
}
