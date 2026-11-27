using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenConnect.Abstractions;
using OpenConnect.Docs.Cloud;
using OpenConnect.Privacy;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudSecurityCoverageTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly CloudCredentialIdentity Identity = new(
        Guid.NewGuid(),
        TenantId,
        Guid.NewGuid(),
        "Development",
        new HashSet<string>());

    private const string LegacyValidPayload =
        """{"schemaVersion":"openconnect.privacy-evidence.v1","eventId":"11111111-1111-1111-1111-111111111111","tenantId":"00000000000000000000000001","destination":"Output","allowed":false,"action":"Block","categories":{"EMAIL":2}}""";

    [Fact]
    public void Legacy_payload_without_additive_receipt_metadata_is_accepted_for_backward_compatibility()
    {
        using var json = JsonDocument.Parse(LegacyValidPayload);
        var report = CloudHostPrivacyReportParser.Parse(json.RootElement, Identity);

        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), report.EventId);
        Assert.Equal("not_recorded", report.ReasonCode);
        Assert.Equal("application-host", report.EnforcementAuthority);
        Assert.Null(report.TraceId);
        Assert.Null(report.RequestId);
        Assert.Null(report.OccurredAt);
        Assert.Null(report.PolicyId);
        Assert.Null(report.PolicyRevision);
        Assert.Null(report.IntegrityDigest);
    }

    [Fact]
    public async Task Sink_payload_with_additive_metadata_and_valid_digest_is_accepted_by_parser()
    {
        var occurredAt = new DateTimeOffset(2026, 11, 22, 13, 15, 0, TimeSpan.Zero);
        var scope = new EgressContext(
            new(Ulid.Parse("00000000000000000000000001")),
            new(Ulid.NewUlid()),
            new(Ulid.NewUlid()),
            EgressDestination.ExternalModel);
        var evidence = HostPrivacyEvidence.Create(
            Guid.NewGuid(),
            scope,
            new EgressDecision(false, null, [new EntitySpan(0, 5, "EMAIL")], SecurityPolicyAction.Block, "literal_sensitive_value_detected"),
            traceId: "4bf92f3577b34da6a3ce929d0e0e4736",
            requestId: "req-42",
            occurredAt: occurredAt,
            policyId: "tenant/strict-local",
            policyRevision: "rev-7");

        CloudHostPrivacyReport? parsed = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            parsed = CloudHostPrivacyReportParser.Parse(json.RootElement, Identity);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"eventId\":\"{parsed.EventId:D}\"}}", Encoding.UTF8, "application/json"),
            };
        }));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await sink.RecordAsync(evidence);

        Assert.NotNull(parsed);
        Assert.Equal(evidence.EventId, parsed!.EventId);
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", parsed.TraceId);
        Assert.Equal("req-42", parsed.RequestId);
        Assert.Equal(occurredAt, parsed.OccurredAt);
        Assert.Equal("tenant/strict-local", parsed.PolicyId);
        Assert.Equal("rev-7", parsed.PolicyRevision);
        Assert.Equal("application-host", parsed.EnforcementAuthority);
        Assert.Equal(evidence.IntegrityDigest, parsed.IntegrityDigest);
    }

    [Fact]
    public void Parser_rejects_tampered_integrity_digest_or_modified_fields()
    {
        var eventId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var occurredAt = new DateTimeOffset(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);
        var digest = HostPrivacyEvidence.ComputeIntegrityDigest(
            eventId,
            "00000000000000000000000001",
            EgressDestination.Output,
            allowed: false,
            SecurityPolicyAction.Block,
            "sensitive_detected",
            new Dictionary<string, int> { ["EMAIL"] = 2 },
            "application-host",
            traceId: "trace-1",
            requestId: "req-1",
            occurredAt: occurredAt,
            policyId: "policy-a",
            policyRevision: "rev-1");

        var validJson = JsonSerializer.Serialize(new
        {
            schemaVersion = "openconnect.privacy-evidence.v1",
            eventId,
            tenantId = "00000000000000000000000001",
            destination = "Output",
            allowed = false,
            action = "Block",
            reasonCode = "sensitive_detected",
            categories = new Dictionary<string, int> { ["EMAIL"] = 2 },
            traceId = "trace-1",
            requestId = "req-1",
            occurredAt = occurredAt.ToString("O"),
            policyId = "policy-a",
            policyRevision = "rev-1",
            enforcementAuthority = "application-host",
            integrityDigest = digest,
        });

        using (var validDoc = JsonDocument.Parse(validJson))
        {
            var report = CloudHostPrivacyReportParser.Parse(validDoc.RootElement, Identity);
            Assert.Equal(digest, report.IntegrityDigest);
        }

        // Flip one character in digest
        var tamperedDigest = (digest[0] == 'a' ? "b" : "a") + digest[1..];
        using (var tamperedDigestDoc = JsonDocument.Parse(validJson.Replace(digest, tamperedDigest, StringComparison.Ordinal)))
        {
            Assert.Throws<ArgumentException>(() => CloudHostPrivacyReportParser.Parse(tamperedDigestDoc.RootElement, Identity));
        }

        // Tamper with traceId while keeping original digest
        using (var tamperedTraceDoc = JsonDocument.Parse(validJson.Replace("\"trace-1\"", "\"trace-2\"", StringComparison.Ordinal)))
        {
            Assert.Throws<ArgumentException>(() => CloudHostPrivacyReportParser.Parse(tamperedTraceDoc.RootElement, Identity));
        }

        // Tamper with policyRevision while keeping original digest
        using (var tamperedPolicyDoc = JsonDocument.Parse(validJson.Replace("\"rev-1\"", "\"rev-9\"", StringComparison.Ordinal)))
        {
            Assert.Throws<ArgumentException>(() => CloudHostPrivacyReportParser.Parse(tamperedPolicyDoc.RootElement, Identity));
        }

        // Reject non-allowlisted enforcementAuthority
        using (var wrongAuthorityDoc = JsonDocument.Parse(validJson.Replace("\"application-host\"", "\"cloud-worker\"", StringComparison.Ordinal)))
        {
            Assert.Throws<ArgumentException>(() => CloudHostPrivacyReportParser.Parse(wrongAuthorityDoc.RootElement, Identity));
        }
    }

    [Fact]
    public void Receipt_metadata_migration_is_strictly_additive_and_non_destructive()
    {
        var options = new DbContextOptionsBuilder<CloudControlPlaneDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var db = new CloudControlPlaneDbContext(options);
        var script = db.GetService<IMigrator>().GenerateScript(
            "20261125000300_RecoverUnconsumedMeteredClaims",
            "20261125000400_AddHostPrivacyReceiptMetadata");

        Assert.Contains("ADD COLUMN \"TraceId\"", script);
        Assert.Contains("ADD COLUMN \"RequestId\"", script);
        Assert.Contains("ADD COLUMN \"OccurredAt\"", script);
        Assert.Contains("ADD COLUMN \"PolicyId\"", script);
        Assert.Contains("ADD COLUMN \"PolicyRevision\"", script);
        Assert.Contains("ADD COLUMN \"IntegrityDigest\"", script);
        Assert.Contains("ck_host_privacy_integrity_digest", script);
        Assert.Contains("ix_host_privacy_evidence_tenant_trace", script);

        Assert.Throws<NotSupportedException>(() =>
            db.GetService<IMigrator>().GenerateScript(
                "20261125000400_AddHostPrivacyReceiptMetadata",
                "20261125000300_RecoverUnconsumedMeteredClaims"));
    }

    [Fact]
    public void Coverage_classifier_reports_no_observations_when_neither_side_has_evidence()
    {
        var coverage = CloudSecurityCoverageClassifier.Classify(0, 0, 0, 0);
        Assert.Equal(CloudSecurityCoverageClassifier.NoObservations, coverage.CoverageStatus);
        Assert.Equal(0, coverage.CloudEvidenceCount);
        Assert.Equal(0, coverage.HostEvidenceCount);
        Assert.Equal(0, coverage.CorrelatedTraceCount);
        Assert.Equal(0, coverage.CloudTracesMissingHostReceipt);
    }

    [Fact]
    public void Coverage_classifier_reports_cloud_only_when_host_evidence_is_absent()
    {
        var coverage = CloudSecurityCoverageClassifier.FromTraces(
            cloudEvidenceCount: 3,
            hostEvidenceCount: 0,
            cloudTraceIds: ["trace-a", "trace-b", "trace-a"],
            hostTraceIds: []);

        Assert.Equal(CloudSecurityCoverageClassifier.CloudOnly, coverage.CoverageStatus);
        Assert.Equal(3, coverage.CloudEvidenceCount);
        Assert.Equal(0, coverage.HostEvidenceCount);
        Assert.Equal(0, coverage.CorrelatedTraceCount);
        Assert.Equal(2, coverage.CloudTracesMissingHostReceipt);
    }

    [Fact]
    public void Coverage_classifier_reports_partial_host_coverage_when_some_cloud_traces_lack_host_receipts()
    {
        var coverage = CloudSecurityCoverageClassifier.FromTraces(
            cloudEvidenceCount: 4,
            hostEvidenceCount: 2,
            cloudTraceIds: ["trace-1", "trace-2", "trace-3"],
            hostTraceIds: ["trace-1", "trace-other"]);

        Assert.Equal(CloudSecurityCoverageClassifier.PartialHostCoverage, coverage.CoverageStatus);
        Assert.Equal(4, coverage.CloudEvidenceCount);
        Assert.Equal(2, coverage.HostEvidenceCount);
        Assert.Equal(1, coverage.CorrelatedTraceCount);
        Assert.Equal(2, coverage.CloudTracesMissingHostReceipt);
    }

    [Fact]
    public void Coverage_classifier_reports_full_host_coverage_when_all_cloud_traces_have_matching_host_receipts()
    {
        var coverage = CloudSecurityCoverageClassifier.FromTraces(
            cloudEvidenceCount: 3,
            hostEvidenceCount: 3,
            cloudTraceIds: ["trace-1", "trace-2"],
            hostTraceIds: ["trace-1", "trace-2", "trace-3"]);

        Assert.Equal(CloudSecurityCoverageClassifier.FullHostCoverage, coverage.CoverageStatus);
        Assert.Equal(3, coverage.CloudEvidenceCount);
        Assert.Equal(3, coverage.HostEvidenceCount);
        Assert.Equal(2, coverage.CorrelatedTraceCount);
        Assert.Equal(0, coverage.CloudTracesMissingHostReceipt);
    }

    [Fact]
    public async Task Coverage_endpoint_enforces_authentication_roles_and_returns_problem_details_with_trace_id()
    {
        const string issuer = "https://idp.example";
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity());
        var authenticated = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "user-1"), new Claim("iss", issuer)],
            "TestAuth"));

        var unauthContext = new DefaultHttpContext { TraceIdentifier = "trace-unauth" };
        var unauthResult = await CloudHostPrivacyObservationEndpoints.HandleCoverageAsync(
            TenantId, unauthenticated, issuer, unauthContext,
            (_, _, _) => throw new InvalidOperationException("Should not query when unauthenticated."),
            CancellationToken.None);
        AssertProblem(unauthResult, StatusCodes.Status401Unauthorized, "unauthorized", "trace-unauth");
        Assert.Equal("no-store", unauthContext.Response.Headers.CacheControl.ToString());

        var badTenantContext = new DefaultHttpContext { TraceIdentifier = "trace-bad-tenant" };
        var badTenantResult = await CloudHostPrivacyObservationEndpoints.HandleCoverageAsync(
            Guid.Empty, authenticated, issuer, badTenantContext,
            (_, _, _) => throw new InvalidOperationException("Should not query for empty tenant."),
            CancellationToken.None);
        AssertProblem(badTenantResult, StatusCodes.Status400BadRequest, "invalid_request", "trace-bad-tenant");

        var forbiddenContext = new DefaultHttpContext { TraceIdentifier = "trace-forbidden" };
        var forbiddenResult = await CloudHostPrivacyObservationEndpoints.HandleCoverageAsync(
            TenantId, authenticated, issuer, forbiddenContext,
            (_, _, _) => Task.FromResult(new CloudSecurityCoverageLookupResult("forbidden")),
            CancellationToken.None);
        AssertProblem(forbiddenResult, StatusCodes.Status403Forbidden, "forbidden", "trace-forbidden");

        var notFoundContext = new DefaultHttpContext { TraceIdentifier = "trace-notfound" };
        var notFoundResult = await CloudHostPrivacyObservationEndpoints.HandleCoverageAsync(
            TenantId, authenticated, issuer, notFoundContext,
            (_, _, _) => Task.FromResult(new CloudSecurityCoverageLookupResult("not_found")),
            CancellationToken.None);
        AssertProblem(notFoundResult, StatusCodes.Status404NotFound, "not_found", "trace-notfound");

        var okContext = new DefaultHttpContext { TraceIdentifier = "trace-ok" };
        var expected = CloudSecurityCoverageClassifier.Classify(5, 2, 2, 3);
        var okResult = await CloudHostPrivacyObservationEndpoints.HandleCoverageAsync(
            TenantId, authenticated, issuer, okContext,
            (_, _, _) => Task.FromResult(new CloudSecurityCoverageLookupResult("succeeded", expected)),
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(okResult).StatusCode);
        var payload = Assert.IsType<CloudSecurityCoverage>(Assert.IsAssignableFrom<IValueHttpResult>(okResult).Value);
        Assert.Equal(expected, payload);
        Assert.Equal("no-store", okContext.Response.Headers.CacheControl.ToString());
    }

    private static void AssertProblem(IResult result, int expectedStatus, string expectedCode, string expectedTraceId)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;
        Assert.Equal(expectedStatus, status);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        var problem = Assert.IsType<ProblemDetails>(value);
        Assert.Equal(expectedCode, problem.Extensions["code"]);
        Assert.Equal(expectedTraceId, problem.Extensions["traceId"]);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
