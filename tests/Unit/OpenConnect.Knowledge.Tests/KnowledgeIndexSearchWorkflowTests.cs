using System.Net;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Knowledge.Tests;

public sealed class KnowledgeIndexSearchWorkflowTests
{
    [Theory]
    [InlineData("completed", true)]
    [InlineData("dead", false)]
    public async Task SearchesOnlyAfterIndexCompletion(string state, bool completes)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var submission = new KnowledgeIndexSubmission(Guid.NewGuid(), "app", "collection", "doc", "v1",
            new string('a', 64), new string('b', 64), new string('c', 64), 42, "version", 0, ["reader"], []);
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            using var command = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var root = command.RootElement;
            var kind = root.GetProperty("kind").GetString()!;
            calls.Add(kind);
            object result = kind switch
            {
                "knowledge.index" => new { job_id = submission.JobId, created = true },
                "knowledge.index.status" => new { state, attempts = 1, result_generation = completes ? (long?)1 : null, retry_at = (string?)null },
                "knowledge.search" => new { model_id = (string?)null, fingerprint = (string?)null, score_type = "raw_logit", warnings = Array.Empty<string>(), hits = Array.Empty<object>() },
                _ => throw new InvalidOperationException()
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                new { schemaVersion = "openconnect.v1", taskId = root.GetProperty("taskId").GetString(), kind, result })) };
        }));
        var gate = new Gate(); var key = Enumerable.Repeat((byte)1, 32).ToArray(); var endpoint = new Uri("https://worker.test");
        using var index = new KnowledgeIndexClient(http, gate, endpoint, "token", key, (_, _, _) => ValueTask.FromResult(true),
            (_, _, _, _, _, _) => ValueTask.FromResult(true));
        using var search = new KnowledgeSearchClient(http, gate, endpoint, "token", key,
            (_, _, _, _, _) => ValueTask.FromResult(true));
        var workflow = new KnowledgeIndexSearchWorkflow(index, search);
        Task<KnowledgeEmbeddingResult> Embed(EgressContext scope, string query, CancellationToken cancellationToken) => Task.FromResult(
            new KnowledgeEmbeddingResult([new KnowledgeEmbedding([1f], false, 1)], "model", new string('a', 64), 1, new string('a', 64)));
        if (completes)
        {
            using var result = await workflow.IndexAndSearchAsync(context, submission, [], "query", Embed);
            Assert.Equal("knowledge.search", result.RootElement.GetProperty("kind").GetString());
            Assert.Equal(new[] { "knowledge.index", "knowledge.index.status", "knowledge.search" }, calls);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.IndexAndSearchAsync(context, submission, [], "query", Embed));
            Assert.Equal(new[] { "knowledge.index", "knowledge.index.status" }, calls);
        }
    }

    [Fact]
    public async Task InvalidRolesAreRejectedBeforeEnqueue()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("worker called")));
        var key = Enumerable.Repeat((byte)1, 32).ToArray(); var endpoint = new Uri("https://worker.test");
        using var index = new KnowledgeIndexClient(http, new Gate(), endpoint, "token", key,
            (_, _, _) => ValueTask.FromResult(true), (_, _, _, _, _, _) => ValueTask.FromResult(true));
        using var search = new KnowledgeSearchClient(http, new Gate(), endpoint, "token", key,
            (_, _, _, _, _) => ValueTask.FromResult(true));
        var submission = new KnowledgeIndexSubmission(Guid.NewGuid(), "app", "collection", "doc", "v1",
            new string('a', 64), new string('b', 64), new string('c', 64), 42, "version", 0, ["reader"], []);
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var workflow = new KnowledgeIndexSearchWorkflow(index, search);
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.IndexAndSearchAsync(context, submission, ["bad\u0000role"],
            "query", (_, _, _) => throw new InvalidOperationException("embedding called")));
    }

    private sealed class Gate : IEgressGate
    {
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "test"));
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
