namespace OpenConnect.QdrantVactor;

public sealed class QdrantVactorOptions
{
    public bool Enabled { get; set; }
    public Uri? Endpoint { get; set; }
    public string CollectionName { get; set; } = "openconnect-memory";
    public bool SubscribeToAgentEvents { get; set; } = true;
}
