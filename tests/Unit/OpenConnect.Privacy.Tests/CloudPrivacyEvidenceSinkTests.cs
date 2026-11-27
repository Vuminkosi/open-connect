using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class CloudPrivacyEvidenceSinkTests
{
    [Fact]
    public void Explicit_server_pin_requires_matching_certificate_hostname_and_validity()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=cloud.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-1), now.AddHours(1));
        var pin = certificate.GetCertHash(HashAlgorithmName.SHA256);
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.False(chain.Build(certificate));
        Assert.True(CloudPrivacyEvidenceSink.IsPinnedServerCertificate(certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors, pin, now));
        Assert.True(CloudPrivacyEvidenceSink.IsPinnedServerCertificate(certificate, chain, SslPolicyErrors.None, pin, now));
        Assert.False(CloudPrivacyEvidenceSink.IsPinnedServerCertificate(certificate, chain, SslPolicyErrors.RemoteCertificateNameMismatch, pin, now));
        Assert.False(CloudPrivacyEvidenceSink.IsPinnedServerCertificate(certificate, chain, SslPolicyErrors.RemoteCertificateNotAvailable, pin, now));
        Assert.False(CloudPrivacyEvidenceSink.IsPinnedServerCertificate(certificate, chain, SslPolicyErrors.None, new byte[32], now));
        Assert.False(CloudPrivacyEvidenceSink.IsPinnedServerCertificate(certificate, chain, SslPolicyErrors.None, pin, now.AddHours(2)));
        Assert.False(CloudPrivacyEvidenceSink.IsPinnedServerCertificate(null, chain, SslPolicyErrors.None, pin, now));
        Assert.Throws<ArgumentException>(() => new CloudPrivacyEvidenceSink(new Uri("https://cloud.example/"), certificate, "token", "invalid"));
    }

    [Fact]
    public void Explicit_server_pin_rejects_client_only_certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=cloud.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-1), now.AddHours(1));
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.False(chain.Build(certificate));
        Assert.False(CloudPrivacyEvidenceSink.IsPinnedServerCertificate(certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors,
            certificate.GetCertHash(HashAlgorithmName.SHA256), now));
    }

    [Fact]
    public async Task TransientFailuresRetryTheSameEvidenceWithoutContent()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context,
            new EgressDecision(false, null, [new(0, 3, "EMAIL")], SecurityPolicyAction.Block, "literal_sensitive_value_detected"));
        var bodies = new List<string>();
        using var http = new HttpClient(new Handler(async request =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync());
            if (bodies.Count == 1)
                throw new HttpRequestException("transient");
            return bodies.Count == 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Reply($"{{\"eventId\":\"{evidence.EventId:D}\"}}");
        }));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await sink.RecordAsync(evidence);
        Assert.Equal(3, bodies.Count);
        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
        using var json = JsonDocument.Parse(bodies[0]);
        Assert.Equal(evidence.EventId, json.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal("sensitive_detected", json.RootElement.GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task AuthorizationFailureIsNotRetried()
    {
        var attempts = 0;
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, new EgressDecision(false, null, [], SecurityPolicyAction.Block, "reason"));
        using var http = new HttpClient(new Handler(_ => { attempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); }));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.RecordAsync(evidence));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RepeatedTemporaryFailuresAreBoundedAndNotAcknowledged()
    {
        var attempts = 0;
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, new EgressDecision(false, null, [], SecurityPolicyAction.Block, "reason"));
        using var http = new HttpClient(new Handler(_ => { attempts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.RecordAsync(evidence));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Exhausted_transport_errors_do_not_expose_diagnostics()
    {
        var attempts = 0;
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, new EgressDecision(false, null, [], SecurityPolicyAction.Block, "reason"));
        using var http = new HttpClient(new Handler(_ =>
        {
            attempts++;
            throw new HttpRequestException("private network diagnostic");
        }));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.RecordAsync(evidence));
        Assert.Equal(3, attempts);
        Assert.DoesNotContain("private network diagnostic", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sends_only_metadata_and_requires_matching_acknowledgment()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context,
            new EgressDecision(true, "secret-content", [new(0, 3, "EMAIL")], SecurityPolicyAction.Mask, "secret-reason"));
        var handler = new Handler(async request =>
        {
            Assert.Equal("https://cloud.example/cloud/internal/v1/privacy/evidence", request.RequestUri!.ToString());
            Assert.Equal("credential", request.Headers.Authorization!.Parameter);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("secret-content", body);
            Assert.DoesNotContain("secret-reason", body);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(evidence.EventId, json.RootElement.GetProperty("eventId").GetGuid());
            Assert.Equal("Output", json.RootElement.GetProperty("destination").GetString());
            Assert.Equal("allowed", json.RootElement.GetProperty("reasonCode").GetString());
            return Reply($"{{\"eventId\":\"{evidence.EventId:D}\"}}");
        });
        using var http = new HttpClient(handler);
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await sink.RecordAsync(evidence);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"eventId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"eventId\":null}")]
    public async Task Invalid_acknowledgment_does_not_report_delivery(string response)
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, new EgressDecision(false, null, [], SecurityPolicyAction.Block, "reason"));
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply(response))));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.RecordAsync(evidence));
    }

    [Fact]
    public async Task Cancels_when_acknowledgment_body_stalls()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, new EgressDecision(false, null, [], SecurityPolicyAction.Block, "reason"));
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) })));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sink.RecordAsync(evidence, cancellation.Token));
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Rejects_oversized_acknowledgment_without_content_length()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, new EgressDecision(false, null, [], SecurityPolicyAction.Block, "reason"));
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new UnseekableStream(new byte[1025])) })));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await sink.RecordAsync(evidence));
    }

    private sealed class UnseekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task Rejects_http_failure_and_oversized_acknowledgment()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Worker);
        var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, new EgressDecision(false, null, [], SecurityPolicyAction.Block, "reason"));
        using var unavailable = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
        using var unavailableSink = new CloudPrivacyEvidenceSink(unavailable, new Uri("https://cloud.example/"), "credential");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await unavailableSink.RecordAsync(evidence));
        using var oversized = new HttpClient(new Handler(_ => Task.FromResult(Reply(new string('x', 1025)))));
        using var oversizedSink = new CloudPrivacyEvidenceSink(oversized, new Uri("https://cloud.example/"), "credential");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await oversizedSink.RecordAsync(evidence));
    }

    [Fact]
    public void Rejects_non_https_or_non_origin_authorities()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        Assert.Throws<ArgumentException>(() => new CloudPrivacyEvidenceSink(http, new Uri("http://cloud.example/"), "credential"));
        Assert.Throws<ArgumentException>(() => new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/path"), "credential"));
    }

    [Fact]
    public async Task Serializes_additive_receipt_metadata_and_integrity_digest_without_content()
    {
        var context = new EgressContext(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.ExternalModel);
        var occurredAt = new DateTimeOffset(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);
        var evidence = HostPrivacyEvidence.Create(
            Guid.NewGuid(),
            context,
            new EgressDecision(false, null, [new(0, 4, "SECRET")], SecurityPolicyAction.Block, "literal_sensitive_value_detected"),
            traceId: "4bf92f3577b34da6a3ce929d0e0e4736",
            requestId: "req-99",
            occurredAt: occurredAt,
            policyId: "default/strict-local",
            policyRevision: "v2");

        string? capturedBody = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return Reply($"{{\"eventId\":\"{evidence.EventId:D}\"}}");
        }));
        using var sink = new CloudPrivacyEvidenceSink(http, new Uri("https://cloud.example/"), "credential");
        await sink.RecordAsync(evidence);

        Assert.NotNull(capturedBody);
        using var json = JsonDocument.Parse(capturedBody!);
        var root = json.RootElement;
        Assert.Equal("application-host", root.GetProperty("enforcementAuthority").GetString());
        Assert.Equal(evidence.IntegrityDigest, root.GetProperty("integrityDigest").GetString());
        Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", root.GetProperty("traceId").GetString());
        Assert.Equal("req-99", root.GetProperty("requestId").GetString());
        Assert.Equal("default/strict-local", root.GetProperty("policyId").GetString());
        Assert.Equal("v2", root.GetProperty("policyRevision").GetString());
        Assert.Equal(occurredAt, DateTimeOffset.Parse(root.GetProperty("occurredAt").GetString()!).ToUniversalTime());
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
