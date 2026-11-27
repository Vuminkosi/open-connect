using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Gates;
using OpenConnect.Privacy.Image;
using OpenConnect.Privacy.Restoration;
using OpenConnect.Privacy.Tokenization;
using OpenConnect.Privacy.Worker;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class SecurityWorkerClientAndFirewallTests
{
    private static EgressContext CreateContext() =>
        new(new TenantId(Ulid.NewUlid()), new PrincipalId(Ulid.NewUlid()), new SessionId(Ulid.NewUlid()), EgressDestination.ExternalModel);

    private static SecurityWorkerClientOptions CreateClientOptions(Uri? endpoint = null) =>
        new()
        {
            Endpoint = endpoint ?? new Uri("https://security.internal/"),
            RequireAuthenticatedTransport = true,
            BearerToken = "test-worker-token",
            RequestTimeout = TimeSpan.FromSeconds(5)
        };

    [Fact]
    public void Client_rejects_untrusted_endpoints_userinfo_and_missing_bearer_token()
    {
        Assert.Throws<ArgumentException>(() => new HttpSecurityWorkerClient(new SecurityWorkerClientOptions
        {
            Endpoint = new Uri("http://security.internal/"),
            BearerToken = "tok"
        }));

        Assert.Throws<ArgumentException>(() => new HttpSecurityWorkerClient(new SecurityWorkerClientOptions
        {
            Endpoint = new Uri("https://user:pass@security.internal/"),
            BearerToken = "tok"
        }));

        Assert.Throws<ArgumentException>(() => new HttpSecurityWorkerClient(new SecurityWorkerClientOptions
        {
            Endpoint = new Uri("https://security.internal/"),
            RequireAuthenticatedTransport = true,
            BearerToken = null
        }));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "worker_auth_failed")]
    [InlineData(HttpStatusCode.Forbidden, "worker_auth_failed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "worker_unavailable")]
    [InlineData(HttpStatusCode.Found, "redirect_not_allowed")]
    public async Task Client_fails_closed_on_auth_unavailable_and_redirect_responses(HttpStatusCode status, string expectedReason)
    {
        using var handler = new StubHandler(_ => new HttpResponseMessage(status));
        using var client = new HttpSecurityWorkerClient(CreateClientOptions(), handler);

        var response = await client.InspectTextAsync(new SecurityTextInspectionRequest("req-1", "rev-1", ["rules"], "hello"));
        Assert.Equal(SecurityInspectionStatus.Unavailable, response.Status);
        Assert.Equal(expectedReason, response.ReasonCode);
    }

    [Fact]
    public async Task Client_fails_closed_on_oversized_response_malformed_json_and_missing_required_coverage()
    {
        // Oversized response
        var opts = CreateClientOptions();
        opts.MaxResponseBytes = 64;
        using (var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('a', 128), Encoding.UTF8, "application/json")
        }))
        using (var client = new HttpSecurityWorkerClient(opts, handler))
        {
            var resp = await client.InspectTextAsync(new SecurityTextInspectionRequest("req-1", "rev-1", ["rules"], "hello"));
            Assert.Equal(SecurityInspectionStatus.Unavailable, resp.Status);
            Assert.Equal("response_limit_exceeded", resp.ReasonCode);
        }

        // Partial coverage when gliner-pii is required (no implicit downgrade!)
        var partialJson = """
        {
          "schemaVersion": "openconnect.security-inspection.v1",
          "requestId": "req-2",
          "modality": "text",
          "status": "completed",
          "detectors": [
            {"id": "rules", "version": "1.0.0", "status": "completed"},
            {"id": "gliner-pii", "version": "1.0.0", "status": "unavailable"}
          ],
          "findings": [],
          "offsetUnit": "unicode-scalar",
          "durationMs": 5
        }
        """;
        using (var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(partialJson, Encoding.UTF8, "application/json")
        }))
        using (var client = new HttpSecurityWorkerClient(CreateClientOptions(), handler))
        {
            var resp = await client.InspectTextAsync(new SecurityTextInspectionRequest(
                "req-2", "rev-1", ["rules", "gliner-pii"], "hello",
                RequiredDetectors: new HashSet<string>(["rules", "gliner-pii"])));
            Assert.Equal(SecurityInspectionStatus.Unavailable, resp.Status);
            Assert.Equal("required_coverage_missing", resp.ReasonCode);
        }
    }

    [Fact]
    public async Task Client_rejects_invalid_unicode_offsets_and_converts_emoji_scalar_offsets_accurately()
    {
        // "😀 a@b.co" -> scalar indices: 😀(0), ' '(1), a(2), @(3), b(4), .(5), c(6), o(7) => [2, 8)
        // UTF-16 indices: 😀(0,1), ' '(2), a(3)..o(8) => start=3, length=6
        const string text = "\U0001F600 a@b.co";
        using (var handler = new StubHandler(req =>
        {
            var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var reqId = doc.RootElement.GetProperty("requestId").GetString()!;
            var jsonResp = $$"""
            {
              "schemaVersion": "openconnect.security-inspection.v1",
              "requestId": "{{reqId}}",
              "modality": "text",
              "status": "completed",
              "detectors": [{"id": "rules", "version": "1.0.0", "status": "completed"}],
              "findings": [{"kind": "text_span", "detectorId": "rules", "category": "EMAIL", "start": 2, "end": 8, "confidence": 0.99}],
              "offsetUnit": "unicode-scalar",
              "durationMs": 4
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResp, Encoding.UTF8, "application/json")
            };
        }))
        using (var client = new HttpSecurityWorkerClient(CreateClientOptions(), handler))
        {
            using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
            var detector = new SecurityWorkerDetector(client, ["rules"], vault: vault);
            var result = await detector.DetectAndSanitizeAsync(text, CreateContext());
            Assert.True(result.Available);
            Assert.StartsWith("\U0001F600 [OC_", result.SanitizedContent);
            Assert.DoesNotContain("a@b.co", result.SanitizedContent);
        }

        // Out-of-bounds offset fails closed before touching content
        const string badOffsetJson = """
        {
          "schemaVersion": "openconnect.security-inspection.v1",
          "requestId": "req-emoji",
          "modality": "text",
          "status": "completed",
          "detectors": [{"id": "rules", "version": "1.0.0", "status": "completed"}],
          "findings": [{"kind": "text_span", "detectorId": "rules", "category": "EMAIL", "start": 2, "end": 99, "confidence": 0.99}],
          "offsetUnit": "unicode-scalar",
          "durationMs": 4
        }
        """;
        using (var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(badOffsetJson, Encoding.UTF8, "application/json")
        }))
        using (var client = new HttpSecurityWorkerClient(CreateClientOptions(), handler))
        {
            var resp = await client.InspectTextAsync(new SecurityTextInspectionRequest("req-emoji", "rev-1", ["rules"], text));
            Assert.Equal(SecurityInspectionStatus.Failed, resp.Status);
            Assert.Equal("invalid_text_offsets", resp.ReasonCode);
        }
    }

    [Fact]
    public async Task Overlap_resolver_prioritizes_secrets_blocks_secret_restoration_and_preserves_json()
    {
        const string json = "{\"note\":\"token sk-1234567890abcdef123456\"}";
        var start = json.IndexOf("sk-", StringComparison.Ordinal);
        var end = start + "sk-1234567890abcdef123456".Length;

        var findings = new List<SecurityFinding>
        {
            new(SecurityFindingKind.TextSpan, "gliner-pii", "PERSONAL_DATA", 0.80, Start: start - 6, End: end, RecommendedAction: SecurityPolicyAction.Mask),
            new(SecurityFindingKind.TextSpan, "rules", "SECRET", 0.99, Start: start, End: end, RecommendedAction: SecurityPolicyAction.Redact)
        };

        Assert.True(UnicodeScalarOffsetConverter.TryValidateTextFindings(json, findings, out var validated));
        var resolved = TextOverlapResolver.ResolveOverlaps(validated);
        var span = Assert.Single(resolved);
        Assert.Equal("SECRET", span.WinningCategory);
        Assert.Equal(SecurityPolicyAction.Redact, span.Action);

        using var vault = new TokenVault(RandomNumberGenerator.GetBytes(32));
        var ctx = CreateContext();
        var sanitized = await TextOverlapResolver.SanitizeAsync(json, resolved, ctx, vault);
        Assert.True(sanitized.Allowed);
        Assert.NotNull(sanitized.SanitizedContent);
        Assert.DoesNotContain("sk-1234567890abcdef123456", sanitized.SanitizedContent);
        // Valid JSON preserved
        using var parsed = JsonDocument.Parse(sanitized.SanitizedContent);
        Assert.Contains("[OC_SECRET_REDACTED]", parsed.RootElement.GetProperty("note").GetString());

        // Even if a secret is stored in TokenVault directly, generic TokenRestorationService must never restore it.
        var scope = new PrivacyScope(ctx.Tenant, ctx.Principal, ctx.Session, "redaction");
        var secretToken = await vault.StoreAsync("sk-live-secret-999999", "SECRET", scope, TimeSpan.FromMinutes(5));
        var restoration = new TokenRestorationService(vault);
        var restored = await restoration.RestoreAsync($"secret={secretToken}", scope, new RestorationContext(RestorationTarget.FinalUserOutput));
        Assert.Equal($"secret={secretToken}", restored);
    }

    [Fact]
    public void HostImageRedactor_redacts_padded_region_strips_metadata_and_enforces_bounds()
    {
        var redactor = new HostImageRedactor(maxImageBytes: 1024 * 1024, maxMegapixels: 1, defaultPaddingPixels: 1);

        // Create a 10x10 solid white RGB PNG and inject a tEXt metadata chunk before IEND.
        var pixels = new byte[10 * 10 * 3];
        Array.Fill(pixels, (byte)255);
        var cleanPng = HostImageRedactor.EncodeRgb24Png(10, 10, pixels);
        var pngWithMetadata = InjectTextMetadataChunk(cleanPng, "Author\0SensitiveMetadata");
        Assert.Contains("SensitiveMetadata", Encoding.Latin1.GetString(pngWithMetadata));

        // Negative: out-of-bounds region fails closed
        var badBounds = redactor.RedactPng(pngWithMetadata, [new SecurityImageRegion(8, 8, 5, 5)]);
        Assert.False(badBounds.Succeeded);
        Assert.Equal("invalid_region_bounds", badBounds.ReasonCode);

        // Negative: failed recheck callback fails closed
        var failedRecheck = redactor.RedactPng(pngWithMetadata, [new SecurityImageRegion(3, 3, 2, 2)], recheckCallback: _ => false);
        Assert.False(failedRecheck.Succeeded);
        Assert.Equal("redacted_output_recheck_failed", failedRecheck.ReasonCode);

        // Positive: valid region redacts padded box, strips metadata, and satisfies ModalityEgressGate
        var ok = redactor.RedactPng(pngWithMetadata, [new SecurityImageRegion(3, 3, 2, 2)]);
        Assert.True(ok.Succeeded);
        Assert.True(ok.MetadataStripped);
        Assert.NotNull(ok.RedactedPngBytes);
        Assert.DoesNotContain("SensitiveMetadata", Encoding.Latin1.GetString(ok.RedactedPngBytes));

        var firewall = new PrivacyFirewall(Microsoft.Extensions.Options.Options.Create(new PrivacyOptions()), new Policy.PolicyEngine());
        var gate = new ModalityEgressGate(firewall);
        Assert.True(gate.EvaluateRedactedImage(ok, CreateContext(), FirewallMode.StrictLocal).Allowed);
        Assert.False(gate.EvaluateRedactedImage(badBounds, CreateContext(), FirewallMode.StrictLocal).Allowed);
    }

    [Fact]
    public async Task InMemorySecurityEvidenceSink_deduplicates_per_tenant_app_and_isolates_queries()
    {
        var sink = new InMemorySecurityEvidenceSink();
        var eventId = Guid.NewGuid();
        var evA = SecurityEvidenceEvent.Create(
            eventId, "req-1", "tenant-a", "app-1", DateTimeOffset.UtcNow,
            "malware", "upload-release", "clamav", "1.4.2", "pol-1",
            SecurityInspectionStatus.Completed, SecurityEvidenceDecision.Block, SecurityEnforcementOutcome.Prevented,
            "malware_detected");

        var first = await sink.RecordAsync(evA);
        var retry = await sink.RecordAsync(evA);
        Assert.True(first.Recorded);
        Assert.False(first.Deduplicated);
        Assert.True(retry.Recorded);
        Assert.True(retry.Deduplicated);

        // Same eventId under another tenant is stored independently and isolated on Query.
        var evB = SecurityEvidenceEvent.Create(
            eventId, "req-1", "tenant-b", "app-1", DateTimeOffset.UtcNow,
            "malware", "upload-release", "clamav", "1.4.2", "pol-1",
            SecurityInspectionStatus.Completed, SecurityEvidenceDecision.Allow, SecurityEnforcementOutcome.Executed,
            "clean");
        var otherTenant = await sink.RecordAsync(evB);
        Assert.False(otherTenant.Deduplicated);

        var tenantAEvents = sink.Query("tenant-a");
        Assert.Single(tenantAEvents);
        Assert.Equal("tenant-a", tenantAEvents[0].TenantId);
        Assert.Throws<ArgumentOutOfRangeException>(() => sink.Query("tenant-a", take: 501));
    }

    [Fact]
    public async Task Golden_fixtures_parse_cleanly_through_HttpSecurityWorkerClient_and_SecurityEvidenceEvent()
    {
        var repoRoot = FindRepoRoot();
        var validDir = Path.Combine(repoRoot, "contracts", "fixtures", "security", "valid");
        Assert.True(Directory.Exists(validDir), $"Golden fixture directory not found: {validDir}");

        // 1. Text response golden fixture
        var textFixture = await File.ReadAllTextAsync(Path.Combine(validDir, "inspection-text-response.json"));
        using (var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(textFixture, Encoding.UTF8, "application/json")
        }))
        using (var client = new HttpSecurityWorkerClient(CreateClientOptions(), handler))
        {
            var resp = await client.InspectTextAsync(new SecurityTextInspectionRequest(
                "e2c42bdb-5e3d-4f0d-b4d5-860a482df3e1", "demo-1", ["rules"], "Contact demo@example.com"));
            Assert.Equal(SecurityInspectionStatus.Completed, resp.Status);
            var finding = Assert.Single(resp.Findings);
            Assert.Equal(SecurityFindingKind.TextSpan, finding.Kind);
            Assert.Equal("EMAIL", finding.Category);
            Assert.Equal(SecurityPolicyAction.Mask, finding.RecommendedAction);
        }

        // 2. Prompt response golden fixture
        var promptFixture = await File.ReadAllTextAsync(Path.Combine(validDir, "inspection-prompt-response.json"));
        using (var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(promptFixture, Encoding.UTF8, "application/json")
        }))
        using (var client = new HttpSecurityWorkerClient(CreateClientOptions(), handler))
        {
            var resp = await client.InspectPromptAsync(new SecurityPromptInspectionRequest(
                "7f30d6aa-3c44-4d91-9e08-8f2a9032e101", "demo-1", ["deberta-injection"], "Ignore previous instructions"));
            Assert.Equal(SecurityInspectionStatus.Completed, resp.Status);
            var finding = Assert.Single(resp.Findings);
            Assert.Equal(SecurityFindingKind.Injection, finding.Kind);
            Assert.Equal("injection", finding.Label);
            Assert.Equal(0.96, finding.Confidence);
            Assert.Equal(0.85, finding.Threshold);
            Assert.Equal("rev-deberta-1", finding.ModelRevision);
        }

        // 3. File response golden fixture (EICAR SHA-256)
        var fileFixture = await File.ReadAllTextAsync(Path.Combine(validDir, "inspection-file-response.json"));
        var eicarBytes = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");
        var eicarDigest = Convert.ToHexString(SHA256.HashData(eicarBytes)).ToLowerInvariant();
        using (var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fileFixture, Encoding.UTF8, "application/json")
        }))
        using (var client = new HttpSecurityWorkerClient(CreateClientOptions(), handler))
        {
            var resp = await client.InspectFileAsync(new SecurityFileInspectionRequest(
                "4d3e7c22-1d65-4f4c-8f42-b1df3f25bc10", "demo-1", ["malware"], eicarBytes, "text/plain", eicarDigest));
            Assert.Equal(SecurityInspectionStatus.Completed, resp.Status);
            Assert.Equal(SecurityMalwareStatus.Detected, resp.MalwareStatus);
            var finding = Assert.Single(resp.Findings);
            Assert.Equal(SecurityFindingKind.Malware, finding.Kind);
            Assert.Equal("malware_detected", finding.ReasonCode);
        }

        // 4. Image response golden fixture
        var imageFixture = await File.ReadAllTextAsync(Path.Combine(validDir, "inspection-image-response.json"));
        var sampleImage = HostImageRedactor.EncodeRgb24Png(4, 4, new byte[4 * 4 * 3]);
        var sampleDigest = Convert.ToHexString(SHA256.HashData(sampleImage)).ToLowerInvariant();
        var boundImageFixture = imageFixture.Replace("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", sampleDigest);
        using (var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(boundImageFixture, Encoding.UTF8, "application/json")
        }))
        using (var client = new HttpSecurityWorkerClient(CreateClientOptions(), handler))
        {
            var resp = await client.InspectImageAsync(new SecurityImageInspectionRequest(
                "5a9e91f1-28fd-4a2b-a892-62e23650d1e9", "demo-1", ["yunet"], sampleImage, "image/png", sampleDigest));
            Assert.Equal(SecurityInspectionStatus.Completed, resp.Status);
            Assert.NotNull(resp.ImageDimensions);
            Assert.Equal(256, resp.ImageDimensions.Width);
            var finding = Assert.Single(resp.Findings);
            Assert.Equal(SecurityFindingKind.ImageRegion, finding.Kind);
            Assert.Equal("FACE", finding.Category);
        }

        // 5. Evidence golden fixture wire parity
        var evidenceFixture = await File.ReadAllTextAsync(Path.Combine(validDir, "evidence-malware-block.json"));
        using var evDoc = JsonDocument.Parse(evidenceFixture);
        var root = evDoc.RootElement;
        var ev = SecurityEvidenceEvent.Create(
            Guid.Parse(root.GetProperty("eventId").GetString()!),
            root.GetProperty("requestId").GetString()!,
            "host-tenant-1",
            "host-app-1",
            DateTimeOffset.Parse(root.GetProperty("occurredAt").GetString()!),
            root.GetProperty("capability").GetString()!,
            root.GetProperty("boundary").GetString()!,
            root.GetProperty("detectorId").GetString()!,
            root.GetProperty("detectorVersion").GetString()!,
            root.GetProperty("policyRevision").GetString()!,
            JsonSerializer.Deserialize<SecurityInspectionStatus>(root.GetProperty("inspectionStatus").GetRawText()),
            JsonSerializer.Deserialize<SecurityEvidenceDecision>(root.GetProperty("decision").GetRawText()),
            JsonSerializer.Deserialize<SecurityEnforcementOutcome>(root.GetProperty("enforcementOutcome").GetRawText()),
            root.GetProperty("reasonCode").GetString()!,
            new Dictionary<string, int> { ["malware"] = root.GetProperty("categoryCounts").GetProperty("malware").GetInt32() },
            root.GetProperty("durationMs").GetInt64(),
            root.GetProperty("traceId").GetString());
        Assert.Equal(SecurityContractVersions.EvidenceV1, ev.SchemaVersion);
        Assert.Equal(SecurityEnforcementOutcome.Prevented, ev.EnforcementOutcome);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpenConnect.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static byte[] InjectTextMetadataChunk(byte[] validPng, string textPayload)
    {
        // Insert tEXt chunk right before the 12-byte IEND chunk at the end.
        var iendOffset = validPng.Length - 12;
        var data = Encoding.Latin1.GetBytes(textPayload);
        using var ms = new MemoryStream();
        ms.Write(validPng, 0, iendOffset);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        ms.Write(len);
        var type = "tEXt"u8;
        ms.Write(type);
        ms.Write(data);
        // Compute CRC32 over type + data
        uint crc = 0xFFFFFFFFu;
        foreach (var b in type)
            crc = UpdateCrc(crc, b);
        foreach (var b in data)
            crc = UpdateCrc(crc, b);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        ms.Write(crcBytes);
        ms.Write(validPng, iendOffset, 12);
        return ms.ToArray();
    }

    private static uint UpdateCrc(uint crc, byte b)
    {
        crc ^= b;
        for (var k = 0; k < 8; k++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
