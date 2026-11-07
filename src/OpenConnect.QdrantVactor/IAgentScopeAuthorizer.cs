namespace OpenConnect.QdrantVactor;

/// <summary>Checks the requested tenant and agent against the server-authenticated scope.</summary>
public interface IAgentScopeAuthorizer
{
    bool Authorize(string tenantId, string agentId);
}
