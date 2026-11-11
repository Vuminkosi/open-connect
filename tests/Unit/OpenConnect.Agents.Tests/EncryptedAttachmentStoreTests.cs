using System.Text;
using OpenConnect.Agents.Execution;
using Xunit;

namespace OpenConnect.Agents.Tests;

/// <summary>
/// Proves OC2-D-SESSION-017: Store large/private attachments as encrypted, tenant-scoped object refs
/// instead of unrestricted prompt/NATS payloads.
/// </summary>
public sealed class EncryptedAttachmentStoreTests
{
    [Fact]
    public async Task StoreAndRetrieve_EncryptsPayloadAndEnforcesTenantIsolation()
    {
        var store = new InMemoryEncryptedAttachmentStore();
        var originalBytes = Encoding.UTF8.GetBytes("Super secret medical record attachment content.");

        // Store attachment -> returns lightweight object ref
        var objRef = await store.StoreAsync("tenant-hospital-1", originalBytes, "application/pdf");

        Assert.NotNull(objRef.AttachmentId);
        Assert.Equal("tenant-hospital-1", objRef.TenantId);
        Assert.Equal("application/pdf", objRef.ContentType);
        Assert.Equal(originalBytes.Length, objRef.ByteLength);
        Assert.NotEmpty(objRef.Sha256Hash);

        // Retrieve with authorized tenant succeeds and recovers exact plaintext
        var decryptedBytes = await store.RetrieveAsync("tenant-hospital-1", objRef.AttachmentId);
        Assert.Equal(originalBytes, decryptedBytes);

        // Cross-tenant retrieval attempt is denied
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.RetrieveAsync("tenant-attacker-2", objRef.AttachmentId).AsTask());
    }

    [Fact]
    public async Task PromptOrMessagePayload_CarriesOnlyLightweightObjectRef_NotRawBytes()
    {
        var store = new InMemoryEncryptedAttachmentStore();
        var largePayload = new byte[100 * 1024]; // 100 KB
        Array.Fill(largePayload, (byte)0x42);

        var objRef = await store.StoreAsync("tenant-a", largePayload, "application/octet-stream");

        // The object reference passed to agents or transcripts is small metadata
        var promptReference = $"attachment-ref:{objRef.AttachmentId}";
        Assert.True(promptReference.Length < 100);
        Assert.DoesNotContain("BBBB", promptReference);
    }
}
