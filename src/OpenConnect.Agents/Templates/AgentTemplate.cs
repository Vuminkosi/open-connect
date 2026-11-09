using OpenConnect.Abstractions;

namespace OpenConnect.Agents.Templates;

/// <summary>A versioned, server-owned definition of an agent's bounded capabilities.</summary>
/// <param name="Id">Stable template identifier.</param>
/// <param name="Version">Template version.</param>
/// <param name="Description">Human-readable template description.</param>
/// <param name="InstructionsPath">Safe relative path to the instruction text.</param>
/// <param name="ModelRequirements">Required model characteristics, if any.</param>
/// <param name="AllowedToolIds">Tools available to agents using this template.</param>
/// <param name="OutputSchemaRef">Optional reference to an output schema.</param>
/// <param name="Budget">Resource limits for agents using this template.</param>
/// <param name="MemoryPolicy">Memory handling policy.</param>
/// <param name="ContextSharing">Context sharing mode.</param>
/// <param name="AckPolicy">Tool acknowledgement policy.</param>
/// <param name="SafetyLabel">Safety classification label.</param>
/// <param name="BaseTemplateId">Optional base template identifier.</param>
/// <param name="AllowedScopes">Optional allowed authorization scopes.</param>
public sealed record AgentTemplate(
    string Id,
    string Version,
    string Description,
    string InstructionsPath,
    ModelRequirement? ModelRequirements,
    IReadOnlySet<ToolId> AllowedToolIds,
    string? OutputSchemaRef,
    AgentBudget Budget,
    AgentMemoryPolicy MemoryPolicy,
    AgentContextSharingMode ContextSharing,
    ToolAcknowledgementMode AckPolicy,
    string SafetyLabel,
    string? BaseTemplateId = null,
    IReadOnlySet<string>? AllowedScopes = null);

/// <summary>Values accepted by template instruction placeholders.</summary>
public sealed record TemplateVariables
{
    /// <summary>Gets or initializes the application name.</summary>
    public string? ApplicationName { get; init; }
    /// <summary>Gets or initializes the task goal.</summary>
    public string? TaskGoal { get; init; }
    /// <summary>Gets or initializes the requested locale.</summary>
    public string? Locale { get; init; }
    /// <summary>Gets or initializes a sanitized user profile summary.</summary>
    public string? UserProfileSummary { get; init; }
}

/// <summary>Rendered instruction text and its content-derived SHA-256 revision.</summary>
/// <param name="Content">Rendered instruction content.</param>
/// <param name="Revision">Lowercase hexadecimal SHA-256 content revision.</param>
public sealed record RenderedTemplateInstructions(string Content, string Revision);
