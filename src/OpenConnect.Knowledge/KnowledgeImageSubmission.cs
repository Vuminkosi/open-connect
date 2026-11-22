namespace OpenConnect.Knowledge;

/// <summary>Immutable parent document and image revision for a derived image vector.</summary>
public sealed class KnowledgeImageSubmission
{
    /// <summary>Creates a generation-fenced image target; the host must authorize it.</summary>
    public KnowledgeImageSubmission(string applicationId, string collectionId, string documentId, string sourceRevision,
        long expectedGeneration, string imageId, string imageRevision, string? expectedImageRevision = null)
    {
        if (!KnowledgeIndexSubmission.Identifier(applicationId) || !KnowledgeIndexSubmission.Identifier(collectionId) ||
            !KnowledgeIndexSubmission.Identifier(documentId) || !KnowledgeIndexSubmission.Identifier(sourceRevision) ||
            !KnowledgeIndexSubmission.Identifier(imageId) || !KnowledgeIndexSubmission.Identifier(imageRevision) ||
            (expectedImageRevision is not null && !KnowledgeIndexSubmission.Identifier(expectedImageRevision)) ||
            expectedGeneration < 1 || expectedGeneration == long.MaxValue)
            throw new ArgumentException("The image indexing submission is invalid.");
        ApplicationId = applicationId; CollectionId = collectionId; DocumentId = documentId; SourceRevision = sourceRevision;
        ExpectedGeneration = expectedGeneration; ImageId = imageId; ImageRevision = imageRevision;
        ExpectedImageRevision = expectedImageRevision;
    }

    public string ApplicationId { get; }
    public string CollectionId { get; }
    public string DocumentId { get; }
    public string SourceRevision { get; }
    public long ExpectedGeneration { get; }
    public string ImageId { get; }
    public string ImageRevision { get; }
    public string? ExpectedImageRevision { get; }
}
