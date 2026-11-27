using System.Text;

namespace OpenConnect.Knowledge;

/// <summary>Immutable, versioned object reference and destination for a knowledge indexing job.</summary>
public sealed class KnowledgeIndexSubmission
{
    /// <summary>Creates a submission; the host must authorize the object and ACL before enqueueing it.</summary>
    public KnowledgeIndexSubmission(Guid jobId, string applicationId, string collectionId, string documentId,
        string sourceRevision, string embeddingSpaceId, string objectId, string objectSha256, int objectSize,
        string objectVersion, long expectedGeneration, IReadOnlyList<string> principals, IReadOnlyList<string> roles,
        DateTimeOffset? documentExpiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(roles);
        if (principals.Count > 64 || roles.Count > 64)
            throw new ArgumentException("The indexing submission is invalid.");
        var principalSnapshot = Array.AsReadOnly(principals.ToArray());
        var roleSnapshot = Array.AsReadOnly(roles.ToArray());
        if (jobId == Guid.Empty || !Identifier(applicationId) || !Identifier(collectionId) || !Identifier(documentId) ||
            !Identifier(sourceRevision) || !Digest(embeddingSpaceId) || !Digest(objectId) || !Digest(objectSha256) ||
            objectSize is < 1 or > 10485760 || !SafeText(objectVersion, 1024) || objectVersion == "null" || expectedGeneration < 0 ||
            expectedGeneration == long.MaxValue || principalSnapshot.Count > 64 || roleSnapshot.Count > 64 ||
            principalSnapshot.Count + roleSnapshot.Count == 0 || principalSnapshot.Any(value => !Identifier(value)) ||
            roleSnapshot.Any(value => !Identifier(value)))
            throw new ArgumentException("The indexing submission is invalid.");
        JobId = jobId;
        ApplicationId = applicationId;
        CollectionId = collectionId;
        DocumentId = documentId;
        SourceRevision = sourceRevision;
        EmbeddingSpaceId = embeddingSpaceId;
        ObjectId = objectId;
        ObjectSha256 = objectSha256;
        ObjectSize = objectSize;
        ObjectVersion = objectVersion;
        ExpectedGeneration = expectedGeneration;
        Principals = principalSnapshot;
        Roles = roleSnapshot;
        DocumentExpiresAt = documentExpiresAt;
    }

    public Guid JobId { get; }
    public string ApplicationId { get; }
    public string CollectionId { get; }
    public string DocumentId { get; }
    public string SourceRevision { get; }
    public string EmbeddingSpaceId { get; }
    public string ObjectId { get; }
    public string ObjectSha256 { get; }
    public int ObjectSize { get; }
    public string ObjectVersion { get; }
    public long ExpectedGeneration { get; }
    public IReadOnlyList<string> Principals { get; }
    public IReadOnlyList<string> Roles { get; }
    public DateTimeOffset? DocumentExpiresAt { get; }

    internal static bool Identifier(string? value) => !string.IsNullOrWhiteSpace(value) && SafeText(value, 256);
    private static bool SafeText(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        try
        { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException) { return false; }
        return value.EnumerateRunes().Count() <= maximum && value.EnumerateRunes().All(rune => rune.Value >= 32 && rune.Value != 127);
    }
    internal static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
