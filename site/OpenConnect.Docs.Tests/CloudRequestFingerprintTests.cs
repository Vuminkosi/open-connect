using System.Text;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudRequestFingerprintTests
{
    [Fact]
    public void CanonicalHmacMatchesIndependentVectorAndBindsRequestFields()
    {
        var key = Enumerable.Range(0, 32).Select(x => (byte)x).ToArray();
        var input = new CloudFingerprintInput(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "inference.chat", "POST", "/cloud/v1/inference/chat",
            "application/json", Encoding.UTF8.GetBytes("{\"message\":\"hello\"}"));
        var fingerprint = CloudRequestFingerprint.Compute("key_1", key, input);
        Assert.Equal("4CBC6A790B4D72FB808E5FDD040E8BD7AFCFD1066AA6B07281D84FD6DE979C85", Convert.ToHexString(fingerprint));
        Assert.NotEqual(fingerprint, CloudRequestFingerprint.Compute("key_2", key, input));
        Assert.NotEqual(fingerprint, CloudRequestFingerprint.Compute("key_1", key, input with { TenantId = Guid.NewGuid() }));
        Assert.NotEqual(fingerprint, CloudRequestFingerprint.Compute("key_1", key, input with { ApplicationId = Guid.NewGuid() }));
        Assert.NotEqual(fingerprint, CloudRequestFingerprint.Compute("key_1", key, input with { CapabilityId = "other" }));
        Assert.NotEqual(fingerprint, CloudRequestFingerprint.Compute("key_1", key, input with { Method = "GET" }));
        Assert.NotEqual(fingerprint, CloudRequestFingerprint.Compute("key_1", key, input with { Route = "/other" }));
        Assert.NotEqual(fingerprint, CloudRequestFingerprint.Compute("key_1", key, input with { MediaType = "text/plain" }));
        Assert.NotEqual(fingerprint, CloudRequestFingerprint.Compute("key_1", key, input with { Body = Encoding.UTF8.GetBytes("changed") }));
    }

    [Fact]
    public void RejectsWeakKeyAndInvalidKeyVersion()
    {
        var input = new CloudFingerprintInput(Guid.NewGuid(), Guid.NewGuid(), "x", "POST", "/x", "application/json", ReadOnlyMemory<byte>.Empty);
        Assert.Throws<ArgumentException>(() => CloudRequestFingerprint.Compute("key_1", new byte[31], input));
        Assert.Throws<ArgumentException>(() => CloudRequestFingerprint.Compute("bad key", new byte[32], input));
    }
}
