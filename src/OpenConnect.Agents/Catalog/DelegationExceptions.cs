namespace OpenConnect.Agents.Catalog;

/// <summary>Base class for rejected catalog or delegation operations.</summary>
public class AgentCatalogException : InvalidOperationException
{
    /// <summary>Initializes an exception describing a rejected catalog or delegation operation.</summary>
    /// <param name="message">A description of the rejection.</param>
    public AgentCatalogException(string message) : base(message) { }
}

/// <summary>Raised when the requested delegation exceeds the permitted graph depth.</summary>
public sealed class DelegationDepthExceededException : AgentCatalogException
{
    /// <summary>Initializes an exception describing a delegation depth violation.</summary>
    /// <param name="requestedDepth">The requested depth.</param>
    /// <param name="maximumDepth">The maximum permitted depth.</param>
    public DelegationDepthExceededException(int requestedDepth, int maximumDepth)
        : base($"Delegation depth {requestedDepth} exceeds the maximum depth {maximumDepth}.")
    {
        RequestedDepth = requestedDepth;
        MaximumDepth = maximumDepth;
    }

    /// <summary>Gets the requested delegation depth.</summary>
    public int RequestedDepth { get; }
    /// <summary>Gets the maximum permitted delegation depth.</summary>
    public int MaximumDepth { get; }
}
