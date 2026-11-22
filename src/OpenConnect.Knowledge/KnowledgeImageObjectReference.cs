using System.Text;

namespace OpenConnect.Knowledge;

/// <summary>Immutable, tenant-scoped MinIO object identity and content integrity metadata.</summary>
public sealed class KnowledgeImageObjectReference
{
    /// <summary>Creates an object reference; the host must authorize it before access.</summary>
    public KnowledgeImageObjectReference(string objectId, string objectSha256, int objectSize, string objectVersion)
    {
        if (!KnowledgeIndexSubmission.Digest(objectId) || !KnowledgeIndexSubmission.Digest(objectSha256) ||
            objectSize is < 1 or > 10 * 1024 * 1024 || string.IsNullOrEmpty(objectVersion) || objectVersion == "null" || objectVersion.Length > 1024 ||
            objectVersion.Any(c => c < 32 || c == 127))
            throw new ArgumentException("The image object reference is invalid.");
        try
        { _ = new UTF8Encoding(false, true).GetByteCount(objectVersion); }
        catch (EncoderFallbackException) { throw new ArgumentException("The image object reference is invalid."); }
        ObjectId = objectId;
        ObjectSha256 = objectSha256;
        ObjectSize = objectSize;
        ObjectVersion = objectVersion;
    }

    public string ObjectId { get; }
    public string ObjectSha256 { get; }
    public int ObjectSize { get; }
    public string ObjectVersion { get; }
}
