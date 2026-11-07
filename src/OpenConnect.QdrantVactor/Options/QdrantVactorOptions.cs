namespace OpenConnect.QdrantVactor;

public sealed class QdrantVactorOptions
{
    public bool Enabled { get; set; }
    public Uri? Endpoint { get; set; }
    public string CollectionName { get; set; } = "openconnect-memory";
    public bool SubscribeToAgentEvents { get; set; } = true;

    /// <summary>API key for the Qdrant REST store. Supply from a secret store; never commit it.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Embedding dimension of the configured collection; must match the embedding generator.</summary>
    public int VectorSize { get; set; } = 8;
}
