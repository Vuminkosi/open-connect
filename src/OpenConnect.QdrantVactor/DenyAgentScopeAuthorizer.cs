namespace OpenConnect.QdrantVactor;

/// <summary>Fail-closed default until the host supplies its authenticated-scope authorizer.</summary>
internal sealed class DenyAgentScopeAuthorizer : IAgentScopeAuthorizer
{
    public bool Authorize(string tenantId, string agentId) => false;
}
