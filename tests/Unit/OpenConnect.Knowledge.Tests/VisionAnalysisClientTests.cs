using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class VisionAnalysisClientTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
    private static IOptions<PrivacyOptions> OptionsValue() => Options.Create(new PrivacyOptions());
    private static IPrivacyFirewall Firewall(IOptions<PrivacyOptions> options) => new PrivacyFirewall(options, new PolicyEngine(new RulesSecurityDetector()));

    [Fact]
    public async Task AuthorizesSnapshotAndSignsTenantBoundRequest()
    {
        var original = new byte[] { 1, 2, 3 }; var context = Context(); ReadOnlyMemory<byte> captured = default;
        var handler = new Handler(async request =>
        {
            var body = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(Convert.ToHexString(HMACSHA256.HashData(Key, body)).ToLowerInvariant(), request.Headers.GetValues("X-OpenConnect-Signature").Single());
            using var json = JsonDocument.Parse(body);
            Assert.Equal(context.Tenant.Value.ToString(), json.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal(new byte[] { 1, 2, 3 }, Convert.FromBase64String(json.RootElement.GetProperty("payload").GetProperty("contentBase64").GetString()!));
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, "tree", 0.8, 1);
        });
        using var http = new HttpClient(handler); var options = OptionsValue();
        using var client = new VisionAnalysisClient(http, "token", Key, Firewall(options), options, (_, image, _) =>
        { captured = image; original[0] = 9; return ValueTask.FromResult(true); });
        using var result = await client.AnalyzeAsync(context, original, topK: 1);
        Assert.Equal("tree", result.RootElement.GetProperty("result").GetProperty("classifications")[0].GetProperty("label").GetString());
        Assert.Equal(new byte[] { 0, 0, 0 }, captured.ToArray());
    }

    [Fact]
    public async Task DeniedOrDisabledPrivacyNeverSendsImage()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler); var options = OptionsValue();
        using var denied = new VisionAnalysisClient(http, "token", Key, Firewall(options), options, (_, _, _) => ValueTask.FromResult(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.AnalyzeAsync(Context(), new byte[] { 1 }, 1));
        var disabled = Options.Create(new PrivacyOptions { Mode = FirewallMode.Disabled });
        using var blocked = new VisionAnalysisClient(http, "token", Key, Firewall(disabled), disabled, (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => blocked.AnalyzeAsync(Context(), new byte[] { 1 }, 1));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Disabled_decision_prevents_image_analysis_dispatch()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler);
        using var client = new VisionAnalysisClient(http, "token", Key, new DisabledDecisionFirewall(), OptionsValue(), (_, _, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.AnalyzeAsync(Context(), new byte[] { 1 }, 1));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CancellationDuringAuthorizationStopsDispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var http = new HttpClient(handler); var options = OptionsValue();
        using var client = new VisionAnalysisClient(http, "token", Key, Firewall(options), options, (_, _, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(true);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.AnalyzeAsync(Context(), new byte[] { 1 }, 1, cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("tree", 1.1, 1, false)]
    [InlineData("tree", 0.8, 1001, false)]
    [InlineData("person@example.test", 0.8, 1, true)]
    public async Task RejectsInvalidOrSensitiveOutput(string label, double score, int index, bool sensitive)
    {
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, label, score, index);
        });
        using var http = new HttpClient(handler); var options = OptionsValue();
        using var client = new VisionAnalysisClient(http, "token", Key, Firewall(options), options, (_, _, _) => ValueTask.FromResult(true));
        if (sensitive) await Assert.ThrowsAsync<PrivacyBlockedException>(() => client.AnalyzeAsync(Context(), new byte[] { 1 }, 1));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnalyzeAsync(Context(), new byte[] { 1 }, 1));
    }

    [Theory]
    [InlineData("valid", true)]
    [InlineData("invalid", false)]
    public async Task SharedResponseFixturesMatchClientValidation(string folder, bool valid)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OpenConnect.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var fixture = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "contracts", "fixtures", folder,
            "vision-analyze-response.json"));
        var handler = new Handler(async request =>
        {
            using var command = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var response = JsonNode.Parse(fixture)!;
            response["taskId"] = command.RootElement.GetProperty("taskId").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString()) };
        });
        using var http = new HttpClient(handler); var options = OptionsValue();
        using var client = new VisionAnalysisClient(http, "token", Key, Firewall(options), options,
            (_, _, _) => ValueTask.FromResult(true));
        if (valid)
        {
            using var result = await client.AnalyzeAsync(Context(), new byte[] { 1 }, 2);
            Assert.Equal(2, result.RootElement.GetProperty("result").GetProperty("classifications").GetArrayLength());
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnalyzeAsync(Context(), new byte[] { 1 }, 2));
    }

    [Fact]
    public async Task ObjectAnalysisAuthorizesExactTenantVersionAndChecksIntegrity()
    {
        var image = new byte[] { 1, 2, 3 };
        var context = Context();
        var digest = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        var reference = new KnowledgeImageObjectReference(new string('a', 64), digest, image.Length, "version-1");
        var handler = new Handler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Reply(json.RootElement.GetProperty("taskId").GetString()!, "tree", 0.8, 1);
        });
        using var http = new HttpClient(handler); var options = OptionsValue();
        using var client = new VisionAnalysisClient(http, "token", Key, Firewall(options), options, (_, _, _) => ValueTask.FromResult(true));
        var opened = false;
        Task<Stream> Open(string key, string version, CancellationToken _)
        {
            opened = true;
            var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(context.Tenant.Value.ToString()))).ToLowerInvariant();
            Assert.Equal($"v1/tenants/{tenantHash}/documents/{reference.ObjectId}", key);
            Assert.Equal("version-1", version);
            return Task.FromResult<Stream>(new MemoryStream(image));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.AnalyzeObjectAsync(context, reference, (_, _, _) => ValueTask.FromResult(false), Open, 1));
        Assert.False(opened);
        using var result = await client.AnalyzeObjectAsync(context, reference, (_, _, _) => ValueTask.FromResult(true), Open, 1);
        Assert.True(opened);
        var badReference = new KnowledgeImageObjectReference(reference.ObjectId, new string('b', 64), image.Length, "version-1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnalyzeObjectAsync(context, badReference, (_, _, _) => ValueTask.FromResult(true), Open, 1));
        Assert.Equal(1, handler.Calls);
    }

    private static HttpResponseMessage Reply(string taskId, string label, double score, int index) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { schemaVersion = "openconnect.v1", taskId, kind = "vision.analyze", result = new {
            model_id = "fixture", model_fingerprint = new string('a', 64), taxonomy_version = "fixture-taxonomy", preprocessing_version = "imagenet-center224-v1",
            width = 100, height = 100, classifications = new[] { new { label, class_index = index, score } } } }), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return send(request); }
    }
}
