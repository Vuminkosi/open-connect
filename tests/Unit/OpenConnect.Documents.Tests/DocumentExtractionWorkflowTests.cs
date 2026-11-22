using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Documents.Tests;

public sealed class DocumentExtractionWorkflowTests
{
    private static readonly byte[] Key = Enumerable.Repeat((byte)0xab, 32).ToArray();
    private static EgressContext Context() => new(new(Ulid.NewUlid()), new(Ulid.NewUlid()),
        new(Ulid.NewUlid()), EgressDestination.Worker);

    [Fact]
    public async Task Authorized_pipeline_maps_unicode_citations_to_source_pages()
    {
        var context = Context();
        var documentAuthorizations = 0;
        var extractionAuthorizations = 0;
        using var docHttp = new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            var taskId = body.RootElement.GetProperty("taskId").GetString();
            return Json(new
            {
                schemaVersion = "openconnect.v1",
                taskId,
                kind = "documents.ocr",
                result = new
                {
                    pages = new[] {
                Page("🙂", 1), Page("Total: 42", 9) }
                }
            });
        }));
        using var extractHttp = new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            Assert.Equal("🙂\nTotal: 42", body.RootElement.GetProperty("payload").GetProperty("document").GetString());
            var taskId = body.RootElement.GetProperty("taskId").GetString();
            return Json(new
            {
                schemaVersion = "openconnect.v1",
                taskId,
                kind = "documents.extract",
                result = new
                {
                    fields = new { total = new { value = 42, start = 9, end = 11 } },
                    warnings = new[] { "source_support_not_semantic_validation" },
                    schema_valid = true,
                    confidence_method = "not_estimated"
                }
            });
        }));
        var firewall = new Firewall();
        using var documents = new DocumentWorkerClient(docHttp, "token", Key, firewall, Options.Create(new PrivacyOptions()),
            (scope, kind, _, _) => { documentAuthorizations++; return ValueTask.FromResult(scope == context && kind == "documents.ocr"); });
        using var extractor = new ExtractionWorkerClient(extractHttp, "token", Key, firewall,
            (scope, _, _, _) => { extractionAuthorizations++; return ValueTask.FromResult(scope == context); });
        using var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["total"],"properties":{"total":{"type":"number"}}}""");
        using var result = await new DocumentExtractionWorkflow(documents, extractor).ExecuteAsync(context, new byte[] { 1, 2 }, schema.RootElement);
        var citation = Assert.IsType<DocumentFieldCitation>(result.Citations["total"]);
        Assert.Equal(1, citation.PageIndex);
        Assert.Equal(7, citation.Start);
        Assert.Equal(9, citation.End);
        Assert.Equal(1, documentAuthorizations);
        Assert.Equal(1, extractionAuthorizations);
    }

    [Fact]
    public async Task Rejects_citation_that_crosses_page_boundaries()
    {
        var context = Context();
        using var docHttp = new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Json(new
            {
                schemaVersion = "openconnect.v1",
                taskId = body.RootElement.GetProperty("taskId").GetString(),
                kind = "documents.ocr",
                result = new { pages = new[] { Page("🙂", 1), Page("Total: 42", 9) } }
            });
        }));
        using var extractHttp = new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Json(new
            {
                schemaVersion = "openconnect.v1",
                taskId = body.RootElement.GetProperty("taskId").GetString(),
                kind = "documents.extract",
                result = new
                {
                    fields = new { value = new { value = "🙂\nTotal", start = 0, end = 7 } },
                    warnings = new[] { "source_support_not_semantic_validation" },
                    schema_valid = true,
                    confidence_method = "not_estimated"
                }
            });
        }));
        var firewall = new Firewall();
        using var documents = new DocumentWorkerClient(docHttp, "token", Key, firewall, Options.Create(new PrivacyOptions()),
            (_, _, _, _) => ValueTask.FromResult(true));
        using var extractor = new ExtractionWorkerClient(extractHttp, "token", Key, firewall, (_, _, _, _) => ValueTask.FromResult(true));
        using var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["value"],"properties":{"value":{"type":"string"}}}""");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new DocumentExtractionWorkflow(documents, extractor)
            .ExecuteAsync(context, new byte[] { 1 }, schema.RootElement));
        Assert.DoesNotContain("Total", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorization_denial_prevents_downstream_worker_calls(bool denyExtraction)
    {
        var docCalls = 0;
        var extractionCalls = 0;
        using var docHttp = new HttpClient(new Handler(async request =>
        {
            docCalls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Json(new
            {
                schemaVersion = "openconnect.v1",
                taskId = body.RootElement.GetProperty("taskId").GetString(),
                kind = "documents.ocr",
                result = new { pages = new[] { Page("Total: 42", 9) } }
            });
        }));
        using var extractHttp = new HttpClient(new Handler(_ => { extractionCalls++; throw new InvalidOperationException("must not send"); }));
        var firewall = new Firewall();
        using var documents = new DocumentWorkerClient(docHttp, "token", Key, firewall, Options.Create(new PrivacyOptions()),
            (_, _, _, _) => ValueTask.FromResult(denyExtraction));
        using var extractor = new ExtractionWorkerClient(extractHttp, "token", Key, firewall, (_, _, _, _) => ValueTask.FromResult(false));
        using var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["total"],"properties":{"total":{"type":"number"}}}""");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new DocumentExtractionWorkflow(documents, extractor)
            .ExecuteAsync(Context(), new byte[] { 1 }, schema.RootElement));
        Assert.Equal(denyExtraction ? 1 : 0, docCalls);
        Assert.Equal(0, extractionCalls);
    }

    [Fact]
    public async Task Snapshots_schema_before_asynchronous_ocr()
    {
        var schema = JsonDocument.Parse("""{"type":"object","additionalProperties":false,"required":["total"],"properties":{"total":{"type":"number"}}}""");
        using var docHttp = new HttpClient(new Handler(async request =>
        {
            schema.Dispose();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Json(new
            {
                schemaVersion = "openconnect.v1",
                taskId = body.RootElement.GetProperty("taskId").GetString(),
                kind = "documents.ocr",
                result = new { pages = new[] { Page("Total: 42", 9) } }
            });
        }));
        using var extractHttp = new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync());
            return Json(new
            {
                schemaVersion = "openconnect.v1",
                taskId = body.RootElement.GetProperty("taskId").GetString(),
                kind = "documents.extract",
                result = new
                {
                    fields = new { total = new { value = 42, start = 7, end = 9 } },
                    warnings = new[] { "source_support_not_semantic_validation" },
                    schema_valid = true,
                    confidence_method = "not_estimated"
                }
            });
        }));
        var firewall = new Firewall();
        using var documents = new DocumentWorkerClient(docHttp, "token", Key, firewall, Options.Create(new PrivacyOptions()),
            (_, _, _, _) => ValueTask.FromResult(true));
        using var extractor = new ExtractionWorkerClient(extractHttp, "token", Key, firewall, (_, _, _, _) => ValueTask.FromResult(true));
        using var result = await new DocumentExtractionWorkflow(documents, extractor).ExecuteAsync(Context(), new byte[] { 1 }, schema.RootElement);
        Assert.Equal(0, result.Citations["total"]!.PageIndex);
    }

    private static object Page(string text, int length) => new
    {
        text,
        width = 10,
        height = 10,
        model_id = "fixture",
        model_fingerprint = new string('a', 64),
        language = "eng",
        spans = new[] { new { text, start = 0, end = length, box = new[] { 0.0, 0.0, 1.0, 1.0 }, confidence = .9 } }
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class Firewall : IPrivacyFirewall
    {
        public bool Enabled => true;
        public ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new EgressDecision(true, content, [], SecurityPolicyAction.Allow, "test"));
    }
}
